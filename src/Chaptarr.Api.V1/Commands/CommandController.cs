using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using Chaptarr.Http.Validation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Notifications.CalibreContentServer;
using NzbDrone.Core.Notifications.Grimmory;
using NzbDrone.Core.IndexerSearch;
using NLog;
using NzbDrone.Common.Composition;
using NzbDrone.Common.Serializer;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ProgressMessaging;
using NzbDrone.Http.REST.Attributes;
using NzbDrone.SignalR;

namespace Chaptarr.Api.V1.Commands
{
    [V1ApiController]
    public class CommandController : RestControllerWithSignalR<CommandResource, CommandModel>, IHandle<CommandUpdatedEvent>
    {
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly KnownTypes _knownTypes;
        private readonly Logger _logger;
        private readonly Debouncer _debouncer;
        private readonly Dictionary<int, CommandResource> _pendingUpdates;
        private readonly IBookshelfLegacyIdentityMapService _bookshelfLegacyIdentityMapService;

        private readonly CommandPriorityComparer _commandPriorityComparer = new CommandPriorityComparer();

        public CommandController(IManageCommandQueue commandQueueManager,
                             IBroadcastSignalRMessage signalRBroadcaster,
                             KnownTypes knownTypes,
                             Logger logger,
                             IBookshelfLegacyIdentityMapService bookshelfLegacyIdentityMapService = null)
            : base(signalRBroadcaster)
        {
            _commandQueueManager = commandQueueManager;
            _knownTypes = knownTypes;
            _logger = logger;
            _bookshelfLegacyIdentityMapService = bookshelfLegacyIdentityMapService;

            _debouncer = new Debouncer(SendUpdates, TimeSpan.FromSeconds(0.1));
            _pendingUpdates = new Dictionary<int, CommandResource>();

            PostValidator.RuleFor(c => c.Name).NotBlank();
        }

        protected override CommandResource GetResourceById(int id)
        {
            return MapToResource(_commandQueueManager.Get(id));
        }

        protected override bool ShouldBroadcastSyncForResource(CommandResource resource)
        {
            var bookIds = GetCommandBookIds(resource?.Body).Where(id => id > 0).Distinct().ToList();
            return bookIds.Count > 0 &&
                   _bookshelfLegacyIdentityMapService?.GetMappingsByTargetIds("Book", bookIds).Count > 0;
        }

        private CommandResource MapToResource(CommandModel model)
        {
            var resource = model.ToResource();
            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true)
            {
                if (resource?.Body != null)
                {
                    resource.Body = (Command)STJson.Deserialize(STJson.ToJson(resource.Body), resource.Body.GetType());
                }

                ProjectLegacyCommandBookIds(resource);
            }

            return resource;
        }

        private void ProjectLegacyCommandBookIds(CommandResource resource)
        {
            if (resource?.Body == null || _bookshelfLegacyIdentityMapService == null)
            {
                return;
            }

            var bookIds = GetCommandBookIds(resource.Body).Where(id => id > 0).Distinct().ToList();
            var sourceIdByTargetId = _bookshelfLegacyIdentityMapService
                .GetMappingsByTargetIds("Book", bookIds)
                .GroupBy(mapping => mapping.TargetId)
                .ToDictionary(group => group.Key, group => group.First().SourceId);

            int ProjectId(int targetId)
            {
                return sourceIdByTargetId.TryGetValue(targetId, out var sourceId) ? sourceId : targetId;
            }

            switch (resource.Body)
            {
                case BookSearchCommand command:
                    command.BookIds = command.BookIds?.Select(ProjectId).Distinct().ToList();
                    break;
                case BulkRefreshBookCommand command:
                    command.BookIds = command.BookIds?.Select(ProjectId).Distinct().ToList();
                    break;
                case PushCalibreMetadataCommand command:
                    command.BookIds = command.BookIds?.Select(ProjectId).Distinct().ToList();
                    break;
                case PushGrimmoryMetadataCommand command:
                    command.BookIds = command.BookIds?.Select(ProjectId).Distinct().ToList();
                    break;
                case RePushBookCommand command:
                    command.BookIds = command.BookIds?.Select(ProjectId).Distinct().ToList();
                    if (command.BookId > 0)
                    {
                        command.BookId = ProjectId(command.BookId);
                    }
                    break;
                case RefreshBookCommand command:
                    command.BookIds = command.BookIds?.Select(ProjectId).Distinct().ToList();
                    if (command.BookId > 0)
                    {
                        command.BookId = ProjectId(command.BookId.Value);
                    }

                    // RefreshBookCommand is a one-work command in BookshelfNG.
                    // Hide the internal fan-out list and keep its original body shape.
                    if (command.BookIds?.Count > 0)
                    {
                        command.BookId ??= command.BookIds[0];
                        command.BookIds = null;
                    }
                    break;
                case DownloadBookMediaCommand command:
                    command.BookIds = command.BookIds?.Select(ProjectId).Distinct().ToList();
                    if (command.BookId > 0)
                    {
                        command.BookId = ProjectId(command.BookId);
                    }
                    break;
            }
        }

        private static IEnumerable<int> GetCommandBookIds(Command command)
        {
            return command switch
            {
                BookSearchCommand value => value.BookIds ?? Enumerable.Empty<int>(),
                BulkRefreshBookCommand value => value.BookIds ?? Enumerable.Empty<int>(),
                PushCalibreMetadataCommand value => value.BookIds ?? Enumerable.Empty<int>(),
                PushGrimmoryMetadataCommand value => value.BookIds ?? Enumerable.Empty<int>(),
                RePushBookCommand value => (value.BookIds ?? new List<int>()).Concat(value.BookId > 0 ? new[] { value.BookId } : Array.Empty<int>()),
                RefreshBookCommand value => (value.BookIds ?? new List<int>()).Concat(value.BookId > 0 ? new[] { value.BookId.Value } : Array.Empty<int>()),
                DownloadBookMediaCommand value => (value.BookIds ?? new List<int>()).Concat(value.BookId > 0 ? new[] { value.BookId } : Array.Empty<int>()),
                _ => Array.Empty<int>()
            };
        }

        [RestPostById]
        public ActionResult<CommandResource> StartCommand([FromBody] CommandResource commandResource)
        {
            var commandType =
                _knownTypes.GetImplementations(typeof(Command))
                               .Single(c => c.Name.Replace("Command", "")
                                             .Equals(commandResource.Name, StringComparison.InvariantCultureIgnoreCase));

            if (commandType == typeof(MoveAuthorMediaCommand) || commandType == typeof(MoveAuthorMediaBatchCommand))
            {
                throw new BadRequestException("Author media moves must be started through the preview-validated author/media-move endpoint.");
            }

            Request.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(Request.Body);
            var body = reader.ReadToEnd();
            var priority = commandType == typeof(ManualImportCommand)
                ? CommandPriority.High
                : CommandPriority.Normal;

            dynamic command = STJson.Deserialize(body, commandType);

            if (_bookshelfLegacyIdentityMapService != null &&
                HttpContext?.GetReadarrFacadeContext()?.AcceptsBookshelfLegacyBookIds == true)
            {
                var facadeMediaType = MediaTypeParameterParser.ParseOptional(HttpContext?.GetReadarrFacadeContext()?.MediaType);
                command = ResolveLegacyBookCommandIds((Command)command, facadeMediaType);
            }

            if (command is RescanFoldersCommand rescanFoldersCommand)
            {
                rescanFoldersCommand.MediaType = ValidateMediaType(rescanFoldersCommand.MediaType, " when unmappedFiles is provided");
                ValidateUnmappedFilesSelection(rescanFoldersCommand.MediaType, rescanFoldersCommand.UnmappedFiles);
            }

            if (command is SendMatchingLogsCommand sendMatchingLogsCommand)
            {
                sendMatchingLogsCommand.MediaType = ValidateMediaType(sendMatchingLogsCommand.MediaType, " when unmappedFiles is provided");
                ValidateUnmappedFilesSelection(sendMatchingLogsCommand.MediaType, sendMatchingLogsCommand.UnmappedFiles);
            }

            if (command is RetryUnmappedMatchCommand retryUnmappedMatchCommand)
            {
                retryUnmappedMatchCommand.MediaType = ValidateMediaType(retryUnmappedMatchCommand.MediaType, " when unmappedFiles is provided");
                ValidateUnmappedFilesSelection(retryUnmappedMatchCommand.MediaType, retryUnmappedMatchCommand.UnmappedFiles);
            }

            if (command is RefreshUnmappedFilesCommand refreshUnmappedFilesCommand)
            {
                refreshUnmappedFilesCommand.MediaType = ValidateMediaType(refreshUnmappedFilesCommand.MediaType, " when unmappedFiles is provided");
                ValidateUnmappedFilesSelection(refreshUnmappedFilesCommand.MediaType, refreshUnmappedFilesCommand.UnmappedFiles);
            }

            if (command is RenameAuthorCommand renameAuthorCommand)
            {
                renameAuthorCommand.MediaType = ValidateMediaType(renameAuthorCommand.MediaType);
            }

            if (command is MissingBookSearchCommand missingBookSearchCommand)
            {
                missingBookSearchCommand.MediaType = ValidateMediaType(missingBookSearchCommand.MediaType);
            }

            if (command is CutoffUnmetBookSearchCommand cutoffUnmetBookSearchCommand)
            {
                cutoffUnmetBookSearchCommand.MediaType = ValidateMediaType(cutoffUnmetBookSearchCommand.MediaType);
            }

            command.Trigger = CommandTrigger.Manual;
            command.SuppressMessages = !command.SendUpdatesToClient;
            command.SendUpdatesToClient = true;
            command.ClientUserAgent = Request.Headers["User-Agent"];

            var trackedCommand = _commandQueueManager.Push(command, priority, CommandTrigger.Manual);

            return Created(trackedCommand.Id);
        }

        private Command ResolveLegacyBookCommandIds(Command command, BookMediaType? mediaType)
        {
            switch (command)
            {
                case BookSearchCommand bookSearchCommand when bookSearchCommand.BookIds?.Count > 0:
                    bookSearchCommand.BookIds = _bookshelfLegacyIdentityMapService.ResolveBookIds(bookSearchCommand.BookIds, mediaType);
                    break;

                case BulkRefreshBookCommand bulkRefreshBookCommand when bulkRefreshBookCommand.BookIds?.Count > 0:
                    bulkRefreshBookCommand.BookIds = _bookshelfLegacyIdentityMapService.ResolveBookIds(bulkRefreshBookCommand.BookIds, mediaType);
                    break;

                case PushCalibreMetadataCommand pushCalibreMetadataCommand when pushCalibreMetadataCommand.BookIds?.Count > 0:
                    pushCalibreMetadataCommand.BookIds = _bookshelfLegacyIdentityMapService.ResolveBookIds(pushCalibreMetadataCommand.BookIds, mediaType);
                    break;

                case PushGrimmoryMetadataCommand pushGrimmoryMetadataCommand when pushGrimmoryMetadataCommand.BookIds?.Count > 0:
                    pushGrimmoryMetadataCommand.BookIds = _bookshelfLegacyIdentityMapService.ResolveBookIds(pushGrimmoryMetadataCommand.BookIds, mediaType);
                    break;

                case RePushBookCommand rePushBookCommand when rePushBookCommand.BookIds?.Count > 0:
                    rePushBookCommand.BookIds = _bookshelfLegacyIdentityMapService.ResolveBookIds(rePushBookCommand.BookIds, mediaType);
                    if (rePushBookCommand.BookIds.Count == 0)
                    {
                        rePushBookCommand.BookId = 0;
                    }
                    break;

                case RePushBookCommand rePushBookCommand when rePushBookCommand.BookId > 0:
                    var rePushBookIds = _bookshelfLegacyIdentityMapService.ResolveBookIds(new[] { rePushBookCommand.BookId }, mediaType);
                    if (rePushBookIds.Count > 1)
                    {
                        rePushBookCommand.BookIds = rePushBookIds;
                    }
                    else if (rePushBookIds.Count == 1)
                    {
                        rePushBookCommand.BookId = rePushBookIds[0];
                    }
                    else
                    {
                        rePushBookCommand.BookId = 0;
                    }
                    break;

                case RefreshBookCommand refreshBookCommand when refreshBookCommand.BookId > 0:
                    var refreshedBookIds = _bookshelfLegacyIdentityMapService.ResolveBookIds(new[] { refreshBookCommand.BookId.Value }, mediaType);
                    if (refreshedBookIds.Count > 1)
                    {
                        refreshBookCommand.BookIds = refreshedBookIds;
                        refreshBookCommand.BookId = refreshedBookIds[0];
                        break;
                    }

                    if (refreshedBookIds.Count == 1)
                    {
                        refreshBookCommand.BookId = refreshedBookIds[0];
                    }
                    else
                    {
                        throw new BadRequestException("Book ID does not match a book in the requested media type.");
                    }
                    break;

                case DownloadBookMediaCommand downloadBookMediaCommand when downloadBookMediaCommand.BookId > 0:
                    var downloadedBookIds = _bookshelfLegacyIdentityMapService.ResolveBookIds(new[] { downloadBookMediaCommand.BookId }, mediaType);
                    if (downloadedBookIds.Count == 0)
                    {
                        throw new BadRequestException("Book ID does not match a book in the requested media type.");
                    }

                    downloadBookMediaCommand.BookId = downloadedBookIds[0];
                    downloadBookMediaCommand.BookIds = downloadedBookIds;
                    break;
            }

            return command;
        }

        private static void ValidateUnmappedFilesSelection(string mediaType, UnmappedFilesSelection selection)
        {
            if (selection == null)
            {
                return;
            }

            var scope = selection.Scope?.Trim();
            if (string.IsNullOrWhiteSpace(scope))
            {
                throw new BadRequestException("unmappedFiles.scope must be provided");
            }

            if (!string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scope, "selected", StringComparison.OrdinalIgnoreCase))
            {
                throw new BadRequestException("unmappedFiles.scope must be either 'all' or 'selected'");
            }

            if (string.Equals(scope, "selected", StringComparison.OrdinalIgnoreCase) &&
                !(selection.BookFileIds?.Any(id => id > 0) ?? false))
            {
                throw new BadRequestException("unmappedFiles.bookFileIds must be provided when scope is 'selected'");
            }
        }

        private static string ValidateMediaType(string mediaType, string suffix = "")
        {
            try
            {
                return MediaTypeParameterParser.NormalizeOptional(mediaType);
            }
            catch (BadRequestException)
            {
                throw new BadRequestException($"mediaType must be 'all', 'audiobook', or 'ebook'{suffix}");
            }
        }

        [HttpGet]
        public List<CommandResource> GetStartedCommands()
        {
            var resources = _commandQueueManager.All()
                .OrderBy(c => c.Status, _commandPriorityComparer)
                .ThenByDescending(c => c.Priority)
                .Select(MapToResource)
                .ToList();

            return resources;
        }

        [RestDeleteById]
        public void CancelCommand(int id)
        {
            _commandQueueManager.Cancel(id);
        }

        [HttpPost("{id:int}/pause")]
        public void PauseCommand(int id)
        {
            _commandQueueManager.Pause(id);
        }

        [HttpPost("{id:int}/resume")]
        public void ResumeCommand(int id)
        {
            _commandQueueManager.Resume(id);
        }

        [HttpPost("pause/all")]
        public ActionResult<int> PauseAllCommands()
        {
            var commands = _commandQueueManager.All()
                .Where(c => c.Status == CommandStatus.Started)
                .ToList();

            var pausedCount = 0;
            foreach (var command in commands)
            {
                try
                {
                    _commandQueueManager.Pause(command.Id);
                    pausedCount++;
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to pause command {0}", command.Id);
                }
            }

            return Ok(pausedCount);
        }

        [HttpPost("cancel/all")]
        public ActionResult<int> CancelAllCommands()
        {
            var commands = _commandQueueManager.All()
                .Where(c => c.Status == CommandStatus.Queued || c.Status == CommandStatus.Started || c.Status == CommandStatus.Paused)
                .ToList();

            var cancelledCount = 0;
            foreach (var command in commands)
            {
                try
                {
                    _commandQueueManager.Cancel(command.Id);
                    cancelledCount++;
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to cancel command {0}", command.Id);
                }
            }

            return Ok(cancelledCount);
        }

        [HttpPost("resume/all")]
        public ActionResult<int> ResumeAllCommands()
        {
            var commands = _commandQueueManager.All()
                .Where(c => c.Status == CommandStatus.Paused)
                .ToList();

            var resumedCount = 0;
            foreach (var command in commands)
            {
                try
                {
                    _commandQueueManager.Resume(command.Id);
                    resumedCount++;
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to resume command {0}", command.Id);
                }
            }

            return Ok(resumedCount);
        }

        [NonAction]
        public void Handle(CommandUpdatedEvent message)
        {
            if (message.Command.Body.SendUpdatesToClient)
            {
                lock (_pendingUpdates)
                {
                    _pendingUpdates[message.Command.Id] = message.Command.ToResource();
                }

                _debouncer.Execute();
            }
        }

        private void SendUpdates()
        {
            lock (_pendingUpdates)
            {
                var pendingUpdates = _pendingUpdates.Values.ToArray();
                _pendingUpdates.Clear();

                foreach (var pendingUpdate in pendingUpdates)
                {
                    BroadcastResourceChange(ModelAction.Updated, pendingUpdate);

                    if (pendingUpdate.Name == typeof(MessagingCleanupCommand).Name.Replace("Command", "") &&
                        pendingUpdate.Status == CommandStatus.Completed)
                    {
                        BroadcastResourceChange(ModelAction.Sync);
                    }
                }
            }
        }
    }
}

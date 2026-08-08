using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Chaptarr.Http.Validation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.IndexerSearch;
using NLog;
using NzbDrone.Common.Composition;
using NzbDrone.Common.Serializer;
using NzbDrone.Common.TPL;
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

        private readonly CommandPriorityComparer _commandPriorityComparer = new CommandPriorityComparer();

        public CommandController(IManageCommandQueue commandQueueManager,
                             IBroadcastSignalRMessage signalRBroadcaster,
                             KnownTypes knownTypes,
                             Logger logger)
            : base(signalRBroadcaster)
        {
            _commandQueueManager = commandQueueManager;
            _knownTypes = knownTypes;
            _logger = logger;

            _debouncer = new Debouncer(SendUpdates, TimeSpan.FromSeconds(0.1));
            _pendingUpdates = new Dictionary<int, CommandResource>();

            PostValidator.RuleFor(c => c.Name).NotBlank();
        }

        protected override CommandResource GetResourceById(int id)
        {
            return _commandQueueManager.Get(id).ToResource();
        }

        [RestPostById]
        public ActionResult<CommandResource> StartCommand([FromBody] CommandResource commandResource)
        {
            var commandType =
                _knownTypes.GetImplementations(typeof(Command))
                               .Single(c => c.Name.Replace("Command", "")
                                             .Equals(commandResource.Name, StringComparison.InvariantCultureIgnoreCase));

            Request.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(Request.Body);
            var body = reader.ReadToEnd();
            var priority = commandType == typeof(ManualImportCommand)
                ? CommandPriority.High
                : CommandPriority.Normal;

            dynamic command = STJson.Deserialize(body, commandType);

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
            return _commandQueueManager.All()
                .OrderBy(c => c.Status, _commandPriorityComparer)
                .ThenByDescending(c => c.Priority)
                .ToResource();
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

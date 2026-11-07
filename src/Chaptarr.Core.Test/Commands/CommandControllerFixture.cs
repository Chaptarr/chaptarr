using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Commands;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Composition;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Notifications.CalibreContentServer;
using NzbDrone.Core.Notifications.Grimmory;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.Commands
{
    [TestFixture]
    public class CommandControllerFixture
    {
        private sealed class StubCommandQueue : IManageCommandQueue
        {
            private CommandModel _pushed;

            public bool WasPushed => _pushed != null;
            public Command PushedCommand => _pushed?.Body as Command;

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands)
                where TCommand : Command => throw new NotImplementedException();

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
                where TCommand : Command
            {
                _pushed = new CommandModel
                {
                    Id = 1,
                    Name = command.Name,
                    Body = command,
                    Priority = priority,
                    Trigger = trigger,
                    Status = CommandStatus.Queued,
                    QueuedAt = DateTime.UtcNow
                };

                return _pushed;
            }

            public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotImplementedException();
            public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken) => throw new NotImplementedException();
            public List<CommandModel> All() => new List<CommandModel>();
            public CommandModel Get(int id) => _pushed;
            public List<CommandModel> GetStarted() => new List<CommandModel>();
            public void SetMessage(CommandModel command, string message) => throw new NotImplementedException();
            public void TouchProgress(CommandModel command) => throw new NotImplementedException();
            public void SetResult(CommandModel command, CommandResult result) => throw new NotImplementedException();
            public void Start(CommandModel command) => throw new NotImplementedException();
            public void Complete(CommandModel command, string message) => throw new NotImplementedException();
            public void Fail(CommandModel command, string message, Exception e) => throw new NotImplementedException();
            public void Requeue() => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => CancellationToken.None;
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }

        private sealed class StubSignalRBroadcaster : IBroadcastSignalRMessage
        {
            public bool IsConnected => false;
            public Task BroadcastMessage(SignalRMessage message) => Task.CompletedTask;
        }

        private sealed class StubLegacyIdentityMapService : IBookshelfLegacyIdentityMapService
        {
            public List<int> LastSourceIds { get; private set; } = new List<int>();
            public BookMediaType? LastMediaType { get; private set; }

            public List<BookshelfLegacyIdentityMapEntry> GetMappings(string entityType, IEnumerable<int> sourceIds) =>
                new List<BookshelfLegacyIdentityMapEntry>();

            public List<BookshelfLegacyIdentityMapEntry> GetMappingsByTargetIds(string entityType, IEnumerable<int> targetIds) =>
                new List<BookshelfLegacyIdentityMapEntry>();

            public List<int> ResolveBookIds(IEnumerable<int> sourceIds, BookMediaType? mediaType = null)
            {
                LastSourceIds = sourceIds?.ToList() ?? new List<int>();
                LastMediaType = mediaType;

                if (!LastSourceIds.Contains(30))
                {
                    return LastSourceIds;
                }

                return mediaType switch
                {
                    BookMediaType.Ebook => new List<int> { 61 },
                    BookMediaType.Audiobook => new List<int> { 30 },
                    _ => new List<int> { 30, 61 }
                };
            }
        }

        [Test]
        public void rename_author_should_reject_unknown_media_type_before_queueing()
        {
            var queue = new StubCommandQueue();
            var controller = CreateController(queue, """{"name":"RenameAuthor","authorIds":[1],"mediaType":"audio"}""");

            var exception = Assert.Throws<BadRequestException>(() => controller.StartCommand(new CommandResource { Name = "RenameAuthor" }));

            Assert.That(exception.Content.ToString(), Does.Contain("mediaType"));
            Assert.That(queue.WasPushed, Is.False);
        }

        [Test]
        public void rename_author_should_accept_all_media_type()
        {
            var queue = new StubCommandQueue();
            var controller = CreateController(queue, """{"name":"RenameAuthor","authorIds":[1],"mediaType":"all"}""");

            var result = controller.StartCommand(new CommandResource { Name = "RenameAuthor" });

            Assert.That(queue.WasPushed, Is.True);
            Assert.That(result.Result, Is.TypeOf<CreatedAtActionResult>());
        }

        [Test]
        public void missing_book_search_should_reject_numeric_media_type_before_queueing()
        {
            var queue = new StubCommandQueue();
            var controller = CreateController(queue, """{"name":"MissingBookSearch","mediaType":"0"}""");

            var exception = Assert.Throws<BadRequestException>(() => controller.StartCommand(new CommandResource { Name = "MissingBookSearch" }));

            Assert.That(exception.Content.ToString(), Does.Contain("mediaType"));
            Assert.That(queue.WasPushed, Is.False);
        }

        [Test]
        public void missing_book_search_should_normalize_all_media_type_to_unfiltered()
        {
            var queue = new StubCommandQueue();
            var controller = CreateController(queue, """{"name":"MissingBookSearch","mediaType":"ALL"}""");

            var result = controller.StartCommand(new CommandResource { Name = "MissingBookSearch" });

            Assert.That(queue.WasPushed, Is.True);
            Assert.That(result.Result, Is.TypeOf<CreatedAtActionResult>());
            Assert.That(((MissingBookSearchCommand)queue.PushedCommand).MediaType, Is.Null);
        }

        [Test]
        public void legacy_retry_unmapped_files_command_name_should_remain_available()
        {
            var queue = new StubCommandQueue();
            var controller = CreateController(queue, """{"name":"RetryUnmappedFiles"}""");

            var result = controller.StartCommand(new CommandResource { Name = "RetryUnmappedFiles" });

            Assert.That(queue.WasPushed, Is.True);
            Assert.That(result.Result, Is.TypeOf<CreatedAtActionResult>());
            Assert.That(queue.PushedCommand, Is.TypeOf<RetryUnmappedFilesCommand>());
            Assert.That(queue.PushedCommand.Name, Is.EqualTo("RetryUnmappedFiles"));
        }

        [Test]
        public void book_search_command_should_resolve_legacy_id_to_both_split_format_rows()
        {
            var queue = new StubCommandQueue();
            var identityMap = new StubLegacyIdentityMapService();
            var controller = CreateController(queue, """{"name":"BookSearch","bookIds":[30]}""", identityMap);

            controller.StartCommand(new CommandResource { Name = "BookSearch" });

            Assert.That(((BookSearchCommand)queue.PushedCommand).BookIds, Is.EqualTo(new[] { 30, 61 }));
            Assert.That(identityMap.LastSourceIds, Is.EqualTo(new[] { 30 }));
            Assert.That(identityMap.LastMediaType, Is.Null);
        }

        [Test]
        public void book_search_command_should_resolve_legacy_id_within_facade_media_type()
        {
            var queue = new StubCommandQueue();
            var identityMap = new StubLegacyIdentityMapService();
            var facadeContext = new ReadarrFacadeContext("hc", "ebook", "/readarr/hc/ebook", isBookshelfCompatibility: true);
            var controller = CreateController(
                queue,
                """{"name":"BookSearch","bookIds":[30]}""",
                identityMap,
                facadeContext);

            controller.StartCommand(new CommandResource { Name = "BookSearch" });

            Assert.That(((BookSearchCommand)queue.PushedCommand).BookIds, Is.EqualTo(new[] { 61 }));
            Assert.That(identityMap.LastMediaType, Is.EqualTo(BookMediaType.Ebook));
        }

        [Test]
        public void scoped_book_search_should_resolve_a_legacy_id_without_projecting_native_command_ids()
        {
            var queue = new StubCommandQueue();
            var identityMap = new StubLegacyIdentityMapService();
            var facadeContext = new ReadarrFacadeContext(
                "hc",
                "ebook",
                "/readarr/hc/ebook",
                acceptsBookshelfLegacyBookIds: true);
            var controller = CreateController(
                queue,
                """{"name":"BookSearch","bookIds":[30]}""",
                identityMap,
                facadeContext);

            controller.StartCommand(new CommandResource { Name = "BookSearch" });

            Assert.That(((BookSearchCommand)queue.PushedCommand).BookIds, Is.EqualTo(new[] { 61 }));
            Assert.That(identityMap.LastMediaType, Is.EqualTo(BookMediaType.Ebook));
        }

        [Test]
        public void refresh_book_command_should_keep_its_contract_and_refresh_both_split_rows()
        {
            var queue = new StubCommandQueue();
            var identityMap = new StubLegacyIdentityMapService();
            var controller = CreateController(queue, """{"name":"RefreshBook","bookId":30}""", identityMap);

            controller.StartCommand(new CommandResource { Name = "RefreshBook" });

            Assert.That(queue.PushedCommand, Is.TypeOf<RefreshBookCommand>());
            Assert.That(((RefreshBookCommand)queue.PushedCommand).BookIds, Is.EqualTo(new[] { 30, 61 }));
        }

        [Test]
        public void refresh_book_command_should_use_the_facade_format_for_legacy_ids()
        {
            var queue = new StubCommandQueue();
            var identityMap = new StubLegacyIdentityMapService();
            var facadeContext = new ReadarrFacadeContext("hc", "ebook", "/readarr/hc/ebook", isBookshelfCompatibility: true);
            var controller = CreateController(
                queue,
                """{"name":"RefreshBook","bookId":30}""",
                identityMap,
                facadeContext);

            controller.StartCommand(new CommandResource { Name = "RefreshBook" });

            Assert.That(((RefreshBookCommand)queue.PushedCommand).BookId, Is.EqualTo(61));
        }

        [TestCase("BulkRefreshBook", typeof(BulkRefreshBookCommand))]
        [TestCase("PushCalibreMetadata", typeof(PushCalibreMetadataCommand))]
        [TestCase("PushGrimmoryMetadata", typeof(PushGrimmoryMetadataCommand))]
        [TestCase("RePushBook", typeof(RePushBookCommand))]
        public void book_id_collection_commands_should_resolve_legacy_ids(string commandName, Type commandType)
        {
            var queue = new StubCommandQueue();
            var identityMap = new StubLegacyIdentityMapService();
            var controller = CreateController(
                queue,
                $"{{\"name\":\"{commandName}\",\"bookIds\":[30]}}",
                identityMap);

            controller.StartCommand(new CommandResource { Name = commandName });

            var bookIds = (IEnumerable<int>)commandType.GetProperty("BookIds", BindingFlags.Instance | BindingFlags.Public).GetValue(queue.PushedCommand);
            Assert.That(bookIds, Is.EqualTo(new[] { 30, 61 }));
        }

        [Test]
        public void download_book_media_command_should_include_both_split_legacy_ids()
        {
            var queue = new StubCommandQueue();
            var identityMap = new StubLegacyIdentityMapService();
            var controller = CreateController(
                queue,
                """{"name":"DownloadBookMedia","bookId":30}""",
                identityMap);

            controller.StartCommand(new CommandResource { Name = "DownloadBookMedia" });

            var command = (DownloadBookMediaCommand)queue.PushedCommand;
            Assert.That(command.BookId, Is.EqualTo(30));
            Assert.That(command.BookIds, Is.EqualTo(new[] { 30, 61 }));
        }

        [TestCase("MoveAuthorMedia")]
        [TestCase("MoveAuthorMediaBatch")]
        public void author_media_move_commands_should_require_the_preview_validated_endpoint(string commandName)
        {
            var queue = new StubCommandQueue();
            var body = $"{{\"name\":\"{commandName}\"}}";
            var controller = CreateController(queue, body);

            var exception = Assert.Throws<BadRequestException>(() => controller.StartCommand(new CommandResource { Name = commandName }));

            Assert.That(exception.Content.ToString(), Does.Contain("preview-validated"));
            Assert.That(queue.WasPushed, Is.False);
        }

        private static CommandController CreateController(
            StubCommandQueue queue,
            string body,
            IBookshelfLegacyIdentityMapService legacyIdentityMapService = null,
            ReadarrFacadeContext facadeContext = null)
        {
            var controller = new CommandController(
                queue,
                new StubSignalRBroadcaster(),
                new KnownTypes(new List<Type>
                {
                    typeof(RenameAuthorCommand),
                    typeof(BookSearchCommand),
                    typeof(RefreshBookCommand),
                    typeof(BulkRefreshBookCommand),
                    typeof(DownloadBookMediaCommand),
                    typeof(PushCalibreMetadataCommand),
                    typeof(PushGrimmoryMetadataCommand),
                    typeof(RePushBookCommand),
                    typeof(MissingBookSearchCommand),
                    typeof(CutoffUnmetBookSearchCommand),
                    typeof(MoveAuthorMediaCommand),
                    typeof(MoveAuthorMediaBatchCommand),
                    typeof(RetryUnmappedFilesCommand)
                }),
                LogManager.GetCurrentClassLogger(),
                legacyIdentityMapService);

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            facadeContext ??= legacyIdentityMapService == null
                ? null
                : new ReadarrFacadeContext("hc", null, "/api", isBookshelfCompatibility: true);
            if (facadeContext != null)
            {
                context.Items[ReadarrFacadeContext.ItemKey] = facadeContext;
            }

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = context
            };

            return controller;
        }
    }
}

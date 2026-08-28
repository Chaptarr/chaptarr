using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Chaptarr.Core.Test;
using Chaptarr.Api.V1.Author;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;
using CoreAuthor = NzbDrone.Core.Books.Author;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorEditorMissingRootHydrationFixture
    {
        [Test]
        public void should_queue_forced_hydration_when_bulk_edit_adds_missing_ebook_root()
        {
            var commandQueue = new RecordingCommandQueue();
            var authorService = CreateAuthorService(new CoreAuthor
            {
                Id = 1,
                Name = "Martha Wells",
                AudiobookRootFolderPath = "/library/audiobooks"
            });

            var controller = new AuthorEditorController(authorService, commandQueue, CreateRootFolderService(), new TestQualityProfileService(), new TestMetadataProfileService());

            controller.SaveAll(new AuthorEditorResource
            {
                AuthorIds = new List<int> { 1 },
                EbookRootFolderPath = "/library/ebooks"
            });

            var command = commandQueue.PushedCommands.Single().Body as RefreshAuthorCommand;

            Assert.That(command, Is.Not.Null);
            Assert.That(command.AuthorId, Is.EqualTo(1));
            Assert.That(command.RefreshMetadata, Is.True);
            Assert.That(command.RescanFolders, Is.False);
            Assert.That(command.ForceRefresh, Is.True);
        }

        [Test]
        public void should_not_queue_forced_hydration_when_bulk_edit_changes_existing_root()
        {
            var commandQueue = new RecordingCommandQueue();
            var authorService = CreateAuthorService(new CoreAuthor
            {
                Id = 1,
                Name = "Martha Wells",
                AudiobookRootFolderPath = "/library/audiobooks",
                EbookRootFolderPath = "/library/ebooks"
            });

            var controller = new AuthorEditorController(authorService, commandQueue, CreateRootFolderService(), new TestQualityProfileService(), new TestMetadataProfileService());

            controller.SaveAll(new AuthorEditorResource
            {
                AuthorIds = new List<int> { 1 },
                EbookRootFolderPath = "/library/ebooks-new"
            });

            Assert.That(commandQueue.PushedCommands, Is.Empty);
        }

        [Test]
        public void should_leave_unconfigured_media_monitoring_unchanged_when_bulk_edit_targets_both_types()
        {
            var author = new CoreAuthor
            {
                Id = 1,
                Name = "Martha Wells",
                AudiobookRootFolderPath = "/library/audiobooks",
                AudiobookMonitored = true,
                AudiobookMonitorNewItems = NewItemMonitorTypes.All,
                EbookMonitored = null,
                EbookMonitorNewItems = null
            };
            var authorService = CreateAuthorService(author);
            var controller = new AuthorEditorController(authorService, new RecordingCommandQueue(), CreateRootFolderService(), new TestQualityProfileService(), new TestMetadataProfileService());

            controller.SaveAll(new AuthorEditorResource
            {
                AuthorIds = new List<int> { 1 },
                AudiobookMonitored = false,
                AudiobookMonitorNewItems = NewItemMonitorTypes.None,
                EbookMonitored = false,
                EbookMonitorNewItems = NewItemMonitorTypes.None
            });

            Assert.That(author.AudiobookMonitored, Is.False);
            Assert.That(author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(author.EbookMonitored, Is.Null);
            Assert.That(author.EbookMonitorNewItems, Is.Null);
        }

        [Test]
        public void should_apply_media_monitoring_when_bulk_edit_configures_that_media_root()
        {
            var author = new CoreAuthor
            {
                Id = 1,
                Name = "Martha Wells",
                AudiobookRootFolderPath = "/library/audiobooks"
            };
            var authorService = CreateAuthorService(author);
            var controller = new AuthorEditorController(authorService, new RecordingCommandQueue(), CreateRootFolderService(), new TestQualityProfileService(), new TestMetadataProfileService());

            controller.SaveAll(new AuthorEditorResource
            {
                AuthorIds = new List<int> { 1 },
                EbookRootFolderPath = "/library/ebooks",
                EbookMonitored = true,
                EbookMonitorNewItems = NewItemMonitorTypes.New
            });

            Assert.That(author.EbookMonitored, Is.True);
            Assert.That(author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
        }

        private static IAuthorService CreateAuthorService(params CoreAuthor[] authors)
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Authors = authors.ToList();
            return authorService;
        }

        private static IRootFolderService CreateRootFolderService()
        {
            return DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public List<CoreAuthor> Authors { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IAuthorService.GetAuthors):
                        var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                        return Authors.Where(author => ids.Contains(author.Id)).ToList();
                    case nameof(IAuthorService.UpdateAuthors):
                        Authors = ((List<CoreAuthor>)args[0]).ToList();
                        return Authors;
                    default:
                        throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
                }
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IRootFolderService.All) => new List<RootFolder>
                    {
                        new RootFolder { Path = "/library/audiobooks", FolderType = FolderType.Audiobook },
                        new RootFolder { Path = "/library/ebooks", FolderType = FolderType.Ebook },
                        new RootFolder { Path = "/library/ebooks-new", FolderType = FolderType.Ebook }
                    },
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }
        }

        private sealed class RecordingCommandQueue : IManageCommandQueue
        {
            public List<CommandModel> PushedCommands { get; } = new();

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command
            {
                return commands.Select(command => Push(command)).ToList();
            }

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) where TCommand : Command
            {
                var model = new CommandModel
                {
                    Name = command.Name,
                    Body = command,
                    Priority = priority,
                    Trigger = trigger,
                    Status = CommandStatus.Queued
                };

                PushedCommands.Add(model);
                return model;
            }

            public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotImplementedException();
            public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken) => throw new NotImplementedException();
            public List<CommandModel> All() => throw new NotImplementedException();
            public CommandModel Get(int id) => throw new NotImplementedException();
            public List<CommandModel> GetStarted() => throw new NotImplementedException();
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
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }
    }
}

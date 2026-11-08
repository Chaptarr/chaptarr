using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Chaptarr.Core.Test;
using Chaptarr.Core.Test.Common;
using Chaptarr.Api.V1;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Messaging.Events;
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

            var controller = new AuthorEditorController(authorService, commandQueue, CreateRootFolderService(), new TestQualityProfileService(), new TestMetadataProfileService(), AuthorFolderUpdateFixture.CreateFileNames());

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

            var controller = new AuthorEditorController(authorService, commandQueue, CreateRootFolderService(), new TestQualityProfileService(), new TestMetadataProfileService(), AuthorFolderUpdateFixture.CreateFileNames());

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
            var controller = new AuthorEditorController(authorService, new RecordingCommandQueue(), CreateRootFolderService(), new TestQualityProfileService(), new TestMetadataProfileService(), AuthorFolderUpdateFixture.CreateFileNames());

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
            var controller = new AuthorEditorController(authorService, new RecordingCommandQueue(), CreateRootFolderService(), new TestQualityProfileService(), new TestMetadataProfileService(), AuthorFolderUpdateFixture.CreateFileNames());

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

        [Test]
        public void bulk_tag_edits_should_persist_each_media_set_and_response_independently(
            [Values(false, true)] bool postgres,
            [Values("add", "remove", "replace-empty", "replace-values", "no-change", "null-stored-add", "null-stored-replace-empty", "one-null-add")] string scenario,
            [Values(null, "audiobook", "ebook")] string mediaType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) => null);
                var logger = LogManager.GetCurrentClassLogger();
                var repository = new AuthorRepository(database, events, logger);
                var roots = CreateRootFolderService();
                var commands = new RecordingCommandQueue();
                var authors = new AuthorService(repository, events, null, roots, commands, new CacheManager(),
                    new BookRepository(database, events), null, logger, null, null, null, database);
                var originals = new List<CoreAuthor>();
                for (var index = 0; index < 2; index++)
                {
                    var legacy = scenario.StartsWith("null-stored-", StringComparison.Ordinal);
                    originals.Add(repository.Insert(new CoreAuthor
                    {
                        Name = "Tag Test Writer " + index,
                        CleanName = "tagtestwriter" + index,
                        SortName = "tag test writer " + index,
                        TitleSlug = Guid.NewGuid().ToString("N"),
                        Path = "/fixture/tag-test-writer-" + index,
                        Added = DateTime.UtcNow,
                        AudiobookMonitored = false,
                        EbookMonitored = false,
                        AudiobookMonitorNewItems = NewItemMonitorTypes.None,
                        EbookMonitorNewItems = NewItemMonitorTypes.None,
                        AudiobookTags = legacy ? null : new HashSet<int> { 1 + index * 2 },
                        EbookTags = legacy || scenario == "one-null-add" ? null : new HashSet<int> { 2 + index * 2 },
                        Tags = legacy ? new HashSet<int> { 5 + index } : new HashSet<int> { 1 + index * 2, 2 + index * 2 }
                    }));
                }

                if (scenario.StartsWith("null-stored-", StringComparison.Ordinal))
                {
                    foreach (var author in originals)
                    {
                        var stored = repository.Get(author.Id);
                        Assert.That(stored.AudiobookTags, Is.Empty, "database NULL reads retain the model constructor's empty set");
                        Assert.That(stored.EbookTags, Is.Empty, "database NULL reads retain the model constructor's empty set");
                    }
                }

                var apply = scenario.StartsWith("replace", StringComparison.Ordinal) || scenario == "null-stored-replace-empty"
                    ? ApplyTags.Replace : scenario == "remove" ? ApplyTags.Remove : ApplyTags.Add;
                var requested = scenario == "no-change" ? null : scenario.Contains("empty", StringComparison.Ordinal)
                    ? new List<int>() : scenario == "remove" ? new List<int> { 1 } : new List<int> { 9 };
                var controller = new AuthorEditorController(authors, commands, roots, new TestQualityProfileService(), new TestMetadataProfileService(), AuthorFolderUpdateFixture.CreateFileNames());
                var result = controller.SaveAll(new AuthorEditorResource
                {
                    AuthorIds = originals.Select(author => author.Id).ToList(),
                    Tags = requested,
                    ApplyTags = apply,
                    MediaType = mediaType
                }) as AcceptedResult;
                Assert.That(result, Is.Not.Null);
                var response = ((IEnumerable<AuthorResource>)result.Value).ToDictionary(author => author.Id);
                for (var index = 0; index < originals.Count; index++)
                {
                    var expectedAudio = scenario switch
                    {
                        "add" or "one-null-add" => new[] { 1 + index * 2, 9 },
                        "remove" => index == 0 ? Array.Empty<int>() : new[] { 3 },
                        "replace-empty" or "null-stored-replace-empty" => Array.Empty<int>(),
                        "replace-values" => new[] { 9 },
                        "null-stored-add" => new[] { 9 },
                        _ => new[] { 1 + index * 2 }
                    };
                    var expectedEbook = scenario switch
                    {
                        "add" => new[] { 2 + index * 2, 9 },
                        "one-null-add" or "replace-values" => new[] { 9 },
                        "replace-empty" or "null-stored-replace-empty" => Array.Empty<int>(),
                        "null-stored-add" => new[] { 9 },
                        _ => new[] { 2 + index * 2 }
                    };
                    if (mediaType == "ebook") expectedAudio = scenario.StartsWith("null-stored-", StringComparison.Ordinal) ? Array.Empty<int>() : new[] { 1 + index * 2 };
                    if (mediaType == "audiobook") expectedEbook = scenario.StartsWith("null-stored-", StringComparison.Ordinal) || scenario == "one-null-add" ? Array.Empty<int>() : new[] { 2 + index * 2 };
                    var stored = repository.Get(originals[index].Id);
                    var resource = response[stored.Id];
                    var readback = stored.ToResource();
                    var union = expectedAudio.Concat(expectedEbook).Distinct().ToArray();
                    Assert.Multiple(() =>
                    {
                        Assert.That(stored.GetTagsForMediaType(BookMediaType.Audiobook), Is.EquivalentTo(expectedAudio), "persisted audiobook tags");
                        Assert.That(stored.GetTagsForMediaType(BookMediaType.Ebook), Is.EquivalentTo(expectedEbook), "persisted ebook tags");
                        Assert.That(stored.Tags, Is.EquivalentTo(union), "persisted aggregate tags");
                        Assert.That(resource.AudiobookTags, Is.EquivalentTo(expectedAudio), "response audiobook tags");
                        Assert.That(resource.EbookTags, Is.EquivalentTo(expectedEbook), "response ebook tags");
                        Assert.That(resource.Tags, Is.EquivalentTo(union), "response aggregate tags");
                        Assert.That(readback.Tags, Is.EquivalentTo(union), "resource from fresh database read");
                        Assert.That(stored.AudiobookMonitored, Is.False);
                        Assert.That(stored.EbookMonitored, Is.False);
                        Assert.That(stored.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                        Assert.That(stored.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    });
                }

                Assert.That(commands.PushedCommands, Is.Empty);
            });
        }

        [TestCase(ApplyTags.Add)]
        [TestCase(ApplyTags.Remove)]
        public void legacy_in_memory_null_media_tags_should_keep_the_existing_both_sides_fallback(ApplyTags operation)
        {
            var author = new CoreAuthor
            {
                Id = 1,
                Name = "Legacy Tag Writer",
                Tags = new HashSet<int> { 5 },
                AudiobookTags = null,
                EbookTags = null
            };
            var controller = new AuthorEditorController(CreateAuthorService(author), new RecordingCommandQueue(),
                CreateRootFolderService(), new TestQualityProfileService(), new TestMetadataProfileService(), AuthorFolderUpdateFixture.CreateFileNames());
            var requested = operation == ApplyTags.Add ? 9 : 5;
            var expected = operation == ApplyTags.Add ? new[] { 5, 9 } : Array.Empty<int>();
            var result = controller.SaveAll(new AuthorEditorResource
            {
                AuthorIds = new List<int> { 1 },
                Tags = new List<int> { requested },
                ApplyTags = operation
            }) as AcceptedResult;
            var resource = ((IEnumerable<AuthorResource>)result.Value).Single();
            Assert.Multiple(() =>
            {
                Assert.That(author.GetTagsForMediaType(BookMediaType.Audiobook), Is.EquivalentTo(expected));
                Assert.That(author.GetTagsForMediaType(BookMediaType.Ebook), Is.EquivalentTo(expected));
                Assert.That(author.Tags, Is.EquivalentTo(expected));
                Assert.That(resource.AudiobookTags, Is.EquivalentTo(expected));
                Assert.That(resource.EbookTags, Is.EquivalentTo(expected));
                Assert.That(resource.Tags, Is.EquivalentTo(expected));
            });
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
            public bool CancelIfQueued(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }
    }
}

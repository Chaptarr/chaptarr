using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshAuthorServiceRescanScopeFixture
    {
        [Test]
        public void single_author_refresh_uses_stored_and_mapped_folder_evidence_with_containment_collapse()
        {
            var root = @"C:\library".AsOsAgnostic();
            var canonical = Path.Combine(root, "George R.R. Martin");
            var legacy = Path.Combine(root, "George R. R. Martin");
            var author = new Author
            {
                Id = 7,
                Name = "George R.R. Martin",
                AudiobookPath = canonical
            };
            var evidence = new[]
            {
                NewEvidence(Path.Combine(canonical, "Book One", "one.m4b")),
                NewEvidence(Path.Combine(canonical, "Book Two", "two.m4b")),
                NewEvidence(Path.Combine(legacy, "flat.m4b")),
                NewEvidence(Path.Combine(legacy, "Book Three", "three.m4b")),
                NewEvidence(Path.Combine(root, "root-file.m4b")),
                NewEvidence(Path.Combine(@"C:\outside".AsOsAgnostic(), "outside.m4b"))
            };
            var queue = new StubCommandQueueManager();
            var events = new StubEventAggregator();
            var subject = CreateSubject(new[] { author }, new[]
            {
                new RootFolder { Id = 1, Path = root, FolderType = FolderType.Mixed }
            }, evidence, queue, events);

            subject.Execute(new RefreshAuthorCommand(author.Id, refreshMetadata: false, rescanFolders: true)
            {
                Trigger = CommandTrigger.Manual
            });

            var command = queue.Pushed.OfType<RescanFoldersCommand>().Single();
            Assert.That(command.Folders, Is.EquivalentTo(new[] { canonical, legacy }));
            Assert.That(command.Folders, Does.Not.Contain(root));
            Assert.That(command.AuthorIds, Is.EqualTo(new[] { author.Id }));
            Assert.That(command.Filter, Is.EqualTo(FilterFilesType.Matched));
            Assert.That(command.MediaType, Is.EqualTo("all"));
            Assert.That(events.PublishedEvents.OfType<AuthorScanSkippedEvent>(), Is.Empty);
        }

        [Test]
        public void single_author_refresh_uses_legacy_path_only_when_both_media_paths_are_blank()
        {
            var root = @"C:\library".AsOsAgnostic();
            var authorPath = Path.Combine(root, "Real Author Folder");
            var author = new Author { Id = 7, Name = "Real Author", Path = authorPath };
            var queue = new StubCommandQueueManager();
            var subject = CreateSubject(
                new[] { author },
                new[] { new RootFolder { Id = 1, Path = root, FolderType = FolderType.Mixed } },
                Array.Empty<BookFile>(),
                queue,
                new StubEventAggregator());

            subject.Execute(new RefreshAuthorCommand(author.Id, refreshMetadata: false, rescanFolders: true)
            {
                Trigger = CommandTrigger.Manual
            });

            Assert.That(queue.Pushed.OfType<RescanFoldersCommand>().Single().Folders, Is.EqualTo(new[] { authorPath }));
        }

        [Test]
        public void new_author_refresh_scans_only_its_computed_folder_without_mapped_files()
        {
            var root = @"C:\library".AsOsAgnostic();
            var computedPath = Path.Combine(root, "New Author");
            var author = new Author
            {
                Id = 7,
                Name = "New Author",
                Path = computedPath,
                AudiobookPath = computedPath
            };
            var queue = new StubCommandQueueManager();
            var subject = CreateSubject(
                new[] { author },
                new[] { new RootFolder { Id = 1, Path = root, FolderType = FolderType.Audiobook } },
                Array.Empty<BookFile>(),
                queue,
                new StubEventAggregator());

            subject.Execute(new RefreshAuthorCommand(
                author.Id,
                refreshMetadata: false,
                rescanFolders: true,
                isNewAuthor: true));

            var command = queue.Pushed.OfType<RescanFoldersCommand>().Single();
            Assert.That(command.Folders, Is.EqualTo(new[] { computedPath }));
            Assert.That(command.Folders, Does.Not.Contain(root));
            Assert.That(command.AuthorIds, Is.EqualTo(new[] { author.Id }));
        }

        [Test]
        public void single_author_refresh_without_bounded_evidence_skips_without_widening_and_publishes_lifecycle_event()
        {
            var root = @"C:\library".AsOsAgnostic();
            var author = new Author { Id = 7, Name = "No Evidence" };
            var queue = new StubCommandQueueManager();
            var events = new StubEventAggregator();
            var subject = CreateSubject(
                new[] { author },
                new[] { new RootFolder { Id = 1, Path = root, FolderType = FolderType.Mixed } },
                new[] { NewEvidence(Path.Combine(root, "directly-under-root.m4b")) },
                queue,
                events);

            subject.Execute(new RefreshAuthorCommand(author.Id, refreshMetadata: false, rescanFolders: true)
            {
                Trigger = CommandTrigger.Manual
            });

            Assert.That(queue.Pushed.OfType<RescanFoldersCommand>(), Is.Empty);
            var skipped = events.PublishedEvents.OfType<AuthorScanSkippedEvent>().Single();
            Assert.That(skipped.Author, Is.SameAs(author));
            Assert.That(skipped.Reason, Is.EqualTo(AuthorScanSkippedReason.NoFolderEvidence));
        }

        [Test]
        public void all_author_refresh_keeps_root_folder_scan()
        {
            var audiobookRoot = @"C:\audiobooks".AsOsAgnostic();
            var ebookRoot = @"C:\ebooks".AsOsAgnostic();
            var queue = new StubCommandQueueManager();
            var subject = CreateSubject(
                new[] { new Author { Id = 7, Name = "Author" } },
                new[]
                {
                    new RootFolder { Id = 1, Path = audiobookRoot, FolderType = FolderType.Audiobook },
                    new RootFolder { Id = 2, Path = ebookRoot, FolderType = FolderType.Ebook }
                },
                Array.Empty<BookFile>(),
                queue,
                new StubEventAggregator());

            subject.Execute(new RefreshAuthorCommand(null, refreshMetadata: false, rescanFolders: true, isNewAuthor: true));

            Assert.That(queue.Pushed.OfType<RescanFoldersCommand>().Single().Folders,
                Is.EquivalentTo(new[] { audiobookRoot, ebookRoot }));
        }

        [Test]
        public void bulk_refresh_keeps_media_filtered_root_folder_scan()
        {
            var audiobookRoot = @"C:\audiobooks".AsOsAgnostic();
            var ebookRoot = @"C:\ebooks".AsOsAgnostic();
            var mixedRoot = @"C:\mixed".AsOsAgnostic();
            var queue = new StubCommandQueueManager();
            var subject = CreateSubject(
                new[] { new Author { Id = 7, Name = "Author" } },
                new[]
                {
                    new RootFolder { Id = 1, Path = audiobookRoot, FolderType = FolderType.Audiobook },
                    new RootFolder { Id = 2, Path = ebookRoot, FolderType = FolderType.Ebook },
                    new RootFolder { Id = 3, Path = mixedRoot, FolderType = FolderType.Mixed }
                },
                Array.Empty<BookFile>(),
                queue,
                new StubEventAggregator());
            var command = new BulkRefreshAuthorCommand(
                new List<int> { 7 },
                refreshMetadata: false,
                rescanFolders: true,
                areNewAuthors: true,
                trigger: CommandTrigger.Manual)
            {
                MediaType = "audiobook"
            };

            subject.Execute(command);

            Assert.That(queue.Pushed.OfType<RescanFoldersCommand>().Single().Folders,
                Is.EquivalentTo(new[] { audiobookRoot, mixedRoot }));
        }

        private static BookFile NewEvidence(string path)
        {
            return new BookFile { Path = path, EditionId = 1, MediaType = "audiobook" };
        }

        private static RefreshAuthorService CreateSubject(
            IEnumerable<Author> authors,
            IEnumerable<RootFolder> roots,
            IEnumerable<BookFile> evidence,
            IManageCommandQueue commandQueue,
            IEventAggregator eventAggregator)
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Authors = authors.ToList();

            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootFolderService).Roots = roots.ToList();

            var mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            ((MediaFileServiceProxy)(object)mediaFileService).Evidence = evidence.ToList();

            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();

            return new RefreshAuthorService(
                authorInfo: null,
                authorService: authorService,
                bookService: null,
                editionService: null,
                metadataProfileService: null,
                refreshBookService: null,
                refreshSeriesService: null,
                eventAggregator: eventAggregator,
                commandQueueManager: commandQueue,
                mediaFileService: mediaFileService,
                historyService: null,
                rootFolderService: rootFolderService,
                checkIfAuthorShouldBeRefreshed: null,
                monitorNewBookService: null,
                configService: configService,
                importListExclusionService: null,
                syncMetadataService: null,
                syncQueueService: null,
                rootFolderSettingsResolver: null,
                logger: LogManager.GetLogger(nameof(RefreshAuthorServiceRescanScopeFixture)));
        }

        public class AuthorServiceProxy : DispatchProxy
        {
            public List<Author> Authors { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IAuthorService.GetAuthor) => Authors.Single(author => author.Id == (int)args[0]),
                    nameof(IAuthorService.GetAuthors) => Authors
                        .Where(author => ((IEnumerable<int>)args[0]).Contains(author.Id))
                        .ToList(),
                    nameof(IAuthorService.GetAllAuthors) => Authors,
                    _ => throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}")
                };
            }
        }

        public class ConfigServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_RescanAfterRefresh")
                {
                    return RescanAfterRefreshType.Always;
                }

                throw new NotImplementedException($"Test proxy does not implement IConfigService.{targetMethod?.Name}");
            }
        }

        public class MediaFileServiceProxy : DispatchProxy
        {
            public List<BookFile> Evidence { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileService.GetMappedFilePathEvidenceByAuthor))
                {
                    return Evidence;
                }

                throw new NotImplementedException($"Test proxy does not implement IMediaFileService.{targetMethod?.Name}");
            }
        }

        public class RootFolderServiceProxy : DispatchProxy
        {
            public List<RootFolder> Roots { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.All))
                {
                    return Roots;
                }

                if (targetMethod?.Name == nameof(IRootFolderService.GetBestRootFolder))
                {
                    var path = (string)args[0];
                    var roots = args.Length > 1 && args[1] is List<RootFolder> supplied
                        ? supplied
                        : Roots;
                    return roots
                        .Where(root => root.Path.PathEquals(path) || root.Path.IsParentPath(path))
                        .OrderByDescending(root => root.Path.Length)
                        .FirstOrDefault();
                }

                throw new NotImplementedException($"Test proxy does not implement IRootFolderService.{targetMethod?.Name}");
            }
        }

        private sealed class StubEventAggregator : IEventAggregator
        {
            public List<IEvent> PublishedEvents { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                PublishedEvents.Add(@event);
            }
        }

        private sealed class StubCommandQueueManager : IManageCommandQueue
        {
            public List<Command> Pushed { get; } = new();

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
                where TCommand : Command
            {
                Pushed.Add(command);
                return new CommandModel
                {
                    Name = command.Name,
                    Body = command,
                    Priority = priority,
                    Trigger = trigger
                };
            }

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command => throw new NotImplementedException();
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

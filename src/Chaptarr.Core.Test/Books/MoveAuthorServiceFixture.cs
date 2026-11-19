using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.MediaFiles;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class MoveAuthorServiceFixture
    {
        private sealed class Events : IEventAggregator
        {
            public Action<AuthorMovedEvent> OnMoved { get; set; }
            public int Moves { get; private set; }
            public void PublishEvent<T>(T message) where T : class, IEvent
            {
                if (message is AuthorMovedEvent moved)
                {
                    Moves++;
                    OnMoved(moved);
                }
            }
        }

        private sealed class Sandbox : IDisposable
        {
            public string Temp { get; } = Path.Combine(Path.GetTempPath(), "chaptarr-move-" + Guid.NewGuid().ToString("N"));
            public string Source => P(@"old\Author");
            public string Target => P(@"new\Author");
            public Author Author { get; }
            public List<BookFile> Files { get; } = new();
            public List<BookFile> Updated { get; } = new();
            public int UpdateCalls { get; private set; }
            public MemoryTarget Logs { get; } = new MemoryTarget { Layout = "${level}|${message}" };
            private readonly LogFactory _logFactory = new();
            private readonly string _mediaType;
            public bool FailTransfer { get; set; }
            public bool SameMount { get; set; }
            public bool FailCleanup { get; set; }
            public Action BeforeCleanup { get; set; } = () => { };
            public Events Events { get; } = new();
            public MoveAuthorService Service { get; }
            public string P(string path) => Path.Combine(Temp, path.AsOsAgnostic());

            public Sandbox(string media, IFileSystem fileSystem = null)
            {
                _mediaType = media;
                var logging = new LoggingConfiguration(_logFactory);
                logging.AddRule(LogLevel.Debug, LogLevel.Fatal, Logs);
                _logFactory.Configuration = logging;
                var logger = _logFactory.GetLogger(nameof(MoveAuthorServiceFixture));
                Directory.CreateDirectory(Source);
                Directory.CreateDirectory(Target);
                Author = new Author { Id = 1, Name = "Synthetic Author", Path = Source,
                    AudiobookPath = media == "audiobook" ? Source : P("opposite"),
                    EbookPath = media == "ebook" ? Source : P("opposite") };
                var authors = AuthorDeletionSafetyFixture.Proxy<IAuthorService>((m, a) => m.Name switch
                {
                    nameof(IAuthorService.GetAuthor) => Author,
                    nameof(IAuthorService.UpdateAuthor) => Author,
                    _ => throw new NotImplementedException(m.Name)
                });
                var realDisk = new NzbDrone.Windows.Disk.DiskProvider(fileSystem ?? new FileSystem());
                var disk = AuthorDeletionSafetyFixture.Proxy<IDiskProvider>((m, a) =>
                {
                    foreach (var path in a.OfType<string>())
                    {
                        if (!Temp.IsParentPath(path)) throw new InvalidOperationException("Sandbox boundary violation");
                    }

                    if (m.Name == nameof(IDiskProvider.GetMount)) return SameMount
                        ? AuthorDeletionSafetyFixture.Proxy<IMount>((method, args) => method.Name == "get_RootDirectory" ? Temp : null)
                        : null;

                    if (m.Name == nameof(IDiskProvider.MoveFile) && FailTransfer) throw new IOException("Synthetic transfer failure");
                    if (m.Name == nameof(IDiskProvider.DeleteFolder))
                    {
                        Assert.That(a[1], Is.False);
                        BeforeCleanup();
                        if (FailCleanup) throw new IOException("Synthetic cleanup failure");
                    }

                    try { return m.Invoke(realDisk, a); }
                    catch (TargetInvocationException e) { throw e.InnerException; }
                });
                var repository = AuthorDeletionSafetyFixture.Proxy<IMediaFileRepository>((m, a) =>
                {
                    if (m.Name == nameof(IMediaFileRepository.GetFilesWithBasePath)) return Files.Where(f => Source.IsParentPath(f.Path)).ToList();
                    if (m.Name == nameof(IMediaFileRepository.UpdateMany))
                    {
                        UpdateCalls++;
                        Updated.AddRange((IEnumerable<BookFile>)a[0]);
                        return null;
                    }
                    throw new NotImplementedException(m.Name);
                });
                var mediaFiles = new MediaFileService(repository, Events, null, disk, logger);
                Events.OnMoved = mediaFiles.Handle;
                var names = AuthorDeletionSafetyFixture.Proxy<IBuildFileNames>((m, a) =>
                    m.Name == nameof(IBuildFileNames.GetAuthorFolder) ? "Author" : throw new NotImplementedException(m.Name));
                var watcher = AuthorDeletionSafetyFixture.Proxy<IRootFolderWatchingService>((m, a) => null);
                Service = new MoveAuthorService(authors, names, disk, watcher,
                    new DiskTransferService(disk, logger), Events, logger);
            }

            public BookFile Track(string name)
            {
                var path = Path.Combine(Source, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "sentinel");
                var file = new BookFile { Id = Files.Count + 1, Path = path, MediaType = _mediaType };
                Files.Add(file);
                return file;
            }

            public void Move(string media) => Service.Execute(new BulkMoveAuthorCommand
            {
                Author = new List<BulkMoveAuthor> { new() { AuthorId = 1, SourcePath = Source, MediaType = media } },
                DestinationRootFolder = P("new")
            });
            public void Dispose()
            {
                _logFactory.Dispose();
                Directory.Delete(Temp, true);
            }
        }

        [TestCase("audiobook")]
        [TestCase("ebook")]
        public void folder_move_preserves_colliding_files_and_their_original_tracking(string media)
        {
            using var s = new Sandbox(media);
            var blocked = s.Track(media == "ebook" ? "Dune.epub" : "Dune.m4b");
            var movable = s.Track(media == "ebook" ? "Dune Messiah.epub" : "Dune Messiah.m4b");
            var original = blocked.Path;
            var destination = Path.Combine(s.Target, Path.GetFileName(blocked.Path));
            File.WriteAllText(destination, "different existing recording");

            s.Move(media);

            Assert.That(File.ReadAllText(destination), Is.EqualTo("different existing recording"));
            Assert.That(File.ReadAllText(original), Is.EqualTo("sentinel"));
            Assert.That(blocked.Path, Is.EqualTo(original));
            Assert.That(movable.Path, Is.EqualTo(Path.Combine(s.Target, Path.GetFileName(movable.Path))));
            Assert.That(s.Updated, Is.EqualTo(new[] { movable }));
            Assert.That(s.Logs.Logs.Any(line => line.StartsWith("Warn|") && line.Contains("was not overwritten")), Is.True);
        }

        [TestCase("audiobook", "debug.log")]
        [TestCase("ebook", "debug.log")]
        [TestCase("audiobook", ".nfs123")]
        [TestCase("ebook", "service.socket")]
        [TestCase("ebook", "nested/debug.log")]
        [TestCase("ebook", "debug.log", "Étoile 𠀀 🌶️.epub")]
        public void completed_move_preserves_leftovers_and_updates_only_moved_file_paths(string media, string leftover, string filename = null)
        {
            using var s = new Sandbox(media);
            var moved = s.Track(filename ?? (media == "ebook" ? "Book.epub" : "Book.m4b"));
            var kept = s.Track(leftover.AsOsAgnostic());
            var oldKept = kept.Path;
            s.Move(media);
            Assert.That(s.Author.Path, Is.EqualTo(s.Target));
            Assert.That(media == "ebook" ? s.Author.EbookPath : s.Author.AudiobookPath, Is.EqualTo(s.Target));
            Assert.That(media == "ebook" ? s.Author.AudiobookPath : s.Author.EbookPath, Is.EqualTo(s.P("opposite")));
            Assert.That(moved.Path, Is.EqualTo(Path.Combine(s.Target, Path.GetFileName(moved.Path))));
            Assert.That(File.ReadAllText(moved.Path), Is.EqualTo("sentinel"));
            Assert.That(kept.Path, Is.EqualTo(oldKept));
            Assert.That(File.ReadAllText(kept.Path), Is.EqualTo("sentinel"));
            Assert.That(s.Updated, Is.EqualTo(new[] { moved }));
            Assert.That(s.UpdateCalls, Is.EqualTo(1));
        }

        [TestCase("audiobook", false)]
        [TestCase("ebook", false)]
        [TestCase("audiobook", true)]
        [TestCase("ebook", true)]
        public void explicit_folder_move_reuses_transfer_and_repoints_the_selected_formats(string media, bool shared)
        {
            using var s = new Sandbox(media);
            s.Author.AudiobookRootFolderPath = s.P("old");
            s.Author.EbookRootFolderPath = s.P("old");
            if (shared) s.Author.AudiobookPath = s.Author.EbookPath = s.Source;
            var first = s.Track(media == "ebook" ? "Dune.epub" : "Dune.m4b");
            var second = shared ? s.Track(media == "ebook" ? "Dune.m4b" : "Dune.epub") : null;
            var roots = new List<RootFolder>
            {
                new() { Path = s.P("old"), FolderType = FolderType.Mixed },
                new() { Path = s.P("new"), FolderType = FolderType.Mixed }
            };
            var commands = AuthorPathBuilder.ChangeFolders(s.Author,
                media == "audiobook" ? s.Target : null, media == "ebook" ? s.Target : null, roots, true);
            Assert.That(commands.Count, Is.EqualTo(1));
            s.Service.Execute(commands.Single());
            Assert.That(s.Events.Moves, Is.EqualTo(1));
            Assert.That(File.ReadAllText(first.Path), Is.EqualTo("sentinel"));
            Assert.That(first.Path, Does.StartWith(s.Target));
            if (shared)
            {
                Assert.That(s.Author.AudiobookPath, Is.EqualTo(s.Target));
                Assert.That(s.Author.EbookPath, Is.EqualTo(s.Target));
                Assert.That(second.Path, Does.StartWith(s.Target));
                Assert.That(File.ReadAllText(second.Path), Is.EqualTo("sentinel"));
                Assert.That(s.Updated.Count, Is.EqualTo(2));
            }
            else
            {
                Assert.That(media == "ebook" ? s.Author.AudiobookPath : s.Author.EbookPath, Is.EqualTo(s.P("opposite")));
            }
        }

        [Test]
        public void explicit_ebook_only_move_does_not_create_an_audiobook_path()
        {
            using var s = new Sandbox("ebook");
            s.Author.AudiobookPath = null;
            var file = s.Track("Dune.epub");
            var moves = AuthorPathBuilder.ChangeFolders(s.Author, null, s.Target,
                new List<RootFolder> { new() { Path = s.P("new"), FolderType = FolderType.Ebook } }, true);
            s.Service.Execute(moves.Single());
            Assert.That(s.Author.AudiobookPath, Is.Null);
            Assert.That(s.Author.EbookPath, Is.EqualTo(s.Target));
            Assert.That(file.Path, Does.StartWith(s.Target));
            Assert.That(File.ReadAllText(file.Path), Is.EqualTo("sentinel"));
        }

        [TestCase("audiobook", false)]
        [TestCase("ebook", false)]
        [TestCase("audiobook", true)]
        [TestCase("ebook", true)]
        public void move_without_retargetable_files_does_not_open_an_update(string media, bool hasRetainedFile)
        {
            using var s = new Sandbox(media);
            if (hasRetainedFile)
            {
                s.Track("debug.log");
            }

            s.Move(media);

            Assert.That(s.Events.Moves, Is.EqualTo(1));
            Assert.That(s.Updated, Is.Empty);
            Assert.That(s.UpdateCalls, Is.Zero);
        }

        [Test]
        public void moved_folder_is_checked_once_without_resolving_every_missing_source_file()
        {
            var realFileSystem = new FileSystem();
            string source = null;
            string sourceRoot = null;
            var folderChecks = 0;
            var enumerations = 0;
            var directory = AuthorDeletionSafetyFixture.Proxy<IDirectory>((method, args) =>
            {
                if (method.Name == nameof(IDirectory.Exists) && (string)args[0] == source) folderChecks++;
                if (method.Name == nameof(IDirectory.EnumerateDirectories) && (string)args[0] == sourceRoot) enumerations++;
                return method.Invoke(realFileSystem.Directory, args);
            });
            var fileSystem = AuthorDeletionSafetyFixture.Proxy<IFileSystem>((method, args) =>
                method.Name == "get_Directory" ? directory : method.Invoke(realFileSystem, args));
            using var s = new Sandbox("ebook", fileSystem);
            for (var i = 0; i < 100; i++) s.Track($"Book-{i}.epub");
            sourceRoot = s.P("old");
            for (var i = 0; i < 100; i++) Directory.CreateDirectory(Path.Combine(sourceRoot, $"Sibling-{i}"));
            Directory.Delete(s.Target, false);
            Directory.Move(s.Source, s.Target);
            source = s.Source;

            s.Events.OnMoved(new AuthorMovedEvent(s.Author, s.Source, s.Target));

            Assert.That(enumerations, Is.Zero, "The absent author folder must not trigger root enumeration for each tracked file.");
            Assert.That(folderChecks, Is.EqualTo(1));
            Assert.That(s.Updated.Count, Is.EqualTo(100));
            Assert.That(s.Files.All(file => s.Target.IsParentPath(file.Path) && File.Exists(file.Path)), Is.True);
        }

        [TestCase("audiobook", false)]
        [TestCase("ebook", false)]
        [TestCase("audiobook", true)]
        [TestCase("ebook", true)]
        public void missing_file_follows_completed_move_and_is_reclaimed_by_destination_cleanup(string media, bool retainSource)
        {
            using var s = new Sandbox(media);
            var extension = media == "ebook" ? ".epub" : ".m4b";
            var present = s.Track("Present" + extension);
            var missing = s.Track("Missing" + extension);
            File.Delete(missing.Path);
            var retained = retainSource ? s.Track("debug.log") : null;

            s.Move(media);

            Assert.That(missing.Path, Is.EqualTo(Path.Combine(s.Target, "Missing" + extension)));
            Assert.That(File.ReadAllText(present.Path), Is.EqualTo("sentinel"));
            Assert.That(s.Updated, Is.EquivalentTo(new[] { present, missing }));
            var deleted = new List<BookFile>();
            var files = AuthorDeletionSafetyFixture.Proxy<IMediaFileService>((method, args) =>
            {
                if (method.Name == nameof(IMediaFileService.GetFilesWithBasePath))
                    return s.Files.Where(file => ((string)args[0]).IsParentPath(file.Path)).ToList();
                if (method.Name == nameof(IMediaFileService.DeleteMany))
                {
                    Assert.That(args[1], Is.EqualTo(DeleteMediaFileReason.MissingFromDisk));
                    deleted.AddRange((List<BookFile>)args[0]);
                    return null;
                }
                throw new NotImplementedException(method.Name);
            });
            new MediaFileTableCleanupService(files, new NzbDrone.Windows.Disk.DiskProvider(), LogManager.GetCurrentClassLogger())
                .Clean(s.Target, new List<string> { present.Path }, media);
            Assert.That(deleted, Is.EqualTo(new[] { missing }));
            if (retained != null)
            {
                Assert.That(retained.Path, Is.EqualTo(Path.Combine(s.Source, "debug.log")));
                Assert.That(File.ReadAllText(retained.Path), Is.EqualTo("sentinel"));
            }
        }

        [Test]
        public void retained_file_paths_are_summarized_once_with_details_at_debug()
        {
            using var s = new Sandbox("ebook");
            for (var i = 0; i < 12; i++)
            {
                s.Track($"retained-{i}.epub");
            }

            // Exercise tracking separately from the transfer's own cleanup diagnostics.
            s.Events.OnMoved(new AuthorMovedEvent(s.Author, s.Source, s.Target));

            Assert.That(s.Logs.Logs.Count(line => line.StartsWith("Warn|")), Is.EqualTo(1));
            Assert.That(s.Logs.Logs.Single(line => line.StartsWith("Warn|")),
                Does.Contain("12").And.Contain(s.Source).And.Contain(s.Target));
            Assert.That(s.Logs.Logs.Count(line => line.StartsWith("Debug|") && line.Contains("keeping its recorded path")), Is.EqualTo(12));
            Assert.That(s.Logs.Logs.Any(line => line.Contains("[AUTHOR-MOVE-TIMING]") && line.Contains("12") && line.Contains("ms")), Is.True);
            Assert.That(s.Files.All(file => File.Exists(file.Path)), Is.True);
            Assert.That(s.UpdateCalls, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void same_name_arrival_does_not_capture_the_moved_files_inventory(bool wholeFolderMove)
        {
            using var s = new Sandbox("ebook") { SameMount = wholeFolderMove };
            var moved = s.Track("Grave Peril.epub");
            var originalPath = moved.Path;
            if (wholeFolderMove)
            {
                Directory.Delete(s.Target);
                var handle = s.Events.OnMoved;
                s.Events.OnMoved = message =>
                {
                    Directory.CreateDirectory(s.Source);
                    File.WriteAllText(originalPath, "new source file");
                    handle(message);
                };
            }
            else
            {
                s.BeforeCleanup = () => File.WriteAllText(originalPath, "new source file");
            }

            s.Move("ebook");

            Assert.That(moved.Path, Is.EqualTo(Path.Combine(s.Target, "Grave Peril.epub")));
            Assert.That(File.ReadAllText(moved.Path), Is.EqualTo("sentinel"));
            Assert.That(File.ReadAllText(originalPath), Is.EqualTo("new source file"));
            Assert.That(s.Updated, Is.EqualTo(new[] { moved }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void late_arrival_or_cleanup_failure_does_not_revert_completed_file_moves(bool lateArrival)
        {
            using var s = new Sandbox("ebook");
            var moved = s.Track("Book.epub");
            BookFile arrival = null;
            if (lateArrival) s.BeforeCleanup = () => { arrival ??= s.Track("Arrival.epub"); };
            else s.FailCleanup = true;
            s.Move("ebook");
            Assert.That(s.Author.EbookPath, Is.EqualTo(s.Target));
            Assert.That(moved.Path, Is.EqualTo(Path.Combine(s.Target, "Book.epub")));
            Assert.That(s.Updated, Is.EqualTo(new[] { moved }));
            if (lateArrival)
            {
                Assert.That(arrival.Path, Is.EqualTo(Path.Combine(s.Source, "Arrival.epub")));
                Assert.That(File.Exists(arrival.Path), Is.True);
            }
        }

        [TestCase("audiobook")]
        [TestCase("ebook")]
        public void actual_transfer_failure_does_not_report_success_or_retarget_files(string media)
        {
            using var s = new Sandbox(media) { FailTransfer = true };
            var file = s.Track("Book.epub");
            var original = file.Path;
            s.Move(media);
            Assert.That(s.Author.Path, Is.EqualTo(s.Source));
            Assert.That(file.Path, Is.EqualTo(original));
            Assert.That(File.Exists(file.Path), Is.True);
            Assert.That(s.Events.Moves, Is.Zero);
            Assert.That(s.Updated, Is.Empty);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Consumers.AudioBookShelf;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class SidecarFailureSafetyFixture
    {
        private static T Proxy<T>(Func<MethodInfo, object[], object> call) where T : class
        {
            var proxy = DispatchProxy.Create<T, AuthorDeletionSafetyFixture.CallbackProxy>();
            ((AuthorDeletionSafetyFixture.CallbackProxy)(object)proxy).Call = call;
            return proxy;
        }

        private sealed class Sandbox : IDisposable
        {
            public string Temp { get; } = Path.Combine(Path.GetTempPath(), "chaptarr-proposal-review-" + Guid.NewGuid().ToString("N"));
            public Author Author { get; }
            public List<MetadataFile> Rows { get; } = new();
            public List<string> PersistedPaths { get; } = new();
            public IDiskProvider Disk { get; }
            private readonly LogFactory _logFactory = new();
            public MemoryTarget Log { get; } = new() { Layout = "${level}|${message}" };
            public Logger Logger { get; }
            public Action<string, string> BeforeMove { get; set; } = (_, _) => { };
            public Action<string> BeforeDelete { get; set; } = _ => { };
            public bool FailUpsert { get; set; }
            public bool Hardlinks { get; set; }
            public bool LinkSucceeds { get; set; }
            public int LinkAttempts { get; private set; }
            public int Copies { get; private set; }
            public string P(string relative) => Path.Combine(Temp, relative.AsOsAgnostic());

            public Sandbox()
            {
                Directory.CreateDirectory(Temp);
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Warn, LogLevel.Fatal, Log);
                _logFactory.Configuration = logging;
                Logger = _logFactory.GetLogger(nameof(SidecarFailureSafetyFixture));
                Author = new Author { Id = 1, Name = "Synthetic Author", Path = P(@"ebooks\Author"), EbookPath = P(@"ebooks\Author"), AudiobookPath = P(@"audio\Author"), EbookRootFolderPath = P("ebooks"), AudiobookRootFolderPath = P("audio") };
                var realDisk = Chaptarr.Core.Test.Common.RealDiskProvider.Create(Logger);
                Disk = Proxy<IDiskProvider>((m, a) =>
                {
                    foreach (var path in a.OfType<string>())
                    {
                        if (!Temp.PathEquals(path) && !Temp.IsParentPath(path)) throw new InvalidOperationException("Sandbox boundary violation");
                    }
                    if (m.Name == nameof(IDiskProvider.CopyFile)) BeforeMove((string)a[0], (string)a[1]);
                    if (m.Name == nameof(IDiskProvider.CopyFile)) Copies++;
                    if (m.Name == nameof(IDiskProvider.TryCreateHardLink))
                    {
                        LinkAttempts++;
                        if (LinkSucceeds) File.Copy((string)a[0], (string)a[1], false);
                        return LinkSucceeds;
                    }
                    if (m.Name == nameof(IDiskProvider.DeleteFolder)) BeforeDelete((string)a[0]);
                    try { return m.Invoke(realDisk, a); }
                    catch (TargetInvocationException ex) { throw ex.InnerException; }
                });
            }

            public string Seed(string relative, string content = "sentinel")
            {
                var path = P(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, content);
                return path;
            }

            public BookFile FileAt(string relative, int id = 1, int bookId = 1)
            {
                var book = new Book { Id = bookId, AuthorId = Author.Id, Author = Author, MediaType = BookMediaType.Ebook, RemoteProviderIds = new HashSet<string> { "hc:123" } };
                return new BookFile { Id = id, Path = Seed(relative), Author = Author, MediaType = "ebook", Quality = new QualityModel(Quality.EPUB), EditionId = bookId, Edition = new Edition { Id = bookId, BookId = bookId, Book = book } };
            }

            public MetadataFile Track(string relative, string name, int? fileId = null)
            {
                var row = new MetadataFile { Id = Rows.Count + 1, AuthorId = Author.Id, BookId = 1, BookFileId = fileId, RelativePath = relative.AsOsAgnostic(), Consumer = nameof(AudioBookShelfMetadata), Extension = Path.GetExtension(name), Type = name == "metadata.json" ? MetadataType.BookMetadata : MetadataType.BookImage, Hash = "sentinel".SHA256Hash() };
                Rows.Add(row);
                return row;
            }

            public void MoveSidecars(params (BookFile File, string Previous)[] files)
            {
                var metadataFiles = Proxy<IMetadataFileService>((m, a) =>
                {
                    if (m.Name == nameof(IMetadataFileService.GetFilesByAuthor)) return Rows;
                    if (m.Name == nameof(IMetadataFileService.Upsert))
                    {
                        if (FailUpsert) throw new IOException("Synthetic repository failure");
                        PersistedPaths.Add(((MetadataFile)a[0]).RelativePath);
                        return null;
                    }
                    throw new NotImplementedException(m.Name);
                });
                var factory = Proxy<IMetadataFactory>((m, a) => m.Name == "GetAvailableProviders" ? new List<IMetadata> { new AudioBookShelfMetadata(null, null, null) } : throw new NotImplementedException(m.Name));
                var config = Proxy<IConfigService>((m, a) => m.Name == "get_CopyUsingHardlinks" ? Hardlinks : throw new NotImplementedException(m.Name));
                new MetadataService(config, Disk, null, null, null, factory, null, null, null, metadataFiles, null, Logger).MoveFilesAfterRename(Author, files.Select(item => new RenamedBookFile
                {
                    BookFile = item.File,
                    PreviousPath = P(item.Previous),
                    PreviousAuthorPath = P(@"ebooks\Author")
                }).ToList());
            }

            public void Dispose()
            {
                _logFactory.Dispose();
                Directory.Delete(Temp, true);
            }
        }

        [TestCase("Dune.cue", "Dune - Frank Herbert.cue")]
        [TestCase("Dune.en.srt", "Dune - Frank Herbert.en.srt")]
        [TestCase("reading-notes.json", "reading-notes.json")]
        [TestCase("Duneology.txt", "Duneology.txt")]
        [TestCase("沙丘.cue", "沙丘 - 弗兰克.cue")]
        public void organize_renames_only_extras_based_on_the_old_media_filename(string name, string expected)
        {
            using var s = new Sandbox();
            var oldStem = name.StartsWith("沙丘", StringComparison.Ordinal) ? "沙丘" : "Dune";
            var newStem = oldStem == "沙丘" ? "沙丘 - 弗兰克" : "Dune - Frank Herbert";
            var file = s.FileAt(@"ebooks\Author\New\" + newStem + ".epub");
            var source = s.Seed(@"ebooks\Author\Old\" + name, "owned sidecar sentinel");
            var row = new OtherExtraFile { Id = 5, AuthorId = 1, BookId = 1, BookFileId = file.Id,
                RelativePath = (@"Old\" + name).AsOsAgnostic(), Extension = Path.GetExtension(name) };
            var writes = 0;
            var service = Proxy<IOtherExtraFileService>((m, a) =>
            {
                if (m.Name == nameof(IOtherExtraFileService.GetFilesByAuthor)) return new List<OtherExtraFile> { row };
                if (m.Name == nameof(IOtherExtraFileService.Upsert)) { writes++; return null; }
                throw new NotImplementedException(m.Name);
            });
            var config = Proxy<IConfigService>((m, a) => m.Name == "get_CopyUsingHardlinks" ? false : throw new NotImplementedException(m.Name));
            var moved = new OtherExtraService(config, s.Disk, null, service, null, null, s.Logger).MoveFilesAfterRename(s.Author,
                new List<RenamedBookFile> { new() { BookFile = file, PreviousPath = s.P(@"ebooks\Author\Old\" + oldStem + ".epub"), PreviousAuthorPath = s.Author.EbookPath } });
            Assert.That(moved.Count(), Is.EqualTo(1));
            Assert.That(File.ReadAllText(s.P(@"ebooks\Author\New\" + expected)), Is.EqualTo("owned sidecar sentinel"));
            Assert.That(File.Exists(source), Is.False);
            Assert.That(row.RelativePath, Is.EqualTo("New/" + expected));
            Assert.That(writes, Is.EqualTo(1));
        }

        [TestCase("cover.jpg")]
        [TestCase("metadata.json")]
        public void unresolved_tracked_metadata_warns_once_per_row_not_per_part(string name)
        {
            using var s = new Sandbox();
            var first = s.FileAt(@"ebooks\Author\New\01.epub");
            var second = s.FileAt(@"ebooks\Author\New\02.epub", 2);
            var sidecar = s.Seed(@"scattered\Old\" + name, "keep sidecar");
            var row = s.Track(@"Old\" + name, name);
            var original = row.RelativePath;
            s.MoveSidecars((first, @"scattered\Old\01.epub"), (second, @"scattered\Old\02.epub"));
            Assert.That(File.ReadAllText(sidecar), Is.EqualTo("keep sidecar"));
            Assert.That(row.RelativePath, Is.EqualTo(original));
            Assert.That(s.PersistedPaths, Is.Empty);
            var warning = s.Log.Logs.Single(line => line.StartsWith("Warn|Could not locate tracked sidecar"));
            Assert.That(warning, Does.Contain(name).And.Contain("row 1"));
        }

        [Test]
        public void unresolved_tracked_other_extra_warns_and_keeps_its_record()
        {
            using var s = new Sandbox();
            var file = s.FileAt(@"ebooks\Author\New\book.epub");
            var source = s.Seed(@"scattered\Old\book.opf", "keep opf");
            var row = new OtherExtraFile { Id = 5, AuthorId = 1, BookId = 1, BookFileId = file.Id, RelativePath = @"Old\book.opf".AsOsAgnostic(), Extension = ".opf" };
            var service = Proxy<IOtherExtraFileService>((m, a) => m.Name == nameof(IOtherExtraFileService.GetFilesByAuthor)
                ? new List<OtherExtraFile> { row } : throw new InvalidOperationException("Unexpected sidecar write"));
            var moved = new OtherExtraService(null, s.Disk, null, service, null, null, s.Logger).MoveFilesAfterRename(s.Author,
                new List<RenamedBookFile> { new() { BookFile = file, PreviousPath = s.P(@"scattered\Old\book.epub"), PreviousAuthorPath = s.Author.EbookPath } });
            Assert.That(moved, Is.Empty);
            Assert.That(File.ReadAllText(source), Is.EqualTo("keep opf"));
            Assert.That(row.RelativePath, Is.EqualTo(@"Old\book.opf".AsOsAgnostic()));
            Assert.That(s.Log.Logs.Count(line => line.StartsWith("Warn|Could not locate tracked sidecar")), Is.EqualTo(1));
        }

        [TestCase("cover.jpg")]
        [TestCase("metadata.json")]
        public void remaining_file_in_shared_source_folder_keeps_folder_sidecar(string name)
        {
            using var s = new Sandbox();
            var moved = s.FileAt(@"ebooks\Author\New\A.epub");
            var remaining = s.FileAt(@"ebooks\Author\Old\B.epub", 2);
            var sidecar = s.Seed(@"ebooks\Author\Old\" + name);
            s.Track(@"Old\" + name, name, name == "metadata.json" ? moved.Id : null);
            s.MoveSidecars((moved, @"ebooks\Author\Old\A.epub"));
            Assert.That(File.Exists(remaining.Path), Is.True);
            Assert.That(File.Exists(sidecar), Is.True, "A fixed-name folder sidecar may serve an unselected file even when linked to the selected producer");
        }

        [Test]
        public void recycling_same_named_format_folders_preserves_both_covers()
        {
            using var s = new Sandbox();
            var audio = s.Seed(@"audio\Author\cover.jpg", "audio cover");
            var ebook = s.Seed(@"ebooks\Author\cover.jpg", "ebook cover");
            Directory.CreateDirectory(s.P("recycle"));
            var config = Proxy<IConfigService>((m, a) => m.Name == "get_RecycleBin"
                ? s.P("recycle") : throw new NotImplementedException(m.Name));
            var recycle = new RecycleBinProvider(new DiskTransferService(s.Disk, LogManager.GetCurrentClassLogger()),
                s.Disk, config, LogManager.GetCurrentClassLogger());

            recycle.DeleteFolder(Path.GetDirectoryName(audio));
            recycle.DeleteFolder(Path.GetDirectoryName(ebook));

            Assert.That(Directory.GetFiles(s.P("recycle"), "cover.jpg", SearchOption.AllDirectories)
                .Select(File.ReadAllText), Is.EquivalentTo(new[] { "audio cover", "ebook cover" }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void importing_a_replacement_sidecar_recycles_the_old_bytes(bool transferFails)
        {
            using var s = new Sandbox();
            var logger = LogManager.GetCurrentClassLogger();
            var source = s.Seed(@"incoming\Dune.nfo", "new sidecar");
            var bookPath = s.Seed(@"ebooks\Author\Dune\Dune.epub", "book");
            var oldPath = s.Seed(@"ebooks\Author\Dune\Dune.nfo", "user edited notes");
            var bin = s.P("bin");
            var config = Proxy<IConfigService>((m, a) => m.Name == "get_RecycleBin" ? bin : false);
            var transfer = new DiskTransferService(s.Disk, logger);
            var recycle = new RecycleBinProvider(transfer, s.Disk, config, logger);
            var importTransfer = Proxy<IDiskTransferService>((m, a) =>
            {
                Assert.That(a[3], Is.False, "No implicit overwrite after recycling");
                if (transferFails) throw new IOException("Synthetic transfer failure");
                return transfer.TransferFile((string)a[0], (string)a[1], (TransferMode)a[2], (bool)a[3]);
            });
            var stored = 0;
            var service = new OtherExtraService(config, s.Disk, importTransfer,
                Proxy<IOtherExtraFileService>((m, a) => { stored++; return null; }),
                Proxy<IMediaFileAttributeService>((m, a) => null), recycle, logger);
            var file = new BookFile { Id = 1, Path = bookPath, MediaType = "ebook", Edition = new Edition { BookId = 1 } };
            void Import() => service.Import(s.Author, file, source, ".nfo", readOnly: false, replaceExisting: true);

            if (transferFails) Assert.Throws<IOException>(Import);
            else Import();

            Assert.That(File.ReadAllText(Directory.GetFiles(bin, "*", SearchOption.AllDirectories).Single()), Is.EqualTo("user edited notes"));
            Assert.That(File.ReadAllText(bookPath), Is.EqualTo("book"));
            Assert.That(File.Exists(source), Is.EqualTo(transferFails));
            Assert.That(stored, Is.EqualTo(transferFails ? 0 : 1));
            if (!transferFails) Assert.That(File.ReadAllText(oldPath), Is.EqualTo("new sidecar"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void folder_transfer_does_not_delete_skipped_or_late_arriving_files(bool lateArrival)
        {
            using var s = new Sandbox();
            var source = s.Seed(@"source\book.epub", "book");
            var target = s.P("target");
            Directory.CreateDirectory(target);
            var remaining = s.P(lateArrival ? @"source\arrived.epub" : @"source\debug.log");
            if (lateArrival)
            {
                s.BeforeDelete = path =>
                {
                    if (path.PathEquals(Path.GetDirectoryName(source))) File.WriteAllText(remaining, "keep");
                };
            }
            else
            {
                File.WriteAllText(remaining, "keep");
            }

            var transfer = new DiskTransferService(s.Disk, LogManager.GetCurrentClassLogger());
            Assert.That(transfer.TransferFolder(Path.GetDirectoryName(source), target, TransferMode.Move), Is.EqualTo(TransferMode.Move));
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(remaining), Is.EqualTo("keep"));
                Assert.That(File.ReadAllText(Path.Combine(target, "book.epub")), Is.EqualTo("book"));
            });
        }

        [TestCase("debug.log")]
        [TestCase(".nfs123")]
        [TestCase(".metadata.json")]
        [TestCase("notes.socket")]
        [TestCase(@".nfs-folder\notes.txt")]
        public void recycling_preserves_ignored_contents_in_the_bin(string relative)
        {
            using var s = new Sandbox();
            var book = s.Seed(@"source\Grave Peril.epub", "book bytes");
            s.Seed(@"source\" + relative, "additional bytes");
            s.Seed(@"source\cover.jpg", "cover bytes");
            s.Seed(@"source\metadata.json", "metadata bytes");
            var disk = Proxy<IDiskProvider>((method, args) =>
            {
                if (method.Name == nameof(IDiskProvider.GetMount)) return null;
                if (method.Name == nameof(IDiskProvider.CopyPermissions)) return null;
                try { return method.Invoke(s.Disk, args); }
                catch (TargetInvocationException e) { throw e.InnerException; }
            });
            var config = Proxy<IConfigService>((method, args) => method.Name == "get_RecycleBin" ? s.P("recycle") : throw new NotImplementedException(method.Name));
            Directory.CreateDirectory(s.P("recycle"));
            var recycle = new RecycleBinProvider(new DiskTransferService(disk, LogManager.GetCurrentClassLogger()), disk, config, LogManager.GetCurrentClassLogger());

            recycle.DeleteFolder(Path.GetDirectoryName(book));

            Assert.That(Directory.Exists(Path.GetDirectoryName(book)), Is.False);
            Assert.That(File.ReadAllText(s.P(@"recycle\source\" + relative)), Is.EqualTo("additional bytes"));
            Assert.That(File.ReadAllText(s.P(@"recycle\source\Grave Peril.epub")), Is.EqualTo("book bytes"));
            Assert.That(File.ReadAllText(s.P(@"recycle\source\cover.jpg")), Is.EqualTo("cover bytes"));
            Assert.That(File.ReadAllText(s.P(@"recycle\source\metadata.json")), Is.EqualTo("metadata bytes"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void folder_deletion_provider_requires_removal_in_both_modes(bool recycling)
        {
            using var s = new Sandbox();
            var sentinel = s.Seed(@"source\Keep.epub");
            var finalizations = 0;
            var disk = Proxy<IDiskProvider>((method, args) =>
            {
                if (method.Name == nameof(IDiskProvider.DeleteFolder)) return null;
                if (method.Name == nameof(IDiskProvider.FolderSetLastWriteTime)) finalizations++;
                try { return method.Invoke(s.Disk, args); }
                catch (TargetInvocationException e) { throw e.InnerException; }
            });
            var transfer = Proxy<IDiskTransferService>((method, args) => method.Name == nameof(IDiskTransferService.TransferFolder)
                ? TransferMode.Move : throw new NotImplementedException(method.Name));
            var config = Proxy<IConfigService>((method, args) => method.Name == "get_RecycleBin"
                ? recycling ? s.P("recycle") : string.Empty : throw new NotImplementedException(method.Name));

            var provider = new RecycleBinProvider(transfer, disk, config, LogManager.GetCurrentClassLogger());
            var error = Assert.Throws<IOException>(() => provider.DeleteFolder(Path.GetDirectoryName(sentinel)));

            Assert.That(error.Message, Does.Contain(Path.GetDirectoryName(sentinel)));
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("sentinel"));
            Assert.That(finalizations, Is.Zero);
        }

        [TestCase(false, false, 0, 1)]
        [TestCase(true, false, 1, 1)]
        [TestCase(true, true, 1, 0)]
        public void sidecar_preparation_honors_hardlink_preference_and_copy_fallback(bool enabled, bool succeeds, int links, int copies)
        {
            using var s = new Sandbox { Hardlinks = enabled, LinkSucceeds = succeeds };
            var moved = s.FileAt(@"ebooks\Author\New\A.epub");
            var source = s.Seed(@"ebooks\Author\Old\cover.jpg");
            s.Track(@"Old\cover.jpg", "cover.jpg");
            s.MoveSidecars((moved, @"ebooks\Author\Old\A.epub"));
            Assert.That(s.LinkAttempts, Is.EqualTo(links));
            Assert.That(s.Copies, Is.EqualTo(copies));
            Assert.That(File.Exists(source), Is.False);
            Assert.That(File.ReadAllText(s.P(@"ebooks\Author\New\cover.jpg")), Is.EqualTo("sentinel"));
            Assert.That(s.PersistedPaths, Has.Count.EqualTo(1));
        }

        [Test]
        public void concurrent_destination_arrival_is_not_overwritten_or_rolled_back()
        {
            using var s = new Sandbox();
            var moved = s.FileAt(@"ebooks\Author\New\A.epub");
            var source = s.Seed(@"ebooks\Author\Old\cover.jpg");
            s.Track(@"Old\cover.jpg", "cover.jpg");
            s.BeforeMove = (_, destination) => File.WriteAllText(destination, "concurrent owner");
            s.MoveSidecars((moved, @"ebooks\Author\Old\A.epub"));
            Assert.That(File.ReadAllText(source), Is.EqualTo("sentinel"));
            Assert.That(File.ReadAllText(s.P(@"ebooks\Author\New\cover.jpg")), Is.EqualTo("concurrent owner"));
            Assert.That(s.PersistedPaths, Is.Empty);
        }

        [TestCase(".hidden")]
        [TestCase("cover.jpg")]
        [TestCase("metadata.json")]
        [TestCase("Untracked.EPUB")]
        public void automatic_cleanup_preserves_all_remaining_contents(string filename)
        {
            using var s = new Sandbox();
            var keep = s.Seed(@"ebooks\Author\Old\Nested\" + filename);
            NzbDrone.Core.Extras.ExtraFilePathHelper.CleanupEmptySourceFolders(s.Disk, LogManager.GetCurrentClassLogger(),
                new[] { (s.P(@"ebooks\Author\Old"), s.Author.EbookPath) });
            Assert.That(File.ReadAllText(keep), Is.EqualTo("sentinel"));
        }

        [Test]
        public void empty_cleanup_does_not_follow_directory_links()
        {
            using var s = new Sandbox();
            var outside = s.P(@"audio\Author\Outside");
            var child = Path.Combine(outside, "Empty");
            Directory.CreateDirectory(child);
            var source = s.P(@"ebooks\Author\Old");
            Directory.CreateDirectory(source);
            var link = Path.Combine(source, "link");
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (UnauthorizedAccessException) { Assert.Ignore("Directory-link creation requires permission on this platform"); }
            try
            {
                NzbDrone.Core.Extras.ExtraFilePathHelper.CleanupEmptySourceFolders(s.Disk, LogManager.GetCurrentClassLogger(),
                    new[] { (source, s.Author.EbookPath) });
                Assert.That(Directory.Exists(child), Is.True);
                Assert.That(Directory.Exists(link), Is.True);
            }
            finally { if (Directory.Exists(link)) Directory.Delete(link); }
        }

        [Test]
        public void cleanup_normalizes_dot_segments_before_enforcing_source_boundary()
        {
            using var s = new Sandbox();
            var other = s.P(@"ebooks\Other");
            Directory.CreateDirectory(other);
            NzbDrone.Core.Extras.ExtraFilePathHelper.CleanupEmptySourceFolders(s.Disk, LogManager.GetCurrentClassLogger(),
                new[] { (Path.Combine(s.Author.EbookPath, "..", "Other"), s.Author.EbookPath) });
            Assert.That(Directory.Exists(other), Is.True);
        }

        [Test]
        public void distinct_selected_copy_folders_keep_their_own_covers()
        {
            using var s = new Sandbox();
            var first = s.FileAt(@"ebooks\Author\New A\A.epub");
            var second = s.FileAt(@"ebooks\Author\New B\B.epub", 2);
            s.Seed(@"ebooks\Author\Old A\cover.jpg", "first cover");
            s.Seed(@"ebooks\Author\Old B\cover.jpg", "second cover");
            s.Track(@"Old A\cover.jpg", "cover.jpg");
            s.Track(@"Old B\cover.jpg", "cover.jpg");
            s.MoveSidecars((first, @"ebooks\Author\Old A\A.epub"), (second, @"ebooks\Author\Old B\B.epub"));
            Assert.Multiple(() =>
            {
                var firstCover = s.P(@"ebooks\Author\New A\cover.jpg");
                var secondCover = s.P(@"ebooks\Author\New B\cover.jpg");
                Assert.That(File.Exists(firstCover), Is.True, "First destination must retain its own cover");
                Assert.That(File.Exists(secondCover), Is.True);
                if (File.Exists(firstCover)) Assert.That(File.ReadAllText(firstCover), Is.EqualTo("first cover"));
                if (File.Exists(secondCover)) Assert.That(File.ReadAllText(secondCover), Is.EqualTo("second cover"));
            });
        }

        [TestCase("cover.jpg")]
        [TestCase("metadata.json")]
        public void mixed_roots_do_not_resolve_old_sidecar_under_destination_root(string name)
        {
            using var s = new Sandbox();
            var moved = s.FileAt(@"audio\Author\New\A.epub");
            var own = s.Seed(@"ebooks\Author\Old\" + name, "own source");
            var other = s.Seed(@"audio\Author\Old\" + name, "other root sentinel");
            s.Track(@"Old\" + name, name, name == "metadata.json" ? moved.Id : null);
            s.MoveSidecars((moved, @"ebooks\Author\Old\A.epub"));
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(other), Is.True, "Destination-root lookalike must remain untouched");
                Assert.That(File.ReadAllText(s.P(@"audio\Author\New\" + name)), Is.EqualTo("own source"));
                Assert.That(File.Exists(own), Is.False);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void sidecar_collision_or_pre_move_failure_preserves_source_and_tracking(bool failMove)
        {
            using var s = new Sandbox();
            var moved = s.FileAt(@"ebooks\Author\New\A.epub");
            var source = s.Seed(@"ebooks\Author\Old\cover.jpg");
            var row = s.Track(@"Old\cover.jpg", "cover.jpg");
            var previous = row.RelativePath;
            if (failMove) s.BeforeMove = (_, _) => throw new IOException("Synthetic move failure");
            else s.Seed(@"ebooks\Author\New\cover.jpg", "destination sentinel");
            s.MoveSidecars((moved, @"ebooks\Author\Old\A.epub"));
            Assert.That(File.Exists(source), Is.True);
            Assert.That(row.RelativePath, Is.EqualTo(previous));
            Assert.That(s.PersistedPaths, Is.Empty);
            if (!failMove) Assert.That(File.ReadAllText(s.P(@"ebooks\Author\New\cover.jpg")), Is.EqualTo("destination sentinel"));
        }

        [Test]
        public void persistence_failure_after_sidecar_move_preserves_original()
        {
            using var s = new Sandbox();
            var moved = s.FileAt(@"ebooks\Author\New\A.epub");
            var source = s.Seed(@"ebooks\Author\Old\cover.jpg");
            s.Track(@"Old\cover.jpg", "cover.jpg");
            s.FailUpsert = true;
            Assert.DoesNotThrow(() => s.MoveSidecars((moved, @"ebooks\Author\Old\A.epub")));
            Assert.That(s.PersistedPaths, Is.Empty);
            Assert.That(File.Exists(source), Is.True, "An unsuccessful persisted relocation must not lose the original path");
        }

        [Test]
        public void source_cleanup_does_not_delete_a_file_arriving_after_empty_check()
        {
            using var s = new Sandbox();
            var oldFile = s.P(@"ebooks\Author\Old\A.epub");
            var source = Path.GetDirectoryName(oldFile);
            Directory.CreateDirectory(source);
            var sentinel = Path.Combine(source, "Arrived.epub");
            s.BeforeDelete = path =>
            {
                if (path.PathEquals(source)) File.WriteAllText(sentinel, "concurrent arrival");
            };
            var rename = new RenameBookFileService(null, null, null, null, s.Disk, LogManager.GetCurrentClassLogger(), null);
            typeof(RenameBookFileService).GetMethod("CleanupEmptySourceFolders", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(rename, new object[] { new List<(string, string)> { (oldFile, s.Author.EbookPath) } });
            Assert.That(File.Exists(sentinel), Is.True, "The final empty-folder deletion must refuse newly nonempty folders");
        }

        [Test]
        public void disk_provider_nonrecursive_delete_refuses_nonempty_folder()
        {
            using var s = new Sandbox();
            var sentinel = s.Seed(@"ebooks\Author\Old\Keep.epub");
            Assert.Throws<IOException>(() => s.Disk.DeleteFolder(Path.GetDirectoryName(sentinel), false));
            Assert.That(File.Exists(sentinel), Is.True, "The wrapper must preserve the filesystem empty-only guarantee");
        }

        [Test]
        public void raw_nonrecursive_directory_delete_refuses_nonempty_folder()
        {
            using var s = new Sandbox();
            var sentinel = s.Seed(@"ebooks\Author\Old\Keep.epub");
            Assert.Throws<IOException>(() => Directory.Delete(Path.GetDirectoryName(sentinel), false));
            Assert.That(File.Exists(sentinel), Is.True);
        }

        [Test]
        public void recursive_deletion_does_not_follow_a_directory_link()
        {
            using var s = new Sandbox();
            var keep = s.Seed(@"audio\Author\Outside\Keep.epub");
            var remove = s.Seed(@"ebooks\Author\Selected\Remove.epub");
            var folder = Path.GetDirectoryName(remove);
            var link = Path.Combine(folder, "linked");
            try { Directory.CreateSymbolicLink(link, Path.GetDirectoryName(keep)); }
            catch (UnauthorizedAccessException) { Assert.Ignore("Directory-link creation requires permission on this platform"); }
            try
            {
                s.Disk.DeleteFolder(folder, true);
                Assert.That(File.ReadAllText(keep), Is.EqualTo("sentinel"));
                Assert.That(Directory.Exists(folder), Is.False);
            }
            finally { if (Directory.Exists(link)) Directory.Delete(link); }
        }

        private sealed class Events : IEventAggregator
        {
            public List<IEvent> Published { get; } = new();
            public void PublishEvent<T>(T message) where T : class, IEvent => Published.Add(message);
        }

        [Test]
        public void later_colocation_failure_does_not_suppress_successful_files_sidecar_event()
        {
            using var s = new Sandbox();
            var first = s.FileAt(@"ebooks\Author\Old A\A.epub");
            var second = s.FileAt(@"ebooks\Author\Old B\B.epub", 2);
            var files = new List<BookFile> { first, second };
            var target = s.P(@"ebooks\Author\New\A.epub");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var events = new Events();
            var roots = Proxy<IRootFolderService>((m, a) => new RootFolder { Path = s.P("ebooks"), FolderType = FolderType.Mixed, PlaceEbooksWithAudiobooks = true });
            var books = Proxy<IBookService>((m, a) => new List<Book> { first.Edition.Book });
            var media = Proxy<IMediaFileService>((m, a) => m.Name == nameof(IMediaFileService.GetFilesByBooks) ? files : null);
            var mover = Proxy<IMoveBookFiles>((m, a) =>
            {
                if (m.Name == nameof(IMoveBookFiles.GetOrganizeDestination)) return new BookFileMovePlan { CanOrganize = true, SourceAuthorFolderPath = s.Author.EbookPath, DestinationPath = target, ReplicaPaths = new List<string>() };
                var file = (BookFile)a[0];
                if (file.Id == second.Id) throw new IOException("Synthetic second-file move failure");
                File.Move(file.Path, target);
                file.Path = target;
                return file;
            });
            var audio = new Book { Id = 3, AuthorId = 1, Author = s.Author, MediaType = BookMediaType.Audiobook, RemoteProviderIds = new HashSet<string> { "hc:123" } };
            var imported = new BookFile { Author = s.Author, MediaType = "audiobook", Quality = new QualityModel(Quality.M4B) };
            new EbookColocateOnAudiobookImportHandler(roots, books, media, mover, s.Disk, events, LogManager.GetCurrentClassLogger())
                .Handle(new BookImportedEvent(s.Author, audio, new List<BookFile> { imported }, new List<BookFile>(), true, null));
            Assert.That(File.Exists(target), Is.True);
            Assert.That(File.Exists(second.Path), Is.True);
            Assert.That(events.Published.OfType<BookFileRenamedEvent>().Count(), Is.EqualTo(1));
            Assert.That(events.Published.OfType<AuthorRenamedEvent>().Count(), Is.EqualTo(1), "Completed moves still require their synchronous sidecar pass");
        }

        [Test]
        public void canonical_author_path_update_does_not_hide_old_sidecar()
        {
            using var s = new Sandbox();
            var source = s.Seed(@"ebooks\Author\Title\cover.jpg");
            var moved = s.FileAt(@"ebooks\Canonical Author\Title\A.epub");
            s.Track(@"Title\cover.jpg", "cover.jpg");
            s.Author.Path = s.Author.EbookPath = s.P(@"ebooks\Canonical Author");
            s.MoveSidecars((moved, @"ebooks\Author\Title\A.epub"));
            Assert.That(File.Exists(s.P(@"ebooks\Canonical Author\Title\cover.jpg")), Is.True, "The old author base must remain available through sidecar relocation");
            Assert.That(File.Exists(source), Is.False);
        }

        [Test]
        public void existing_book_delete_path_does_not_delete_sidecar_from_wrong_media_base()
        {
            using var s = new Sandbox();
            var colocated = s.FileAt(@"audio\Author\Title\A.epub");
            colocated.Edition.Book.BookFiles = new List<BookFile> { colocated };
            var own = s.Seed(@"audio\Author\Title\cover.jpg", "owned colocated cover");
            var other = s.Seed(@"ebooks\Author\Title\cover.jpg", "other base sentinel");
            s.Track(@"Title\cover.jpg", "cover.jpg");
            var repo = Proxy<IExtraFileRepository<MetadataFile>>((m, a) => m.Name == "GetFilesByBook" ? s.Rows : null);
            var recycle = Proxy<IRecycleBinProvider>((m, a) =>
            {
                var path = (string)a[0];
                if (!s.Temp.IsParentPath(path)) throw new InvalidOperationException("Sandbox boundary violation");
                File.Delete(path);
                return null;
            });
            var service = new MetadataFileService(repo, null, s.Disk, recycle, LogManager.GetCurrentClassLogger());
            service.DeleteBookFiles(colocated.Edition.Book);
            service.Handle(new BookDeletedEvent(colocated.Edition.Book, true, false, filesAlreadyDeleted: true));
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(other), Is.True, "Book media type does not locate a physically colocated sidecar");
                Assert.That(File.Exists(own), Is.False);
            });
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class OrganizeTrackedSourceFixture
    {
        // Synthetic bytes, real naming/planning/transfer; persistence is recorded separately.
        private sealed class Sandbox : IDisposable
        {
            public string Temp { get; } = Path.Combine(Path.GetTempPath(), "chaptarr-organize-tracked-" + Guid.NewGuid().ToString("N"));
            public Author Author { get; }
            public NamingConfig Naming { get; } = NamingConfig.Default;
            public List<BookFile> Files { get; } = new();
            public Dictionary<int, (string Path, string[] Replicas)> Saved { get; } = new();
            public List<int> Updated { get; } = new();
            public List<IEvent> Events { get; } = new();
            public List<string> Recycled { get; } = new();
            private readonly LogFactory _logFactory = new();
            public MemoryTarget Log { get; } = new() { Layout = "${level}|${message}" };
            public Action<string, string> BeforeTransfer { get; set; } = (_, _) => { };
            public Action<string> BeforeRecycle { get; set; } = _ => { };
            public bool DenyWrites { get; set; }
            public BookFileMovingService Mover { get; }
            public RenameBookFileService Renamer { get; }
            public string P(string path) => Path.Combine(Temp, path.Replace('/', Path.DirectorySeparatorChar));

            public Sandbox(bool mixed = false)
            {
                Directory.CreateDirectory(P("audio"));
                Directory.CreateDirectory(P("ebooks"));
                Author = new Author
                {
                    Id = 1, Name = "Frank Herbert", Path = P("old/Frank Herbert"),
                    AudiobookRootFolderPath = P("audio"), EbookRootFolderPath = P(mixed ? "audio" : "ebooks"),
                    AudiobookPath = P("old/audio"), EbookPath = P("old/ebooks")
                };
                Naming.RenameBooks = Naming.EbookRenameBooks = true;
                Naming.StandardBookFormat = Naming.EbookStandardBookFormat = "{Book Title}/{Book Title}{ (PartNumber:00)}";
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Info, LogLevel.Fatal, Log);
                _logFactory.Configuration = logging;
                var logger = _logFactory.GetLogger(nameof(OrganizeTrackedSourceFixture));
                var realDisk = Chaptarr.Core.Test.Common.RealDiskProvider.Create(logger);
                var disk = Proxy<IDiskProvider>((method, args) =>
                {
                    foreach (var path in args.OfType<string>().Where(Path.IsPathRooted))
                    {
                        if (!Temp.PathEquals(path) && !Temp.IsParentPath(path)) throw new InvalidOperationException("Sandbox boundary violation: " + path);
                    }

                    if (method.Name == nameof(IDiskProvider.FolderWritable) && DenyWrites) return false;
                    if (method.Name == nameof(IDiskProvider.MoveFile) || method.Name == nameof(IDiskProvider.CopyFile)) BeforeTransfer((string)args[0], (string)args[1]);
                    try { return method.Invoke(realDisk, args); }
                    catch (TargetInvocationException ex) { throw ex.InnerException; }
                });
                var media = Proxy<IMediaFileService>((method, args) =>
                {
                    switch (method.Name)
                    {
                        case nameof(IMediaFileService.GetFilesByAuthor): return Files.ToList();
                        case nameof(IMediaFileService.GetFilesByBook): return Files.Where(f => f.Edition.BookId == (int)args[0]).ToList();
                        case nameof(IMediaFileService.Get): return Files.Where(f => ((IEnumerable<int>)args[0]).Contains(f.Id)).ToList();
                        case nameof(IMediaFileService.Update):
                            var file = (BookFile)args[0];
                            Saved[file.Id] = (file.Path, file.ReplicaPaths?.ToArray() ?? Array.Empty<string>());
                            Updated.Add(file.Id);
                            return null;
                        default: throw new NotImplementedException(method.Name);
                    }
                });
                var authors = Proxy<IAuthorService>((m, a) => m.Name switch
                {
                    nameof(IAuthorService.GetAuthor) => Author,
                    nameof(IAuthorService.UpdateAuthor) => Author,
                    _ => throw new NotImplementedException(m.Name)
                });
                var roots = new List<RootFolder>
                {
                    new() { Id = 1, Path = P("audio"), FolderType = mixed ? FolderType.Mixed : FolderType.Audiobook, PlaceEbooksWithAudiobooks = mixed },
                    new() { Id = 2, Path = P("ebooks"), FolderType = FolderType.Ebook }
                };
                var rootService = Proxy<IRootFolderService>((m, a) => m.Name switch
                {
                    nameof(IRootFolderService.All) => roots,
                    nameof(IRootFolderService.GetBestRootFolder) => roots.FirstOrDefault(r => r.Path.PathEquals((string)a[0]) || r.Path.IsParentPath((string)a[0])),
                    _ => throw new NotImplementedException(m.Name)
                });
                var books = Proxy<IBookService>((m, a) => m.Name == nameof(IBookService.GetBooksByAuthorId)
                    ? Files.Select(f => f.Edition.Book).DistinctBy(b => b.Id).ToList() : throw new NotImplementedException(m.Name));
                var naming = Proxy<INamingConfigService>((m, a) => Naming);
                var builder = new FileNameBuilder(naming,
                    Proxy<IQualityDefinitionService>((m, a) => new QualityDefinition((Quality)a[0])), new CacheManager(),
                    Proxy<ICustomFormatCalculationService>((m, a) => new List<CustomFormat>()), logger);
                var events = Proxy<IEventAggregator>((m, a) => { Events.Add((IEvent)a[0]); return null; });
                Mover = new BookFileMovingService(
                    Proxy<IEditionService>((m, a) => throw new NotImplementedException(m.Name)),
                    Proxy<IUpdateBookFileService>((m, a) => null), builder, null, naming,
                    new EbookColocationPlanner(books, media, rootService, disk),
                    new DiskTransferService(disk, logger), disk,
                    Proxy<IRecycleBinProvider>((m, a) =>
                    {
                        var path = (string)a[0];
                        BeforeRecycle(path);
                        File.Delete(path);
                        Recycled.Add(path);
                        return null;
                    }),
                    Proxy<IRootFolderWatchingService>((m, a) => null),
                    Proxy<IMediaFileAttributeService>((m, a) => null), events,
                    Proxy<IConfigService>((m, a) => false),
                    Proxy<IFileMutationSafetyService>((m, a) => null), logger);
                Renamer = new RenameBookFileService(authors, media, Mover, events, disk, logger, null);
            }

            public string Seed(string relative, string bytes = "synthetic media bytes")
            {
                var path = P(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, bytes);
                return path;
            }

            public BookFile Add(string path, string title = "Dune", bool ebook = false, string work = "gr:1")
            {
                var id = Files.Count + 1;
                var book = new Book { Id = id, AuthorId = Author.Id, Author = Author, Title = title,
                    MediaType = ebook ? BookMediaType.Ebook : BookMediaType.Audiobook,
                    GoodreadsWorkId = work, SeriesLinks = new List<SeriesBookLink>() };
                var edition = new Edition { Id = id, BookId = id, Book = book, Title = title };
                var file = new BookFile { Id = id, Author = Author, EditionId = id, Edition = edition,
                    Path = Seed(path), MediaType = ebook ? "ebook" : "audiobook",
                    Quality = new QualityModel(ebook ? Quality.EPUB : Quality.M4B), ReplicaPaths = new List<string>() };
                Files.Add(file);
                Saved[id] = (file.Path, Array.Empty<string>());
                return file;
            }

            public void Run(params BookFile[] files) => Renamer.Execute(new RenameFilesCommand(Author.Id, files.Select(f => f.Id).ToList(), true));
            public void Dispose()
            {
                _logFactory.Dispose();
                Directory.Delete(Temp, true);
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void canonical_move_uses_real_naming_and_transfer_for_scattered_files(bool ebook, bool rename)
        {
            using var s = new Sandbox();
            s.Naming.RenameBooks = s.Naming.EbookRenameBooks = rename;
            var file = s.Add("scattered/nested/original" + (ebook ? ".epub" : ".m4b"), ebook: ebook);
            var original = file.Path;
            var otherPath = ebook ? s.Author.AudiobookPath : s.Author.EbookPath;
            var sentinel = s.Seed("scattered/nested/cover.jpg", "untracked cover");
            var preview = s.Renamer.GetRenamePreviews(s.Author.Id, file.MediaType, true).Single();
            Assert.That(preview.CanOrganize, Is.True);
            Assert.That(preview.NewPath, Is.EqualTo(s.P((ebook ? "ebooks" : "audio") + "/Frank Herbert/Dune/" + (rename ? "Dune" : "original") + (ebook ? ".epub" : ".m4b"))));

            s.Run(file);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(preview.NewPath), Is.EqualTo("synthetic media bytes"));
                Assert.That(File.Exists(original), Is.False);
                Assert.That(file.EditionId, Is.EqualTo(1));
                Assert.That(s.Saved[file.Id].Path, Is.EqualTo(preview.NewPath));
                Assert.That(s.Updated, Is.EqualTo(new[] { file.Id }));
                Assert.That(s.Events.OfType<AuthorRenamedEvent>().Single().RenamedFiles.Single().PreviousPath, Is.EqualTo(original));
                Assert.That(ebook ? s.Author.EbookPath : s.Author.AudiobookPath, Is.EqualTo(s.P((ebook ? "ebooks" : "audio") + "/Frank Herbert")));
                Assert.That(ebook ? s.Author.AudiobookPath : s.Author.EbookPath, Is.EqualTo(otherPath));
                Assert.That(File.ReadAllText(sentinel), Is.EqualTo("untracked cover"));
            });
        }

        [Test]
        public void unchecked_outside_root_and_blank_source_remain_unmovable()
        {
            using var s = new Sandbox();
            var file = s.Add("scattered/Dune.m4b");
            Assert.That(s.Mover.GetOrganizeDestination(file, s.Author, false).CanOrganize, Is.False);
            foreach (var blank in new[] { null, "", " " })
            {
                file.Path = blank;
                var plan = s.Mover.GetOrganizeDestination(file, s.Author, true);
                Assert.That(plan.CanOrganize, Is.False);
                Assert.That(plan.SkipReason, Does.Contain("no source path"));
            }
        }

        [Test]
        public void blank_path_reports_its_own_completion_reason()
        {
            using var s = new Sandbox();
            var file = s.Add("scattered/Dune.m4b");
            file.Path = null;
            s.Run(file);
            var completion = s.Log.Logs.Single(line => line.Contains("Organized 0 of 1 file"));
            Assert.That(completion, Does.Contain("no source file path is recorded"));
            Assert.That(completion, Does.Not.Contain("author folder could not be determined"));
            Assert.That(completion, Does.Not.Contain("not eligible"));
            Assert.That(s.Updated, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_colocated_primary_preserves_all_replicas_and_their_records(bool import)
        {
            using var s = new Sandbox(mixed: true);
            s.Add("audio/Frank Herbert/Dune/Dune.m4b");
            s.Add("audio/Frank Herbert/Other recording/Dune.m4b");
            var file = s.Add("audio/Frank Herbert/Dune/Dune.epub", ebook: true);
            var replica = s.Seed("audio/Frank Herbert/Other recording/Dune.epub", "managed copy");
            var stale = s.Seed("audio/Frank Herbert/Previous recording/Dune.epub", "stale managed copy");
            file.ReplicaPaths.AddRange(new[] { replica, stale });
            s.Saved[file.Id] = (file.Path, file.ReplicaPaths.ToArray());
            var plan = s.Mover.GetOrganizeDestination(file, s.Author, true);
            Assert.That(plan.DestinationPath, Is.EqualTo(file.Path));
            Assert.That(plan.ReplicaPaths, Is.EqualTo(new[] { replica }));
            File.Delete(file.Path);

            if (import)
            {
                var local = new LocalBook { Book = file.Edition.Book, PendingReplicaPaths = plan.ReplicaPaths };
                Assert.Throws<FileNotFoundException>(() => s.Mover.CompleteImportReplicas(file, local));
                Assert.That(local.PendingReplicaPaths, Is.EqualTo(new[] { replica }), "Failure must not acknowledge replica work as complete");
            }
            else
            {
                s.Run(file);
            }

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(replica), Is.EqualTo("managed copy"));
                Assert.That(File.ReadAllText(stale), Is.EqualTo("stale managed copy"));
                Assert.That(file.ReplicaPaths, Is.EqualTo(new[] { replica, stale }));
                Assert.That(s.Saved[file.Id].Replicas, Is.EqualTo(new[] { replica, stale }));
                Assert.That(s.Recycled, Is.Empty);
                Assert.That(s.Updated, Is.Empty);
                Assert.That(s.Log.Logs.Count(line => line.StartsWith("Warn|Keeping all ebook replicas")), Is.EqualTo(1));
                Assert.That(s.Events.OfType<BookFileRenamedEvent>(), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void organize_keeps_existing_replica_bytes_and_tracking(bool stillDesired)
        {
            using var s = new Sandbox(mixed: true);
            s.Add("audio/Frank Herbert/Dune/Dune.m4b");
            s.Add("audio/Frank Herbert/Other recording/Dune.m4b");
            var file = s.Add("audio/Frank Herbert/Dune/Dune.epub", ebook: true);
            var relative = stillDesired ? "audio/Frank Herbert/Other recording/Dune.epub" : "audio/Frank Herbert/Previous recording/Dune.epub";
            var replica = s.Seed(relative, "managed copy retained after permission failure");
            file.ReplicaPaths.Add(replica);
            s.Saved[file.Id] = (file.Path, new[] { replica });
            s.BeforeRecycle = _ => Assert.Fail("Organize must not delete existing ebook copies");

            s.Run(file);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(replica), Is.EqualTo("managed copy retained after permission failure"));
                Assert.That(file.ReplicaPaths, Does.Contain(replica));
                Assert.That(s.Saved[file.Id].Replicas, Does.Contain(replica));
                Assert.That(s.Recycled, Does.Not.Contain(replica));
                Assert.That(s.Log.Logs.Any(line => line.Contains("Failed to delete managed ebook replica")), Is.False);
                Assert.That(s.Log.Logs.Any(line => line.Contains("Failed to create ebook replica")), Is.False);
                Assert.That(File.ReadAllText(file.Path), Is.EqualTo("synthetic media bytes"));
            });
        }

        [TestCase("collision")]
        [TestCase("missing root")]
        [TestCase("permission")]
        [TestCase("transfer")]
        public void failed_primary_move_keeps_source_replicas_and_persisted_row(string failure)
        {
            using var s = new Sandbox();
            var file = s.Add("scattered/Dune.epub", ebook: true);
            var source = file.Path;
            var replica = s.Seed("copies/Dune.epub", "managed copy");
            file.ReplicaPaths.Add(replica);
            s.Saved[file.Id] = (source, new[] { replica });
            var plan = s.Mover.GetOrganizeDestination(file, s.Author, true);
            Assert.That(plan.ReplicaPaths, Is.Null);
            if (failure == "collision") s.Seed(Path.GetRelativePath(s.Temp, plan.DestinationPath), "existing destination");
            if (failure == "missing root") Directory.Delete(s.Author.EbookRootFolderPath);
            if (failure == "permission") s.DenyWrites = true;
            if (failure == "transfer") s.BeforeTransfer = (_, _) => throw new IOException("Injected transfer failure");

            s.Run(file);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(source), Is.EqualTo("synthetic media bytes"));
                Assert.That(File.ReadAllText(replica), Is.EqualTo("managed copy"));
                Assert.That(file.Path, Is.EqualTo(source));
                Assert.That(file.ReplicaPaths, Is.EqualTo(new[] { replica }));
                Assert.That(s.Saved[file.Id], Is.EqualTo((source, new[] { replica })));
                Assert.That(s.Recycled, Is.Empty);
                Assert.That(s.Updated, Is.Empty);
                Assert.That(s.Events.OfType<BookFileRenamedEvent>(), Is.Empty);
                Assert.That(s.Events.OfType<AuthorRenamedEvent>(), Is.Empty);
                if (failure == "collision") Assert.That(File.ReadAllText(plan.DestinationPath), Is.EqualTo("existing destination"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void organize_can_adopt_its_identical_copy_without_losing_either_file(bool different)
        {
            using var s = new Sandbox();
            var file = s.Add("scattered/Dune.epub", ebook: true);
            var original = file.Path;
            var destination = s.Mover.GetOrganizeDestination(file, s.Author, true).DestinationPath;
            s.Seed(Path.GetRelativePath(s.Temp, destination), different ? "different ebook content" : "synthetic media bytes");
            file.ReplicaPaths.Add(destination);
            s.Saved[file.Id] = (original, new[] { destination });
            s.Run(file);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(original), Is.EqualTo("synthetic media bytes"));
                Assert.That(File.ReadAllText(destination), Is.EqualTo(different ? "different ebook content" : "synthetic media bytes"));
                Assert.That(file.Path, Is.EqualTo(different ? original : destination));
                Assert.That(s.Saved[file.Id].Path, Is.EqualTo(file.Path));
                Assert.That(s.Saved[file.Id].Replicas, Is.EqualTo(new[] { different ? destination : original }));
                Assert.That(s.Recycled, Is.Empty);
                if (different) Assert.That(s.Log.Logs, Has.Some.Contains("this ebook's managed copy"));
            });
        }

        [Test]
        public void organize_moves_primary_but_keeps_existing_managed_copies()
        {
            using var s = new Sandbox();
            var file = s.Add("scattered/Dune.epub", ebook: true);
            var replica = s.Seed("copies/Dune.epub");
            file.ReplicaPaths.Add(replica);
            var destination = s.Mover.GetOrganizeDestination(file, s.Author, true).DestinationPath;
            s.BeforeRecycle = _ => Assert.That(File.ReadAllText(destination), Is.EqualTo("synthetic media bytes"));
            s.Run(file);
            Assert.That(s.Recycled, Is.Empty);
            Assert.That(File.Exists(replica), Is.True);
            Assert.That(s.Saved[file.Id].Replicas, Is.EqualTo(new[] { replica }));
        }

        [TestCase(false, true)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(true, false)]
        public void already_in_place_organize_leaves_existing_copies_alone(bool mixedWithoutAudiobooks, bool primaryExists)
        {
            // A non-Mixed root disables colocation; a Mixed root with no audiobook
            // candidates represents the state after the associated audiobook is removed.
            using var s = new Sandbox(mixed: mixedWithoutAudiobooks);
            var file = s.Add((mixedWithoutAudiobooks ? "audio" : "ebooks") + "/Frank Herbert/Dune/Dune.epub", ebook: true);
            var source = file.Path;
            var replica = s.Seed("copies/Dune.epub", "managed copy");
            file.ReplicaPaths.Add(replica);
            s.Saved[file.Id] = (source, new[] { replica });
            var plan = s.Mover.GetOrganizeDestination(file, s.Author, true);
            Assert.That(plan.DestinationPath, Is.EqualTo(source));
            Assert.That(plan.ReplicaPaths, Is.Null);
            if (!primaryExists) File.Delete(source);
            var transfers = 0;
            s.BeforeTransfer = (_, _) => transfers++;

            s.Run(file);

            Assert.Multiple(() =>
            {
                Assert.That(transfers, Is.Zero, "An unchanged path must not transfer the primary");
                Assert.That(file.Path, Is.EqualTo(source));
                Assert.That(s.Saved[file.Id].Path, Is.EqualTo(source));
                Assert.That(s.Events.OfType<BookFileRenamedEvent>(), Is.Empty);
                Assert.That(s.Events.OfType<AuthorRenamedEvent>(), Is.Empty);
                if (primaryExists)
                {
                    Assert.That(File.ReadAllText(source), Is.EqualTo("synthetic media bytes"));
                    Assert.That(s.Recycled, Is.Empty);
                    Assert.That(File.Exists(replica), Is.True);
                    Assert.That(s.Saved[file.Id].Replicas, Is.EqualTo(new[] { replica }));
                    Assert.That(s.Updated, Is.Empty);
                }
                else
                {
                    Assert.That(File.ReadAllText(replica), Is.EqualTo("managed copy"));
                    Assert.That(file.ReplicaPaths, Is.EqualTo(new[] { replica }));
                    Assert.That(s.Saved[file.Id].Replicas, Is.EqualTo(new[] { replica }));
                    Assert.That(s.Recycled, Is.Empty);
                    Assert.That(s.Updated, Is.Empty);
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void already_in_place_without_replicas_does_not_write_or_publish_rename(bool ebook)
        {
            using var s = new Sandbox();
            var file = s.Add((ebook ? "ebooks" : "audio") + "/Frank Herbert/Dune/Dune" + (ebook ? ".epub" : ".m4b"), ebook: ebook);
            s.Run(file);
            Assert.That(File.ReadAllText(file.Path), Is.EqualTo("synthetic media bytes"));
            Assert.That(s.Updated, Is.Empty);
            Assert.That(s.Recycled, Is.Empty);
            Assert.That(s.Events.OfType<BookFileRenamedEvent>(), Is.Empty);
            Assert.That(s.Events.OfType<AuthorRenamedEvent>(), Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void flat_folder_colocation_tracks_the_matching_audiobook_file(bool collision)
        {
            using var s = new Sandbox(mixed: true);
            var dune = s.Add("audio/Loose/Dune.m4b");
            var messiah = s.Add("audio/Loose/Messiah.m4b", "Dune Messiah", work: "gr:2");
            var ebook = s.Add("scattered/Dune.epub", ebook: true);
            var source = dune.Path;
            var preview = s.Renamer.GetRenamePreviews(s.Author.Id, moveToCanonicalAuthorFolder: true);
            var expected = preview.Single(p => p.BookFileId == ebook.Id).NewPath;
            Assert.That(expected, Is.EqualTo(s.P("audio/Frank Herbert/Dune/Dune.epub")));
            if (collision) s.Seed("audio/Frank Herbert/Dune/Dune.m4b", "unrelated file");

            s.Run(dune, messiah, ebook);

            // A failed audiobook move must not send its ebook after the OTHER book.
            Assert.That(ebook.Path, Is.EqualTo(collision ? s.P("audio/Loose/Dune.epub") : expected));
            Assert.That(File.ReadAllText(ebook.Path), Is.EqualTo("synthetic media bytes"));
            Assert.That(ebook.EditionId, Is.EqualTo(3));
            Assert.That(ebook.ReplicaPaths, Is.Empty);
            if (collision)
            {
                Assert.That(File.ReadAllText(s.P("audio/Frank Herbert/Dune/Dune.m4b")), Is.EqualTo("unrelated file"));
                Assert.That(File.ReadAllText(source), Is.EqualTo("synthetic media bytes"));
                Assert.That(dune.Path, Is.EqualTo(source));
                Assert.That(s.Saved[dune.Id].Path, Is.EqualTo(source));
                Assert.That(s.Updated, Does.Not.Contain(dune.Id));
            }
        }

        [Test]
        public void deselected_audiobook_does_not_follow_another_book_out_of_the_shared_folder()
        {
            using var s = new Sandbox(mixed: true);
            var dune = s.Add("audio/Loose/Dune.m4b");
            var messiah = s.Add("audio/Loose/Messiah.m4b", "Dune Messiah", work: "gr:2");
            var ebook = s.Add("scattered/Dune.epub", ebook: true);
            var original = dune.Path;
            s.Run(messiah, ebook);
            Assert.That(dune.Path, Is.EqualTo(original));
            Assert.That(File.Exists(original), Is.True);
            Assert.That(ebook.Path, Is.EqualTo(s.P("audio/Loose/Dune.epub")));
            Assert.That(s.Updated, Is.EquivalentTo(new[] { messiah.Id, ebook.Id }));
        }

        [Test]
        public void failed_part_does_not_lose_its_bytes_or_stop_the_other_selected_parts()
        {
            using var s = new Sandbox();
            var first = s.Add("scattered/01.m4b");
            var second = s.Add("scattered/02.m4b");
            var third = s.Add("scattered/03.m4b");
            second.Edition = first.Edition;
            second.EditionId = first.EditionId;
            third.Edition = first.Edition;
            third.EditionId = first.EditionId;
            var firstSource = first.Path;
            var source = second.Path;
            var thirdSource = third.Path;
            File.WriteAllText(firstSource, "first part");
            File.WriteAllText(source, "middle part");
            File.WriteAllText(thirdSource, "last part");
            var preview = s.Renamer.GetRenamePreviews(s.Author.Id, moveToCanonicalAuthorFolder: true);
            s.BeforeTransfer = (path, _) => { if (path == source) throw new IOException("Injected second-part failure"); };
            s.Run(first, second, third);
            Assert.That(first.Path, Is.EqualTo(preview.Single(p => p.BookFileId == first.Id).NewPath));
            Assert.That(File.ReadAllText(first.Path), Is.EqualTo("first part"));
            Assert.That(File.Exists(firstSource), Is.False);
            Assert.That(File.Exists(thirdSource), Is.False);
            Assert.That(first.Path, Is.Not.EqualTo(third.Path));
            Assert.That(third.Path, Is.EqualTo(preview.Single(p => p.BookFileId == third.Id).NewPath));
            Assert.That(File.ReadAllText(third.Path), Is.EqualTo("last part"));
            Assert.That(File.ReadAllText(source), Is.EqualTo("middle part"));
            Assert.That(s.Log.Logs.Any(line => line.Contains("stranded", StringComparison.OrdinalIgnoreCase)), Is.False);
            Assert.That(s.Saved[second.Id].Path, Is.EqualTo(source));
            Assert.That(s.Updated, Is.EqualTo(new[] { first.Id, third.Id }));
            Assert.That(s.Events.OfType<AuthorRenamedEvent>().Single().RenamedFiles.Select(f => f.BookFile.Id), Is.EqualTo(new[] { first.Id, third.Id }));
        }
    }
}

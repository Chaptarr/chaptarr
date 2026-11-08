using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    [NonParallelizable]
    public class MissingPrimaryPreservationFixture
    {
        [Test]
        public void missing_primary_exposes_surviving_copy_immediately_without_duplicate_rows(
            [Values] bool postgres, [Values("single", "batch", "folder")] string route, [Values] bool alreadyTracked)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var folder = Directory.CreateTempSubdirectory("chaptarr-missing-primary-").FullName;
                try
                {
                    var replica = Path.Combine(folder, "copy.epub");
                    File.WriteAllText(replica, "surviving ebook");
                    var events = new List<object>();
                    var publisher = Proxy<IEventAggregator>((m, a) => { events.Add(a[0]); return null; });
                    var author = new AuthorRepository(db, publisher, LogManager.GetCurrentClassLogger())
                        .Insert(new Author { Name = "Author", CleanName = "author", SortName = "author", TitleSlug = "author", Path = folder, Added = DateTime.UtcNow });
                    var book = new BookRepository(db, publisher).Insert(new Book
                        { AuthorId = author.Id, Title = "Book", CleanTitle = "book", TitleSlug = "book", MediaType = BookMediaType.Ebook, Added = DateTime.UtcNow });
                    var edition = new EditionRepository(db, publisher).Insert(new Edition { BookId = book.Id, Title = "Book", ForeignEditionId = "gr:1-ebook", ReadingFormatId = 3 });
                    var repository = new MediaFileRepository(db, publisher);
                    var primary = NewFile(Path.Combine(folder, "missing.epub"));
                    primary.EditionId = edition.Id;
                    primary.ReplicaPaths = new List<string> { replica, replica, Path.Combine(folder, "also-missing.epub") };
                    repository.Insert(primary);
                    if (alreadyTracked) repository.Insert(NewFile(replica));
                    events.Clear();
                    var service = new MediaFileService(repository, publisher, null,
                        Proxy<IDiskProvider>((m, a) => m.Name == "FileExistsCanonical" ? File.Exists((string)a[0]) : throw new AssertionException(m.Name)),
                        LogManager.GetCurrentClassLogger());

                    if (route == "folder") service.DeleteRecordsForAuthorDeletion(new List<BookFile> { primary });
                    else if (route == "batch") service.DeleteMany(new List<BookFile> { primary }, DeleteMediaFileReason.MissingFromDisk);
                    else service.Delete(primary, DeleteMediaFileReason.MissingFromDisk);

                    var saved = repository.All().ToList();
                    Assert.That(saved, Has.Count.EqualTo(1));
                    Assert.That(saved[0].Path, Is.EqualTo(replica));
                    Assert.That(saved[0].EditionId, Is.Zero);
                    Assert.That(saved[0].ReplicaPaths, Is.Empty);
                    Assert.That(saved[0].MediaType, Is.EqualTo("ebook"));
                    Assert.That(repository.GetUnmappedFiles("ebook").Select(file => file.Path), Does.Contain(replica));
                    Assert.That(File.ReadAllText(replica), Is.EqualTo("surviving ebook"));
                    if (route == "folder") Assert.That(events.OfType<BookFileDeletedEvent>(), Is.Empty, "Folder cleanup must not repeat physical deletion");
                    else Assert.That(events.OfType<BookFileDeletedEvent>().Single().Reason, Is.EqualTo(DeleteMediaFileReason.MissingFromDisk));
                    Assert.That(events.OfType<BookFilesAddedEvent>().Count(), Is.EqualTo(alreadyTracked ? 0 : 1));
                }
                finally { Directory.Delete(folder, true); }
            });
        }

        [Test]
        public void deletion_reason_controls_replica_and_sidecar_bytes_and_keeps_missing_sidecar_tracking(
            [Values] bool postgres,
            [Values(DeleteMediaFileReason.Manual, DeleteMediaFileReason.ManualOverride, DeleteMediaFileReason.Upgrade,
                DeleteMediaFileReason.MissingFromDisk, DeleteMediaFileReason.NoLinkedEpisodes)] DeleteMediaFileReason reason)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var folder = Directory.CreateTempSubdirectory("chaptarr-delete-reason-").FullName;
                try
                {
                    var bookFolder = Directory.CreateDirectory(Path.Combine(folder, "Book")).FullName;
                    var primary = Path.Combine(bookFolder, "missing.epub");
                    var replica = Path.Combine(folder, "copy.epub");
                    var sidecar = Path.Combine(bookFolder, "book.nfo");
                    File.WriteAllText(replica, "copy");
                    File.WriteAllText(sidecar, "notes");
                    var author = new Author { Id = 1, Path = folder, EbookPath = folder };
                    var file = NewFile(primary);
                    file.Id = 10;
                    file.Author = author;
                    file.Edition = new Edition { Id = 3, BookId = 2 };
                    file.ReplicaPaths = new List<string> { replica };
                    var extras = new ExtraFileRepository<MetadataFile>(db, Proxy<IEventAggregator>((m, a) => null));
                    extras.Insert(new MetadataFile { AuthorId = 1, BookId = 2, BookFileId = 10, Consumer = "fixture",
                        RelativePath = Path.Combine("Book", "book.nfo"), Extension = ".nfo", Type = MetadataType.BookMetadata,
                        Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow });
                    var disk = Proxy<IDiskProvider>((m, a) => m.Name switch
                    {
                        "FileExistsCanonical" => File.Exists((string)a[0]),
                        "GetFiles" => Directory.GetFiles((string)a[0]).ToList(),
                        "GetParentFolder" => Path.GetDirectoryName((string)a[0]),
                        _ => throw new AssertionException("Unexpected disk action: " + m.Name)
                    });
                    var recycle = Proxy<IRecycleBinProvider>((m, a) => { File.Delete((string)a[0]); return null; });
                    var logger = LogManager.GetCurrentClassLogger();
                    var sidecars = new MetadataFileService(extras, Proxy<IAuthorService>((m, a) => author), disk, recycle, logger);
                    var deletion = new MediaFileDeletionService(disk, recycle, null, null,
                        Proxy<IConfigService>((m, a) => false), null, null, null, logger, null, null, null, null);
                    var message = new BookFileDeletedEvent(file, reason);
                    sidecars.Handle(message);
                    deletion.Handle(message);

                    var removes = reason == DeleteMediaFileReason.Manual || reason == DeleteMediaFileReason.ManualOverride || reason == DeleteMediaFileReason.Upgrade;
                    Assert.That(File.Exists(replica), Is.EqualTo(!removes));
                    Assert.That(File.Exists(sidecar), Is.EqualTo(!removes));
                    var retained = extras.All().ToList();
                    Assert.That(retained, Has.Count.EqualTo(reason == DeleteMediaFileReason.MissingFromDisk ? 1 : 0));
                    if (retained.Count > 0)
                    {
                        Assert.That(retained[0].BookFileId, Is.Null);
                        Assert.That(retained[0].BookId, Is.EqualTo(2));
                    }
                }
                finally { Directory.Delete(folder, true); }
            });
        }

        [Test]
        public void removing_nested_root_retains_files_covered_by_another_root([Values] bool postgres, [Values] bool parentRemains)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var publisher = Proxy<IEventAggregator>((m, a) => null);
                var repository = new MediaFileRepository(db, publisher);
                var file = NewFile("/library/nested/Author/book.epub");
                repository.Insert(file);
                var roots = Proxy<IRootFolderService>((m, a) => m.Name == "All"
                    ? new List<RootFolder> { new RootFolder { Id = 2, Path = parentRemains ? "/library" : "/library-other" } }
                    : throw new AssertionException(m.Name));
                var service = new MediaFileService(repository, publisher, null,
                    Proxy<IDiskProvider>((m, a) => throw new AssertionException("Root removal must not touch disk")),
                    LogManager.GetCurrentClassLogger(), roots);
                service.HandleAsync(new ModelEvent<RootFolder>(new RootFolder { Id = 1, Path = "/library/nested" }, ModelAction.Deleted));
                Assert.That(repository.All().Count(), Is.EqualTo(parentRemains ? 1 : 0));
            });
        }

        [Test]
        public void unmapped_delete_removes_previewed_copies_and_keeps_row_when_a_copy_cannot_be_deleted(
            [Values] bool postgres, [Values] bool bulk, [Values] bool failCopy)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var folder = Directory.CreateTempSubdirectory("chaptarr-unmapped-delete-").FullName;
                try
                {
                    var primary = Path.Combine(folder, "Dune.epub");
                    var copy = Path.Combine(folder, "copy.epub");
                    File.WriteAllText(primary, "primary");
                    File.WriteAllText(copy, "copy");
                    var logger = LogManager.GetCurrentClassLogger();
                    var events = Proxy<IEventAggregator>((_, _) => null);
                    var repository = new MediaFileRepository(db, events);
                    var file = NewFile(primary);
                    file.ReplicaPaths = new List<string> { copy };
                    repository.Insert(file);
                    var disk = RealDiskProvider.Create(logger);
                    var media = new MediaFileService(repository, events, null, disk, logger);
                    var config = Proxy<IConfigService>((method, _) => method.ReturnType == typeof(string) ? "" : false);
                    var roots = Proxy<IRootFolderService>((method, _) => method.Name == "All"
                        ? new List<RootFolder> { new RootFolder { Path = folder } }
                        : new RootFolder { Path = folder });
                    var preview = new NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFiles(disk,
                        new ExtraFileRepository<MetadataFile>(db, events),
                        new ExtraFileRepository<NzbDrone.Core.Extras.Others.OtherExtraFile>(db, events),
                        new AuthorRepository(db, events, logger), new BookRepository(db, events), config);
                    var deletion = new MediaFileDeletionService(disk,
                        Proxy<IRecycleBinProvider>((_, args) =>
                        {
                            if ((string)args[0] == copy && failCopy) throw new IOException("copy is locked");
                            File.Delete((string)args[0]);
                            return null;
                        }), media, null, config, events, roots, null, logger, null, null, null, null);
                    var controller = new Chaptarr.Api.V1.BookFiles.BookFileController(null, media, deletion, null,
                        null, null, null, roots, logger, preview);
                    var inventory = controller.PreviewDeleteBookFiles(new Chaptarr.Api.V1.BookFiles.BookFileDeletePreviewRequest
                        { BookFileIds = new List<int> { file.Id } });
                    Assert.That(inventory.Files.Single().AssociatedPaths, Is.EqualTo(new[] { copy }));
                    void Delete()
                    {
                        if (bulk) controller.DeleteTrackFiles(new Chaptarr.Api.V1.BookFiles.BookFileListResource { BookFileIds = new List<int> { file.Id } });
                        else controller.DeleteBookFile(file.Id);
                    }
                    if (failCopy)
                    {
                        Assert.Throws<NzbDrone.Core.Exceptions.NzbDroneClientException>(Delete);
                        Assert.That(repository.All().Single().ReplicaPaths, Is.EqualTo(new[] { copy }));
                        Assert.That(File.ReadAllText(copy), Is.EqualTo("copy"));
                        failCopy = false;
                    }
                    Delete();
                    Assert.That(repository.All(), Is.Empty);
                    Assert.That(File.Exists(primary), Is.False);
                    Assert.That(File.Exists(copy), Is.False);
                }
                finally { Directory.Delete(folder, true); }
            });
        }

        private static BookFile NewFile(string path) => new BookFile
        {
            Path = path, DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
            MediaType = "ebook", Quality = new QualityModel(Quality.EPUB), MediaInfo = new MediaInfoModel()
        };
    }
}

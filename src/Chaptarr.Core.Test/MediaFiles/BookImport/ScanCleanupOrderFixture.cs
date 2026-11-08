using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    [NonParallelizable]
    public class ScanCleanupOrderFixture
    {
        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);

        private static object Default(Type type) => type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);

        [TestCase(false, ".epub", false, false)]
        [TestCase(true, ".epub", false, false)]
        [TestCase(false, ".m4b", false, false)]
        [TestCase(true, ".m4b", false, false)]
        [TestCase(false, ".m4b", true, false)]
        [TestCase(true, ".m4b", true, false)]
        [TestCase(false, ".mp3", false, true)]
        [TestCase(true, ".mp3", false, true)]
        [TestCase(false, ".m4b", false, false, ".mp3")]
        [TestCase(true, ".m4b", false, false, ".mp3")]
        [TestCase(false, ".m4b", false, false, null, true)]
        [TestCase(true, ".m4b", false, false, null, true)]
        [TestCase(false, ".epub", false, false, null, false, true)]
        [TestCase(true, ".epub", false, false, null, false, true)]
        public void scan_cleans_before_publishing_new_paths_and_preserves_real_copies(bool postgres, string extension, bool keepOriginal, bool multipart, string oldExtension = null, bool mixedRoot = false, bool cleanupFails = false)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var folder = Directory.CreateTempSubdirectory("chaptarr-scan-order-").FullName;
                try
                {
                    using var logFactory = new LogFactory();
                    var logTarget = new NLog.Targets.MemoryTarget { Layout = "${message}" };
                    var logConfig = new NLog.Config.LoggingConfiguration();
                    logConfig.AddRule(LogLevel.Debug, LogLevel.Fatal, logTarget);
                    logFactory.Configuration = logConfig;
                    var logger = logFactory.GetLogger("scan-order");
                    var disk = RealDiskProvider.Create(logger);
                    var scanEvents = new List<NzbDrone.Core.MediaFiles.Events.AuthorScannedEvent>();
                    var events = Proxy<IEventAggregator>((_, args) => {
                        if (args[0] is NzbDrone.Core.MediaFiles.Events.AuthorScannedEvent scanned) scanEvents.Add(scanned);
                        return null;
                    });
                    var author = new AuthorRepository(database, events, logger).Insert(new Author
                    {
                        Name = "Test Writer", CleanName = "testwriter", TitleSlug = "test-writer", Path = folder,
                        Added = DateTime.UtcNow
                    });
                    author.AddOptions = new AddAuthorOptions();
                    author.AudiobookPath = folder;
                    author.EbookPath = folder;
                    var books = new BookRepository(database, events);
                    var editions = new EditionRepository(database, events);
                    var fileRows = new MediaFileRepository(database, events);
                    var mediaType = extension == ".epub" ? BookMediaType.Ebook : BookMediaType.Audiobook;
                    var book = books.Insert(new Book
                    {
                        AuthorId = author.Id, Title = "A Book", CleanTitle = "abook", TitleSlug = "a-book",
                        MediaType = mediaType, Added = DateTime.UtcNow, AnyEditionOk = true,
                        AudiobookMonitored = mediaType == BookMediaType.Audiobook, EbookMonitored = mediaType == BookMediaType.Ebook
                    });
                    var edition = editions.Insert(new Edition { BookId = book.Id, Title = book.Title, ForeignEditionId = "hc:edition:701", Monitored = true });
                    var oldPath = Path.Combine(folder, "original" + (oldExtension ?? extension));
                    var newPath = Path.Combine(folder, "renamed" + extension);
                    File.WriteAllText(oldPath, "Disposable unchanged book bytes");
                    BookFile AddFile(string path, int editionId) => fileRows.Insert(new BookFile
                    {
                        Path = path, EditionId = editionId, Size = new FileInfo(path).Length,
                        Modified = File.GetLastWriteTimeUtc(path), DateAdded = DateTime.UtcNow, Quality = new NzbDrone.Core.Qualities.QualityModel(),
                        MediaInfo = new NzbDrone.Core.Parser.Model.MediaInfoModel(),
                        MediaType = mediaType == BookMediaType.Ebook ? "ebook" : "audiobook"
                    });
                    var oldFile = AddFile(oldPath, edition.Id);
                    if (keepOriginal) File.Copy(oldPath, newPath); else File.Move(oldPath, newPath);
                    BookFile otherPart = null;
                    if (multipart)
                    {
                        var otherPath = Path.Combine(folder, "part-two.mp3");
                        File.WriteAllText(otherPath, "Second part");
                        otherPart = AddFile(otherPath, edition.Id);
                    }

                    BookFile colocatedEbook = null;
                    if (mixedRoot)
                    {
                        var ebookBook = books.Insert(new Book
                        {
                            AuthorId = author.Id, Title = book.Title, CleanTitle = book.CleanTitle,
                            TitleSlug = "a-book-ebook", MediaType = BookMediaType.Ebook, Added = DateTime.UtcNow,
                            EbookMonitored = true, AnyEditionOk = true
                        });
                        var ebookEdition = editions.Insert(new Edition
                        {
                            BookId = ebookBook.Id, Title = book.Title, ForeignEditionId = "hc:edition:702", Monitored = true
                        });
                        var ebookPath = Path.Combine(folder, "colocated.epub");
                        var replicaFolder = Directory.CreateDirectory(Path.Combine(folder, "other-audio-copy")).FullName;
                        var replica = Path.Combine(replicaFolder, "colocated.epub");
                        File.WriteAllText(ebookPath, "Keep colocated ebook");
                        File.Copy(ebookPath, replica);
                        colocatedEbook = AddFile(ebookPath, ebookEdition.Id);
                        colocatedEbook.MediaType = "ebook";
                        colocatedEbook.ReplicaPaths = new List<string> { replica };
                        fileRows.Update(colocatedEbook);
                    }

                    var bookService = Proxy<IBookService>((method, args) => method.Name switch
                    {
                        nameof(IBookService.GetBooksByAuthor) => books.GetBooksByAuthorId((int)args[0]),
                        nameof(IBookService.UpdateMany) => UpdateBooks(args),
                        nameof(IBookService.InsertMany) => InsertBooks(args),
                        _ => throw new NotSupportedException(method.Name)
                    });
                    object UpdateBooks(object[] args)
                    {
                        books.UpdateMany((List<Book>)args[0]);
                        return null;
                    }
                    object InsertBooks(object[] args)
                    {
                        books.InsertMany((List<Book>)args[0], (IDbConnection)args[1], (IDbTransaction)args[2]);
                        return null;
                    }
                    var editionService = Proxy<IEditionService>((method, args) => method.Name switch
                    {
                        nameof(IEditionService.GetEditionsByBook) => editions.FindRowsByBook(new[] { (int)args[0] }),
                        nameof(IEditionService.GetEdition) => editions.Get((int)args[0]),
                        nameof(IEditionService.InsertMany) => InsertEditions(args),
                        _ => throw new NotSupportedException(method.Name)
                    });
                    object InsertEditions(object[] args)
                    {
                        editions.InsertMany((List<Edition>)args[0], (IDbConnection)args[1], (IDbTransaction)args[2]);
                        return null;
                    }
                    var published = 0;
                    BookUnitDestinationService destinations = null;
                    var queue = Proxy<IIngestQueueRepository>((method, args) =>
                    {
                        if (method.Name == nameof(IIngestQueueRepository.InsertBatch))
                        {
                            foreach (var item in (List<IngestQueueItem>)args[0])
                            {
                                Assert.That(logTarget.Logs.Any(line => line.Contains("[STAGE] Enumerated") && line.Contains("elapsedMs=")), Is.True);
                                Assert.That(logTarget.Logs.Count(line => line.StartsWith("[CLEANUP] folder=")), Is.EqualTo(cleanupFails ? 0 : mixedRoot ? 2 : 1));
                                published++;
                                // Matching is supplied; exercise cleanup and destination persistence here.
                                var key = multipart
                                    ? destinations.BuildRootUnitKeyWithExtension(item.Path, edition.Title, mediaType)
                                    : BookCoalescingHelper.BuildPerFileUnitKey(item.Path, edition.Title, mediaType);
                                var destination = destinations.ResolveDestinationForUnit(book, edition, key);
                                AddFile(item.Path, destination.EditionId);
                            }
                        }
                        return Default(method.ReturnType);
                    });
                    var media = new MediaFileService(fileRows, events, queue, disk, logger);
                    destinations = new BookUnitDestinationService(media, bookService, editionService, database, logger);
                    IMediaFileTableCleanupService cleanup = cleanupFails
                        ? Proxy<IMediaFileTableCleanupService>((_, _) => throw new InvalidOperationException("Cleanup database read failed"))
                        : new MediaFileTableCleanupService(media, disk, logger);
                    var stage = (ImportOrchestratorV2)RuntimeHelpers.GetUninitializedObject(typeof(ImportOrchestratorV2));
                    Set(stage, "_diskProvider", disk);
                    Set(stage, "_mediaFileRepository", fileRows);
                    Set(stage, "_ingestQueue", queue);
                    Set(stage, "_logger", logger);
                    var stageMethod = typeof(ImportOrchestratorV2).GetMethod("StageFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                    var orchestration = Proxy<IImportOrchestrator>((method, args) => RunStage(args));
                    async Task<OrchestratorImportResult> RunStage(object[] args)
                    {
                        var task = (Task)stageMethod.Invoke(stage, args);
                        await task;
                        var result = task.GetType().GetProperty("Result").GetValue(task);
                        return new OrchestratorImportResult
                        {
                            ScannedFilePaths = (List<string>)result.GetType().GetProperty("SeenFilePaths").GetValue(result),
                            CleanupSafe = (bool)result.GetType().GetProperty("CleanupSafe").GetValue(result)
                        };
                    }
                    var root = new RootFolder { Path = folder, FolderType = mixedRoot ? FolderType.Mixed : mediaType == BookMediaType.Ebook ? FolderType.Ebook : FolderType.Audiobook };
                    var scan = new DiskScanService(null, disk, null, media, null, queue, orchestration,
                        Proxy<IAuthorService>((_, _) => new List<Author> { author }), Proxy<IRootFolderService>((_, _) => root),
                        cleanup, Proxy<IManageCommandQueue>((m, _) => Default(m.ReturnType)), events, logger);
                    scan.Scan(new List<string> { folder });

                    Assert.That(published, Is.EqualTo(1));
                    Assert.That(scanEvents.Single().CanCompleteInitialSetup, Is.EqualTo(!cleanupFails));
                    if (cleanupFails)
                    {
                        Assert.That(logTarget.Logs.Count(line => line.Contains("Scan cleanup failed") && line.Contains("continuing to process 1 observed files")), Is.EqualTo(1));
                    }
                    var imported = fileRows.GetFileWithPath(new List<string> { newPath }).Single();
                    Assert.That(books.All(), Has.Count.EqualTo((keepOriginal || cleanupFails ? 2 : 1) + (mixedRoot ? 1 : 0)));
                    Assert.That(editions.Get(imported.EditionId).BookId, keepOriginal || cleanupFails ? Is.Not.EqualTo(book.Id) : Is.EqualTo(book.Id));
                    Assert.That(File.ReadAllText(newPath), Is.EqualTo("Disposable unchanged book bytes"));
                    Assert.That(fileRows.All().Any(file => file.Id == oldFile.Id), Is.EqualTo(keepOriginal || cleanupFails));
                    if (otherPart != null) Assert.That(fileRows.Get(otherPart.Id).EditionId, Is.EqualTo(edition.Id));
                    if (colocatedEbook != null)
                    {
                        var kept = fileRows.Get(colocatedEbook.Id);
                        Assert.That(kept.EditionId, Is.EqualTo(colocatedEbook.EditionId));
                        Assert.That(kept.ReplicaPaths, Is.EqualTo(colocatedEbook.ReplicaPaths));
                        Assert.That(File.ReadAllText(kept.Path), Is.EqualTo("Keep colocated ebook"));
                        Assert.That(File.ReadAllText(kept.ReplicaPaths.Single()), Is.EqualTo("Keep colocated ebook"));
                        Assert.That(fileRows.All(), Has.Count.EqualTo(2), "The replica must not become a second imported ebook");
                    }
                    var persistedBook = books.Get(book.Id);
                    Assert.That(mediaType == BookMediaType.Ebook ? persistedBook.EbookMonitored : persistedBook.AudiobookMonitored, Is.True);
                    Assert.That(fileRows.GetFilesByBook(book.Id), Has.Count.EqualTo(keepOriginal || !multipart ? 1 : 2));
                }
                finally
                {
                    Directory.Delete(folder, true);
                }
            });
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using Dapper;
using Chaptarr.Api.V1.BookFiles;
using Chaptarr.Api.V1.Books;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Cache;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Extras.Files;
using DryIoc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Configuration;
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
    public class BookDeletionSafetyFixture
    {
        private static IEnumerable<TestCaseData> DeleteCases()
        {
            foreach (var postgres in new[] { false, true })
            {
                foreach (var scope in new[] { "ebook", "both" })
                {
                    foreach (var failure in new[] { "none", "replica", "sidecar", "root", "database" })
                    {
                        yield return new TestCaseData(postgres, scope, failure, false, false, "book", false);
                    }
                }

                yield return new TestCaseData(postgres, "audio", "none", false, false, "book", false);
                foreach (var scope in new[] { "audio", "both" })
                foreach (var recyclingBin in new[] { false, true })
                    yield return new TestCaseData(postgres, scope, "none", true, recyclingBin, "book", false);
                foreach (var route in new[] { "book-editor", "files" })
                foreach (var reverse in new[] { false, true })
                    yield return new TestCaseData(postgres, "both", "none", true, false, route, reverse);
                foreach (var entryPoint in new[] { "book-editor", "file", "files", "author", "cleanup" })
                foreach (var recyclingBin in new[] { false, true })
                    yield return new TestCaseData(postgres, "audio", "none", true, recyclingBin, entryPoint, false);
            }
        }

        [TestCaseSource(nameof(DeleteCases))]
        public void deletion_preserves_inventory_on_failure_and_retries_only_the_selected_files(
            bool postgres, string scope, string failure, bool colocatedPrimary, bool recyclingBin, string entryPoint, bool reverseOrder)
        {
            var bothFormats = scope == "both";
            var deleteAudio = scope != "ebook";
            var deleteEbook = scope != "audio";
            var scratch = Path.Combine(Path.GetTempPath(), "chaptarr-book-deletion-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                DualDatabaseFixture.Run(postgres, database =>
                {
                    var roots = new List<RootFolder>
                    {
                        new RootFolder { Id = 1, Path = Path.Combine(scratch, "audio") },
                        new RootFolder { Id = 2, Path = Path.Combine(scratch, "ebooks") }
                    };
                    if (colocatedPrimary)
                    {
                        roots[0].FolderType = FolderType.Mixed;
                        roots[0].PlaceEbooksWithAudiobooks = true;
                    }
                    var logger = LogManager.GetCurrentClassLogger();
                    var emitted = new List<IEvent>();
                    MetadataFileService extras = null;
                    IDeleteMediaFiles deletion = null;
                    var events = Proxy<IEventAggregator>((_, args) =>
                    {
                        emitted.Add((IEvent)args[0]);
                        if (args[0] is BookDeletedEvent deleted) extras.Handle(deleted);
                        if (args[0] is BookFileDeletedEvent fileDeleted)
                        {
                            extras.Handle(fileDeleted);
                            ((MediaFileDeletionService)deletion).Handle(fileDeleted);
                        }
                        return null;
                    });
                    var author = new AuthorRepository(database, events, logger).Insert(new Author
                    {
                        Name = "Jim Butcher", CleanName = "jimbutcher", TitleSlug = "jim-butcher",
                        Path = Path.Combine(roots[0].Path, "Jim Butcher"),
                        AudiobookPath = Path.Combine(roots[0].Path, "Jim Butcher"),
                        EbookPath = Path.Combine(roots[1].Path, "Jim Butcher"), Added = DateTime.UtcNow
                    });
                    var books = new BookRepository(database, events);
                    var editions = new EditionRepository(database, events);
                    Book AddBook(BookMediaType type) => books.Insert(new Book
                    {
                        AuthorId = author.Id, Title = "Captain's Fury", CleanTitle = "captainsfury",
                        TitleSlug = "captains-fury-" + type, HardcoverBookId = "701", MediaType = type, Added = DateTime.UtcNow
                    });
                    var audio = AddBook(BookMediaType.Audiobook);
                    var ebook = AddBook(BookMediaType.Ebook);
                    var audioEdition = editions.Insert(new Edition { BookId = audio.Id, Title = audio.Title, ForeignEditionId = "hc:edition:801" });
                    var ebookEdition = editions.Insert(new Edition { BookId = ebook.Id, Title = ebook.Title, ForeignEditionId = "hc:edition:802" });
                    var audioFolder = Path.Combine(author.AudiobookPath, "Captains Fury");
                    var ebookFolder = colocatedPrimary ? audioFolder : Path.Combine(author.EbookPath, "Captains Fury");
                    Directory.CreateDirectory(audioFolder);
                    Directory.CreateDirectory(ebookFolder);
                    var fileRows = new MediaFileRepository(database, events);
                    BookFile AddFile(Edition edition, string path, string replica = null)
                    {
                        File.WriteAllText(path, "Disposable book file");
                        if (replica != null) File.WriteAllText(replica, "Disposable managed replica");
                        return fileRows.Insert(new BookFile
                        {
                            EditionId = edition.Id, MediaType = edition.Id == audioEdition.Id ? "audiobook" : "ebook", Path = path, ReplicaPaths = replica == null ? new List<string>() : new List<string> { replica },
                            Quality = new QualityModel(), MediaInfo = new MediaInfoModel(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow
                        });
                    }
                    var audioFile = AddFile(audioEdition, Path.Combine(audioFolder, "Captains Fury.m4b"));
                    var ebookFile = AddFile(ebookEdition, Path.Combine(ebookFolder, "Captains Fury.epub"), colocatedPrimary ? null : Path.Combine(audioFolder, "Captains Fury.epub"));
                    var extraRows = new MetadataFileRepository(database, events);
                    MetadataFile AddCover(Book book, string folder, string fileName = "cover.jpg")
                    {
                        File.WriteAllText(Path.Combine(folder, fileName), "Disposable tracked cover");
                        return extraRows.Insert(new MetadataFile
                        {
                            AuthorId = author.Id, BookId = book.Id, Consumer = "Test", Type = MetadataType.BookImage,
                            RelativePath = Path.Combine("Captains Fury", fileName), Extension = ".jpg",
                            Added = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
                        });
                    }
                    var audioCover = AddCover(audio, audioFolder);
                    var ebookCoverName = colocatedPrimary ? "ebook-cover.jpg" : "cover.jpg";
                    var ebookCover = AddCover(ebook, ebookFolder, ebookCoverName);
                    var unrelated = Path.Combine(audioFolder, "notes.txt");
                    File.WriteAllText(unrelated, "Keep unrelated files");
                    var fileReads = 0;
                    var rootReads = 0;
                    var failureEnabled = true;
                    MediaFileService mediaImplementation = null;
                    var media = Proxy<IMediaFileService>((method, args) =>
                    {
                        if (method.Name == nameof(IMediaFileService.GetFilesByBooks)) fileReads++;
                        try { return method.Invoke(mediaImplementation, args); }
                        catch (TargetInvocationException ex) { throw ex.InnerException; }
                    });
                    var rootService = Proxy<IRootFolderService>((method, args) =>
                    {
                        if (method.Name == nameof(IRootFolderService.All)) { rootReads++; return roots; }
                        Assert.That(method.Name, Is.EqualTo(nameof(IRootFolderService.GetBestRootFolder)));
                        if (entryPoint == "book") Assert.That(args.Length, Is.EqualTo(2), "Reuse the batch root snapshot");
                        return roots.FirstOrDefault(root => root.Path.IsParentPath((string)args[0]));
                    });
                    var realDisk = RealDiskProvider.Create(logger);
                    var disk = Proxy<IDiskProvider>((method, args) =>
                    {
                        if (failureEnabled && failure == "root" && method.Name == nameof(IDiskProvider.FolderExists) &&
                            (string)args[0] == roots[1].Path) return false;
                        try { return method.Invoke(realDisk, args); }
                        catch (TargetInvocationException ex) { throw ex.InnerException; }
                    });
                    mediaImplementation = new MediaFileService(fileRows, events, null, disk, logger, rootService);
                    var attempts = new List<string>();
                    var recycle = Proxy<IRecycleBinProvider>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IRecycleBinProvider.DeleteFile)), "Never delete an entire book directory");
                        var path = (string)args[0];
                        Assert.That(scratch.IsParentPath(path), Is.True);
                        attempts.Add(path);
                        if (failureEnabled && ((failure == "replica" && path == ebookFile.ReplicaPaths.Single()) ||
                            (failure == "sidecar" && path == Path.Combine(ebookFolder, "cover.jpg"))))
                        {
                            throw new IOException("Constructed delete failure: " + path);
                        }
                        if (recyclingBin)
                        {
                            var target = Path.Combine(scratch, "recycle", Path.GetRelativePath(scratch, path));
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            File.Move(path, target);
                        }
                        else File.Delete(path);
                        return null;
                    });
                    var authors = Proxy<IAuthorService>((method, _) => method.Name == nameof(IAuthorService.GetAuthor)
                        ? author : throw new NotSupportedException(method.Name));
                    extras = new MetadataFileService(extraRows, authors, disk, recycle, logger);
                    var config = Proxy<IConfigService>((method, _) => method.Name == "get_DeleteEmptyFolders"
                        ? colocatedPrimary : method.Name == "get_RecycleBin" ? "" : throw new NotSupportedException(method.Name));
                    using var container = new Container(rules => rules.WithNzbDroneRules());
                    container.RegisterInstance<IDiskProvider>(disk);
                    container.RegisterInstance<IRecycleBinProvider>(recycle);
                    container.RegisterInstance<IMediaFileService>(media);
                    container.RegisterInstance<IAuthorRepository>(new AuthorRepository(database, events, logger));
                    container.RegisterInstance<IAuthorService>(authors);
                    container.RegisterInstance<IConfigService>(config);
                    container.RegisterInstance<IEventAggregator>(events);
                    container.RegisterInstance<IRootFolderService>(rootService);
                    container.RegisterInstance<IMetadataFileService>(extras);
                    container.RegisterInstance<IExtraFileRepository<MetadataFile>>(extraRows);
                    container.RegisterInstance<IExtraFileRepository<OtherExtraFile>>(new OtherExtraFileRepository(database, events));
                    container.RegisterInstance<IOtherExtraFileService>(Proxy<IOtherExtraFileService>((_, _) => null));
                    container.RegisterInstance<ICalibreProxy>(Proxy<ICalibreProxy>((_, _) => throw new NotSupportedException()));
                    container.RegisterInstance<IBookRepository>(books);
                    container.RegisterInstance<IEditionService>(new EditionService(editions, events, logger));
                    container.RegisterInstance<ISeriesBookLinkRepository>(new SeriesBookLinkRepository(database, events));
                    container.RegisterInstance<IManageCommandQueue>(Proxy<IManageCommandQueue>((_, _) => throw new NotSupportedException()));
                    container.RegisterInstance<IMultiCopySeriesService>(Proxy<IMultiCopySeriesService>((_, _) => throw new NotSupportedException()));
                    container.RegisterInstance(logger);
                    container.Register<IDeleteMediaFiles, MediaFileDeletionService>();
                    container.Register<IBookService, BookService>();
                    var service = container.Resolve<IBookService>();
                    deletion = container.Resolve<IDeleteMediaFiles>();
                    var preview = new ManualImportReplacementFiles(disk, extraRows,
                        new OtherExtraFileRepository(database, events), new AuthorRepository(database, events, logger), books,
                        Proxy<IConfigService>((method, _) => method.Name == "get_RecycleBin" ? (recyclingBin ? "configured" : "") : null));
                    var fileController = new BookFileController(null, media, deletion, null, authors, service, null, rootService, logger, preview);
                    if (colocatedPrimary && !deleteEbook)
                    {
                        foreach (var request in new[] {
                            new BookFileDeletePreviewRequest { BookIds = new List<int> { audio.Id } },
                            new BookFileDeletePreviewRequest { BookFileIds = new List<int> { audioFile.Id } } })
                        {
                            var listed = fileController.PreviewDeleteBookFiles(request).Files;
                            Assert.That(listed.SelectMany(file => file.AssociatedPaths.Append(file.Path)), Does.Not.Contain(ebookFile.Path));
                            Assert.That(listed.SelectMany(file => file.AssociatedPaths), Does.Contain(Path.Combine(audioFolder, "cover.jpg")));
                        }
                        fileReads = 0; rootReads = 0;
                    }
                    var selectedBooks = new[] { deleteAudio ? audio.Id : 0, deleteEbook ? ebook.Id : 0 }.Where(id => id > 0).ToList();
                    var selectedFiles = new[] { deleteAudio ? audioFile.Id : 0, deleteEbook ? ebookFile.Id : 0 }.Where(id => id > 0).ToList();
                    if (reverseOrder) { selectedBooks.Reverse(); selectedFiles.Reverse(); }
                    var bulkPreview = bothFormats && entryPoint is "book-editor" or "files"
                        ? fileController.PreviewDeleteBookFiles(entryPoint == "book-editor"
                            ? new BookFileDeletePreviewRequest { BookIds = selectedBooks }
                            : new BookFileDeletePreviewRequest { BookFileIds = selectedFiles }).Files
                            .SelectMany(file => file.AssociatedPaths.Append(file.Path)).Distinct().ToArray()
                        : null;
                    using var connection = database.OpenConnection();
                    if (failure == "database")
                    {
                        connection.Execute(postgres
                            ? "CREATE FUNCTION deny_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'constructed persistence failure'; END $$; CREATE TRIGGER deny_delete BEFORE DELETE ON \"Books\" FOR EACH ROW EXECUTE FUNCTION deny_delete();"
                            : "CREATE TRIGGER deny_delete BEFORE DELETE ON Books BEGIN SELECT RAISE(ABORT, 'constructed persistence failure'); END;");
                    }

                    var watch = Stopwatch.StartNew();
                    void Delete()
                    {
                        switch (entryPoint)
                        {
                            case "book-editor":
                                var editor = new BookEditorController(service, null, null)
                                    { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
                                editor.DeleteBook(new BookEditorResource { BookIds = selectedBooks, DeleteFiles = true });
                                break;
                            case "file": fileController.DeleteBookFile(audioFile.Id); break;
                            case "files": fileController.DeleteTrackFiles(new BookFileListResource { BookFileIds = selectedFiles }); break;
                            case "cleanup":
                                File.Delete(audioFile.Path);
                                new MediaFileTableCleanupService(mediaImplementation, disk, logger)
                                    .Clean(audioFolder, new List<string> { ebookFile.Path }, "audiobook");
                                break;
                            case "author":
                                var authorService = new AuthorService(new AuthorRepository(database, events, logger), events, null,
                                    rootService, null, new CacheManager(), books, media, logger, null,
                                    new Lazy<IDeleteMediaFiles>(() => deletion), new Lazy<IBookService>(() => service), database);
                                authorService.DeleteAuthors(new List<int> { author.Id }, true, mediaType: BookMediaType.Audiobook);
                                break;
                            default: service.DeleteBook(scope == "audio" ? audio.Id : ebook.Id, deleteFiles: true, applyToBothFormats: bothFormats); break;
                        }
                    }
                    if (failure != "none")
                    {
                        Assert.Catch(Delete);
                        Assert.That(books.Find(ebook.Id), Is.Not.Null);
                        Assert.That(editions.Find(ebookEdition.Id), Is.Not.Null);
                        Assert.That(fileRows.Find(ebookFile.Id), Is.Not.Null);
                        Assert.That(extraRows.Find(ebookCover.Id), Is.Not.Null);
                        Assert.That(emitted.OfType<BookDeletedEvent>(), Is.Empty);
                        Assert.That(emitted.OfType<DeleteCompletedEvent>(), Is.Empty);
                        if (failure == "root") Assert.That(attempts, Is.Empty);
                        failureEnabled = false;
                        if (failure == "database") connection.Execute(postgres ? "DROP TRIGGER deny_delete ON \"Books\"; DROP FUNCTION deny_delete();" : "DROP TRIGGER deny_delete;");
                    }

                    Delete();
                    Assert.That(books.Find(ebook.Id) == null, Is.EqualTo(deleteEbook && entryPoint is not ("file" or "files" or "cleanup")));
                    Assert.That(editions.Find(ebookEdition.Id) == null, Is.EqualTo(deleteEbook && entryPoint is not ("file" or "files" or "cleanup")));
                    Assert.That(fileRows.Find(ebookFile.Id) == null, Is.EqualTo(deleteEbook));
                    Assert.That(extraRows.Find(ebookCover.Id) == null, Is.EqualTo(deleteEbook));
                    Assert.That(File.Exists(ebookFile.Path), Is.EqualTo(!deleteEbook));
                    if (!colocatedPrimary) Assert.That(File.Exists(ebookFile.ReplicaPaths.Single()), Is.EqualTo(!deleteEbook));
                    Assert.That(File.Exists(Path.Combine(ebookFolder, ebookCoverName)), Is.EqualTo(!deleteEbook));
                    Assert.That(File.Exists(audioFile.Path), Is.EqualTo(!deleteAudio));
                    Assert.That(File.Exists(Path.Combine(audioFolder, "cover.jpg")), Is.EqualTo(!deleteAudio || entryPoint == "cleanup"),
                        "The audiobook cover follows its tracked association; the ebook's own cover stays");
                    Assert.That(books.Find(audio.Id) != null, Is.EqualTo(!deleteAudio || entryPoint is "file" or "files" or "cleanup"));
                    Assert.That(fileRows.Find(audioFile.Id) != null, Is.EqualTo(!deleteAudio));
                    Assert.That(extraRows.Find(audioCover.Id) != null, Is.EqualTo(!deleteAudio || entryPoint == "cleanup"));
                    Assert.That(File.ReadAllText(unrelated), Is.EqualTo("Keep unrelated files"));
                    if (colocatedPrimary && !deleteEbook)
                    {
                        var retained = fileRows.Get(ebookFile.Id);
                        Assert.That(retained.Path, Is.EqualTo(ebookFile.Path));
                        Assert.That(retained.EditionId, Is.EqualTo(ebookEdition.Id));
                        Assert.That(retained.ReplicaPaths, Is.Empty);
                        Assert.That(File.ReadAllText(retained.Path), Is.EqualTo("Disposable book file"));
                        Assert.That(Directory.Exists(audioFolder), Is.True);
                        Assert.That(attempts, Does.Not.Contain(ebookFile.Path));
                        Assert.That(emitted.OfType<BookDeletedEvent>().Any(e => e.Book.Id == ebook.Id), Is.False);
                        Assert.That(emitted.OfType<BookFileDeletedEvent>().Any(e => e.BookFile.Id == ebookFile.Id), Is.False);
                    }
                    Assert.That(emitted.OfType<BookDeletedEvent>().Count(), Is.EqualTo(entryPoint is "file" or "files" or "cleanup" ? 0 : bothFormats ? 2 : 1));
                    Assert.That(emitted.OfType<DeleteCompletedEvent>().Count(), Is.EqualTo(entryPoint == "cleanup" ? 0 :
                        entryPoint is "book-editor" or "files" ? selectedBooks.Count : 1));
                    if (bulkPreview != null)
                        Assert.That(attempts.Distinct(), Is.EquivalentTo(bulkPreview), "The complete bulk selection must delete exactly its previewed inventory in either order");
                    if (entryPoint == "book")
                    {
                        Assert.That(fileReads, Is.EqualTo(failure == "none" ? 1 : 2));
                        Assert.That(rootReads, Is.EqualTo(fileReads));
                    }
                    TestContext.Progress.WriteLine($"Delete safety: postgres={postgres}, scope={scope}, failure={failure}, fileReads={fileReads}, rootReads={rootReads}, elapsedMs={watch.Elapsed.TotalMilliseconds:F2}");
                });
            }
            finally
            {
                Directory.Delete(scratch, true);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class TrackedFileDeletionRetentionFixture
    {
        [Test]
        public void explicit_deletion_keeps_uncertain_files_visible_across_all_routes(
            [Values] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values("author", "book", "file")] string route,
            [Values("exact", "missing", "near", "inaccessible")] string scenario)
        {
            var scratch = Path.Combine(Path.GetTempPath(), "chaptarr-retained-delete-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                DualDatabaseFixture.Run(postgres, database =>
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    BookService bookService = null;
                    var events = Proxy<IEventAggregator>((_, args) =>
                    {
                        if (args[0] is AuthorDeletedEvent authorDeleted) bookService.Handle(authorDeleted);
                        return null;
                    });
                    var authors = new AuthorRepository(database, events, logger);
                    var books = new BookRepository(database, events);
                    var editions = new EditionRepository(database, events);
                    var files = new MediaFileRepository(database, events);
                    var root = new RootFolder { Id = 1, Path = scratch };
                    var authorFolder = Path.Combine(scratch, "Author");
                    Directory.CreateDirectory(authorFolder);
                    File.WriteAllText(Path.Combine(authorFolder, "notes.txt"), "explicit folder contents");
                    var author = authors.Insert(new Author
                    {
                        Name = "Ursula Le Guin", CleanName = "ursulaleguin", TitleSlug = "ursula-le-guin", Path = authorFolder,
                        AudiobookPath = mediaType == BookMediaType.Audiobook ? authorFolder : null,
                        EbookPath = mediaType == BookMediaType.Ebook ? authorFolder : null, Added = DateTime.UtcNow
                    });
                    var book = books.Insert(new Book { AuthorId = author.Id, Title = "Earthsea", CleanTitle = "earthsea",
                        TitleSlug = "earthsea", MediaType = mediaType, Added = DateTime.UtcNow });
                    var edition = editions.Insert(new Edition { BookId = book.Id, Title = book.Title, ForeignEditionId = "gr:13849" });
                    var directory = Path.Combine(scratch, "Scattered");
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, mediaType == BookMediaType.Audiobook ? "Earthsea.m4b" : "Earthsea.epub");
                    var physicalPath = scenario == "near" ? path.Replace("Earthsea", "earthsea") : path;
                    if (scenario != "missing") File.WriteAllText(physicalPath, "keep or explicitly delete this recording");
                    if (scenario == "near" && File.Exists(path))
                    {
                        Assert.Ignore("This filesystem resolves the differently cased path to the same file; the uncertain-neighbor case requires case-sensitive filenames.");
                    }
                    var file = files.Insert(new BookFile { Path = path, EditionId = edition.Id, MediaType = mediaType.ToString().ToLowerInvariant(),
                        Quality = new QualityModel(), MediaInfo = new MediaInfoModel(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow });
                    var realDisk = RealDiskProvider.Create(logger);
                    var disk = Proxy<IDiskProvider>((method, args) =>
                    {
                        if (scenario == "inaccessible" && method.Name == nameof(IDiskProvider.FileExistsCanonical) && (string)args[0] == path)
                            throw new UnauthorizedAccessException("permission denied while reading the selected directory");
                        try { return method.Invoke(realDisk, args); }
                        catch (TargetInvocationException ex) { throw ex.InnerException; }
                    });
                    var roots = Proxy<IRootFolderService>((method, args) => method.Name switch
                    {
                        nameof(IRootFolderService.All) => new List<RootFolder> { root },
                        nameof(IRootFolderService.GetBestRootFolder) => scratch.IsParentPath((string)args[0]) ? root : null,
                        _ => throw new NotImplementedException(method.Name)
                    });
                    var config = Proxy<IConfigService>((method, _) => method.Name switch
                    {
                        "get_DeleteEmptyFolders" => false,
                        "get_RecycleBin" => string.Empty,
                        _ => throw new NotImplementedException(method.Name)
                    });
                    var recycled = new List<string>();
                    var recycle = Proxy<IRecycleBinProvider>((method, args) =>
                    {
                        var target = (string)args[0];
                        Assert.That(scratch.IsParentPath(target), Is.True);
                        recycled.Add(target);
                        if (method.Name == nameof(IRecycleBinProvider.DeleteFolder)) Directory.Delete(target, true);
                        else File.Delete(target);
                        return null;
                    });
                    var media = new MediaFileService(files, events, null, disk, logger, roots);
                    var deletion = new MediaFileDeletionService(disk, recycle, media, authors, config, events, roots, null, logger,
                        EmptyDeletionExtras.Repository<MetadataFile>(), EmptyDeletionExtras.Repository<OtherExtraFile>(),
                        EmptyDeletionExtras.Metadata(), EmptyDeletionExtras.Other());
                    var authorLookup = Proxy<IAuthorService>((method, _) => method.Name == nameof(IAuthorService.GetAuthor) ? author : throw new NotImplementedException(method.Name));
                    bookService = new BookService(books, new EditionService(editions, events, logger), events, authorLookup, media, roots,
                        null, null, logger, deleteMediaFiles: new Lazy<IDeleteMediaFiles>(() => deletion));
                    var authorService = new AuthorService(authors, events, null, roots, null, new CacheManager(), books, media, logger,
                        null, new Lazy<IDeleteMediaFiles>(() => deletion), new Lazy<IBookService>(() => bookService), database);
                    var retained = scenario is "near" or "inaccessible";
                    if (route == "author")
                    {
                        var preview = deletion.PreviewAuthorFiles(new List<Author> { author }, mediaType);
                        Assert.That(preview.Files.Single(item => item.Path == path).Reason != null, Is.EqualTo(retained));
                        Assert.That(files.Get(file.Id).EditionId, Is.EqualTo(edition.Id), "preview never unmaps a file");
                        authorService.DeleteAuthors(new List<int> { author.Id }, true, mediaType: mediaType);
                        Assert.That(authors.Find(author.Id), Is.Null);
                    }
                    else if (route == "book") bookService.DeleteBook(book.Id, true);
                    else deletion.DeleteTrackFile(author, files.GetFilesByBook(book.Id).Single());

                    Assert.That(File.Exists(physicalPath), Is.EqualTo(retained));
                    Assert.That(files.Find(file.Id) != null, Is.EqualTo(retained));
                    if (retained)
                    {
                        Assert.That(files.Get(file.Id).EditionId, Is.Zero);
                        Assert.That(files.Get(file.Id).MatchDetails, Is.Not.Empty);
                        Assert.That(recycled, Does.Not.Contain(path).And.Not.Contain(physicalPath));
                    }
                    if (route != "file") Assert.That(books.Find(book.Id), Is.Null);
                });
            }
            finally { Directory.Delete(scratch, true); }
        }
    }
}

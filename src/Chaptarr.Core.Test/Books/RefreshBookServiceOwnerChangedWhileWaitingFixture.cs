using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshBookServiceOwnerChangedWhileWaitingFixture
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void refresh_should_skip_book_reassigned_while_waiting(bool postgres, bool bulk)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var rootPath = @"C:\Audio".AsOsAgnostic();
                var author = authors.Insert(new Author
                {
                    Name = "Audit Author", CleanName = "auditauthor", TitleSlug = "audit-author",
                    HardcoverAuthorId = "hc:9001", Path = @"C:\Audio\Audit Author".AsOsAgnostic(),
                    AudiobookRootFolderPath = rootPath, Added = DateTime.UtcNow
                });
                var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAuthor) => authors.Get((int)args[0]),
                    nameof(IAuthorService.FindByProviderId) => authors.Get(author.Id),
                    nameof(IAuthorService.GetExistingAuthors) => authors.Get((IEnumerable<int>)args[0]).ToList(),
                    nameof(IAuthorService.GetAuthors) => authors.Get((IEnumerable<int>)args[0]).ToList(),
                    _ => throw new AssertionException(method.Name)
                });
                var root = new RootFolder { Path = rootPath, FolderType = FolderType.Audiobook };
                var roots = InterfaceProxy.Proxy<IRootFolderService>((method, _) => method.Name == nameof(IRootFolderService.All)
                    ? new List<RootFolder> { root } : throw new AssertionException(method.Name));
                Action beforeBatch = null;
                var files = InterfaceProxy.Proxy<IMediaFileService>((method, _) =>
                {
                    if (method.Name == nameof(IMediaFileService.GetFilesByBooks)) beforeBatch?.Invoke();
                    if (method.Name is nameof(IMediaFileService.GetFilesByBooks) or nameof(IMediaFileService.GetFilesByBook))
                        return new List<BookFile>();
                    throw new AssertionException(method.Name);
                });
                var bookRows = new BookRepository(database, events);
                var editionRows = new EditionRepository(database, events);
                var editions = new EditionService(editionRows, events, logger);
                var books = new BookService(bookRows, editions, events, authorService, files, roots,
                    new SeriesBookLinkRepository(database, events), null, logger);
                var source = bookRows.Insert(new Book
                {
                    AuthorId = author.Id, Title = "Audit Work", CleanTitle = "auditwork", TitleSlug = "audit-work",
                    HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook, AnyEditionOk = true,
                    Monitored = true, AudiobookMonitored = true, Added = DateTime.UtcNow,
                    AddOptions = new AddBookOptions { AddType = BookAddType.Automatic }
                });
                var first = editionRows.Insert(new Edition
                {
                    BookId = source.Id, Title = source.Title, TitleSlug = "first", ForeignEditionId = "hc:edition:2001-audiobook",
                    HardcoverEditionId = "2001", ReadingFormatId = 2, Narrator = "Stephen Fry",
                    NarratorNames = new List<string> { "Stephen Fry" }, Monitored = true
                });
                var requested = editionRows.Insert(new Edition
                {
                    BookId = source.Id, Title = source.Title, TitleSlug = "requested", ForeignEditionId = "hc:edition:2002-audiobook",
                    HardcoverEditionId = "2002", ReadingFormatId = 2, Narrator = "Jim Dale",
                    NarratorNames = new List<string> { "Jim Dale" }, Monitored = false
                });
                var refreshEditions = new RefreshEditionService(editions,
                    InterfaceProxy.Proxy<IMetadataTagService>((_, _) => null), null, database, logger);
                var otherAuthor = authors.Insert(new Author
                {
                    Name = "Other Audit Author", CleanName = "otherauditauthor", TitleSlug = "other-audit-author",
                    HardcoverAuthorId = "hc:9002", Path = @"C:\Audio\Other Audit Author".AsOsAgnostic(),
                    AudiobookRootFolderPath = rootPath, Added = DateTime.UtcNow
                });
                var staleLocal = bookRows.Get(source.Id);
                staleLocal.Author = author;
                staleLocal.Editions = editionRows.FindByBook(new[] { source.Id }).ToList();
                staleLocal.BookFiles = new List<BookFile>();
                var remote = RefreshEntityCopy.CloneBook(staleLocal, includeEditions: true, includeSeriesLinks: false);
                remote.Author = author;
                using var refreshAttempt = new ManualResetEventSlim();
                var metadata = InterfaceProxy.Proxy<IProvideBookInfo>((method, _) =>
                {
                    refreshAttempt.Set();
                    return Tuple.Create("hc:9001", remote, new List<Author>());
                });
                var authorMetadata = InterfaceProxy.Proxy<IProvideAuthorInfo>((method, _) => author);
                var refresh = new RefreshBookService(books, authorService, roots, editions, authorMetadata, metadata,
                    refreshEditions, files, null, events, null, new EditionSelector(logger), null, null, logger, mainDatabase: null);
                var writerLock = BookService.GetWantedEditionLock(author.Id);
                Task refreshTask = null;
                Monitor.Enter(writerLock);
                try
                {
                    refreshTask = Task.Run(() =>
                    {
                        if (bulk)
                        {
                            refresh.Execute(new BulkRefreshBookCommand { BookIds = new List<int> { source.Id } });
                        }
                        else
                        {
                            refreshAttempt.Set();
                            refresh.RefreshBookInfo(staleLocal, new List<Book> { remote }, author, false);
                        }
                    });
                    Assert.That(refreshAttempt.Wait(TimeSpan.FromSeconds(15)), Is.True);
                    Assert.That(refreshTask.Wait(TimeSpan.FromMilliseconds(200)), Is.False,
                        "refresh must wait for the existing writer monitor before taking a fresh snapshot");
                    bookRows.SetFields(new Book { Id = source.Id, AuthorId = otherAuthor.Id, Title = "Moved owner work" },
                        book => book.AuthorId, book => book.Title);
                }
                finally
                {
                    Monitor.Exit(writerLock);
                    if (refreshTask != null)
                    {
                        Assert.That(refreshTask.Wait(TimeSpan.FromSeconds(15)), Is.True);
                    }
                }
                var saved = bookRows.Get(source.Id);
                TestContext.WriteLine($"Provider={(postgres ? "PostgreSQL" : "SQLite")}, bulk={bulk}, owner after wait={saved.AuthorId}, expected={otherAuthor.Id}, title={saved.Title}");
                Assert.That(saved.AuthorId, Is.EqualTo(otherAuthor.Id));
                Assert.That(saved.Title, Is.EqualTo("Moved owner work"));
                Assert.That(editionRows.FindByBook(new[] { source.Id }).Count, Is.EqualTo(2));
            });
        }
    }
}

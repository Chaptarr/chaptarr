using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookProviderIdCanonicalizationFixture
    {
        private class BookRepositoryProxy : DispatchProxy
        {
            public Book LastUpsert { get; private set; }
            public List<Book> LastUpdatedMany { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookRepository.Upsert))
                {
                    var book = (Book)args[0];
                    book.Id = book.Id == 0 ? 123 : book.Id;
                    LastUpsert = book;
                    return book;
                }

                if (targetMethod?.Name == nameof(IBookRepository.InsertMany))
                {
                    return null;
                }

                if (targetMethod?.Name == nameof(IBookRepository.UpdateMany))
                {
                    LastUpdatedMany = ((IEnumerable<Book>)args[0]).ToList();
                    return null;
                }

                if (targetMethod?.Name == nameof(IBookRepository.GetBooksByAuthorId))
                {
                    return new List<Book>();
                }

                if (targetMethod?.Name == nameof(IBookRepository.Get))
                {
                    return ((IEnumerable<int>)args[0])
                        .Select(id => new Book { Id = id })
                        .ToList();
                }

                throw new NotImplementedException($"Test proxy does not implement IBookRepository.{targetMethod?.Name}");
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            public List<Edition> Editions { get; private set; } = new List<Edition>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.InsertMany))
                {
                    Editions = ((IEnumerable<Edition>)args[0]).ToList();
                    return null;
                }

                if (targetMethod?.Name == nameof(IEditionService.SetMonitored))
                {
                    return Editions;
                }

                if (targetMethod?.Name == nameof(IEditionService.GetEditionsByBook) &&
                    args?.Length == 1 &&
                    args[0] is int bookId)
                {
                    return Editions.Where(e => e.BookId == bookId).ToList();
                }

                throw new NotImplementedException($"Test proxy does not implement IEditionService.{targetMethod?.Name}");
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthor))
                {
                    return Author;
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }
        }

        private sealed class StubSeriesBookLinkRepository : ISeriesBookLinkRepository
        {
            public List<SeriesBookLink> GetLinksBySeries(int seriesId) => new List<SeriesBookLink>();
            public List<SeriesBookLink> GetLinksBySeriesAndAuthor(int seriesId, string foreignAuthorId) => new List<SeriesBookLink>();
            public List<SeriesBookLink> GetLinksByBook(List<int> bookIds) => new List<SeriesBookLink>();
            public HashSet<int> GetClaimedBookIdsForSeriesIdentity(BookMediaType mediaType, string goodreadsSeriesId) => new HashSet<int>();
            public IEnumerable<SeriesBookLink> All() => throw new NotImplementedException();
            public int Count() => throw new NotImplementedException();
            public SeriesBookLink Find(int id) => throw new NotImplementedException();
            public SeriesBookLink Get(int id) => throw new NotImplementedException();
            public IEnumerable<SeriesBookLink> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public SeriesBookLink Insert(SeriesBookLink model) => throw new NotImplementedException();
            public SeriesBookLink Update(SeriesBookLink model) => throw new NotImplementedException();
            public SeriesBookLink Upsert(SeriesBookLink model) => throw new NotImplementedException();
            public void InsertMany(IList<SeriesBookLink> model) => throw new NotImplementedException();
            public void InsertMany(IList<SeriesBookLink> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<SeriesBookLink> model) => throw new NotImplementedException();
            public void SetFields(SeriesBookLink model, params System.Linq.Expressions.Expression<Func<SeriesBookLink, object>>[] properties) => throw new NotImplementedException();
            public void SetFields(IList<SeriesBookLink> models, params System.Linq.Expressions.Expression<Func<SeriesBookLink, object>>[] properties) => throw new NotImplementedException();
            public void Delete(SeriesBookLink model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void DeleteMany(List<SeriesBookLink> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public SeriesBookLink Single() => throw new NotImplementedException();
            public SeriesBookLink SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<SeriesBookLink> GetPaged(PagingSpec<SeriesBookLink> pagingSpec) => throw new NotImplementedException();
        }

        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
            {
            }
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => new List<BookFile>();
            public List<BookFile> GetFilesByBook(int bookId) => new List<BookFile>();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => new List<BookFile>();
            public List<BookFile> GetFilesByEdition(int editionId) => new List<BookFile>();
            public List<BookFile> GetUnmappedFiles() => new List<BookFile>();
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> path) => throw new NotImplementedException();
            public BookFile GetFileWithPath(string path) => throw new NotImplementedException();
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => new List<BookFile>();
        }

        private sealed class TestableRefreshBookService : RefreshBookService
        {
            public TestableRefreshBookService(Logger logger)
                : base(bookService: null,
                    authorService: null,
                    rootFolderService: null,
                    editionService: null,
                    authorInfo: null,
                    bookInfo: null,
                    refreshEditionService: null,
                    mediaFileService: new StubMediaFileService(),
                    historyService: null,
                    eventAggregator: null,
                    checkIfBookShouldBeRefreshed: null,
                    editionSelector: new EditionSelector(logger),
                    editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                    mediaCoverService: null,
                    logger: logger)
            {
            }

            public string GetPrimaryProviderKeyFor(Book book)
            {
                var method = typeof(RefreshBookService).GetMethod("GetPrimaryProviderKey", BindingFlags.NonPublic | BindingFlags.Instance);
                return (string)method.Invoke(this, new object[] { book });
            }
        }

        private static BookService CreateSubject(out BookRepositoryProxy repoProxy, out EditionServiceProxy editionProxy, out AuthorServiceProxy authorProxy)
        {
            var repo = DispatchProxy.Create<IBookRepository, BookRepositoryProxy>();
            repoProxy = (BookRepositoryProxy)(object)repo;

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            editionProxy = (EditionServiceProxy)(object)editionService;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            authorProxy = (AuthorServiceProxy)(object)authorService;

            return new BookService(
                repo,
                editionService,
                new StubEventAggregator(),
                authorService,
                new StubMediaFileService(),
                rootFolderService: null,
                seriesBookLinkRepository: new StubSeriesBookLinkRepository(),
                multiCopySeriesService: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void add_book_should_canonicalize_work_provider_ids_before_upsert()
        {
            var subject = CreateSubject(out var repoProxy, out var editionProxy, out var authorProxy);
            authorProxy.Author = new Author { Id = 7, Name = "Albert Rutherford" };

            var book = new Book
            {
                Title = "Legacy Book",
                AuthorId = 7,
                GoodreadsWorkId = "72579321",
                HardcoverBookId = "4567",
                OpenLibraryWorkId = "OL12345W",
                BaseBookId = "72579321",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Legacy Book", ForeignEditionId = "gr:12345" }
                }
            };

            subject.AddBook(book, doRefresh: false);

            Assert.That(repoProxy.LastUpsert, Is.Not.Null);
            Assert.That(repoProxy.LastUpsert.HardcoverBookId, Is.EqualTo("hc:4567"));
            Assert.That(repoProxy.LastUpsert.GoodreadsWorkId, Is.EqualTo("gr:72579321"));
            Assert.That(repoProxy.LastUpsert.OpenLibraryWorkId, Is.EqualTo("ol:OL12345W"));
            Assert.That(repoProxy.LastUpsert.BaseBookId, Is.EqualTo("hc:4567"));
            Assert.That(editionProxy.Editions.Select(e => e.BookId).Distinct().Single(), Is.EqualTo(repoProxy.LastUpsert.Id));
        }

        [Test]
        public void update_many_should_canonicalize_raw_provider_ids_and_backfill_base_book_id()
        {
            var subject = CreateSubject(out var repoProxy, out _, out _);

            var books = new List<Book>
            {
                new Book
                {
                    Id = 42,
                    Title = "Legacy Refresh Copy",
                    AuthorId = 7,
                    GoodreadsWorkId = "72579321",
                    BaseBookId = "72579321"
                }
            };

            subject.UpdateMany(books);

            Assert.That(repoProxy.LastUpdatedMany, Has.Count.EqualTo(1));
            Assert.That(repoProxy.LastUpdatedMany[0].GoodreadsWorkId, Is.EqualTo("gr:72579321"));
            Assert.That(repoProxy.LastUpdatedMany[0].BaseBookId, Is.EqualTo("gr:72579321"));
        }

        [Test]
        public void update_many_should_drop_poisoned_provider_ids_without_throwing()
        {
            var subject = CreateSubject(out var repoProxy, out _, out _);

            var books = new List<Book>
            {
                new Book
                {
                    Id = 43,
                    Title = "Poisoned Refresh Copy",
                    AuthorId = 7,
                    HardcoverBookId = "System.Collections.Generic.List`1[System.String]",
                    BaseBookId = "System.Collections.Generic.List`1[System.String]"
                }
            };

            subject.UpdateMany(books);

            Assert.That(repoProxy.LastUpdatedMany, Has.Count.EqualTo(1));
            Assert.That(repoProxy.LastUpdatedMany[0].HardcoverBookId, Is.Null);
            Assert.That(repoProxy.LastUpdatedMany[0].BaseBookId, Is.Null);
        }

        [Test]
        public void refresh_book_service_should_canonicalize_legacy_local_provider_ids_without_throwing()
        {
            var subject = new TestableRefreshBookService(LogManager.GetCurrentClassLogger());

            var key = subject.GetPrimaryProviderKeyFor(new Book
            {
                GoodreadsWorkId = "72579321"
            });

            Assert.That(key, Is.EqualTo("gr:72579321"));
        }

        [Test]
        public void refresh_book_service_should_skip_poisoned_lookup_keys_and_fall_back_to_asin()
        {
            var subject = new TestableRefreshBookService(LogManager.GetCurrentClassLogger());

            var key = subject.GetPrimaryProviderKeyFor(new Book
            {
                Id = 99,
                Title = "Poisoned Local Book",
                RemoteProviderIds = new HashSet<string> { "System.Collections.Generic.List`1[System.String]" },
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        HardcoverEditionId = "System.Collections.Generic.List`1[System.String]",
                        Asin = "b01mtqmk2a"
                    }
                }
            });

            Assert.That(key, Is.EqualTo("az:B01MTQMK2A"));
        }

        [Test]
        public void refresh_book_service_should_ignore_books_with_only_poisoned_lookup_keys()
        {
            var subject = new TestableRefreshBookService(LogManager.GetCurrentClassLogger());

            var key = subject.GetPrimaryProviderKeyFor(new Book
            {
                Id = 100,
                Title = "Only Poison",
                BaseBookId = "System.Collections.Generic.List`1[System.String]",
                RemoteProviderIds = new HashSet<string> { "System.Collections.Generic.List`1[System.String]" },
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        HardcoverEditionId = "System.Collections.Generic.List`1[System.String]"
                    }
                }
            });

            Assert.That(key, Is.Null);
        }
    }
}

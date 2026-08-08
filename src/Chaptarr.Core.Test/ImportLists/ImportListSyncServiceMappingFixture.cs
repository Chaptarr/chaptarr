using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using Chaptarr.Core.Test.Books;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class ImportListSyncServiceMappingFixture
    {
        private class AuthorLookupProxy : DispatchProxy
        {
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthor) &&
                    args?.Length == 1 &&
                    args[0] is int id &&
                    Author != null &&
                    Author.Id == id)
                {
                    return Author;
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }
        }

        private class BookRepositoryLookupProxy : DispatchProxy
        {
            public Book Book { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "Find" &&
                    args?.Length == 1 &&
                    args[0] is int id)
                {
                    return Book != null && Book.Id == id ? Book : null;
                }

                throw new NotImplementedException($"Test proxy does not implement IBookRepository.{targetMethod?.Name}");
            }
        }

        private sealed class StubEditionService : IEditionService
        {
            public Edition GetEdition(int id) => throw new NotImplementedException();
            public List<Edition> GetEditions(IEnumerable<int> ids) => throw new NotImplementedException();
            public Edition GetEditionByForeignEditionId(string foreignEditionId) => null;
            public Edition GetEditionByHardcoverEditionId(string hardcoverEditionId) => null;
            public Edition GetEditionByGoodreadsEditionId(long goodreadsEditionId) => null;
            public Edition GetEditionByGoogleBooksEditionId(string googleBooksEditionId) => null;
            public Edition GetEditionByOpenLibraryEditionId(string openLibraryEditionId) => null;
            public Edition GetEditionByProviderAndId(string providerPrefix, string providerId) => null;
            public System.Collections.Generic.List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId) => new System.Collections.Generic.List<Edition>();
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions) => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
        }

        private sealed class LocalEditionService : IEditionService
        {
            public Edition Edition { get; set; }

            public Edition GetEdition(int id) => throw new NotImplementedException();
            public List<Edition> GetEditions(IEnumerable<int> ids) => throw new NotImplementedException();
            public Edition GetEditionByForeignEditionId(string foreignEditionId) => null;
            public Edition GetEditionByHardcoverEditionId(string hardcoverEditionId) => null;
            public Edition GetEditionByGoodreadsEditionId(long goodreadsEditionId)
            {
                return Edition != null && Edition.GoodreadsEditionId == goodreadsEditionId ? Edition : null;
            }

            public Edition GetEditionByGoogleBooksEditionId(string googleBooksEditionId)
            {
                return Edition != null && Edition.GoogleBooksEditionId == googleBooksEditionId ? Edition : null;
            }

            public Edition GetEditionByOpenLibraryEditionId(string openLibraryEditionId) => null;
            public Edition GetEditionByProviderAndId(string providerPrefix, string providerId) => null;
            public System.Collections.Generic.List<Edition> GetEditionsByProviderAndId(string providerPrefix, string providerId) => new System.Collections.Generic.List<Edition>();
            public List<Edition> GetAllMonitoredEditions() => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions) => throw new NotImplementedException();
            public void InsertMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();
        }

        private sealed class StubBookInfoProxy : IProvideBookInfo
        {
            public Func<string, Tuple<string, Book, List<Author>>> GetEditionInfoFunc { get; set; }

            public Tuple<string, Book, List<Author>> GetBookInfo(string id, BookMediaType mediaType = BookMediaType.Audiobook, string authorHintProviderId = null) => throw new NotImplementedException();

            public Tuple<string, Book, List<Author>> GetWorkInfo(string id, BookMediaType mediaType = BookMediaType.Audiobook, string authorHintProviderId = null) => throw new NotImplementedException();

            public Tuple<string, Book, List<Author>> GetEditionInfo(string id, BookMediaType mediaType = BookMediaType.Audiobook) =>
                GetEditionInfoFunc?.Invoke(id) ?? throw new NotImplementedException();
        }

        private class IdentityCacheRepositoryProxy : DispatchProxy
        {
            public ImportListBookIdentityCache Cache { get; set; }
            public List<ImportListBookIdentityCache> Upserts { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IImportListBookIdentityCacheRepository.FindBySourceProviderId) &&
                    args?.Length == 1 &&
                    args[0] is string sourceProviderId)
                {
                    return Cache != null && string.Equals(Cache.SourceProviderId, sourceProviderId, StringComparison.OrdinalIgnoreCase)
                        ? Cache
                        : null;
                }

                if (targetMethod?.Name == nameof(IImportListBookIdentityCacheRepository.UpsertBySourceProviderId) &&
                    args?.Length == 1 &&
                    args[0] is ImportListBookIdentityCache cache)
                {
                    Upserts.Add(cache);
                    Cache = cache;
                    return cache;
                }

                throw new NotImplementedException($"Test proxy does not implement IImportListBookIdentityCacheRepository.{targetMethod?.Name}");
            }
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private static ImportListSyncService BuildService(IProvideBookInfo bookInfoProxy,
            IEditionService editionService,
            IImportListBookIdentityCacheRepository identityCacheRepository = null)
        {
            return new ImportListSyncService(
                importListFactory: DispatchProxy.Create<IImportListFactory, ThrowingProxy<IImportListFactory>>(),
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ThrowingProxy<IImportListExclusionService>>(),
                listFetcherAndParser: DispatchProxy.Create<IFetchAndParseImportList, ThrowingProxy<IFetchAndParseImportList>>(),
                bookInfoProxy: bookInfoProxy,
                authorService: DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                bookService: DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                bookRepository: DispatchProxy.Create<IBookRepository, ThrowingProxy<IBookRepository>>(),
                editionService: editionService,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()),
                authorLibraryService: DispatchProxy.Create<IAuthorLibraryService, ThrowingProxy<IAuthorLibraryService>>(),
                pendingAuthorImportService: DispatchProxy.Create<IPendingAuthorImportService, ThrowingProxy<IPendingAuthorImportService>>(),
                rootFolderService: DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                rootFolderSettingsResolver: DispatchProxy.Create<IRootFolderSettingsResolver, ThrowingProxy<IRootFolderSettingsResolver>>(),
                eventAggregator: DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                commandQueueManager: DispatchProxy.Create<IManageCommandQueue, ThrowingProxy<IManageCommandQueue>>(),
                logger: LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: identityCacheRepository ?? DispatchProxy.Create<IImportListBookIdentityCacheRepository, IdentityCacheRepositoryProxy>());
        }

        private static void MapBookReport(ImportListSyncService service, ImportListItemInfo report)
        {
            var method = typeof(ImportListSyncService).GetMethod("MapBookReport", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new InvalidOperationException("Could not find ImportListSyncService.MapBookReport via reflection");
            }

            method.Invoke(service, new object[] { report });
        }

        [Test]
        public void should_map_goodreads_bookshelf_book_id_via_metadata_server()
        {
            var author = new Author
            {
                Name = "Mapped Author",
                GoodreadsAuthorId = "gr:789"
            };

            var book = new Book
            {
                Title = "Mapped Book",
                GoodreadsWorkId = "gr:123",
                GoodreadsBookId = "gr:456",
                Author = author
            };

            var bookInfoProxy = new StubBookInfoProxy
            {
                GetEditionInfoFunc = id =>
                {
                    Assert.That(id, Is.EqualTo("gr:456"));
                    return Tuple.Create(id, book, new List<Author> { author });
                }
            };

            var identityCacheRepository = DispatchProxy.Create<IImportListBookIdentityCacheRepository, IdentityCacheRepositoryProxy>();
            var service = BuildService(bookInfoProxy, new StubEditionService(), identityCacheRepository);

            var report = new ImportListItemInfo
            {
                Book = "Test Book",
                Author = null,
                EditionGoodreadsId = "456"
            };

            MapBookReport(service, report);

            Assert.That(report.EditionGoodreadsId, Is.EqualTo("gr:456"));
            Assert.That(report.BookGoodreadsId, Is.EqualTo("gr:123"));
            Assert.That(report.AuthorGoodreadsId, Is.EqualTo("gr:789"));
            Assert.That(report.Book, Is.EqualTo("Mapped Book"));
            Assert.That(report.Author, Is.EqualTo("Mapped Author"));

            var upsert = ((IdentityCacheRepositoryProxy)(object)identityCacheRepository).Upserts.Single();
            Assert.That(upsert.SourceProviderId, Is.EqualTo("gr:456"));
            Assert.That(upsert.BookProviderId, Is.EqualTo("gr:123"));
            Assert.That(upsert.AuthorProviderId, Is.EqualTo("gr:789"));
        }

        [Test]
        public void should_map_goodreads_bookshelf_book_id_from_identity_cache()
        {
            var identityCacheRepository = DispatchProxy.Create<IImportListBookIdentityCacheRepository, IdentityCacheRepositoryProxy>();
            ((IdentityCacheRepositoryProxy)(object)identityCacheRepository).Cache = new ImportListBookIdentityCache
            {
                SourceProviderId = "gr:456",
                BookProviderId = "gr:123",
                AuthorProviderId = "gr:789",
                Book = "Cached Book",
                Author = "Cached Author"
            };

            var service = BuildService(
                DispatchProxy.Create<IProvideBookInfo, ThrowingProxy<IProvideBookInfo>>(),
                new StubEditionService(),
                identityCacheRepository);

            var report = new ImportListItemInfo
            {
                Book = null,
                Author = null,
                EditionGoodreadsId = "456"
            };

            MapBookReport(service, report);

            Assert.That(report.EditionGoodreadsId, Is.EqualTo("gr:456"));
            Assert.That(report.BookGoodreadsId, Is.EqualTo("gr:123"));
            Assert.That(report.AuthorGoodreadsId, Is.EqualTo("gr:789"));
            Assert.That(report.Book, Is.EqualTo("Cached Book"));
            Assert.That(report.Author, Is.EqualTo("Cached Author"));
        }

        [Test]
        public void should_not_throw_when_metadata_server_cannot_map_edition_id()
        {
            var bookInfoProxy = new StubBookInfoProxy
            {
                GetEditionInfoFunc = _ => throw new NzbDrone.Core.Exceptions.BookNotFoundException("gr:456")
            };

            var service = BuildService(bookInfoProxy, new StubEditionService());

            var report = new ImportListItemInfo
            {
                Book = "Test Book",
                Author = "Test Author",
                EditionGoodreadsId = "456"
            };

            Assert.DoesNotThrow(() => MapBookReport(service, report));
            Assert.That(report.EditionGoodreadsId, Is.EqualTo("gr:456"));
            Assert.That(report.BookGoodreadsId, Is.Null);
            Assert.That(report.AuthorGoodreadsId, Is.Null);
        }

        [Test]
        public void should_map_goodreads_edition_id_from_local_library_without_metadata_server()
        {
            var author = new Author
            {
                Id = 42,
                Name = "Local Author",
                GoodreadsAuthorId = "gr:789"
            };

            var book = new Book
            {
                Id = 100,
                Title = "Local Book",
                AuthorId = author.Id,
                GoodreadsWorkId = "gr:123",
                GoodreadsBookId = "gr:456"
            };

            var edition = new Edition
            {
                BookId = book.Id,
                Title = "Local Edition",
                GoodreadsEditionId = 456
            };

            var editionService = new LocalEditionService { Edition = edition };

            var authorService = DispatchProxy.Create<IAuthorService, AuthorLookupProxy>();
            ((AuthorLookupProxy)(object)authorService).Author = author;

            var bookRepository = DispatchProxy.Create<IBookRepository, BookRepositoryLookupProxy>();
            ((BookRepositoryLookupProxy)(object)bookRepository).Book = book;

            var service = new ImportListSyncService(
                importListFactory: DispatchProxy.Create<IImportListFactory, ThrowingProxy<IImportListFactory>>(),
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ThrowingProxy<IImportListExclusionService>>(),
                listFetcherAndParser: DispatchProxy.Create<IFetchAndParseImportList, ThrowingProxy<IFetchAndParseImportList>>(),
                bookInfoProxy: DispatchProxy.Create<IProvideBookInfo, ThrowingProxy<IProvideBookInfo>>(),
                authorService: authorService,
                bookService: DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                bookRepository: bookRepository,
                editionService: editionService,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()),
                authorLibraryService: DispatchProxy.Create<IAuthorLibraryService, ThrowingProxy<IAuthorLibraryService>>(),
                pendingAuthorImportService: DispatchProxy.Create<IPendingAuthorImportService, ThrowingProxy<IPendingAuthorImportService>>(),
                rootFolderService: DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                rootFolderSettingsResolver: DispatchProxy.Create<IRootFolderSettingsResolver, ThrowingProxy<IRootFolderSettingsResolver>>(),
                eventAggregator: DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                commandQueueManager: DispatchProxy.Create<IManageCommandQueue, ThrowingProxy<IManageCommandQueue>>(),
                logger: LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: DispatchProxy.Create<IImportListBookIdentityCacheRepository, IdentityCacheRepositoryProxy>());

            var report = new ImportListItemInfo
            {
                EditionGoodreadsId = "gr:456",
                Book = null,
                Author = null
            };

            MapBookReport(service, report);

            Assert.That(report.BookGoodreadsId, Is.EqualTo("gr:123"));
            Assert.That(report.AuthorGoodreadsId, Is.EqualTo("gr:789"));
            Assert.That(report.Book, Is.EqualTo("Local Edition"));
            Assert.That(report.Author, Is.EqualTo("Local Author"));
        }

        [Test]
        public void should_map_hardcover_edition_id_without_forcing_goodreads_ids()
        {
            var author = new Author
            {
                Name = "Mapped Author",
                HardcoverAuthorId = "hc:789"
            };

            var book = new Book
            {
                Title = "Mapped Book",
                HardcoverBookId = "hc:123",
                Author = author
            };

            var bookInfoProxy = new StubBookInfoProxy
            {
                GetEditionInfoFunc = id =>
                {
                    Assert.That(id, Is.EqualTo("hc-ed:456"));
                    return Tuple.Create(id, book, new List<Author> { author });
                }
            };

            var service = BuildService(bookInfoProxy, new StubEditionService());

            var report = new ImportListItemInfo
            {
                Book = "Test Book",
                Author = null,
                EditionProviderId = "hc-ed:456"
            };

            MapBookReport(service, report);

            Assert.That(report.EditionProviderId, Is.EqualTo("hc-ed:456"));
            Assert.That(report.BookProviderId, Is.EqualTo("hc:123"));
            Assert.That(report.AuthorProviderId, Is.EqualTo("hc:789"));
            Assert.That(report.Book, Is.EqualTo("Mapped Book"));
            Assert.That(report.Author, Is.EqualTo("Mapped Author"));
        }
    }
}

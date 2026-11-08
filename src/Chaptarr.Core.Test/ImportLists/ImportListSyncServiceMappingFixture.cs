using System;
using NzbDrone.Core.MediaFiles;
using Chaptarr.Core.Test.Common;
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
            public void UpdateMany(List<Edition> editions, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Edition selectedEdition = null) => throw new System.NotSupportedException();
            public void UpdateMany(List<Edition> editions) => throw new NotImplementedException();
            public void DeleteMany(List<Edition> editions) => throw new NotImplementedException();
            public List<Edition> GetEditionsForRefresh(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(int bookId) => throw new NotImplementedException();
            public List<Edition> GetEditionsByBook(IEnumerable<int> bookIds) => throw new NotImplementedException();
            public List<Edition> GetEditionsByAuthor(int authorId, bool onlyMonitored = false) => throw new NotImplementedException();
            public Edition FindByTitle(int authorId, string title) => throw new NotImplementedException();
            public Edition FindByTitleInexact(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> GetCandidates(int authorId, string title) => throw new NotImplementedException();
            public List<Edition> SetMonitored(Edition edition, bool isManualSelection = false) => throw new NotImplementedException();

            public void RefreshProviderAliases(List<Edition> editions) { }
            public void UpdateSelection(List<Edition> editions) => UpdateMany(editions);
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
                editionService: editionService,
                pendingAuthorImportService: DispatchProxy.Create<IPendingAuthorImportService, ThrowingProxy<IPendingAuthorImportService>>(),
                rootFolderService: DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                rootFolderSettingsResolver: DispatchProxy.Create<IRootFolderSettingsResolver, ThrowingProxy<IRootFolderSettingsResolver>>(),
                eventAggregator: DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                commandQueueManager: DispatchProxy.Create<IManageCommandQueue, ThrowingProxy<IManageCommandQueue>>(),
                logger: LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: identityCacheRepository ?? DispatchProxy.Create<IImportListBookIdentityCacheRepository, IdentityCacheRepositoryProxy>());
        }

        private static void MapBookReport(ImportListSyncService service, ImportListItemInfo report, Book book = null, Author author = null)
        {
            var method = typeof(ImportListSyncService).GetMethod("MapBookReportWithMappings", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
            {
                throw new InvalidOperationException("Could not find ImportListSyncService.MapBookReportWithMappings via reflection");
            }

            var lookupType = typeof(ImportListSyncService).GetNestedType("ImportListLocalLookup", BindingFlags.NonPublic);
            var lookup = Activator.CreateInstance(lookupType, nonPublic: true);
            lookupType.GetMethod("AddAuthor").Invoke(lookup, new object[] { author });
            lookupType.GetMethod("AddBook").Invoke(lookup, new object[] { book });
            var stats = Activator.CreateInstance(typeof(ImportListSyncService).GetNestedType("ImportListSyncStats", BindingFlags.NonPublic), true);
            var args = new object[] { report, lookup, null, stats };
            method.Invoke(service, method.GetParameters().Length == 5 ? args.Append(null).ToArray() : args);
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
                Editions = new List<Edition> { new() { GoodreadsEditionId = 456 } },
                Author = author
            };

            var bookInfoProxy = new StubBookInfoProxy
            {
                GetEditionInfoFunc = id =>
                {
                    Assert.That(id, Is.EqualTo("gr:456"));
                    return Tuple.Create(author.GoodreadsAuthorId, book, new List<Author> { author });
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
            Assert.That(upsert.SourceProviderId, Is.EqualTo("edition:gr:456"));
            Assert.That(upsert.BookProviderId, Is.EqualTo("gr:123"));
            Assert.That(upsert.AuthorProviderId, Is.EqualTo("gr:789"));
        }

        [Test]
        public void should_recheck_a_cached_pair_without_local_primary_ownership()
        {
            var identityCacheRepository = DispatchProxy.Create<IImportListBookIdentityCacheRepository, IdentityCacheRepositoryProxy>();
            ((IdentityCacheRepositoryProxy)(object)identityCacheRepository).Cache = new ImportListBookIdentityCache
            {
                SourceProviderId = "edition:gr:456",
                BookProviderId = "gr:123",
                AuthorProviderId = "gr:789",
                Book = "Cached Book",
                Author = "Cached Author",
                EditionHasNarration = false
            };

            var service = BuildService(
                new StubBookInfoProxy
                {
                    GetEditionInfoFunc = id => Tuple.Create("gr:321",
                        new Book { Title = "Verified Book", GoodreadsWorkId = "gr:123",
                            Editions = new List<Edition> { new() { GoodreadsEditionId = 456 } } },
                        new List<Author> { new() { GoodreadsAuthorId = "gr:321", Name = "Verified Primary" } })
                },
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
            Assert.That(report.AuthorGoodreadsId, Is.EqualTo("gr:321"));
            Assert.That(report.Book, Is.EqualTo("Verified Book"));
            Assert.That(report.Author, Is.EqualTo("Verified Primary"));
            Assert.That(((IdentityCacheRepositoryProxy)(object)identityCacheRepository).Upserts.Single().AuthorProviderId, Is.EqualTo("gr:321"));
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

            book.Editions = new List<Edition> { edition };
            var service = BuildService(DispatchProxy.Create<IProvideBookInfo, ThrowingProxy<IProvideBookInfo>>(), new StubEditionService());

            var report = new ImportListItemInfo
            {
                EditionGoodreadsId = "gr:456",
                Book = null,
                Author = null
            };

            MapBookReport(service, report, book, author);

            Assert.That(report.BookGoodreadsId, Is.EqualTo("gr:123"));
            Assert.That(report.AuthorGoodreadsId, Is.EqualTo("gr:789"));
            Assert.That(report.Book, Is.EqualTo("Local Book"));
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
                Editions = new List<Edition> { new() { HardcoverEditionId = "hc:456" } },
                Author = author
            };

            var bookInfoProxy = new StubBookInfoProxy
            {
                GetEditionInfoFunc = id =>
                {
                    Assert.That(id, Is.EqualTo("hc-ed:456"));
                    return Tuple.Create(author.HardcoverAuthorId, book, new List<Author> { author });
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
        [Test]
        public void primary_mapping_should_retain_local_owner_proof_when_cached_narration_is_incomplete_and_source_pending()
        {
            var author = new Author { Id = 42, GoodreadsAuthorId = "gr:789", Name = "Local Primary" };
            var book = new Book { Id = 100, AuthorId = author.Id, GoodreadsWorkId = "gr:123", Title = "Known Work", Editions = new List<Edition>() };
            var cache = DispatchProxy.Create<IImportListBookIdentityCacheRepository, IdentityCacheRepositoryProxy>();
            ((IdentityCacheRepositoryProxy)(object)cache).Cache = new ImportListBookIdentityCache
            {
                SourceProviderId = "edition:gr:456", BookProviderId = "gr:123", AuthorProviderId = "gr:789",
                EditionHasNarration = true, UpdatedAt = DateTime.UtcNow
            };
            var calls = 0;
            var metadata = new StubBookInfoProxy
            {
                GetEditionInfoFunc = _ =>
                {
                    calls++;
                    throw new NzbDrone.Core.Exceptions.BookNotFoundException("gr:456");
                }
            };
            var report = new ImportListItemInfo { EditionProviderId = "gr:456", AuthorProviderId = "gr:987", Book = "Known Work" };
            MapBookReport(BuildService(metadata, new StubEditionService(), cache), report, book, author);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(report.BookProviderId, Is.EqualTo("gr:123"));
            Assert.That(report.AuthorProviderId, Is.EqualTo(author.GoodreadsAuthorId));
            Assert.That(report.Author, Is.EqualTo(author.Name));
            Assert.That(((IdentityCacheRepositoryProxy)(object)cache).Upserts, Is.Empty);
        }

        [Test]
        public void primary_mapping_should_prefer_identified_book_primary_over_secondary_tuple_credit()
        {
            var primary = new Author { HardcoverAuthorId = "hc:1111", Name = "Selected Primary" };
            var contributor = new Author { HardcoverAuthorId = "hc:2222", Name = "Secondary Contributor" };
            var book = new Book { HardcoverBookId = "hc:3333", Author = primary,
                Editions = new List<Edition> { new() { HardcoverEditionId = "hc:4444" } } };
            var metadata = new StubBookInfoProxy { GetEditionInfoFunc = id => Tuple.Create(primary.HardcoverAuthorId, book, new List<Author> { contributor }) };
            var report = new ImportListItemInfo { EditionProviderId = "hc-ed:4444", AuthorProviderId = contributor.HardcoverAuthorId };
            MapBookReport(BuildService(metadata, new StubEditionService()), report);
            Assert.That(report.BookProviderId, Is.EqualTo("hc:3333"));
            Assert.That(report.AuthorProviderId, Is.EqualTo(primary.HardcoverAuthorId));
            Assert.That(report.Author, Is.EqualTo(primary.Name));
        }

    }
}

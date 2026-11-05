using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.Hardcover;
using Moq;
using SearchJsonResource = NzbDrone.Core.MetadataSource.Goodreads.SearchJsonResource;
using IGoodreadsSearchProxy = NzbDrone.Core.MetadataSource.Goodreads.IGoodreadsSearchProxy;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class BookInfoProxyHardcoverSearchCacheFixture
    {
        private sealed class RecordingHardcoverSearchClient : IHardcoverSearchClient
        {
            private readonly Func<List<object>> _handler;

            public RecordingHardcoverSearchClient(Func<List<object>> handler)
            {
                _handler = handler;
            }

            public int CallCount { get; private set; }
            public bool LastInteractiveSearch { get; private set; }

            public List<object> Search(string query, bool interactiveSearch = false)
            {
                CallCount++;
                LastInteractiveSearch = interactiveSearch;
                return _handler();
            }
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            public bool HardcoverEnabled { get; set; } = true;
            public string HardcoverApiToken { get; set; } = "token-a";

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "IsDefined")
                {
                    return false;
                }

                return targetMethod?.Name switch
                {
                    "get_HardcoverEnabled" => HardcoverEnabled,
                    "get_HardcoverApiToken" => HardcoverApiToken,
                    "get_MetadataServerUrl" => "http://metadata",
                    _ when targetMethod?.Name.StartsWith("get_", StringComparison.Ordinal) == true =>
                        targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null,
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }
        }

        private sealed class NativeHardcoverMetadataProxy : IHardcoverMetadataProxy
        {
            public bool IsConfigured => true;
            public bool IsNativeEnabled => true;
            public int WorkLookups { get; private set; }
            public int EditionLookups { get; private set; }
            public bool SearchWasInteractive { get; private set; }
            public bool WorkWasInteractive { get; private set; }
            public bool EditionWasInteractive { get; private set; }
            public bool AuthorWasInteractive { get; private set; }

            public List<SearchJsonResource> Search(string query, bool interactiveSearch = false)
            {
                SearchWasInteractive = interactiveSearch;
                return new List<SearchJsonResource>
                {
                    new SearchJsonResource { WorkId = 101, BookId = 201, Title = "Native Book" }
                };
            }

            public Newtonsoft.Json.Linq.JObject ExecuteSearchGraphQl(
                string operationName,
                string query,
                Newtonsoft.Json.Linq.JObject variables,
                bool interactiveSearch = false)
            {
                throw new NotSupportedException();
            }

            public AuthorResource GetAuthor(string foreignAuthorId, bool interactiveSearch = false)
            {
                AuthorWasInteractive = interactiveSearch;
                return NativeAuthor();
            }

            public WorkResource GetWork(string foreignWorkId, bool interactiveSearch = false)
            {
                WorkLookups++;
                WorkWasInteractive = interactiveSearch;
                return NativeWork();
            }

            public WorkResource GetEdition(string foreignEditionId, bool interactiveSearch = false)
            {
                EditionLookups++;
                EditionWasInteractive = interactiveSearch;
                return NativeWork();
            }

            private static AuthorResource NativeAuthor() => new()
            {
                ForeignId = 42,
                Name = "Native Author",
                Works = new List<WorkResource> { NativeWork() },
                Series = new List<SeriesResource>()
            };

            private static WorkResource NativeWork() => new()
            {
                ForeignId = 101,
                Title = "Native Book",
                Authors = new List<AuthorResource>
                {
                    new() { ForeignId = 42, Name = "Native Author" }
                },
                Books = new List<BookResource>
                {
                    new()
                    {
                        ForeignId = 201,
                        Title = "Native Book Edition",
                        Isbn13 = "9780000000001",
                        Format = "Audiobook",
                        NarratorNames = new List<string> { "Voice Example" },
                        Contributors = new List<ContributorResource> { new() { ForeignId = 42 } }
                    }
                },
                Series = new List<SeriesResource>()
            };
        }

        private static (BookInfoProxy Proxy, ConfigServiceProxy Config) CreateProxy(
            IHardcoverSearchClient hardcoverSearchClient,
            IHardcoverMetadataProxy hardcoverMetadataProxy = null,
            IAdditionalBookMetadataProxy additionalBookMetadataProxy = null,
            IGoodreadsSearchProxy goodreadsSearchProxy = null)
        {
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var config = (ConfigServiceProxy)configService;
            var logger = LogManager.GetCurrentClassLogger();
            var healthGate = new MetadataServerHealthGate(
                configService,
                new MetadataServerHealthService(logger),
                logger);
            if (additionalBookMetadataProxy == null)
            {
                var additionalMetadataProxy = new Mock<IAdditionalBookMetadataProxy>();
                additionalMetadataProxy.Setup(x => x.ApplyFieldSourcePreferences(It.IsAny<Book>()))
                    .Returns<Book>(book => book);
                additionalMetadataProxy.Setup(x => x.ApplyAuthorImageSourcePreference(It.IsAny<Author>(), It.IsAny<Book>()))
                    .Returns<Author, Book>((author, _) => author);
                additionalBookMetadataProxy = additionalMetadataProxy.Object;
            }

            var proxy = new BookInfoProxy(
                httpClient: null,
                cachedHttpClient: null,
                goodreadsSearchProxy: goodreadsSearchProxy,
                hardcoverSearchClient: hardcoverSearchClient,
                audibleCatalogProxy: null,
                authorService: Mock.Of<IAuthorService>(service => service.FindByProviderId(It.IsAny<string>(), It.IsAny<string>()) == null),
                bookService: Mock.Of<IBookService>(service => service.FindByProviderId(It.IsAny<string>(), It.IsAny<string>()) == null),
                editionService: Mock.Of<IEditionService>(),
                configService: configService,
                requestBuilder: new MetadataRequestBuilder(configService),
                logger: logger,
                cacheManager: new CacheManager(),
                metadataServerHealthGate: healthGate,
                additionalBookMetadataProxy: additionalBookMetadataProxy,
                hardcoverMetadataProxy: hardcoverMetadataProxy);

            return (proxy, config);
        }

        [Test]
        public void should_reuse_a_successful_search_for_the_normalized_term()
        {
            var client = new RecordingHardcoverSearchClient(() => new List<object>
            {
                new HardcoverAuthorResult
                {
                    Id = "123",
                    Name = "Matt Dinniman",
                    AlternateNames = Array.Empty<string>()
                }
            });
            var (proxy, _) = CreateProxy(client);

            Assert.That(proxy.SearchForNewEntity(" Matt Dinniman ", "hardcover"), Has.Count.EqualTo(1));
            Assert.That(proxy.SearchForNewEntity("matt dinniman", "hardcover"), Has.Count.EqualTo(1));

            Assert.That(client.CallCount, Is.EqualTo(1));
            Assert.That(client.LastInteractiveSearch, Is.True);
        }

        [Test]
        public void should_not_cache_an_empty_provider_result()
        {
            var client = new RecordingHardcoverSearchClient(() => new List<object>());
            var (proxy, _) = CreateProxy(client);

            Assert.That(proxy.SearchForNewEntity("matt dinniman", "hardcover"), Is.Empty);
            Assert.That(proxy.SearchForNewEntity("matt dinniman", "hardcover"), Is.Empty);

            Assert.That(client.CallCount, Is.EqualTo(2));
        }

        [Test]
        public void should_not_cache_a_provider_failure()
        {
            var client = new RecordingHardcoverSearchClient(() => null);
            var (proxy, _) = CreateProxy(client);

            Assert.Throws<NzbDroneClientException>(() => proxy.SearchForNewEntity("matt dinniman", "hardcover"));
            Assert.Throws<NzbDroneClientException>(() => proxy.SearchForNewEntity("matt dinniman", "hardcover"));

            Assert.That(client.CallCount, Is.EqualTo(2));
        }

        [Test]
        public void should_not_reuse_a_search_after_the_hardcover_token_changes()
        {
            var client = new RecordingHardcoverSearchClient(() => new List<object>());
            var (proxy, config) = CreateProxy(client);

            Assert.That(proxy.SearchForNewEntity("matt dinniman", "hardcover"), Is.Empty);
            config.HardcoverApiToken = "token-b";
            Assert.That(proxy.SearchForNewEntity("matt dinniman", "hardcover"), Is.Empty);

            Assert.That(client.CallCount, Is.EqualTo(2));
        }

        [Test]
        public void should_not_serve_a_cached_search_after_hardcover_is_disabled()
        {
            var client = new RecordingHardcoverSearchClient(() => new List<object>());
            var (proxy, config) = CreateProxy(client);

            Assert.That(proxy.SearchForNewEntity("matt dinniman", "hardcover"), Is.Empty);
            config.HardcoverEnabled = false;

            Assert.Throws<NzbDroneClientException>(() => proxy.SearchForNewEntity("matt dinniman", "hardcover"));
            Assert.That(client.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void should_preserve_hardcover_anchor_order_through_domain_mapping_and_filtering()
        {
            var client = new RecordingHardcoverSearchClient(() => new List<object>
            {
                new HardcoverBookResult
                {
                    Id = "383236",
                    Title = "Harry Potter and the Goblet of Fire",
                    AuthorNames = new[] { "J.K. Rowling" },
                    AuthorIds = new[] { "80626" },
                    SeriesIds = new[] { "1185" },
                    SeriesNames = new[] { "Harry Potter" },
                    Isbns = Array.Empty<string>()
                },
                new HardcoverAuthorResult
                {
                    Id = "80626",
                    Name = "J.K. Rowling",
                    AlternateNames = Array.Empty<string>()
                },
                new HardcoverSeriesResult
                {
                    Id = "1185",
                    Name = "Harry Potter",
                    AuthorId = "80626",
                    AuthorName = "J.K. Rowling",
                    BooksCount = 7
                }
            });
            var (proxy, _) = CreateProxy(client);

            var results = proxy.SearchForNewEntity("Harry Potter goblet of fire", "hardcover");

            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(results[0], Is.TypeOf<Book>());
            Assert.That(((Book)results[0]).HardcoverBookId, Is.EqualTo("hc:383236"));
            Assert.That(results[1], Is.TypeOf<Author>());
            Assert.That(((Author)results[1]).HardcoverAuthorId, Is.EqualTo("hc:80626"));
            Assert.That(results[2], Is.TypeOf<Series>());
            Assert.That(((Series)results[2]).HardcoverSeriesId, Is.EqualTo("hc:1185"));
        }

        [Test]
        public void should_resolve_hardcover_work_ids_through_the_native_provider_with_provider_identity()
        {
            var nativeProvider = new NativeHardcoverMetadataProxy();
            var (proxy, _) = CreateProxy(new RecordingHardcoverSearchClient(() => new List<object>()), nativeProvider);

            var result = proxy.GetBookInfo("hc:101", BookMediaType.Ebook);

            Assert.Multiple(() =>
            {
                Assert.That(nativeProvider.WorkLookups, Is.EqualTo(1));
                Assert.That(result.Item2.HardcoverBookId, Is.EqualTo("hc:101"));
                Assert.That(result.Item2.MediaType, Is.EqualTo(BookMediaType.Ebook));
                Assert.That(result.Item2.Editions, Has.Count.EqualTo(1));
                Assert.That(result.Item2.Editions[0].HardcoverEditionId, Is.EqualTo("201"));
                Assert.That(result.Item2.Editions[0].ForeignEditionId, Is.EqualTo("hc:edition:201"));
                Assert.That(result.Item2.Editions[0].NarratorNames, Is.EqualTo(new[] { "Voice Example" }));
                Assert.That(result.Item2.Author.HardcoverAuthorId, Is.EqualTo("hc:42"));
            });
        }

        [Test]
        public void should_resolve_legacy_hardcover_author_ids_through_the_native_provider()
        {
            var nativeProvider = new NativeHardcoverMetadataProxy();
            var (proxy, _) = CreateProxy(new RecordingHardcoverSearchClient(() => new List<object>()), nativeProvider);

            var author = proxy.GetAuthorInfo("hardcover-author:42");

            Assert.Multiple(() =>
            {
                Assert.That(author.HardcoverAuthorId, Is.EqualTo("hc:42"));
                Assert.That(author.Books, Has.Count.EqualTo(2));
                Assert.That(author.Books.Select(book => book.MediaType),
                    Is.EquivalentTo(new[] { BookMediaType.Audiobook, BookMediaType.Ebook }));
                Assert.That(author.Books.All(book => book.HardcoverBookId == "hc:101"), Is.True);
            });
        }

        [Test]
        public void should_resolve_hardcover_edition_ids_to_one_monitored_native_edition()
        {
            var nativeProvider = new NativeHardcoverMetadataProxy();
            var (proxy, _) = CreateProxy(new RecordingHardcoverSearchClient(() => new List<object>()), nativeProvider);

            var result = proxy.GetEditionInfo("hc:edition:201", BookMediaType.Ebook);

            Assert.Multiple(() =>
            {
                Assert.That(nativeProvider.EditionLookups, Is.EqualTo(1));
                Assert.That(result.Item2.HardcoverBookId, Is.EqualTo("hc:101"));
                Assert.That(result.Item2.MediaType, Is.EqualTo(BookMediaType.Ebook));
                Assert.That(result.Item2.Editions, Has.Count.EqualTo(1));
                Assert.That(result.Item2.Editions[0].HardcoverEditionId, Is.EqualTo("201"));
                Assert.That(result.Item2.Editions[0].Monitored, Is.True);
            });
        }

        [Test]
        public void should_search_and_load_full_hardcover_records_through_the_native_provider()
        {
            var nativeProvider = new NativeHardcoverMetadataProxy();
            var searchClient = new RecordingHardcoverSearchClient(() => new List<object>());
            var (proxy, _) = CreateProxy(searchClient, nativeProvider);

            var results = proxy.SearchForNewEntity("Native Book", "hardcover");
            var books = results.OfType<Book>().ToList();
            var authors = results.OfType<Author>().ToList();

            Assert.Multiple(() =>
            {
                Assert.That(nativeProvider.WorkLookups, Is.EqualTo(1));
                Assert.That(nativeProvider.SearchWasInteractive, Is.True);
                Assert.That(nativeProvider.WorkWasInteractive, Is.True);
                Assert.That(searchClient.CallCount, Is.Zero);
                Assert.That(books, Has.Count.EqualTo(1));
                Assert.That(authors, Has.Count.EqualTo(1));
                Assert.That(books.Single().HardcoverBookId, Is.EqualTo("hc:101"));
            });
        }

        [Test]
        public void should_include_configured_supplemental_catalogs_in_native_hardcover_search()
        {
            var nativeProvider = new NativeHardcoverMetadataProxy();
            var additionalMetadataProxy = new Mock<IAdditionalBookMetadataProxy>();
            additionalMetadataProxy.Setup(x => x.Search("Native Book"))
                .Returns(new List<Book> { new() { Title = "Supplemental Book" } });
            additionalMetadataProxy.Setup(x => x.ApplyFieldSourcePreferences(It.IsAny<Book>()))
                .Returns<Book>(book => book);
            additionalMetadataProxy.Setup(x => x.ApplyAuthorImageSourcePreference(It.IsAny<Author>(), It.IsAny<Book>()))
                .Returns<Author, Book>((author, _) => author);
            var (proxy, _) = CreateProxy(
                new RecordingHardcoverSearchClient(() => new List<object>()),
                nativeProvider,
                additionalMetadataProxy.Object);

            var results = proxy.SearchForNewEntity("Native Book", "hardcover");

            Assert.That(results.OfType<Book>().Select(book => book.Title), Does.Contain("Supplemental Book"));
        }

        [Test]
        public void should_keep_an_explicit_goodreads_search_out_of_native_hardcover_mode()
        {
            var nativeProvider = new NativeHardcoverMetadataProxy();
            var goodreadsSearchProxy = new Mock<IGoodreadsSearchProxy>();
            goodreadsSearchProxy.Setup(x => x.Search("native book"))
                .Returns(new List<SearchJsonResource>());
            var additionalMetadataProxy = new Mock<IAdditionalBookMetadataProxy>();
            additionalMetadataProxy.Setup(x => x.Search(It.IsAny<string>()))
                .Returns(new List<Book>());
            additionalMetadataProxy.Setup(x => x.ApplyFieldSourcePreferences(It.IsAny<Book>()))
                .Returns<Book>(book => book);
            additionalMetadataProxy.Setup(x => x.ApplyAuthorImageSourcePreference(It.IsAny<Author>(), It.IsAny<Book>()))
                .Returns<Author, Book>((author, _) => author);
            var (proxy, _) = CreateProxy(
                new RecordingHardcoverSearchClient(() => new List<object>()),
                nativeProvider,
                additionalMetadataProxy.Object,
                goodreadsSearchProxy.Object);

            var results = proxy.SearchForNewEntity("Native Book", "goodreads");

            Assert.Multiple(() =>
            {
                Assert.That(results, Is.Empty);
                Assert.That(goodreadsSearchProxy.Invocations, Has.Count.EqualTo(1));
                Assert.That(nativeProvider.WorkLookups, Is.Zero);
            });
        }

        [Test]
        public void should_use_interactive_quota_for_native_book_search_and_provider_ids()
        {
            var nativeProvider = new NativeHardcoverMetadataProxy();
            var (proxy, _) = CreateProxy(new RecordingHardcoverSearchClient(() => new List<object>()), nativeProvider);

            var searchResults = proxy.SearchForNewBook("Native Book", null, mediaType: BookMediaType.Ebook);
            Assert.That(searchResults, Has.Count.EqualTo(1));
            Assert.That(nativeProvider.SearchWasInteractive, Is.True);
            Assert.That(nativeProvider.WorkWasInteractive, Is.True);
            Assert.That(searchResults[0].MediaType, Is.EqualTo(BookMediaType.Ebook));

            var idProvider = new NativeHardcoverMetadataProxy();
            var (idProxy, _) = CreateProxy(new RecordingHardcoverSearchClient(() => new List<object>()), idProvider);
            var idResults = idProxy.SearchForNewBook("hc:101", null, mediaType: BookMediaType.Ebook);
            Assert.That(idResults, Has.Count.EqualTo(1));
            Assert.That(idProvider.WorkWasInteractive, Is.True);
            Assert.That(idResults[0].HardcoverBookId, Is.EqualTo("hc:101"));
        }

        [Test]
        public void should_use_interactive_quota_for_native_author_and_edition_ids()
        {
            var nativeProvider = new NativeHardcoverMetadataProxy();
            var (proxy, _) = CreateProxy(new RecordingHardcoverSearchClient(() => new List<object>()), nativeProvider);

            var authors = proxy.SearchForNewAuthor("hc:42");
            Assert.That(authors, Has.Count.EqualTo(1));
            Assert.That(nativeProvider.AuthorWasInteractive, Is.True);

            var editionProvider = new NativeHardcoverMetadataProxy();
            var (editionProxy, _) = CreateProxy(new RecordingHardcoverSearchClient(() => new List<object>()), editionProvider);
            var editions = editionProxy.SearchForNewBook("hc:edition:201", null, mediaType: BookMediaType.Ebook);
            Assert.That(editions, Has.Count.EqualTo(1));
            Assert.That(editionProvider.EditionWasInteractive, Is.True);
            Assert.That(editionProvider.WorkWasInteractive, Is.True);
            Assert.That(editions[0].Editions, Has.Count.EqualTo(1));
            Assert.That(editions[0].Editions[0].Monitored, Is.True);
            Assert.That(editions[0].MediaType, Is.EqualTo(BookMediaType.Ebook));
        }
    }
}

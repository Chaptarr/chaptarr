using System;
using System.Collections.Generic;
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

            public List<object> Search(string query)
            {
                CallCount++;
                return _handler();
            }
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            public bool HardcoverEnabled { get; set; } = true;
            public string HardcoverApiToken { get; set; } = "token-a";

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_HardcoverEnabled" => HardcoverEnabled,
                    "get_HardcoverApiToken" => HardcoverApiToken,
                    "get_MetadataServerUrl" => "http://metadata",
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }
        }

        private static (BookInfoProxy Proxy, ConfigServiceProxy Config) CreateProxy(IHardcoverSearchClient hardcoverSearchClient)
        {
            var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var config = (ConfigServiceProxy)configService;
            var logger = LogManager.GetCurrentClassLogger();
            var healthGate = new MetadataServerHealthGate(
                configService,
                new MetadataServerHealthService(logger),
                logger);

            var proxy = new BookInfoProxy(
                httpClient: null,
                cachedHttpClient: null,
                goodreadsSearchProxy: null,
                hardcoverSearchClient: hardcoverSearchClient,
                audibleCatalogProxy: null,
                authorService: null,
                bookService: null,
                editionService: null,
                configService: configService,
                requestBuilder: new MetadataRequestBuilder(configService),
                logger: logger,
                cacheManager: new CacheManager(),
                metadataServerHealthGate: healthGate);

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

            Assert.That(proxy.SearchForNewEntity(" Matt Dinniman "), Has.Count.EqualTo(1));
            Assert.That(proxy.SearchForNewEntity("matt dinniman", "hardcover"), Has.Count.EqualTo(1));

            Assert.That(client.CallCount, Is.EqualTo(1));
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
    }
}

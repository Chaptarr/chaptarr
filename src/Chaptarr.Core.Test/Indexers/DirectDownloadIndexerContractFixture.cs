using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Http.ClientSchema;
using NUnit.Framework;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Indexers.DirectDownload;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    [NonParallelizable]
    public class DirectDownloadIndexerContractFixture
    {
        private sealed class TestLocalizationService : ILocalizationService
        {
            private readonly Dictionary<string, string> _english;

            public TestLocalizationService()
            {
                var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Localization", "Core", "en.json");
                _english = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
            }

            public Dictionary<string, string> GetLocalizationDictionary() => _english;

            public string GetCurrentLocale() => "en";

            public string GetLocalizedString(string phrase) =>
                _english.TryGetValue(phrase, out var localized) ? localized : phrase;

            public string GetLocalizedString(string phrase, Dictionary<string, object> tokens) => GetLocalizedString(phrase);
        }

        private FieldInfo _localizationServiceField;
        private ILocalizationService _previousLocalizationService;

        [SetUp]
        public void SetUp()
        {
            _localizationServiceField = typeof(SchemaBuilder)
                .GetField("_localizationService", BindingFlags.NonPublic | BindingFlags.Static);
            _previousLocalizationService = (ILocalizationService)_localizationServiceField.GetValue(null);
            _localizationServiceField.SetValue(null, new TestLocalizationService());
        }

        [TearDown]
        public void TearDown()
        {
            _localizationServiceField.SetValue(null, _previousLocalizationService);
        }

        [Test]
        public void should_expose_one_neutral_ebook_template_schema()
        {
            var schema = SchemaBuilder.ToSchema(new DirectDownloadSettings());

            Assert.That(schema.Select(field => field.Name), Is.EqualTo(new[] { "urls", "apiKey", "enableSlowFallback" }));
            Assert.That(schema.Single(field => field.Name == "urls").Type, Is.EqualTo("textArea"));
            Assert.That(schema.Single(field => field.Name == "urls").HelpText, Does.Contain("URL per line"));
            Assert.That(schema.Single(field => field.Name == "apiKey").Privacy, Is.EqualTo("apiKey"));
            Assert.That(schema.Single(field => field.Name == "enableSlowFallback").Type, Is.EqualTo("checkbox"));
        }

        [Test]
        public void should_keep_the_direct_template_capability_neutral()
        {
            var resource = new IndexerResourceMapper().ToResource(new NzbDrone.Core.Indexers.IndexerDefinition
            {
                Name = "Direct Download",
                Implementation = "DirectDownloadIndexer",
                ImplementationName = "Direct Download",
                ConfigContract = nameof(DirectDownloadSettings),
                Settings = new DirectDownloadSettings(),
                Protocol = NzbDrone.Core.Indexers.DownloadProtocol.Direct,
                SupportsSearch = true,
                SupportsRss = false
            });

            Assert.That(resource.DefinitionName, Is.EqualTo("DirectDownloadIndexer"));
            Assert.That(resource.Protocol.ToString(), Is.EqualTo("Direct"));
            Assert.That(resource.SupportsSearch, Is.True);
            Assert.That(resource.SupportsRss, Is.False);
            var provider = new DirectDownloadIndexer(null, null, null, null);
            Assert.That(provider.Message.Message, Does.Contain("ebook searches only"));

            provider.Definition = new NzbDrone.Core.Indexers.IndexerDefinition
            {
                Settings = new DirectDownloadSettings
                {
                    Urls = "https://203.0.113.30\nhttps://203.0.113.33",
                    EnableSlowFallback = true
                }
            };

            var testResult = provider.Test();

            Assert.That(testResult.IsValid, Is.True);
            Assert.That(provider.Definition.Message.Message, Does.Contain("URL 1: configuration valid"));
            Assert.That(provider.Definition.Message.Message, Does.Contain("URL 2: configuration valid"));
            Assert.That(provider.Definition.Message.Message, Does.Contain("No API key configured"));
            Assert.That(provider.Definition.Message.Message, Does.Not.Contain("203.0.113.30"));
        }

        [Test]
        public void should_report_validation_failures_without_claiming_probe_success()
        {
            var provider = new DirectDownloadIndexer(null, null, null, null)
            {
                Definition = new NzbDrone.Core.Indexers.IndexerDefinition
                {
                    Settings = new DirectDownloadSettings
                    {
                        Urls = "ftp://203.0.113.24"
                    }
                }
            };

            var testResult = provider.Test();

            Assert.That(testResult.IsValid, Is.False);
            Assert.That(provider.Definition.Message, Is.Null);
        }

        [Test]
        public async Task should_fetch_direct_download_releases_via_probe_service()
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterCatalogSource(
                transport,
                "https://203.0.113.30",
                "9780441172719",
                "Dune",
                "9780441172719",
                "https://203.0.113.19/files/dune.epub");
            var probeService = new DirectDownloadSourceProbeService(transport.CreateClient(), NLog.LogManager.GetCurrentClassLogger());
            var provider = new DirectDownloadIndexer(null, null, null, null, probeService)
            {
                Definition = new NzbDrone.Core.Indexers.IndexerDefinition
                {
                    Id = 41,
                    Name = "Direct Download Test",
                    Priority = 7,
                    Settings = new DirectDownloadSettings
                    {
                        Urls = "https://203.0.113.30",
                        ApiKey = "real-secret"
                    }
                }
            };

            var releases = await provider.Fetch(new BookSearchCriteria
            {
                Author = new Author { Name = "Frank Herbert" },
                Books = new List<Book> { new() { MediaType = BookMediaType.Ebook } },
                BookTitle = "Dune",
                BookIsbn = "978-0-441-17271-9"
            });

            Assert.That(releases, Has.Count.EqualTo(1));
            Assert.That(releases.Single().Title, Is.EqualTo("Frank Herbert - Dune [epub]"));
            Assert.That(releases.Single().Isbn, Is.EqualTo("9780441172719"));
            Assert.That(releases.Single().DownloadUrl, Does.Contain("/md5/"), "Download URL should be the info URI for grab-time resolution");
            Assert.That(releases.Single().DownloadProtocol, Is.EqualTo(NzbDrone.Core.Indexers.DownloadProtocol.Direct));
            Assert.That(releases.Single().IndexerId, Is.EqualTo(41));
            Assert.That(releases.Single().Indexer, Is.EqualTo("Direct Download Test"));
            Assert.That(transport.RequestedUrls.Any(url => url.Contains("q=9780441172719", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public async Task should_use_alternate_title_without_primary_isbn_for_direct_download_search()
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterCatalogSource(
                transport,
                "https://203.0.113.30",
                "9780441172719",
                "Pretty Girls",
                "9780441172719",
                "https://203.0.113.19/files/pretty-girls.epub");
            var probeService = new DirectDownloadSourceProbeService(transport.CreateClient(), NLog.LogManager.GetCurrentClassLogger());
            var provider = new DirectDownloadIndexer(null, null, null, null, probeService)
            {
                Definition = new NzbDrone.Core.Indexers.IndexerDefinition
                {
                    Id = 41,
                    Name = "Direct Download Test",
                    Settings = new DirectDownloadSettings
                    {
                        Urls = "https://203.0.113.30",
                        ApiKey = "real-secret"
                    }
                }
            };

            await provider.Fetch(new BookSearchCriteria
            {
                Author = new Author { Name = "Karin Slaughter" },
                Books = new List<Book> { new() { MediaType = BookMediaType.Ebook } },
                BookTitle = "Mooie Meisjes",
                SearchTitleOverride = "Pretty Girls",
                BookIsbn = "9780441172719"
            });

            var searchUrl = transport.RequestedUrls.First(url => url.StartsWith("https://203.0.113.30/search", StringComparison.Ordinal));
            Assert.That(searchUrl, Does.Contain("Pretty"));
            Assert.That(searchUrl, Does.Not.Contain("9780441172719"));
        }

        [Test]
        public async Task should_reject_audiobook_book_search_without_probning_sources()
        {
            var transport = new DirectDownloadTestHttp();
            var probeService = new DirectDownloadSourceProbeService(transport.CreateClient(), NLog.LogManager.GetCurrentClassLogger());
            var provider = new DirectDownloadIndexer(null, null, null, null, probeService)
            {
                Definition = new NzbDrone.Core.Indexers.IndexerDefinition
                {
                    Settings = new DirectDownloadSettings
                    {
                        Urls = "https://203.0.113.30",
                        ApiKey = "real-secret"
                    }
                }
            };

            var releases = await provider.Fetch(new BookSearchCriteria
            {
                Author = new Author { Name = "Frank Herbert" },
                Books = new List<Book> { new() { MediaType = BookMediaType.Audiobook } },
                BookTitle = "Dune"
            });

            Assert.That(releases, Is.Empty);
            Assert.That(transport.RequestedUrls, Is.Empty);
        }

        [Test]
        public async Task should_skip_audiobook_books_during_author_search_without_probning_sources()
        {
            var transport = new DirectDownloadTestHttp();
            DirectDownloadSourceProbeFixtureSupport.RegisterCatalogSource(
                transport,
                "https://203.0.113.30",
                "9780441172719",
                "Dune",
                "9780441172719",
                "https://203.0.113.19/files/dune.epub");
            var probeService = new DirectDownloadSourceProbeService(transport.CreateClient(), NLog.LogManager.GetCurrentClassLogger());
            var provider = new DirectDownloadIndexer(null, null, null, null, probeService)
            {
                Definition = new NzbDrone.Core.Indexers.IndexerDefinition
                {
                    Settings = new DirectDownloadSettings
                    {
                        Urls = "https://203.0.113.30",
                        ApiKey = "real-secret"
                    }
                }
            };

            var releases = await provider.Fetch(new AuthorSearchCriteria
            {
                Author = new Author { Name = "Frank Herbert" },
                Books = new List<Book>
                {
                    new()
                    {
                        Title = "Dune",
                        MediaType = BookMediaType.Ebook,
                        Editions = new List<Edition>
                        {
                            new() { Id = 1, Monitored = true, Title = "Dune", Isbn13 = "9780441172719" }
                        }
                    },
                    new()
                    {
                        Title = "Dune Audio",
                        MediaType = BookMediaType.Audiobook,
                        Editions = new List<Edition>
                        {
                            new() { Id = 2, Monitored = true, Title = "Dune Audio", Isbn13 = "9789999999999" }
                        }
                    }
                }
            });

            Assert.That(releases, Has.Count.EqualTo(1));
            Assert.That(releases.Single().Book, Is.EqualTo("Dune"));
            Assert.That(transport.RequestedUrls.Any(url => url.Contains("9789999999999", StringComparison.Ordinal)), Is.False);
            Assert.That(transport.RequestedUrls.Any(url => url.Contains("9780441172719", StringComparison.Ordinal)), Is.True);
        }
    }
}

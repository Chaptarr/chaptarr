using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    public class DirectDownloadIndexerContractFixture
    {
        private sealed class TestLocalizationService : ILocalizationService
        {
            public Dictionary<string, string> GetLocalizationDictionary() => new();

            public string GetLocalizedString(string phrase) => phrase;

            public string GetLocalizedString(string phrase, Dictionary<string, object> tokens) => phrase;
        }

        [SetUp]
        public void SetUp()
        {
            typeof(SchemaBuilder)
                .GetField("_localizationService", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, new TestLocalizationService());
        }

        [Test]
        public void should_expose_one_neutral_ebook_template_schema()
        {
            var schema = SchemaBuilder.ToSchema(new DirectDownloadSettings());

            Assert.That(schema.Select(field => field.Name), Is.EqualTo(new[] { "urls", "apiKey" }));
            Assert.That(schema.Single(field => field.Name == "urls").Type, Is.EqualTo("textArea"));
            Assert.That(schema.Single(field => field.Name == "urls").HelpText, Does.Contain("URL per line"));
            Assert.That(schema.Single(field => field.Name == "apiKey").Privacy, Is.EqualTo("apiKey"));
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
                    Urls = "https://primary.example\nhttps://secondary.example"
                }
            };

            var testResult = provider.Test();

            Assert.That(testResult.IsValid, Is.True);
            Assert.That(provider.Definition.Message.Message, Does.Contain("URL 1: configuration valid"));
            Assert.That(provider.Definition.Message.Message, Does.Contain("URL 2: configuration valid"));
            Assert.That(provider.Definition.Message.Message, Does.Contain("No API key configured"));
            Assert.That(provider.Definition.Message.Message, Does.Not.Contain("primary.example"));
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
                        Urls = "ftp://invalid.example"
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
                "https://primary.example",
                "9780441172719",
                "Dune",
                "9780441172719",
                "https://downloads.primary.example/files/dune.epub");
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
                        Urls = "https://primary.example",
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
                        Urls = "https://primary.example",
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
                "https://primary.example",
                "9780441172719",
                "Dune",
                "9780441172719",
                "https://downloads.primary.example/files/dune.epub");
            var probeService = new DirectDownloadSourceProbeService(transport.CreateClient(), NLog.LogManager.GetCurrentClassLogger());
            var provider = new DirectDownloadIndexer(null, null, null, null, probeService)
            {
                Definition = new NzbDrone.Core.Indexers.IndexerDefinition
                {
                    Settings = new DirectDownloadSettings
                    {
                        Urls = "https://primary.example",
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

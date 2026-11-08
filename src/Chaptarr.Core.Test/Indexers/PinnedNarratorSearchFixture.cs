using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Indexers.Torznab;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture("Newznab")]
    [TestFixture("Torznab")]
    [TestFixture("MyAnonaMouse")]
    public class PinnedNarratorSearchFixture
    {
        private readonly string _kind;

        public PinnedNarratorSearchFixture(string kind)
        {
            _kind = kind;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_prepend_narrator_tier_and_preserve_every_existing_fallback(bool interactive)
        {
            var generator = CreateGenerator();
            var criteria = Criteria();
            criteria.InteractiveSearch = interactive;
            criteria.Books[0].AnyEditionOk = true;
            var baseline = generator.GetSearchRequests(criteria);
            criteria.Books[0].AnyEditionOk = false;

            var chain = generator.GetSearchRequests(criteria);

            Assert.That(chain.Tiers, Is.EqualTo(baseline.Tiers + 1));
            Assert.That(SearchText(chain.GetTier(0).Single().First()), Is.EqualTo("Storm Front Jim Butcher James Marsters"));
            Assert.That(chain.GetTier(0).Single().First().Url.FullUri,
                _kind == "MyAnonaMouse" ? Does.Contain("loadSearchJSONbasic.php") : Does.Contain("t=search"));
            for (var i = 0; i < baseline.Tiers; i++)
            {
                Assert.That(chain.GetTier(i + 1).Select(p => SearchText(p.First())),
                    Is.EqualTo(baseline.GetTier(i).Select(p => SearchText(p.First()))));
            }
        }

        [Test]
        public void should_reuse_the_manual_edition_pin_target()
        {
            var criteria = Criteria();
            criteria.Books[0].AnyEditionOk = true;
            criteria.Books[0].Editions[0].ManualAdd = true;

            Assert.That(FirstQuery(criteria), Is.EqualTo("Storm Front Jim Butcher James Marsters"));
        }

        [TestCase("unpinned")]
        [TestCase("ebook")]
        [TestCase("no narrator")]
        [TestCase("no editions")]
        [TestCase("no target")]
        [TestCase("mixed targets")]
        public void should_not_invent_a_narrator_target(string state)
        {
            var criteria = Criteria();
            switch (state)
            {
                case "unpinned":
                    criteria.Books[0].AnyEditionOk = true;
                    break;
                case "ebook":
                    criteria.Books[0].MediaType = BookMediaType.Ebook;
                    break;
                case "no narrator":
                    criteria.Books[0].Editions[0].Narrator = null;
                    break;
                case "no editions":
                    criteria.Books[0].Editions = new List<Edition>();
                    break;
                case "no target":
                    criteria.Books = null;
                    break;
                case "mixed targets":
                    criteria.Books.Add(new Book { MediaType = BookMediaType.Ebook });
                    break;
            }

            var queries = CreateGenerator().GetSearchRequests(criteria).GetAllTiers().Select(p => SearchText(p.First())).ToList();
            Assert.That(queries, Is.Not.Empty);
            Assert.That(queries, Has.All.Not.Contains("James Marsters"));
        }

        [Test]
        public void should_use_structured_cast_names_and_shared_query_normalization()
        {
            var criteria = Criteria();
            criteria.Books[0].Editions[0].NarratorNames = new List<string> { "Renée Voice", "李 明" };
            criteria.Books[0].Editions[0].Narrator = null;

            Assert.That(FirstQuery(criteria), Is.EqualTo("Storm Front Jim Butcher Renee Voice 李 明"));
        }

        [Test]
        public void should_use_only_supported_search_parameters()
        {
            if (_kind == "MyAnonaMouse")
            {
                var request = CreateGenerator().GetSearchRequests(Criteria()).GetTier(0).Single().First();
                var payload = JObject.Parse(Encoding.UTF8.GetString(request.HttpRequest.ContentData));
                Assert.That(payload["tor"]["srchIn"].Values<string>(), Does.Contain("narrator"));
                Assert.That(payload["tor"]["text"].Value<string>(), Is.EqualTo("Storm Front Jim Butcher James Marsters"));
                return;
            }

            foreach (var genericOnly in new[] { true, false })
            {
                var capabilities = new NewznabCapabilities
                {
                    SupportedSearchParameters = genericOnly ? new[] { "q" } : null,
                    SupportedBookSearchParameters = genericOnly ? null : new[] { "author", "title" }
                };
                var chain = CreateGenerator(capabilities).GetSearchRequests(Criteria());
                Assert.That(chain.Tiers, Is.EqualTo(genericOnly ? 3 : 2));
                Assert.That(SearchText(chain.GetTier(0).Single().First()),
                    genericOnly ? Is.EqualTo("Storm Front Jim Butcher James Marsters") : Is.EqualTo("book|Jim Butcher|Storm Front"));
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task should_fall_back_only_when_the_narrator_tier_is_empty(bool firstTierHasResults)
        {
            var probe = new TierProbe(CreateGenerator(), firstTierHasResults);

            var releases = await probe.Fetch(Criteria());

            Assert.That(releases, Has.Count.EqualTo(1));
            Assert.That(probe.Queries, Has.Count.EqualTo(firstTierHasResults ? 1 : 2));
            Assert.That(probe.Queries[0], Is.EqualTo("Storm Front Jim Butcher James Marsters"));
            if (!firstTierHasResults)
            {
                Assert.That(probe.Queries[1], Is.EqualTo(_kind == "MyAnonaMouse"
                    ? "Storm Front Jim Butcher"
                    : "book|Jim Butcher|Storm Front"));
            }
        }

        private string FirstQuery(BookSearchCriteria criteria)
        {
            return SearchText(CreateGenerator().GetSearchRequests(criteria).GetTier(0).Single().First());
        }

        private IIndexerRequestGenerator CreateGenerator(NewznabCapabilities capabilities = null)
        {
            var logger = LogManager.GetLogger(nameof(PinnedNarratorSearchFixture));
            if (_kind == "MyAnonaMouse")
            {
                return new MyAnonaMouseRequestGenerator { Settings = new MyAnonaMouseSettings(), Logger = logger };
            }

            var provider = InterfaceProxy.Proxy<INewznabCapabilitiesProvider>((_, _) => capabilities ?? new NewznabCapabilities());
            NewznabRequestGenerator generator = _kind == "Torznab"
                ? new TorznabRequestGenerator(provider, logger, _kind)
                : new NewznabRequestGenerator(provider, logger, _kind);
            generator.Settings = new NewznabSettings { BaseUrl = "https://indexer.example", Categories = new[] { 3030, 7020 } };
            generator.MaxPages = 1;
            return generator;
        }

        private static BookSearchCriteria Criteria()
        {
            return new BookSearchCriteria
            {
                Author = new Author { Name = "Jim Butcher" },
                BookTitle = "Storm Front",
                Books = new List<Book>
                {
                    new Book
                    {
                        MediaType = BookMediaType.Audiobook,
                        AnyEditionOk = false,
                        Editions = new List<Edition>
                        {
                            new Edition { ReadingFormatId = 2, Monitored = true, Narrator = "James Marsters" }
                        }
                    }
                }
            };
        }

        private static string SearchText(IndexerRequest request)
        {
            if (request.HttpRequest.ContentData?.Length > 0)
            {
                return JObject.Parse(Encoding.UTF8.GetString(request.HttpRequest.ContentData))["tor"]["text"].Value<string>();
            }

            var query = HttpUtility.ParseQueryString(request.Url.Query);
            return query["q"] ?? $"{query["t"]}|{query["author"]}|{query["title"]}";
        }

        private sealed class TierProbe : HttpIndexerBase<NewznabSettings>
        {
            private readonly IIndexerRequestGenerator _generator;
            private readonly bool _firstTierHasResults;
            public List<string> Queries { get; } = new List<string>();
            public override string Name => "Tier probe";
            public override DownloadProtocol Protocol => DownloadProtocol.Usenet;

            public TierProbe(IIndexerRequestGenerator generator, bool firstTierHasResults)
                : base(null, InterfaceProxy.Proxy<IIndexerStatusService>((_, _) => null), null, null,
                    LogManager.GetLogger(nameof(TierProbe)))
            {
                _generator = generator;
                _firstTierHasResults = firstTierHasResults;
                Definition = new IndexerDefinition { Id = 1, Name = Name, Settings = new NewznabSettings() };
            }

            public override IIndexerRequestGenerator GetRequestGenerator() => _generator;
            public override IParseIndexerResponse GetParser() => null;

            protected override Task<IList<ReleaseInfo>> FetchPage(IndexerRequest request, IParseIndexerResponse parser)
            {
                Queries.Add(SearchText(request));
                IList<ReleaseInfo> releases = _firstTierHasResults || Queries.Count > 1
                    ? new List<ReleaseInfo> { new ReleaseInfo { Title = "Jim Butcher - Storm Front", DownloadUrl = "https://indexer.example/release", Guid = "result" } }
                    : new List<ReleaseInfo>();
                return Task.FromResult(releases);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class ReleaseNarratorMetadataEnricherFixture
    {
        private sealed class StubEnhancedIndexer : IIndexer, INarratorMetadataProvider
        {
            private readonly Func<ReleaseInfo, bool> _populate;

            public StubEnhancedIndexer(int id, Func<ReleaseInfo, bool> populate)
            {
                _populate = populate;
                Definition = new IndexerDefinition { Id = id, Name = $"Stub {id}", Enable = true };
            }

            public int PopulateCalls { get; private set; }

            public bool? SupportsNarratorMetadata => null;
            public void TestNarratorMetadata(IEnumerable<ReleaseInfo> releases) { }
            public bool CanProvideNarratorMetadata => true;

            public bool TryPopulateNarratorMetadata(ReleaseInfo release)
            {
                PopulateCalls++;
                return _populate(release);
            }

            public string Name => "StubEnhancedIndexer";
            public Type ConfigContract => typeof(object);
            public ProviderMessage Message => null;
            public IEnumerable<ProviderDefinition> DefaultDefinitions => Enumerable.Empty<ProviderDefinition>();
            public ProviderDefinition Definition { get; set; }
            public ValidationResult Test() => new ValidationResult();
            public object RequestAction(string stage, IDictionary<string, string> query) => null;

            public bool SupportsRss => false;
            public bool SupportsSearch => false;
            public DownloadProtocol Protocol => DownloadProtocol.Usenet;
            public Task<IList<ReleaseInfo>> FetchRecent() => throw new NotImplementedException();
            public Task<IList<ReleaseInfo>> Fetch(BookSearchCriteria searchCriteria) => throw new NotImplementedException();
            public HttpRequest GetDownloadRequest(string link) => new HttpRequest(link);
            public Task<HttpResponse> ExecuteDownloadRequestAsync(HttpRequest request) => Task.FromResult(new HttpResponse(request, new HttpHeader(), string.Empty));
        }

        private sealed class StubIndexerFactory : IIndexerFactory
        {
            private readonly Dictionary<int, IIndexer> _indexers;

            public StubIndexerFactory(params IIndexer[] indexers)
            {
                _indexers = indexers.ToDictionary(indexer => indexer.Definition.Id);
            }

            public List<IndexerDefinition> All() => throw new NotImplementedException();
            public List<IIndexer> GetAvailableProviders() => _indexers.Values.ToList();
            public bool Exists(int id) => _indexers.ContainsKey(id);
            public IndexerDefinition Find(int id) => _indexers.TryGetValue(id, out var indexer) ? (IndexerDefinition)indexer.Definition : null;
            public IndexerDefinition Get(int id) => Find(id);
            public IEnumerable<IndexerDefinition> Get(IEnumerable<int> ids) => ids.Select(Find).Where(definition => definition != null);
            public IndexerDefinition Create(IndexerDefinition definition) => throw new NotImplementedException();
            public void Update(IndexerDefinition definition) => throw new NotImplementedException();
            public IEnumerable<IndexerDefinition> Update(IEnumerable<IndexerDefinition> definitions) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(IEnumerable<int> ids) => throw new NotImplementedException();
            public IEnumerable<IndexerDefinition> GetDefaultDefinitions() => Enumerable.Empty<IndexerDefinition>();
            public IEnumerable<IndexerDefinition> GetPresetDefinitions(IndexerDefinition providerDefinition) => Enumerable.Empty<IndexerDefinition>();
            public void SetProviderCharacteristics(IndexerDefinition definition) => throw new NotImplementedException();
            public void SetProviderCharacteristics(IIndexer provider, IndexerDefinition definition) => throw new NotImplementedException();
            public IIndexer GetInstance(IndexerDefinition definition) => definition != null && _indexers.TryGetValue(definition.Id, out var indexer) ? indexer : null;
            public ValidationResult Test(IndexerDefinition definition) => new ValidationResult();
            public object RequestAction(IndexerDefinition definition, string action, IDictionary<string, string> query) => null;
            public List<IndexerDefinition> AllForTag(int tagId) => new List<IndexerDefinition>();
            public List<IIndexer> RssEnabled(bool filterBlockedIndexers = true) => new List<IIndexer>();
            public List<IIndexer> AutomaticSearchEnabled(bool filterBlockedIndexers = true) => new List<IIndexer>();
            public List<IIndexer> InteractiveSearchEnabled(bool filterBlockedIndexers = true) => new List<IIndexer>();
        }

        [Test]
        public void should_enrich_missing_narrator_metadata_before_custom_format_scoring()
        {
            var indexer = new StubEnhancedIndexer(1, release =>
            {
                release.Narrator = "Jim Dale";
                return true;
            });

            var enricher = CreateEnricher(indexer);
            var missingNarrator = new ReleaseInfo { Title = "Harry Potter m4b", IndexerId = 1 };
            var existingNarrator = new ReleaseInfo { Title = "Harry Potter mp3", IndexerId = 1, Narrator = "Stephen Fry" };
            var noNfo = new ReleaseInfo { Title = "Harry Potter flac", IndexerId = 1, HasNfo = false };
            var titleCarriesNarrator = new ReleaseInfo { Title = "Harry Potter narrated by Jim Dale m4b", IndexerId = 1 };

            enricher.EnrichReleaseNarratorMetadata(
                new List<ReleaseInfo> { missingNarrator, existingNarrator, noNfo, titleCarriesNarrator },
                CreateCriteria(anyEditionOk: false));

            Assert.That(indexer.PopulateCalls, Is.EqualTo(1));
            Assert.That(missingNarrator.Narrator, Is.EqualTo("Jim Dale"));
            Assert.That(existingNarrator.Narrator, Is.EqualTo("Stephen Fry"));
            Assert.That(noNfo.Narrator, Is.Null);
            Assert.That(titleCarriesNarrator.Narrator, Is.Null);
        }

        [Test]
        public void should_not_enrich_when_search_has_no_preferred_narrator_target()
        {
            var indexer = new StubEnhancedIndexer(1, release =>
            {
                release.Narrator = "Jim Dale";
                return true;
            });

            var enricher = CreateEnricher(indexer);
            var release = new ReleaseInfo { Title = "Harry Potter m4b", IndexerId = 1 };

            enricher.EnrichReleaseNarratorMetadata(new List<ReleaseInfo> { release }, CreateCriteria(anyEditionOk: true));

            Assert.That(indexer.PopulateCalls, Is.EqualTo(0));
            Assert.That(release.Narrator, Is.Null);
        }

        [Test]
        public void should_enrich_every_offered_release_and_report_the_populated_ones()
        {
            var indexer = new StubEnhancedIndexer(1, release =>
            {
                release.Narrator = "Jim Dale";
                return true;
            });

            var enricher = CreateEnricher(indexer);
            var releases = Enumerable.Range(1, 12)
                .Select(i => new ReleaseInfo { Title = $"Harry Potter {i} m4b", IndexerId = 1 })
                .ToList();

            var enriched = enricher.EnrichReleaseNarratorMetadata(releases, CreateCriteria(anyEditionOk: false));

            Assert.That(indexer.PopulateCalls, Is.EqualTo(12));
            Assert.That(releases.Count(release => release.Narrator == "Jim Dale"), Is.EqualTo(12));
            Assert.That(enriched, Is.EquivalentTo(releases));
        }

        private static ReleaseNarratorMetadataEnricher CreateEnricher(params IIndexer[] indexers)
        {
            return new ReleaseNarratorMetadataEnricher(
                LogManager.GetCurrentClassLogger(),
                new StubIndexerFactory(indexers), null);
        }

        [Test]
        public void unexpected_provider_failure_does_not_discard_healthy_neighbors()
        {
            var indexer = new StubEnhancedIndexer(1, release =>
            {
                if (release.Guid == "failed")
                {
                    throw new InvalidOperationException("Provider metadata unavailable");
                }

                release.Narrator = "Jim Dale";
                return true;
            });
            var releases = new[] { "before", "failed", "after" }
                .Select(id => new ReleaseInfo { Guid = id, Title = "Harry Potter M4B", IndexerId = 1 }).ToList();

            var populated = CreateEnricher(indexer).EnrichReleaseNarratorMetadata(releases, CreateCriteria(anyEditionOk: false));

            Assert.Multiple(() =>
            {
                Assert.That(indexer.PopulateCalls, Is.EqualTo(3));
                Assert.That(populated, Is.EqualTo(new[] { releases[0], releases[2] }));
                Assert.That(releases[1].Narrator, Is.Null);
            });
        }

        [TestCase("Read by Jim Dale, Ben Reader, Cara Reader", 0)]
        [TestCase("Gelesen von Jim Dale, Ben Reader, Cara Reader", 0)]
        [TestCase("Jim Dale + 2 more narrators", 1)]
        [TestCase("Jim Dale + 2 weitere Sprecher", 1)]
        public void known_complete_credit_should_avoid_fetch_but_partial_free_text_should_not(string credit, int expectedCalls)
        {
            var names = new[] { "Jim Dale", "Ben Reader", "Cara Reader" };
            var criteria = CreateCriteria(anyEditionOk: false);
            var edition = criteria.Books.Single().Editions.Single();
            edition.NarratorNames = names.ToList();
            edition.Narrator = string.Join(", ", names);
            var indexer = new StubEnhancedIndexer(1, release =>
            {
                release.Narrator = string.Join(", ", names);
                return true;
            });
            var release = new ReleaseInfo { Title = "Harry Potter M4B [" + credit + "]", IndexerId = 1 };
            CreateEnricher(indexer).EnrichReleaseNarratorMetadata(new List<ReleaseInfo> { release }, criteria);
            Assert.That(indexer.PopulateCalls, Is.EqualTo(expectedCalls));
        }

        [TestCase("Read by Jim Dale, Ben Reader, Cara Reader", 0)]
        [TestCase("Gelesen von Jim Dale, Ben Reader, Cara Reader", 0)]
        [TestCase("Jim Dale + 2 more narrators", 1)]
        [TestCase("Jim Dale + 2 weitere Sprecher", 1)]
        public void raw_fields_should_reuse_known_cast_evidence_without_promoting_partial_credit(string field, int expectedCalls)
        {
            var names = new[] { "Jim Dale", "Ben Reader", "Cara Reader" };
            var criteria = CreateCriteria(anyEditionOk: false);
            var edition = criteria.Books.Single().Editions.Single();
            edition.NarratorNames = names.ToList();
            edition.Narrator = string.Join(", ", names);
            var indexer = new StubEnhancedIndexer(1, _ => true);
            var release = new ReleaseInfo { Title = "Harry Potter M4B", IndexerId = 1, NarratorEvidenceFields = new List<string> { field } };
            CreateEnricher(indexer).EnrichReleaseNarratorMetadata(new List<ReleaseInfo> { release }, criteria);
            Assert.That(indexer.PopulateCalls, Is.EqualTo(expectedCalls));
        }

        private static BookSearchCriteria CreateCriteria(bool anyEditionOk)
        {
            return new BookSearchCriteria
            {
                Books = new List<Book>
                {
                    new Book
                    {
                        Title = "Harry Potter",
                        AnyEditionOk = anyEditionOk,
                        Editions = new List<Edition>
                        {
                            new Edition { Id = 1, Monitored = true, IsEbook = false, ReadingFormatId = 2, Narrator = "Jim Dale" }
                        }
                    }
                }
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
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
            public Task<IList<ReleaseInfo>> Fetch(AuthorSearchCriteria searchCriteria) => throw new NotImplementedException();
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

        private sealed class StubQualityProfileService : IQualityProfileService
        {
            private readonly Dictionary<int, QualityProfile> _profiles;

            public StubQualityProfileService(params QualityProfile[] profiles)
            {
                _profiles = profiles.ToDictionary(profile => profile.Id);
            }

            public QualityProfile Get(int id) => _profiles[id];
            public List<QualityProfile> All() => _profiles.Values.ToList();
            public List<QualityProfile> GetByType(ProfileType type) => _profiles.Values.Where(profile => profile.ProfileType == type).ToList();
            public bool Exists(int id) => _profiles.ContainsKey(id);
            public QualityProfile Add(QualityProfile profile) => throw new NotImplementedException();
            public void Update(QualityProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public QualityProfile GetDefaultProfile(string name, Quality cutoff = null, params Quality[] allowed) => throw new NotImplementedException();
        }

        private sealed class StubCustomFormatService : ICustomFormatService
        {
            private readonly List<CustomFormat> _formats;

            public StubCustomFormatService(params CustomFormat[] formats)
            {
                _formats = formats.ToList();
            }

            public List<CustomFormat> All() => _formats;
            public CustomFormat GetById(int id) => _formats.Single(format => format.Id == id);
            public CustomFormat Insert(CustomFormat customFormat) => throw new NotImplementedException();
            public void Update(CustomFormat customFormat) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
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
            var titleCarriesNarrator = new ReleaseInfo { Title = "Harry Potter narrated by Stephen Fry m4b", IndexerId = 1 };

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
        public void should_cap_narrator_metadata_enrichment_per_indexer()
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

            enricher.EnrichReleaseNarratorMetadata(releases, CreateCriteria(anyEditionOk: false));

            Assert.That(indexer.PopulateCalls, Is.EqualTo(8));
            Assert.That(releases.Count(release => release.Narrator == "Jim Dale"), Is.EqualTo(8));
        }

        [Test]
        public void should_enrich_unpinned_search_when_relevant_profile_has_scored_narrator_condition()
        {
            var indexer = new StubEnhancedIndexer(1, release =>
            {
                release.Narrator = "Jeff Hays";
                return true;
            });
            var format = new CustomFormat
            {
                Id = 9,
                Name = "No Jeff Hays",
                Specifications = new List<ICustomFormatSpecification>
                {
                    new NarratorSpecification { Name = "Jeff Hays", Value = @"^Jeff\s+Hays$" }
                }
            };
            var profile = new QualityProfile
            {
                Id = 7,
                ProfileType = ProfileType.Audiobook,
                FormatItems = new List<ProfileFormatItem>
                {
                    new ProfileFormatItem { Format = new CustomFormat { Id = format.Id }, Score = -10000 }
                }
            };
            var enricher = CreateEnricher(new[] { indexer }, new[] { profile }, new[] { format });
            var criteria = CreateCriteria(anyEditionOk: true);
            criteria.Author = new Author { AudiobookQualityProfileId = profile.Id };
            var release = new ReleaseInfo { Title = "Dungeon Crawler Carl m4b", IndexerId = 1 };

            enricher.EnrichReleaseNarratorMetadata(new List<ReleaseInfo> { release }, criteria);

            Assert.That(indexer.PopulateCalls, Is.EqualTo(1));
            Assert.That(release.Narrator, Is.EqualTo("Jeff Hays"));
        }

        [Test]
        public void should_enrich_unpinned_search_for_a_scored_narrator_names_condition()
        {
            var indexer = new StubEnhancedIndexer(1, release =>
            {
                release.Narrator = "James Marsters";
                return true;
            });
            var format = new CustomFormat
            {
                Id = 10,
                Name = "Preferred Narrators",
                Specifications = new List<ICustomFormatSpecification>
                {
                    new NarratorNamesSpecification { Names = new[] { "James Marsters", "Stephen Fry" } }
                }
            };
            var profile = new QualityProfile
            {
                Id = 8,
                ProfileType = ProfileType.Audiobook,
                FormatItems = new List<ProfileFormatItem>
                {
                    new ProfileFormatItem { Format = new CustomFormat { Id = format.Id }, Score = 50 }
                }
            };
            var enricher = CreateEnricher(new[] { indexer }, new[] { profile }, new[] { format });
            var criteria = CreateCriteria(anyEditionOk: true);
            criteria.Author = new Author { AudiobookQualityProfileId = profile.Id };
            var release = new ReleaseInfo { Title = "Buffy m4b", IndexerId = 1 };

            enricher.EnrichReleaseNarratorMetadata(new List<ReleaseInfo> { release }, criteria);

            Assert.That(indexer.PopulateCalls, Is.EqualTo(1));
            Assert.That(release.Narrator, Is.EqualTo("James Marsters"));
        }

        [Test]
        public void should_not_enrich_for_zero_scored_narrator_condition()
        {
            var indexer = new StubEnhancedIndexer(1, release => true);
            var format = new CustomFormat
            {
                Id = 9,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new NarratorSpecification { Name = "Jeff Hays", Value = @"^Jeff\s+Hays$" }
                }
            };
            var profile = new QualityProfile
            {
                Id = 7,
                FormatItems = new List<ProfileFormatItem>
                {
                    new ProfileFormatItem { Format = new CustomFormat { Id = format.Id }, Score = 0 }
                }
            };
            var enricher = CreateEnricher(new[] { indexer }, new[] { profile }, new[] { format });
            var criteria = CreateCriteria(anyEditionOk: true);
            criteria.Author = new Author { AudiobookQualityProfileId = profile.Id };

            enricher.EnrichReleaseNarratorMetadata(
                new List<ReleaseInfo> { new ReleaseInfo { Title = "Dungeon Crawler Carl m4b", IndexerId = 1 } },
                criteria);

            Assert.That(indexer.PopulateCalls, Is.EqualTo(0));
        }

        private static ReleaseNarratorMetadataEnricher CreateEnricher(params IIndexer[] indexers)
        {
            return CreateEnricher(indexers, Array.Empty<QualityProfile>(), Array.Empty<CustomFormat>());
        }

        private static ReleaseNarratorMetadataEnricher CreateEnricher(
            IEnumerable<IIndexer> indexers,
            IEnumerable<QualityProfile> profiles,
            IEnumerable<CustomFormat> formats)
        {
            return new ReleaseNarratorMetadataEnricher(
                LogManager.GetCurrentClassLogger(),
                new StubIndexerFactory(indexers.ToArray()),
                new StubQualityProfileService(profiles.ToArray()),
                new StubCustomFormatService(formats.ToArray()));
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
                            new Edition { Id = 1, Monitored = true, IsEbook = false, Narrator = "Jim Dale" }
                        }
                    }
                }
            };
        }
    }
}

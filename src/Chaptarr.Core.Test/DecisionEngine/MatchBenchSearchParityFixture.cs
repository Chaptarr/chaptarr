using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.History;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Qualities;
using NzbDrone.MatchBench;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    [NonParallelizable]
    public class MatchBenchSearchParityFixture
    {
        private sealed class NoOpCustomFormatCalculationService : ICustomFormatCalculationService
        {
            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author author) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => new();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author author) => new();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author author) => new();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => new();
        }

        private sealed class NoOpRemoteBookAggregationService : IRemoteBookAggregationService
        {
            public RemoteBook Augment(RemoteBook remoteBook) => remoteBook;
        }

        private sealed class NoOpReleaseNarratorMetadataEnricher : IReleaseNarratorMetadataEnricher
        {
            public IReadOnlyCollection<ReleaseInfo> EnrichReleaseNarratorMetadata(List<ReleaseInfo> releases, SearchCriteriaBase searchCriteria)
            {
                return Array.Empty<ReleaseInfo>();
            }
        }

        private sealed class SwallowingHttpNarratorMetadataEnricher : IReleaseNarratorMetadataEnricher
        {
            private readonly Program.OfflineReplayHttpDispatcher _networkGuard;

            public SwallowingHttpNarratorMetadataEnricher(Program.OfflineReplayHttpDispatcher networkGuard)
            {
                _networkGuard = networkGuard;
            }

            public IReadOnlyCollection<ReleaseInfo> EnrichReleaseNarratorMetadata(List<ReleaseInfo> releases, SearchCriteriaBase searchCriteria)
            {
                try
                {
                    _networkGuard.GetResponseAsync(new HttpRequest("https://offline.invalid/narrator"), new CookieContainer())
                        .GetAwaiter()
                        .GetResult();
                }
                catch (HttpRequestException)
                {
                    // The decision replay must still report this swallowed outbound request.
                }

                return Array.Empty<ReleaseInfo>();
            }
        }

        private sealed class TestDelayProfileService : IDelayProfileService
        {
            public DelayProfile Add(DelayProfile profile) => throw new NotSupportedException();
            public DelayProfile Update(DelayProfile profile) => throw new NotSupportedException();
            public void Delete(int id) => throw new NotSupportedException();
            public List<DelayProfile> All() => throw new NotSupportedException();
            public DelayProfile Get(int id) => throw new NotSupportedException();
            public List<DelayProfile> AllForTag(int tagId) => throw new NotSupportedException();
            public List<DelayProfile> AllForTags(HashSet<int> tagIds) => throw new NotSupportedException();
            public List<DelayProfile> Reorder(int id, int? afterId) => throw new NotSupportedException();

            public DelayProfile BestForTags(HashSet<int> tagIds)
            {
                return new DelayProfile { PreferredProtocol = NzbDrone.Core.Indexers.DownloadProtocol.Torrent };
            }
        }

        [TestCase("mambench")]
        [TestCase("releasereplay")]
        public void shared_runner_should_keep_the_production_sibling_verdict_and_real_prioritizer_order(string inputMode)
        {
            var (author, target, catalog) = MakeJamesHuntCatalog(BookMediaType.Ebook);
            var networkGuard = new Program.OfflineReplayHttpDispatcher();
            var searchService = MakeReleaseSearchService(networkGuard);
            var prioritizer = MakePrioritizer();
            var criteria = MakeCriteria(searchService, author, target, catalog);
            var preferredDuplicate = MakeRelease(inputMode, "same-guid", "James Hunt - Powerless (epub)", 1);
            var crossMedia = MakeRelease(inputMode, "cross-media", "James Hunt - Powerless (mp3)", 0);
            crossMedia.Categories = new List<int> { 3030 };
            if (crossMedia is TorrentInfo crossMediaTorrent)
            {
                crossMediaTorrent.FileType = "mp3";
            }

            var reports = new List<ReleaseInfo>
            {
                MakeRelease(inputMode, "broad-priority", "James Hunt - Powerless (epub)", 10),
                MakeRelease(inputMode, "sibling", "James Hunt - The Final Homestead - EMP Survival in a Powerless World (epub)", 0),
                MakeRelease(inputMode, "same-guid", "James Hunt - Powerless (epub)", 8),
                preferredDuplicate,
                crossMedia
            };

            var replay = Program.RunProductionSearchDecision(
                searchService, prioritizer, networkGuard, reports, criteria,
                usedSyntheticInput: false, hasCompleteSourceData: true);

            var sibling = replay.Decisions.Single(decision => decision.RemoteBook.Release.Guid == "sibling");
            var siblingMatch = sibling.RemoteBook.SearchCriteriaMatch;
            var positive = replay.Decisions.Single(decision => decision.RemoteBook.Release.Guid == "same-guid");
            var positiveMatch = positive.RemoteBook.SearchCriteriaMatch;

            Assert.Multiple(() =>
            {
                Assert.That(replay.Complete, Is.True, inputMode);
                Assert.That(replay.HasCompleteSourceData, Is.True);
                Assert.That(replay.BlockedExternalRequestCount, Is.Zero);
                Assert.That(sibling.Approved, Is.False, inputMode);
                Assert.That(sibling.Rejections.Select(rejection => rejection.Reason),
                    Does.Contain("Release appears to match a different book by this author"));
                Assert.That(siblingMatch, Is.Not.Null);
                Assert.That(siblingMatch.Book, Is.SameAs(target));
                Assert.That(siblingMatch.EvidenceTitle, Is.EqualTo(sibling.RemoteBook.Release.Title));
                Assert.That(siblingMatch.IsMatch, Is.False);
                Assert.That(siblingMatch.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SiblingTitleContradiction));
                Assert.That(siblingMatch.Problems.Any(problem => problem.Value.Contains("The Final Homestead", StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(positive.Approved, Is.True, inputMode);
                Assert.That(positiveMatch, Is.Not.Null);
                Assert.That(positiveMatch.Book, Is.SameAs(target));
                Assert.That(positiveMatch.IsMatch, Is.True);
                Assert.That(positiveMatch.ProblemCode, Is.EqualTo(TitleMatchProblemCode.None));
                Assert.That(positive.RemoteBook.Release, Is.SameAs(preferredDuplicate));
                Assert.That(replay.Decisions.Any(decision => decision.RemoteBook.Release.Guid == "cross-media"), Is.False);
                Assert.That(replay.Decisions.Where(decision => decision.Approved)
                    .Select(decision => decision.RemoteBook.Release.Guid),
                    Is.EqualTo(new[] { "same-guid", "broad-priority" }));
                Assert.That(replay.Decisions.Last(), Is.SameAs(sibling));
            });
        }

        [TestCase("mambench", BookMediaType.Ebook, false)]
        [TestCase("mambench", BookMediaType.Audiobook, true)]
        [TestCase("releasereplay", BookMediaType.Ebook, false)]
        [TestCase("releasereplay", BookMediaType.Audiobook, true)]
        public void sibling_competition_should_follow_the_target_media_enum(string inputMode, BookMediaType siblingMediaType, bool expectedApproved)
        {
            var (author, target, _) = MakeJamesHuntCatalog(BookMediaType.Ebook);
            var sibling = MakeBook(42407, author, "The Final Homestead", "EMP Survival in a Powerless World", "1", siblingMediaType);
            var catalog = new List<Book> { target, sibling };
            author.Books = catalog;
            var networkGuard = new Program.OfflineReplayHttpDispatcher();
            var searchService = MakeReleaseSearchService(networkGuard);
            var criteria = MakeCriteria(searchService, author, target, catalog);
            var release = MakeRelease(
                inputMode,
                "media-scope",
                "James Hunt - The Final Homestead - EMP Survival in a Powerless World (epub)",
                0);

            var replay = Program.RunProductionSearchDecision(
                searchService, MakePrioritizer(), networkGuard,
                new[] { release }, criteria, usedSyntheticInput: false, hasCompleteSourceData: true);

            var decision = replay.Decisions.Single();
            var match = decision.RemoteBook.SearchCriteriaMatch;

            Assert.Multiple(() =>
            {
                Assert.That(replay.Complete, Is.True, inputMode);
                Assert.That(decision.Approved, Is.EqualTo(expectedApproved));
                Assert.That(match, Is.Not.Null);
                Assert.That(match.Book, Is.SameAs(target));
                Assert.That(match.IsMatch, Is.EqualTo(expectedApproved));
                Assert.That(match.ProblemCode,
                    Is.EqualTo(expectedApproved ? TitleMatchProblemCode.None : TitleMatchProblemCode.SiblingTitleContradiction));
            });
        }

        [Test]
        public void swallowed_outbound_http_and_incomplete_input_must_withhold_a_complete_verdict()
        {
            var (author, target, catalog) = MakeJamesHuntCatalog(BookMediaType.Ebook);
            var guard = new Program.OfflineReplayHttpDispatcher();
            var guardedService = MakeReleaseSearchService(guard, triggerHttp: true);
            var criteria = MakeCriteria(guardedService, author, target, catalog);
            var report = MakeRelease("releasereplay", "positive", "James Hunt - Powerless (epub)", 0);
            var guardedReplay = Program.RunProductionSearchDecision(
                guardedService,
                MakePrioritizer(),
                guard,
                new[] { report }, criteria);
            var incompleteGuard = new Program.OfflineReplayHttpDispatcher();
            var incompleteReplay = Program.RunProductionSearchDecision(
                MakeReleaseSearchService(incompleteGuard), MakePrioritizer(), incompleteGuard,
                new[] { MakeRelease("releasereplay", "incomplete", "James Hunt - Powerless (epub)", 0) },
                criteria, usedSyntheticInput: false, hasCompleteSourceData: false);
            var syntheticGuard = new Program.OfflineReplayHttpDispatcher();
            var syntheticReplay = Program.RunProductionSearchDecision(
                MakeReleaseSearchService(syntheticGuard), MakePrioritizer(), syntheticGuard,
                new[] { MakeRelease("releasereplay", "synthetic", "James Hunt - Powerless (epub)", 0) },
                criteria, usedSyntheticInput: true, hasCompleteSourceData: true);

            Assert.Multiple(() =>
            {
                Assert.That(guardedReplay.Decisions.Single().Approved, Is.True);
                Assert.That(guardedReplay.BlockedExternalRequestCount, Is.EqualTo(1));
                Assert.That(guardedReplay.BlockedRequests.Single(), Is.EqualTo("GET offline.invalid"));
                Assert.That(guardedReplay.Complete, Is.False);
                Assert.That(incompleteReplay.BlockedExternalRequestCount, Is.Zero);
                Assert.That(incompleteReplay.HasCompleteSourceData, Is.False);
                Assert.That(incompleteReplay.Complete, Is.False);
                Assert.That(syntheticReplay.UsedSyntheticInput, Is.True);
                Assert.That(syntheticReplay.Complete, Is.False);
            });
        }

        [Test]
        public void replay_indexer_client_factory_should_send_proxy_client_requests_through_the_offline_guard()
        {
            var guard = new Program.OfflineReplayHttpDispatcher();
            var cacheManager = new CacheManager();
            var factory = new Program.OfflineReplayIndexerHttpClient(
                Array.Empty<IHttpRequestInterceptor>(),
                cacheManager,
                new RateLimitService(cacheManager, LogManager.GetCurrentClassLogger()),
                guard,
                LogManager.GetCurrentClassLogger());
            var client = factory.GetClient(17);

            Assert.Throws<HttpRequestException>(() => client.Execute(new HttpRequest("https://offline.invalid/indexer")));

            Assert.Multiple(() =>
            {
                Assert.That(client, Is.SameAs(factory));
                Assert.That(guard.BlockedRequestCount, Is.EqualTo(1));
                Assert.That(guard.BlockedRequests.Single(), Is.EqualTo("GET offline.invalid"));
            });
        }

        private static BookSearchCriteria MakeCriteria(ReleaseSearchService searchService, Author author, Book target, List<Book> catalog)
        {
            return searchService.CreateBookSearchCriteria(author, target, catalog, userInvokedSearch: true, interactiveSearch: true);
        }

        private static (Author Author, Book Target, List<Book> Catalog) MakeJamesHuntCatalog(BookMediaType mediaType)
        {
            var author = new Author { Id = 631, Name = "James Hunt" };
            var target = MakeBook(42421, author, "Powerless", "A Tale Of Survival in a Powerless World", "1-4", mediaType);
            var catalog = new List<Book>
            {
                target,
                MakeBook(42407, author, "The Final Homestead", "EMP Survival in a Powerless World", "1", mediaType),
                MakeBook(42454, author, "Broken Roads", "A Tale of Survival in a Powerless World", "2", mediaType)
            };
            author.Books = catalog;
            return (author, target, catalog);
        }

        private static Book MakeBook(int id, Author author, string title, string series, string position, BookMediaType mediaType)
        {
            return new Book
            {
                Id = id,
                AuthorId = author.Id,
                Author = author,
                Title = title,
                SeriesName = series,
                SeriesPosition = position,
                MediaType = mediaType,
                Editions = new List<Edition> { new Edition { Id = id, BookId = id, Title = title, Monitored = true } }
            };
        }

        private static ReleaseInfo MakeRelease(string inputMode, string guid, string title, int indexerPriority)
        {
            var mamReport = string.Equals(inputMode, "mambench", StringComparison.Ordinal);
            ReleaseInfo report = mamReport ? new TorrentInfo { FileType = "epub" } : new ReleaseInfo();
            report.Guid = guid;
            report.Title = title;
            report.Author = "James Hunt";
            report.Indexer = mamReport ? "MyAnonaMouse" : "OfflineReplay";
            report.IndexerPriority = indexerPriority;
            report.DownloadProtocol = NzbDrone.Core.Indexers.DownloadProtocol.Torrent;
            report.Categories = new List<int> { 7020 };
            report.PublishDate = DateTime.UtcNow;
            report.Size = 1_000_000;
            return report;
        }

        private static ReleaseSearchService MakeReleaseSearchService(Program.OfflineReplayHttpDispatcher networkGuard, bool triggerHttp = false)
        {
            var decisionMaker = MakeDecisionMaker(networkGuard, triggerHttp);
            return new ReleaseSearchService(null, null, null, null, decisionMaker, LogManager.GetCurrentClassLogger());
        }

        private static DownloadDecisionMaker MakeDecisionMaker(Program.OfflineReplayHttpDispatcher networkGuard, bool triggerHttp = false)
        {
            var logger = LogManager.GetCurrentClassLogger();
            var config = ConfigServiceTestProxy.Create(BookMatchingStrictness.Balanced);
            return new DownloadDecisionMaker(
                new IDecisionEngineSpecification[]
                {
                    new ReleaseTitleMatchSpecification(logger, config),
                    new MultiBookReleaseSpecification(logger)
                },
                parsingService: null,
                bookService: null,
                editionFtsRepository: null,
                formatService: new NoOpCustomFormatCalculationService(),
                aggregationService: new NoOpRemoteBookAggregationService(),
                releaseNarratorMetadataEnricher: triggerHttp
                    ? new SwallowingHttpNarratorMetadataEnricher(networkGuard)
                    : new NoOpReleaseNarratorMetadataEnricher(),
                configService: config,
                logger: logger);
        }

        private static IPrioritizeDownloadDecision MakePrioritizer()
        {
            var config = ConfigServiceTestProxy.Create(BookMatchingStrictness.Balanced);
            return new DownloadDecisionPriorizationService(
                config,
                new TestDelayProfileService(),
                qualityProfileService: null,
                logger: LogManager.GetCurrentClassLogger());
        }
    }
}

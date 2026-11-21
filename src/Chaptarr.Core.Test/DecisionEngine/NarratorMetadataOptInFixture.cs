using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.Search;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Indexers.Torznab;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture(false)]
    [TestFixture(true)]
    public class NarratorMetadataOptInFixture
    {
        private readonly bool _torznab;
        private readonly List<HttpRequest> _requests = new();
        private ReleaseNarratorMetadataEnricher _enricher;
        private NewznabSettings _settings;
        private BookSearchCriteria _criteria;
        private CustomFormat _format;
        private ICustomFormatService _formats;
        private int _formatReads;
        private string _nfo;
        private bool _available = true;
        private Func<HttpRequest, HttpResponse> _respond;

        public NarratorMetadataOptInFixture(bool torznab)
        {
            _torznab = torznab;
        }

        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        public void nfo_http_requires_opt_in_and_a_pin_or_scored_narrator_format(bool enabled, bool pinned, bool scoredNarratorFormat)
        {
            SetUp(enabled, pinned, scoredNarratorFormat);
            var release = Release();

            var populated = _enricher.EnrichReleaseNarratorMetadata(new List<ReleaseInfo> { release }, _criteria);

            var expected = enabled && (pinned || scoredNarratorFormat) ? 1 : 0;
            Assert.That(_requests, Has.Count.EqualTo(expected));
            Assert.That(populated, Has.Count.EqualTo(expected));
            Assert.That(release.Narrator, expected == 1 ? Is.EqualTo("Jim Dale") : Is.Null);
        }

        [Test]
        public void manual_edition_pin_uses_the_same_opt_in_gate()
        {
            SetUp(true, false, false);
            _criteria.Books[0].Editions[0].ManualAdd = true;

            _enricher.EnrichReleaseNarratorMetadata(new List<ReleaseInfo> { Release() }, _criteria);

            Assert.That(_requests, Has.Count.EqualTo(1));
        }

        [TestCase("existing narrator")]
        [TestCase("known title credit")]
        [TestCase("no NFO")]
        [TestCase("no target names")]
        [TestCase("ebook")]
        public void unnecessary_nfo_lookups_do_not_reach_http(string state)
        {
            SetUp(true, true, false);
            var release = Release();
            switch (state)
            {
                case "existing narrator": release.Narrator = "Jim Dale"; break;
                case "known title credit": release.Title += " - Jim Dale"; break;
                case "no NFO": release.HasNfo = false; break;
                case "no target names": _criteria.Books[0].Editions[0].Narrator = null; break;
                case "ebook":
                    _criteria.Books[0].MediaType = BookMediaType.Ebook;
                    _criteria.Books[0].Editions[0].ReadingFormatId = 1;
                    release.Title = "Writer - A Book EPUB";
                    break;
            }

            _enricher.EnrichReleaseNarratorMetadata(new List<ReleaseInfo> { release }, _criteria);

            Assert.That(_requests, Is.Empty);
        }

        [Test]
        public void disabling_the_option_stops_even_a_cached_provider_from_populating_new_results()
        {
            SetUp(true, true, false);
            _enricher.EnrichReleaseNarratorMetadata(new List<ReleaseInfo> { Release() }, _criteria);
            Assert.That(_requests, Has.Count.EqualTo(1));
            _settings.EnableNarratorMetadata = false;
            var next = Release();

            var populated = _enricher.EnrichReleaseNarratorMetadata(new List<ReleaseInfo> { next }, _criteria);

            Assert.That(_requests, Has.Count.EqualTo(1));
            Assert.That(populated, Is.Empty);
            Assert.That(next.Narrator, Is.Null);
        }

        [TestCase(50, false)]
        [TestCase(-50, false)]
        [TestCase(50, true)]
        [TestCase(-50, true)]
        public void scored_production_condition_fetches_missing_evidence_even_with_known_narrator_and_duration(int score, bool negate)
        {
            SetUp(true, false, true);
            UseProductionFormat(score, negate);
            var release = Release();
            release.Narrator = "Existing Reader";
            release.Duration = "9 hours";

            var populated = _enricher.EnrichReleaseNarratorMetadata(new() { release }, _criteria);

            Assert.Multiple(() =>
            {
                Assert.That(_requests, Has.Count.EqualTo(1));
                Assert.That(populated, Is.EqualTo(new[] { release }));
                Assert.That(release.IsGraphicAudio, Is.True);
                Assert.That(release.Narrator, Is.EqualTo("Existing Reader"));
                Assert.That(release.Duration, Is.EqualTo("9 hours"));
            });
        }

        [TestCase("[dramatized]")]
        [TestCase("[graphic audio]")]
        [TestCase("[full cast]")]
        [TestCase("structured flag")]
        [TestCase("raw structured field")]
        public void known_production_evidence_scores_without_an_nfo_request(string evidence)
        {
            SetUp(true, false, true);
            UseProductionFormat(-100, false);
            var release = Release();
            if (evidence == "structured flag") release.IsGraphicAudio = true;
            else if (evidence == "raw structured field") release.NarratorEvidenceFields = new() { "Full Cast" };
            else release.Title += " " + evidence;

            var populated = _enricher.EnrichReleaseNarratorMetadata(new() { release }, _criteria);
            var formats = new CustomFormatCalculationService(_formats, LogManager.GetCurrentClassLogger())
                .ParseCustomFormat(new RemoteBook { Release = release, Author = _criteria.Author, Books = _criteria.Books }, 0);

            Assert.Multiple(() =>
            {
                Assert.That(_requests, Is.Empty);
                Assert.That(populated, Is.Empty);
                Assert.That(formats, Does.Contain(_format));
            });
        }

        [TestCase("opt out")]
        [TestCase("zero score")]
        [TestCase("not in profile")]
        [TestCase("deleted format")]
        [TestCase("ebook format")]
        [TestCase("ebook search")]
        [TestCase("no NFO")]
        [TestCase("no search")]
        [TestCase("unrelated condition")]
        public void irrelevant_or_disabled_production_lookup_costs_no_request(string state)
        {
            SetUp(state != "opt out", false, true);
            UseProductionFormat(-100, false);
            var release = Release();
            switch (state)
            {
                case "zero score": _criteria.Author.AudiobookQualityProfile.Value.FormatItems[0].Score = 0; break;
                case "not in profile": _criteria.Author.AudiobookQualityProfile.Value.FormatItems.Clear(); break;
                case "deleted format": _available = false; break;
                case "ebook format": _format.AppliesTo = CustomFormatMediaType.Ebook; break;
                case "ebook search": _criteria.Books[0].MediaType = BookMediaType.Ebook; break;
                case "no NFO": release.HasNfo = false; break;
                case "no search": _criteria = null; break;
                case "unrelated condition": _format.Specifications = new() { new ReleaseTitleSpecification { Value = "GraphicAudio" } }; break;
            }

            _enricher.EnrichReleaseNarratorMetadata(new() { release }, _criteria);

            Assert.That(_requests, Is.Empty);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void known_narrator_condition_does_not_fetch_again(bool advanced, bool negate)
        {
            SetUp(true, false, true);
            _format.Specifications = new()
            {
                advanced ? new NarratorSpecification { Value = "Jim Dale", Negate = negate }
                    : new NarratorNamesSpecification { Names = new[] { "Jim Dale" }, Negate = negate }
            };
            var release = Release();
            release.Title += " [Jim Dale]";

            _enricher.EnrichReleaseNarratorMetadata(new() { release }, _criteria);

            Assert.That(_requests, Is.Empty);
        }

        [Test]
        public void known_production_does_not_hide_missing_pinned_narrator()
        {
            SetUp(true, true, true);
            UseProductionFormat(50, false);
            _nfo = "Narration: Jim Dale";
            var release = Release();
            release.IsGraphicAudio = true;

            _enricher.EnrichReleaseNarratorMetadata(new() { release }, _criteria);

            Assert.That(_requests, Has.Count.EqualTo(1));
            Assert.That(release.Narrator, Is.EqualTo("Jim Dale"));
        }

        [Test]
        public void profile_is_prepared_once_and_metadata_cache_is_reused_for_the_batch()
        {
            SetUp(true, false, true);
            UseProductionFormat(50, false);
            var releases = Enumerable.Range(0, 12).Select(_ => Release()).ToList();

            var populated = _enricher.EnrichReleaseNarratorMetadata(releases, _criteria);

            Assert.Multiple(() =>
            {
                Assert.That(_formatReads, Is.EqualTo(1));
                Assert.That(_requests, Has.Count.EqualTo(1));
                Assert.That(populated, Is.EquivalentTo(releases));
                Assert.That(releases.All(release => release.IsGraphicAudio), Is.True);
            });
        }

        [Test]
        public void production_only_nfo_recalculates_the_search_score_and_rejection()
        {
            SetUp(true, false, true);
            UseProductionFormat(-100, false);
            var maker = DecisionMaker();

            var result = maker.GetSearchDecision(new() { Release() }, _criteria).Single();

            Assert.Multiple(() =>
            {
                Assert.That(_requests, Has.Count.EqualTo(1));
                Assert.That(result.RemoteBook.Release.Narrator, Is.Null);
                Assert.That(result.RemoteBook.Release.IsGraphicAudio, Is.True);
                Assert.That(result.RemoteBook.CustomFormatScore, Is.EqualTo(-100));
                Assert.That(result.Rejections.Any(rejection => rejection.Reason.Contains("below Author profile minimum")), Is.True);
            });
        }

        [TestCase("mapping", true, true)]
        [TestCase("mapping", false, true)]
        [TestCase("mapping", true, false)]
        [TestCase("mapping", false, false)]
        [TestCase("scoring", true, true)]
        [TestCase("scoring", false, true)]
        [TestCase("scoring", true, false)]
        [TestCase("scoring", false, false)]
        [TestCase("specification", true, true)]
        [TestCase("specification", false, true)]
        [TestCase("specification", true, false)]
        [TestCase("specification", false, false)]
        public void processing_failure_stays_on_its_release_across_nfo_rescoring(string stage, bool interactive, bool neighbors)
        {
            SetUp(true, false, true);
            UseProductionFormat(50, false);
            _criteria.InteractiveSearch = interactive;
            var maker = DecisionMaker(stage);
            var failed = Release();
            var reports = new List<ReleaseInfo> { failed };
            if (neighbors)
            {
                foreach (var suffix in new[] { "before", "after" })
                {
                    var healthy = Release();
                    healthy.Guid = suffix;
                    healthy.NfoUrl = "https://indexer.example/api?t=getnfo&id=" + suffix;
                    reports.Insert(suffix == "before" ? 0 : reports.Count, healthy);
                }
            }

            var decisions = maker.GetSearchDecision(reports, _criteria);
            var error = decisions.Single(decision => decision.RemoteBook.Release == failed);
            var healthyDecisions = decisions.Where(decision => decision != error).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(decisions.Select(decision => decision.RemoteBook.Release), Is.EqualTo(reports));
                Assert.That(error.Rejections, Has.Exactly(1).Matches<Rejection>(rejection =>
                    rejection.Category == "Error" && !rejection.CanBypass && !rejection.IsSoftFilter));
                Assert.That(error.RemoteBook.DownloadAllowed, Is.False);
                Assert.That(error.RemoteBook.ParsedBookInfo?.Quality, Is.Not.Null);
                Assert.That(error.RemoteBook.ReleaseSource, Is.EqualTo(interactive ? ReleaseSourceType.InteractiveSearch : ReleaseSourceType.Search));
                Assert.That(InteractiveSearchVisibility.IsHidden(error), Is.False);
                Assert.That(healthyDecisions, Has.Count.EqualTo(neighbors ? 2 : 0));
                Assert.That(healthyDecisions.All(decision => decision.Approved && decision.RemoteBook.DownloadAllowed && decision.RemoteBook.CustomFormatScore == 50), Is.True);
                Assert.That(_requests, Has.Count.EqualTo(reports.Count - (stage == "mapping" ? 1 : 0)));
                Assert.That(failed.IsGraphicAudio, Is.EqualTo(stage != "mapping"));
                Assert.That(_requests.Any(request => request.Url.FullUri.Contains("id=fixture-release")), Is.EqualTo(stage != "mapping"));
            });
        }

        [TestCase("１２:３０")]
        [TestCase("99999999999 minutes")]
        [TestCase("99999999 hours")]
        public void malformed_nfo_duration_preserves_search_results_and_usable_narrator(string duration)
        {
            SetUp(true, true, true);
            _nfo = $"Length: {duration}\nNarration: Jim Dale";
            var reports = new List<ReleaseInfo> { Release(), Release() };
            reports[1].NfoUrl += "-neighbor";
            reports[1].Guid = "neighbor";

            var decisions = DecisionMaker().GetSearchDecision(reports, _criteria);

            Assert.Multiple(() =>
            {
                Assert.That(decisions.Select(decision => decision.RemoteBook.Release), Is.EqualTo(reports));
                Assert.That(decisions.All(decision => decision.Approved && decision.RemoteBook.DownloadAllowed), Is.True);
                Assert.That(decisions.All(decision => decision.RemoteBook.CustomFormatScore == 50), Is.True);
                Assert.That(reports.All(release => release.Narrator == "Jim Dale" && release.Duration == duration), Is.True);
            });
        }

        [TestCase("timeout", true)]
        [TestCase("timeout", false)]
        [TestCase("503", true)]
        [TestCase("503", false)]
        [TestCase("429", true)]
        [TestCase("429", false)]
        [TestCase("no response", true)]
        [TestCase("no response", false)]
        public void failed_optional_lookup_preserves_decisions_and_can_recover_on_next_search(string failure, bool interactive)
        {
            SetUp(true, true, true);
            _criteria.InteractiveSearch = interactive;
            var reports = new List<ReleaseInfo> { Release(), Release() };
            reports[1].NfoUrl += "-neighbor";
            reports[1].Guid = "neighbor";
            reports[0].Duration = "4h";
            _respond = request =>
            {
                if (request.Url.FullUri.Contains("-neighbor"))
                {
                    return new HttpResponse(request, new HttpHeader(), _nfo, HttpStatusCode.OK);
                }

                return failure switch
                {
                    "timeout" => throw new TimeoutException("Optional NFO timed out"),
                    "no response" => null,
                    _ => new HttpResponse(request, new HttpHeader(), "Unavailable", (HttpStatusCode)int.Parse(failure))
                };
            };
            var maker = DecisionMaker();

            var decisions = maker.GetSearchDecision(reports, _criteria);

            var quotaStopped = failure == "429";
            Assert.Multiple(() =>
            {
                Assert.That(decisions.Select(decision => decision.RemoteBook.Release), Is.EqualTo(reports));
                Assert.That(decisions.All(decision => decision.Approved && decision.RemoteBook.DownloadAllowed), Is.True);
                Assert.That(decisions[0].RemoteBook.CustomFormatScore, Is.Zero);
                Assert.That(decisions[1].RemoteBook.CustomFormatScore, Is.EqualTo(quotaStopped ? 0 : 50));
                Assert.That(reports[0].Narrator, Is.Null);
                Assert.That(reports[0].Duration, Is.EqualTo("4h"));
                Assert.That(reports[1].Narrator, Is.EqualTo(quotaStopped ? null : "Jim Dale"));
                Assert.That(_requests, Has.Count.EqualTo(quotaStopped ? 1 : 2));
            });

            _respond = null;
            var recovered = maker.GetSearchDecision(new() { Release() }, _criteria).Single();

            Assert.Multiple(() =>
            {
                Assert.That(_requests, Has.Count.EqualTo(quotaStopped ? 2 : 3));
                Assert.That(recovered.Approved, Is.True);
                Assert.That(recovered.RemoteBook.Release.Narrator, Is.EqualTo("Jim Dale"));
                Assert.That(recovered.RemoteBook.CustomFormatScore, Is.EqualTo(50));
            });
        }

        [Test]
        public void successful_empty_nfo_remains_cached_after_endpoint_fallback()
        {
            SetUp(true, true, false);
            _nfo = string.Empty;

            var first = _enricher.EnrichReleaseNarratorMetadata(new() { Release() }, _criteria);
            var second = _enricher.EnrichReleaseNarratorMetadata(new() { Release() }, _criteria);

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.Empty);
                Assert.That(second, Is.Empty);
                Assert.That(_requests, Has.Count.EqualTo(2));
                Assert.That(_requests.Select(request => request.Url.FullUri),
                    Has.One.Contains("t=getnfo").And.One.Contains("t=info"));
            });
        }

        [TestCase("info", "getnfo")]
        [TestCase("getnfo", "info")]
        public void fallback_transport_failure_is_not_cached_as_empty_metadata(string first, string fallback)
        {
            SetUp(true, true, false);
            var release = Release();
            release.NfoUrl = release.NfoUrl.Replace("t=getnfo", "t=" + first);
            var unavailable = true;
            _respond = request =>
            {
                if (request.Url.FullUri.Contains("t=" + first))
                {
                    return new HttpResponse(request, new HttpHeader(), string.Empty, HttpStatusCode.OK);
                }

                Assert.That(request.Url.FullUri, Does.Contain("t=" + fallback));
                if (unavailable)
                {
                    throw new TimeoutException("Fallback NFO timed out");
                }

                return new HttpResponse(request, new HttpHeader(), _nfo, HttpStatusCode.OK);
            };

            var failed = _enricher.EnrichReleaseNarratorMetadata(new() { release }, _criteria);
            Assert.That(failed, Is.Empty);
            Assert.That(_requests, Has.Count.EqualTo(2));
            unavailable = false;

            var recovered = _enricher.EnrichReleaseNarratorMetadata(new() { release }, _criteria);

            Assert.Multiple(() =>
            {
                Assert.That(_requests, Has.Count.EqualTo(4));
                Assert.That(recovered, Is.EqualTo(new[] { release }));
                Assert.That(release.Narrator, Is.EqualTo("Jim Dale"));
            });
        }

        [Test]
        public void failed_optional_lookup_keeps_the_existing_profile_rejection()
        {
            SetUp(true, true, true);
            _criteria.Author.AudiobookQualityProfile.Value.MinFormatScore = 50;
            var maker = DecisionMaker();
            _settings.EnableNarratorMetadata = false;
            var original = maker.GetSearchDecision(new() { Release() }, _criteria).Single();
            _settings.EnableNarratorMetadata = true;
            _respond = _ => throw new TimeoutException("Optional NFO timed out");

            var result = maker.GetSearchDecision(new() { Release() }, _criteria).Single();

            Assert.Multiple(() =>
            {
                Assert.That(_requests, Has.Count.EqualTo(1));
                Assert.That(original.Approved, Is.False);
                Assert.That(result.Approved, Is.False);
                Assert.That(result.Rejections.Select(rejection => rejection.Reason),
                    Is.EqualTo(original.Rejections.Select(rejection => rejection.Reason)));
                Assert.That(result.RemoteBook.CustomFormatScore, Is.EqualTo(original.RemoteBook.CustomFormatScore));
            });
        }

        [TestCase(100)]
        [TestCase(429)]
        [TestCase(900)]
        public void provider_error_inside_http_success_is_not_cached_as_missing_metadata(int code)
        {
            SetUp(true, true, false);
            _respond = request => new HttpResponse(request, new HttpHeader { ContentType = "application/xml" },
                $"<error code=\"{code}\" description=\"Metadata unavailable\"/>", HttpStatusCode.OK);

            var failed = _enricher.EnrichReleaseNarratorMetadata(new() { Release() }, _criteria);
            Assert.That(failed, Is.Empty);
            _respond = null;
            var release = Release();

            var recovered = _enricher.EnrichReleaseNarratorMetadata(new() { release }, _criteria);

            Assert.Multiple(() =>
            {
                Assert.That(recovered, Is.EqualTo(new[] { release }));
                Assert.That(release.Narrator, Is.EqualTo("Jim Dale"));
                Assert.That(_requests, Has.Count.EqualTo(2));
            });
        }

        [Test]
        public void missing_item_answer_still_uses_the_negative_cache()
        {
            SetUp(true, true, false);
            _respond = request => new HttpResponse(request, new HttpHeader { ContentType = "application/xml" },
                "<error code=\"300\" description=\"No such item\"/>", HttpStatusCode.OK);
            var release = Release();
            release.DownloadUrl = release.NfoUrl;
            release.NfoUrl = null;

            var first = _enricher.EnrichReleaseNarratorMetadata(new() { release }, _criteria);
            var second = _enricher.EnrichReleaseNarratorMetadata(new() { release }, _criteria);

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.Empty);
                Assert.That(second, Is.Empty);
                Assert.That(_requests, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void unsupported_endpoint_still_uses_the_other_endpoint()
        {
            SetUp(true, true, false);
            _respond = request => request.Url.FullUri.Contains("t=getnfo")
                ? new HttpResponse(request, new HttpHeader { ContentType = "application/xml" },
                    "<error code=\"202\" description=\"No such function\"/>", HttpStatusCode.OK)
                : new HttpResponse(request, new HttpHeader(), _nfo, HttpStatusCode.OK);
            var release = Release();

            var populated = _enricher.EnrichReleaseNarratorMetadata(new() { release }, _criteria);

            Assert.Multiple(() =>
            {
                Assert.That(populated, Is.EqualTo(new[] { release }));
                Assert.That(release.Narrator, Is.EqualTo("Jim Dale"));
                Assert.That(_requests, Has.Count.EqualTo(2));
            });
        }

        private DownloadDecisionMaker DecisionMaker(string failureStage = null)
        {
            var book = _criteria.Books[0];
            book.Id = 10;
            book.Title = "A Book";
            book.AuthorId = 4;
            book.Author = _criteria.Author;
            book.Editions[0].Title = book.Title;
            _criteria.Author.Id = 4;
            _criteria.Author.Name = "Writer";
            _criteria.Author.Books = _criteria.Books;
            _criteria.AuthorCatalog = _criteria.Books;
            var parsing = Proxy<IParsingService>((_, args) => new RemoteBook
            {
                Author = _criteria.Author, Books = _criteria.Books, ParsedBookInfo = (ParsedBookInfo)args[0]
            });
            var logger = LogManager.GetCurrentClassLogger();
            var config = ConfigServiceTestProxy.Create(BookMatchingStrictness.Balanced);
            var calculator = new CustomFormatCalculationService(_formats, logger);
            bool Fails(RemoteBook remote, string stage) => failureStage == stage && remote.Release.Guid == "fixture-release" &&
                (stage == "mapping" || remote.Release.IsGraphicAudio);
            var fault = Proxy<IDecisionEngineSpecification>((method, args) => method.Name switch
            {
                "get_Priority" => SpecificationPriority.Default,
                "get_Type" => RejectionType.Permanent,
                nameof(IDecisionEngineSpecification.IsSatisfiedBy) => Fails((RemoteBook)args[0], "specification")
                    ? throw new InvalidOperationException("Dependency unavailable during rescore") : Decision.Accept(),
                _ => throw new AssertionException(method.Name)
            });
            var maker = new DownloadDecisionMaker(new IDecisionEngineSpecification[]
            {
                new AuthorSpecification(logger),
                fault,
                new ReleaseTitleMatchSpecification(logger, config),
                new CustomFormatAllowedbyProfileSpecification()
            }, parsing, Proxy<IBookService>((_, _) => _criteria.Books),
                Proxy<IEditionFtsRepository>((method, _) => throw new AssertionException(method.Name)),
                Proxy<ICustomFormatCalculationService>((_, args) => Fails((RemoteBook)args[0], "scoring")
                    ? throw new InvalidOperationException("Dependency unavailable during rescore")
                    : calculator.ParseCustomFormat((RemoteBook)args[0], (long)args[1])),
                Proxy<IRemoteBookAggregationService>((_, args) => Fails((RemoteBook)args[0], "mapping")
                    ? throw new InvalidOperationException("Dependency unavailable during mapping") : args[0]), _enricher, config, logger);
            return maker;
        }

        private void UseProductionFormat(int score, bool negate)
        {
            _format.Specifications = new() { new AudioProductionSpecification { Negate = negate } };
            _criteria.Author.AudiobookQualityProfile.Value.FormatItems[0].Score = score;
            _nfo = "Publisher: Graphic Audio";
        }

        private void SetUp(bool enabled, bool pinned, bool scoredNarratorFormat)
        {
            _requests.Clear();
            _formatReads = 0;
            _available = true;
            _respond = null;
            _nfo = "Narration: Jim Dale\nLength: 8 hours";
            var settingsJson = JsonSerializer.Serialize(new
            {
                BaseUrl = "https://indexer.example",
                ApiKey = "fixture-key",
                EnableNarratorMetadata = enabled
            });
            _settings = _torznab
                ? JsonSerializer.Deserialize<TorznabSettings>(settingsJson)
                : JsonSerializer.Deserialize<NewznabSettings>(settingsJson);
            var http = Proxy<IIndexerHttpClient>((method, args) =>
            {
                if (method.Name != nameof(IIndexerHttpClient.Execute)) throw new NotImplementedException(method.Name);
                var request = (HttpRequest)args[0];
                _requests.Add(request);
                if (_respond != null)
                {
                    return _respond(request);
                }

                return new HttpResponse(request, new HttpHeader { ContentType = "text/plain" }, _nfo, HttpStatusCode.OK);
            });
            var httpFactory = Proxy<IIndexerHttpClientFactory>((_, _) => http);
            var logger = LogManager.GetLogger(nameof(NarratorMetadataOptInFixture));
            var cache = new CacheManager();
            IIndexer indexer = _torznab
                ? new Torznab(null, httpFactory, null, null, null, cache, logger)
                : new Newznab(null, httpFactory, null, null, null, cache, logger);
            indexer.Definition = new IndexerDefinition { Id = 1, Name = "Fixture", Settings = _settings };
            var factory = Proxy<IIndexerFactory>((method, _) => method.Name switch
            {
                nameof(IIndexerFactory.Find) => indexer.Definition,
                nameof(IIndexerFactory.GetInstance) => indexer,
                _ => throw new NotImplementedException(method.Name)
            });
            _format = new CustomFormat
            {
                Id = 9,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new NarratorNamesSpecification { Names = new[] { "Jim Dale" } }
                }
            };
            var profile = new QualityProfile
            {
                Id = 7,
                FormatItems = new List<ProfileFormatItem>
                {
                    new ProfileFormatItem { Format = new CustomFormat { Id = _format.Id }, Score = scoredNarratorFormat ? 50 : 0 }
                }
            };
            _criteria = new BookSearchCriteria
            {
                Author = new Author { AudiobookQualityProfileId = profile.Id, AudiobookQualityProfile = profile },
                Books = new List<Book>
                {
                    new Book
                    {
                        MediaType = BookMediaType.Audiobook,
                        AnyEditionOk = !pinned,
                        Editions = new List<Edition> { new Edition { ReadingFormatId = 2, Monitored = true, Narrator = "Jim Dale" } }
                    }
                }
            };
            _formats = Proxy<ICustomFormatService>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(ICustomFormatService.All)));
                _formatReads++;
                return _available ? new List<CustomFormat> { _format } : new List<CustomFormat>();
            });
            _enricher = new ReleaseNarratorMetadataEnricher(logger, factory, _formats);
        }

        private static ReleaseInfo Release() => new ReleaseInfo
        {
            Title = "Writer - A Book M4B",
            IndexerId = 1,
            Indexer = "Fixture",
            HasNfo = true,
            NfoUrl = "https://indexer.example/api?t=getnfo&id=fixture-release",
            Guid = "fixture-release"
        };
    }
}

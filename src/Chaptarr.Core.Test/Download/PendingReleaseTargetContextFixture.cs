using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.Parser;
using Chaptarr.Api.V1.Queue;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class PendingReleaseTargetContextFixture
    {
        private sealed class Observed
        {
            public int RepositoryReads { get; set; }
            public int AuthorReads { get; set; }
            public int DelayReads { get; set; }
            public int DelayProfileReads { get; set; }
            public int IndexerReads { get; set; }
            public int DownloadClientReads { get; set; }
            public int Events { get; set; }
            public int Updates { get; set; }
            public List<int> Deleted { get; } = new();
        }

        private static PendingRelease Pending(int id, ParsedBookInfo parsed, params int[] bookIds) => new()
        {
            Id = id, AuthorId = 38, Title = $"release-{id}", ParsedBookInfo = parsed,
            Added = DateTime.UtcNow,
            Release = new ReleaseInfo { Title = $"release-{id}", Indexer = "Test Indexer", IndexerId = 7, PublishDate = DateTime.UtcNow.AddHours(-1) },
            AdditionalInfo = Json.Deserialize<PendingReleaseAdditionalInfo>(new { BookIds = bookIds, ReleaseSource = ReleaseSourceType.InteractiveSearch }.ToJson())
        };

        private static (PendingReleaseService Service, Observed Observed) Service(
            ParsingServiceTitleLookupFixture.Harness harness, List<PendingRelease> pending,
            IPendingReleaseRepository nativeRepository = null, bool configuredClient = false, List<CustomFormat> formats = null)
        {
            var observed = new Observed();
            var repository = nativeRepository ?? ParsingServiceTitleLookupFixture.Proxy<IPendingReleaseRepository>((method, args) =>
            {
                if (method.Name is "All" or "WithoutFallback" or "AllByAuthorId")
                {
                    observed.RepositoryReads++;
                    return method.Name == "AllByAuthorId" ? pending.Where(row => row.AuthorId == (int)args[0]).ToList() : pending.ToList();
                }

                if (method.Name == "Insert")
                {
                    var row = (PendingRelease)args[0];
                    row.Id = pending.Count + 1;
                    pending.Add(row);
                    return row;
                }

                if (method.Name is "Update" or "UpdateMany")
                {
                    observed.Updates++;
                    return null;
                }

                if (method.Name == "DeleteMany")
                {
                    var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                    observed.Deleted.AddRange(ids);
                    pending.RemoveAll(row => ids.Contains(row.Id));
                    return null;
                }

                if (method.Name == "Delete")
                {
                    var id = args[0] is PendingRelease row ? row.Id : (int)args[0];
                    observed.Deleted.Add(id);
                    pending.RemoveAll(row => row.Id == id);
                    return null;
                }

                throw new NotImplementedException(method.Name);
            });
            var authors = ParsingServiceTitleLookupFixture.Proxy<IAuthorService>((method, args) =>
            {
                if (method.Name is nameof(IAuthorService.GetAuthors) or nameof(IAuthorService.GetExistingAuthors))
                {
                    observed.AuthorReads++;
                    return new List<Author> { harness.Author };
                }

                throw new NotImplementedException(method.Name);
            });
            var delays = ParsingServiceTitleLookupFixture.Proxy<IDelayProfileService>((method, args) =>
            {
                observed.DelayReads++;
                if (method.Name == nameof(IDelayProfileService.AllForTags))
                {
                    observed.DelayProfileReads++;
                }
                var profile = new DelayProfile { EnableUsenet = true, PreferredProtocol = DownloadProtocol.Usenet };
                return method.Name == nameof(IDelayProfileService.BestForTags) ? profile : new List<DelayProfile> { profile };
            });
            var tasks = ParsingServiceTitleLookupFixture.Proxy<ITaskManager>((method, args) => DateTime.UtcNow.AddMinutes(5));
            var config = ParsingServiceTitleLookupFixture.Proxy<IConfigService>((method, args) => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
            var calculator = ParsingServiceTitleLookupFixture.Proxy<ICustomFormatCalculationService>((method, args) => formats ?? new List<CustomFormat>());
            var aggregation = ParsingServiceTitleLookupFixture.Proxy<IRemoteBookAggregationService>((method, args) => null);
            var indexers = ParsingServiceTitleLookupFixture.Proxy<IIndexerFactory>((method, args) =>
            {
                observed.IndexerReads++;
                return configuredClient ? new IndexerDefinition { DownloadClientId = 7 } : null;
            });
            var clients = ParsingServiceTitleLookupFixture.Proxy<IDownloadClientFactory>((method, args) =>
            {
                observed.DownloadClientReads++;
                return new DownloadClientDefinition { Id = 7, Name = "Test Client" };
            });
            var events = ParsingServiceTitleLookupFixture.Proxy<IEventAggregator>((method, args) => { observed.Events++; return null; });
            var arguments = new List<object> { ParsingServiceTitleLookupFixture.Proxy<IIndexerStatusService>((method, args) => new List<IndexerStatus>()), repository, authors, harness.Parser, delays, tasks, config,
                calculator, aggregation, clients, indexers, events, LogManager.GetCurrentClassLogger() };
            var constructor = typeof(PendingReleaseService).GetConstructors().Single();
            if (constructor.GetParameters().Length == arguments.Count + 1)
            {
                arguments.Add(harness.Books);
            }

            return ((PendingReleaseService)constructor.Invoke(arguments.ToArray()), observed);
        }

        [TestCase(1)]
        [TestCase(25)]
        public void rejected_pending_rows_should_not_hydrate_authors_or_title_catalogs(int count)
        {
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns") });
            var pending = Enumerable.Range(1, count).Select(id => Pending(id, ParsingServiceTitleLookupFixture.Parsed("Paper Lanterns"), 501)).ToList();
            var rejected = pending.Select(row => new DownloadDecision(new RemoteBook
            {
                Release = row.Release, Books = new List<Book> { new() { Id = 501 } }
            })).ToList();
            var (service, observed) = Service(harness, pending);
            var timer = Stopwatch.StartNew();

            service.Handle(new RssSyncCompleteEvent(new ProcessedDecisions(new(), new(), rejected)));

            timer.Stop();
            Assert.Multiple(() =>
            {
                Assert.That(pending, Is.Empty);
                Assert.That(observed.Deleted.Count, Is.EqualTo(count));
                Assert.That(observed.Events, Is.EqualTo(count));
                Assert.That(observed.RepositoryReads, Is.EqualTo(1));
                Assert.That(observed.AuthorReads + harness.BookReads + harness.EditionReads + harness.SeriesReads + harness.ExistingBookReads, Is.Zero);
            });
            TestContext.Progress.WriteLine($"PENDING_REJECTED count={count} repositoryReads={observed.RepositoryReads} authorReads={observed.AuthorReads} catalogReads={harness.BookReads} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
        }

        [TestCase(1)]
        [TestCase(25)]
        public void saved_pending_targets_should_load_once_without_title_remapping(int count)
        {
            var catalog = Enumerable.Range(1, 1000).Select(id => ParsingServiceTitleLookupFixture.Make(id, "Paper Lanterns")).ToList();
            var harness = new ParsingServiceTitleLookupFixture.Harness(catalog);
            var pending = Enumerable.Range(1, count).Select(id => Pending(id, ParsingServiceTitleLookupFixture.Parsed("Renamed release"), id)).ToList();
            var (service, observed) = Service(harness, pending);
            var timer = Stopwatch.StartNew();

            var remotes = service.GetPendingRemoteBooks(harness.Author.Id);

            timer.Stop();
            Assert.Multiple(() =>
            {
                Assert.That(remotes.Select(remote => remote.Books.Single().Id), Is.EqualTo(Enumerable.Range(1, count)));
                Assert.That(harness.BookReads, Is.Zero);
                Assert.That(harness.ExistingBookReads, Is.EqualTo(1));
                Assert.That(harness.EditionReads, Is.EqualTo(1));
                Assert.That(harness.SeriesReads, Is.EqualTo(1));
                Assert.That(observed.AuthorReads, Is.EqualTo(1));
                Assert.That(remotes.All(remote => remote.ReleaseSource == ReleaseSourceType.InteractiveSearch), Is.True);
            });
            TestContext.Progress.WriteLine($"PENDING_TARGETS count={count} books=1000 existingBatchReads={harness.ExistingBookReads} catalogReads={harness.BookReads} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
        }

        [Test]
        public void partial_deleted_targets_should_keep_survivors_without_adding_title_candidates()
        {
            var survivor = ParsingServiceTitleLookupFixture.Make(501, "Changed title");
            var decoy = ParsingServiceTitleLookupFixture.Make(502, "Paper Lanterns");
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { survivor, decoy });
            var pending = Pending(1, ParsingServiceTitleLookupFixture.Parsed(decoy.Title), 999, survivor.Id);

            var remote = Service(harness, new() { pending }).Service.GetPendingRemoteBooks(harness.Author.Id).Single();

            Assert.That(remote.Books.Select(book => book.Id), Is.EqualTo(new[] { survivor.Id }));
            Assert.That(harness.BookReads, Is.Zero);
        }

        [Test]
        public void all_deleted_captured_targets_should_not_adopt_a_title_replacement()
        {
            var replacement = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns");
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { replacement });
            var pending = Pending(1, ParsingServiceTitleLookupFixture.Parsed(replacement.Title), 999);

            var remote = Service(harness, new() { pending }).Service.GetPendingRemoteBooks(harness.Author.Id).Single();

            Assert.That(remote.Books, Is.Empty);
            Assert.That(harness.BookReads, Is.Zero);
            Assert.That(harness.ExistingBookReads, Is.EqualTo(1));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void existing_ineligible_targets_should_not_fall_back_to_a_different_book(bool differentAuthor)
        {
            var moved = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns", differentAuthor ? BookMediaType.Audiobook : BookMediaType.Ebook,
                authorId: differentAuthor ? 39 : 38);
            var decoy = ParsingServiceTitleLookupFixture.Make(502, "Paper Lanterns");
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { moved, decoy });
            var pending = Pending(1, ParsingServiceTitleLookupFixture.Parsed(decoy.Title), moved.Id);

            var remote = Service(harness, new() { pending }).Service.GetPendingRemoteBooks(harness.Author.Id).Single();

            Assert.That(remote.Books, Is.Empty);
            Assert.That(harness.BookReads, Is.Zero);
        }

        [Test]
        public void unresolved_pending_rows_should_stay_visible_separately_and_removable_without_delay_inference()
        {
            var first = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns");
            var second = ParsingServiceTitleLookupFixture.Make(502, "Paper Lanterns");
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { first, second });
            var pending = new List<PendingRelease>
            {
                Pending(1, ParsingServiceTitleLookupFixture.Parsed(first.Title)),
                Pending(2, ParsingServiceTitleLookupFixture.Parsed("No matching book"))
            };
            var (service, observed) = Service(harness, pending);

            var queue = service.GetPendingQueue();

            Assert.That(queue.Count, Is.EqualTo(2));
            Assert.Multiple(() =>
            {
                Assert.That(queue.All(row => row.Book == null && row.RemoteBook.Books.Count == 0), Is.True);
                Assert.That(queue.Select(row => row.Id).Distinct().Count(), Is.EqualTo(2));
                Assert.That(queue.All(row => row.Status == "warning" && !string.IsNullOrWhiteSpace(row.ErrorMessage)), Is.True);
                Assert.That(queue.All(row => row.EstimatedCompletionTime == null && row.Timeleft == null), Is.True);
                Assert.That(observed.DelayReads, Is.Zero);
                Assert.That(observed.IndexerReads, Is.Zero);
                Assert.That(observed.DownloadClientReads, Is.Zero);
                Assert.That(service.OldestPendingRelease(harness.Author.Id, new[] { first.Id, second.Id }), Is.Null);
            });
            var remove = typeof(PendingReleaseService).GetMethods().Single(method => method.Name == nameof(IPendingReleaseService.RemovePendingQueueItems));
            remove.Invoke(service, new object[] { remove.GetParameters()[0].ParameterType == typeof(int) ? (object)queue[0].Id : new[] { queue[0] } });
            Assert.That(observed.Deleted, Is.EqualTo(new[] { 1 }));
            Assert.That(observed.Events, Is.EqualTo(1));
            Assert.That(pending.Select(row => row.Id), Is.EqualTo(new[] { 2 }));
        }

        [Test]
        public void rss_without_rejected_releases_should_not_read_pending_rows()
        {
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns") });
            var pending = new List<PendingRelease> { Pending(1, ParsingServiceTitleLookupFixture.Parsed("Paper Lanterns")) };
            var (service, observed) = Service(harness, pending);

            service.Handle(new RssSyncCompleteEvent(new ProcessedDecisions(new(), new(), new())));

            Assert.That(observed.RepositoryReads + observed.AuthorReads + harness.BookReads + observed.Events, Is.Zero);
            Assert.That(pending.Count, Is.EqualTo(1));
        }

        [Test]
        public void saved_ebook_targets_should_keep_their_media_side_when_quality_is_unknown()
        {
            var book = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns", BookMediaType.Ebook);
            var audio = ParsingServiceTitleLookupFixture.Make(502, "Paper Lanterns");
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { book, audio });
            var parsed = ParsingServiceTitleLookupFixture.Parsed(book.Title);
            parsed.Quality = new NzbDrone.Core.Qualities.QualityModel(NzbDrone.Core.Qualities.Quality.Unknown);
            parsed.ReleaseTitle = "Test Writer - Paper Lanterns [MP3]";

            var remote = Service(harness, new() { Pending(1, parsed, book.Id) }).Service.GetPendingRemoteBooks(harness.Author.Id).Single();

            Assert.That(remote.Books.Select(target => target.Id), Is.EqualTo(new[] { book.Id }));
            Assert.That(remote.GetPreferredMediaType(), Is.EqualTo(BookMediaType.Ebook));
            Assert.That(harness.BookReads, Is.Zero);
        }

        [Test]
        public void a_deleted_pending_owner_should_not_hide_a_healthy_neighbor_or_load_the_deleted_targets()
        {
            var book = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns");
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { book });
            var missingOwner = Pending(1, ParsingServiceTitleLookupFixture.Parsed(book.Title), 999);
            missingOwner.AuthorId = 99;
            var healthy = Pending(2, ParsingServiceTitleLookupFixture.Parsed(book.Title), book.Id);
            var (service, observed) = Service(harness, new() { missingOwner, healthy });

            var queue = service.GetPendingQueue();

            Assert.Multiple(() =>
            {
                Assert.That(queue.Select(row => row.Book.Id), Is.EqualTo(new[] { book.Id }));
                Assert.That(queue.Single().Author.Id, Is.EqualTo(harness.Author.Id));
                Assert.That(observed.AuthorReads, Is.EqualTo(1));
                Assert.That(harness.ExistingBookReads, Is.EqualTo(1));
                Assert.That(harness.ExistingBookIds, Is.EqualTo(new[] { book.Id }));
                Assert.That(harness.BookReads, Is.Zero);
                Assert.That(observed.Deleted, Is.Empty);
            });
        }

        [TestCase(1)]
        [TestCase(25)]
        public void a_pending_collection_should_load_release_delay_and_indexer_once(int count)
        {
            var catalog = Enumerable.Range(1, count).Select(id => ParsingServiceTitleLookupFixture.Make(id, $"Collection member {id}")).ToList();
            var harness = new ParsingServiceTitleLookupFixture.Harness(catalog);
            var parsed = ParsingServiceTitleLookupFixture.Parsed("Collected works");
            parsed.Discography = true;
            var pending = Pending(1, parsed, catalog.Select(book => book.Id).ToArray());
            var (service, observed) = Service(harness, new() { pending }, configuredClient: true);
            var timer = Stopwatch.StartNew();

            var queue = service.GetPendingQueue();

            timer.Stop();
            Assert.Multiple(() =>
            {
                Assert.That(queue.Select(row => row.Book.Id), Is.EqualTo(catalog.Select(book => book.Id)));
                Assert.That(observed.DelayProfileReads, Is.EqualTo(1));
                Assert.That(observed.IndexerReads, Is.EqualTo(1));
                Assert.That(observed.DownloadClientReads, Is.EqualTo(1));
                Assert.That(queue.All(row => row.DownloadClient == "Test Client"), Is.True);
            });
            TestContext.Progress.WriteLine($"PENDING_COLLECTION books={count} delayProfileReads={observed.DelayProfileReads} indexerReads={observed.IndexerReads} clientReads={observed.DownloadClientReads} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
        }

        [TestCase(BookMediaType.Audiobook, 15)]
        [TestCase(BookMediaType.Ebook, 50)]
        public void saved_target_media_should_select_its_own_custom_format_profile_when_quality_is_unknown(BookMediaType mediaType, int expectedScore)
        {
            var book = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns", mediaType);
            var harness = new ParsingServiceTitleLookupFixture.Harness(new() { book });
            var format = new CustomFormat { Id = 10, Name = "Shared Format", AppliesTo = CustomFormatMediaType.Both };
            harness.Author.AudiobookQualityProfileId = 20;
            harness.Author.AudiobookQualityProfile = new LazyLoaded<QualityProfile>(new QualityProfile
            {
                Id = 20, ProfileType = ProfileType.Audiobook,
                FormatItems = new() { new ProfileFormatItem { Format = format, Score = 15 } }
            });
            harness.Author.EbookQualityProfileId = 30;
            harness.Author.EbookQualityProfile = new LazyLoaded<QualityProfile>(new QualityProfile
            {
                Id = 30, ProfileType = ProfileType.Ebook,
                FormatItems = new() { new ProfileFormatItem { Format = format, Score = 50 } }
            });
            var parsed = ParsingServiceTitleLookupFixture.Parsed(book.Title);
            parsed.Quality = new NzbDrone.Core.Qualities.QualityModel(NzbDrone.Core.Qualities.Quality.Unknown);
            var service = Service(harness, new() { Pending(1, parsed, book.Id) }, formats: new() { format }).Service;

            var remote = service.GetPendingRemoteBooks(harness.Author.Id).Single();

            Assert.That(remote.Books.Select(target => target.Id), Is.EqualTo(new[] { book.Id }));
            Assert.That(remote.CustomFormatScore, Is.EqualTo(expectedScore));
            Assert.That(service.GetPendingQueue().Single().ToResource(false, false).CustomFormatScore, Is.EqualTo(expectedScore));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void pending_decision_targets_should_survive_native_json_persistence_and_a_restart(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var first = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns");
                var duplicate = ParsingServiceTitleLookupFixture.Make(502, "Paper Lanterns");
                var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { first, duplicate });
                var repositoryEvents = ParsingServiceTitleLookupFixture.Proxy<IEventAggregator>((method, args) => null);
                var repository = new PendingReleaseRepository(database, repositoryEvents);
                var remote = new RemoteBook
                {
                    Author = harness.Author, Books = new() { first }, ReleaseSource = ReleaseSourceType.InteractiveSearch,
                    ParsedBookInfo = ParsingServiceTitleLookupFixture.Parsed(first.Title),
                    Release = new ReleaseInfo { Title = "Test Writer - Paper Lanterns", PublishDate = DateTime.UtcNow }
                };

                Service(harness, new(), repository).Service.Add(new DownloadDecision(remote), PendingReleaseReason.Delay);
                var saved = repository.All().Single();
                Assert.That(saved.AdditionalInfo, Is.Not.Null, "The existing JSON column must persist the pending context.");
                Assert.That(saved.AdditionalInfo.ReleaseSource, Is.EqualTo(ReleaseSourceType.InteractiveSearch));
                var persistedIds = JObject.FromObject(saved.AdditionalInfo)["bookIds"] ?? JObject.FromObject(saved.AdditionalInfo)["BookIds"];
                var restored = Service(harness, new(), repository).Service.GetPendingRemoteBooks(harness.Author.Id).Single();

                Assert.Multiple(() =>
                {
                    Assert.That(persistedIds?.Values<int>(), Is.EqualTo(new[] { first.Id }));
                    Assert.That(restored.Books.Select(book => book.Id), Is.EqualTo(new[] { first.Id }));
                    Assert.That(restored.ReleaseSource, Is.EqualTo(ReleaseSourceType.InteractiveSearch));
                    Assert.That(harness.BookReads, Is.Zero);
                    Assert.That(harness.ExistingBookReads, Is.EqualTo(1));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void a_reapproved_legacy_pending_row_should_save_proven_targets_with_one_update(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var first = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns");
                var duplicate = ParsingServiceTitleLookupFixture.Make(502, "Paper Lanterns");
                var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { first, duplicate });
                var events = ParsingServiceTitleLookupFixture.Proxy<IEventAggregator>((method, args) => null);
                var repository = new PendingReleaseRepository(database, events);
                var legacy = Pending(0, ParsingServiceTitleLookupFixture.Parsed(first.Title));
                legacy.Reason = PendingReleaseReason.DownloadClientUnavailable;
                repository.Insert(legacy);
                var persistedLegacy = repository.All().Single();
                Assert.That(persistedLegacy.Title, Is.EqualTo(legacy.Release.Title));
                TestContext.Progress.WriteLine($"PENDING_LEGACY_RELEASE title={persistedLegacy.Title} originalPublishDate={legacy.Release.PublishDate:O} storedPublishDate={persistedLegacy.Release.PublishDate:O}");
                var updates = 0;
                var observedRepository = ParsingServiceTitleLookupFixture.Proxy<IPendingReleaseRepository>((method, args) =>
                {
                    if (method.Name == nameof(IPendingReleaseRepository.AllByAuthorId))
                    {
                        return repository.AllByAuthorId((int)args[0]);
                    }

                    if (method.Name is "Update" or "UpdateMany")
                    {
                        updates++;
                        return method.Invoke(repository, args);
                    }

                    throw new NotImplementedException(method.Name);
                });
                var remote = new RemoteBook
                {
                    Author = harness.Author, Books = new() { first }, ReleaseSource = ReleaseSourceType.Rss,
                    ParsedBookInfo = persistedLegacy.ParsedBookInfo, Release = persistedLegacy.Release
                };

                Service(harness, new(), observedRepository).Service.Add(new DownloadDecision(remote), PendingReleaseReason.Delay);
                var saved = repository.All().Single();
                var restored = Service(harness, new(), repository).Service.GetPendingRemoteBooks(harness.Author.Id).Single();

                Assert.Multiple(() =>
                {
                    Assert.That(updates, Is.EqualTo(1));
                    Assert.That(saved.Id, Is.EqualTo(legacy.Id));
                    Assert.That(saved.Reason, Is.EqualTo(PendingReleaseReason.DownloadClientUnavailable));
                    Assert.That(restored.ReleaseSource, Is.EqualTo(ReleaseSourceType.InteractiveSearch));
                    Assert.That(restored.Books.Select(book => book.Id), Is.EqualTo(new[] { first.Id }));
                    Assert.That(harness.BookReads, Is.Zero);
                });
            });
        }

        [TestCase(false, 1)]
        [TestCase(false, 25)]
        [TestCase(true, 1)]
        [TestCase(true, 25)]
        public void repeated_book_scoped_decisions_should_insert_once_per_batch(bool postgres, int count)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var book = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns");
                var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { book });
                var events = ParsingServiceTitleLookupFixture.Proxy<IEventAggregator>((method, args) => null);
                var repository = new PendingReleaseRepository(database, events);
                var inserts = 0;
                var reads = 0;
                var observedRepository = ParsingServiceTitleLookupFixture.Proxy<IPendingReleaseRepository>((method, args) =>
                {
                    if (method.Name == "Insert") inserts++;
                    if (method.Name == nameof(IPendingReleaseRepository.AllByAuthorId)) reads++;
                    return method.Invoke(repository, args);
                });
                var pending = Pending(0, ParsingServiceTitleLookupFixture.Parsed(book.Title));
                var remote = new RemoteBook
                {
                    Author = harness.Author, Books = new() { book }, ReleaseSource = ReleaseSourceType.Search,
                    ParsedBookInfo = pending.ParsedBookInfo, Release = pending.Release
                };
                var (service, observed) = Service(harness, new(), observedRepository);

                service.AddMany(Enumerable.Range(0, count)
                    .Select(index => Tuple.Create(new DownloadDecision(remote), PendingReleaseReason.DownloadClientUnavailable)).ToList());
                var saved = repository.All().ToList();
                var restored = Service(harness, new(), repository).Service.GetPendingRemoteBooks(harness.Author.Id);

                Assert.Multiple(() =>
                {
                    Assert.That(saved.Count, Is.EqualTo(1));
                    Assert.That(inserts, Is.EqualTo(1));
                    Assert.That(reads, Is.EqualTo(1));
                    Assert.That(observed.Events, Is.EqualTo(1));
                    Assert.That(saved.First().AdditionalInfo.BookIds, Is.EqualTo(new[] { book.Id }));
                    Assert.That(restored.First().Books.Select(target => target.Id), Is.EqualTo(new[] { book.Id }));
                    Assert.That(harness.ExistingBookReads, Is.EqualTo(1));
                    Assert.That(harness.BookReads, Is.Zero);
                });
                TestContext.Progress.WriteLine($"PENDING_BATCH database={database.DatabaseType} decisions={count} reads={reads} inserts={inserts} persistedRows={saved.Count}");
            });
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void identical_reports_for_distinct_book_targets_should_keep_each_context(bool postgres, bool sameWork)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var first = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns");
                var second = ParsingServiceTitleLookupFixture.Make(502, "Paper Lanterns");
                if (sameWork) second.HardcoverBookId = first.HardcoverBookId;
                var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { first, second });
                var events = ParsingServiceTitleLookupFixture.Proxy<IEventAggregator>((method, args) => null);
                var repository = new PendingReleaseRepository(database, events);
                var pending = Pending(0, ParsingServiceTitleLookupFixture.Parsed(first.Title));
                pending.Release.Guid = "same-indexer-guid";
                var decisions = new[] { first, second }.Select(book => Tuple.Create(new DownloadDecision(new RemoteBook
                {
                    Author = harness.Author, Books = new() { book }, ReleaseSource = ReleaseSourceType.Search,
                    ParsedBookInfo = pending.ParsedBookInfo, Release = pending.Release
                }), PendingReleaseReason.Delay)).ToList();

                Service(harness, new(), repository).Service.AddMany(decisions);
                var saved = repository.All().ToList();
                var restored = Service(harness, new(), repository).Service.GetPendingRemoteBooks(harness.Author.Id);
                var queue = Service(harness, new(), repository).Service.GetPendingQueue();

                Assert.Multiple(() =>
                {
                    Assert.That(saved.Count, Is.EqualTo(2));
                    Assert.That(saved.SelectMany(row => row.AdditionalInfo.BookIds), Is.EquivalentTo(new[] { first.Id, second.Id }));
                    Assert.That(restored.Select(remote => remote.Books.Single().Id), Is.EquivalentTo(new[] { first.Id, second.Id }));
                    Assert.That(queue.Select(row => row.Book.Id), Is.EquivalentTo(new[] { first.Id, second.Id }));
                    Assert.That(harness.BookReads, Is.Zero);
                });
            });
        }

        [TestCase(false, "all-deleted")]
        [TestCase(true, "all-deleted")]
        [TestCase(false, "partial-deleted")]
        [TestCase(true, "partial-deleted")]
        [TestCase(false, "collection-subset")]
        [TestCase(true, "collection-subset")]
        public void reapproval_should_preserve_captured_ids_and_keep_new_requests_separate(bool postgres, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var first = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns");
                var second = ParsingServiceTitleLookupFixture.Make(502, "Paper Lanterns");
                var collection = scenario == "collection-subset";
                var catalog = collection ? new List<Book> { first, second } : new List<Book> { second };
                var savedIds = scenario == "all-deleted" ? new[] { 999 } : collection ? new[] { first.Id, second.Id } : new[] { 999, second.Id };
                var expectedIds = savedIds;
                var harness = new ParsingServiceTitleLookupFixture.Harness(catalog);
                var events = ParsingServiceTitleLookupFixture.Proxy<IEventAggregator>((method, args) => null);
                var repository = new PendingReleaseRepository(database, events);
                var pending = Pending(0, ParsingServiceTitleLookupFixture.Parsed(second.Title), savedIds);
                pending.Reason = PendingReleaseReason.DownloadClientUnavailable;
                repository.Insert(pending);
                var original = repository.All().Single();
                var updates = 0;
                var reads = 0;
                var observedRepository = ParsingServiceTitleLookupFixture.Proxy<IPendingReleaseRepository>((method, args) =>
                {
                    if (method.Name == "Update") updates++;
                    if (method.Name == nameof(IPendingReleaseRepository.AllByAuthorId)) reads++;
                    return method.Invoke(repository, args);
                });
                var remote = new RemoteBook
                {
                    Author = harness.Author, Books = new() { second }, ReleaseSource = ReleaseSourceType.Rss,
                    ParsedBookInfo = original.ParsedBookInfo, Release = original.Release
                };

                Service(harness, new(), observedRepository).Service.Add(new DownloadDecision(remote), PendingReleaseReason.Delay);
                var allSaved = repository.All().ToList();
                var saved = allSaved.Single(row => row.Id == original.Id);
                var restored = Service(harness, new(), repository).Service.GetPendingRemoteBooks(harness.Author.Id).Single(remote => remote.Release.PendingContext?.Id == original.Id || remote.Release.Title == original.Title && remote.Books.Count == (scenario == "all-deleted" ? 0 : collection ? 2 : 1));

                Assert.Multiple(() =>
                {
                    Assert.That(saved.Id, Is.EqualTo(original.Id));
                    Assert.That(saved.AdditionalInfo.BookIds, Is.EqualTo(expectedIds));
                    Assert.That(restored.Books.Select(book => book.Id), Is.EqualTo(scenario == "all-deleted" ? Array.Empty<int>() : collection ? expectedIds : new[] { second.Id }));
                    Assert.That(allSaved.Count, Is.EqualTo(scenario == "all-deleted" ? 2 : 1));
                    if (scenario == "all-deleted") Assert.That(allSaved.Single(row => row.Id != original.Id).AdditionalInfo.BookIds, Is.EqualTo(new[] { second.Id }));
                    Assert.That(saved.Reason, Is.EqualTo(PendingReleaseReason.DownloadClientUnavailable));
                    Assert.That(restored.ReleaseSource, Is.EqualTo(ReleaseSourceType.InteractiveSearch));
                    Assert.That(updates, Is.Zero);
                    Assert.That(reads, Is.EqualTo(1));
                    Assert.That(harness.BookReads, Is.Zero);
                    Assert.That(harness.ExistingBookReads, Is.EqualTo(2));
                });
                TestContext.Progress.WriteLine($"PENDING_REAPPROVAL database={database.DatabaseType} scenario={scenario} reads={reads} updates={updates}");
            });
        }

        [Test]
        public void a_pending_remote_without_a_proven_book_should_not_reach_the_download_client()
        {
            var downloadCalls = 0;
            var client = ParsingServiceTitleLookupFixture.Proxy<IDownloadClient>((method, args) =>
            {
                downloadCalls++;
                throw new InvalidOperationException("No client operation is allowed without a target.");
            });
            var provider = ParsingServiceTitleLookupFixture.Proxy<IProvideDownloadClient>((method, args) => client);
            var service = new DownloadService(provider, null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger());
            var remote = new RemoteBook
            {
                Author = new Author { Id = 38, Name = "Test Writer" },
                Release = new ReleaseInfo { Title = "Unidentified pending release" }
            };

            Assert.ThrowsAsync<ArgumentException>(async () => await service.DownloadReport(remote, null));
            Assert.That(downloadCalls, Is.Zero);
        }

        [TestCase(false, BookMediaType.Audiobook, "queue")]
        [TestCase(true, BookMediaType.Audiobook, "queue")]
        [TestCase(false, BookMediaType.Ebook, "queue")]
        [TestCase(true, BookMediaType.Ebook, "queue")]
        [TestCase(false, BookMediaType.Audiobook, "delete")]
        [TestCase(true, BookMediaType.Audiobook, "delete")]
        [TestCase(false, BookMediaType.Ebook, "delete")]
        [TestCase(true, BookMediaType.Ebook, "delete")]
        [TestCase(false, BookMediaType.Audiobook, "missing")]
        [TestCase(true, BookMediaType.Audiobook, "missing")]
        [TestCase(false, BookMediaType.Ebook, "missing")]
        [TestCase(true, BookMediaType.Ebook, "missing")]
        [TestCase(false, BookMediaType.Audiobook, "unmonitor")]
        [TestCase(true, BookMediaType.Audiobook, "unmonitor")]
        [TestCase(false, BookMediaType.Ebook, "unmonitor")]
        [TestCase(true, BookMediaType.Ebook, "unmonitor")]
        public void selected_or_cancelled_targets_should_keep_the_other_alternative_and_cancel_required_collections(bool postgres, BookMediaType media, string action)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var first = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns", media);
                var second = ParsingServiceTitleLookupFixture.Make(502, "Paper Lanterns", media);
                first.SetMonitored(true); second.SetMonitored(true);
                var catalog = new List<Book> { first, second };
                var harness = new ParsingServiceTitleLookupFixture.Harness(catalog);
                var events = ParsingServiceTitleLookupFixture.Proxy<IEventAggregator>((method, args) => null);
                var repository = new PendingReleaseRepository(database, events);
                var alternative = Pending(0, ParsingServiceTitleLookupFixture.Parsed(first.Title, media), first.Id, second.Id);
                alternative.AdditionalInfo.AlternativeCopies = true;
                alternative.AdditionalInfo.TargetEditionIds = new() { [first.Id] = 1501, [second.Id] = 2502 };
                alternative.AdditionalInfo.TargetCustomFormatScores = new() { [first.Id] = 30, [second.Id] = 90 };
                repository.Insert(alternative);
                var required = Pending(0, ParsingServiceTitleLookupFixture.Parsed(first.Title, media), first.Id, second.Id);
                required.Title = "required-collection"; repository.Insert(required);
                var service = Service(harness, new(), repository).Service;
                void Act()
                {
                    if (action == "queue") service.RemovePendingQueueItems(new[] { new NzbDrone.Core.Queue.Queue { Book = first, PendingReleaseId = alternative.Id } });
                    else if (action == "delete")
                    {
                        if (service is IHandle<BookDeletedEvent> handler) handler.Handle(new BookDeletedEvent(first, false, false));
                    }
                    else
                    {
                        if (action == "missing") catalog.Remove(first); else first.SetMonitored(false);
                        service.GetPending();
                    }
                }
                Act();
                var retained = repository.All().Single();
                Assert.Multiple(() =>
                {
                    Assert.That(retained.Id, Is.EqualTo(alternative.Id));
                    Assert.That(retained.AdditionalInfo.BookIds, Is.EqualTo(new[] { second.Id }));
                    Assert.That(retained.AdditionalInfo.TargetEditionIds, Is.EquivalentTo(new Dictionary<int, int> { [second.Id] = 2502 }));
                    Assert.That(retained.AdditionalInfo.TargetCustomFormatScores, Is.EquivalentTo(new Dictionary<int, int> { [second.Id] = 90 }));
                    Assert.That(retained.AdditionalInfo.ReleaseSource, Is.EqualTo(ReleaseSourceType.InteractiveSearch));
                    Assert.That(harness.BookReads, Is.Zero);
                });
                Act();
                Assert.That(repository.All().Single().Id, Is.EqualTo(alternative.Id));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void legacy_hydration_should_capture_ids_once_and_later_unmonitor_should_cancel_the_same_request(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var book = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns"); book.SetMonitored(true);
                var harness = new ParsingServiceTitleLookupFixture.Harness(new() { book });
                var events = ParsingServiceTitleLookupFixture.Proxy<IEventAggregator>((method, args) => null);
                var repository = new PendingReleaseRepository(database, events);
                var row = Pending(0, ParsingServiceTitleLookupFixture.Parsed(book.Title)); repository.Insert(row);
                var service = Service(harness, new(), repository).Service;
                Assert.That(service.GetPendingQueue().Single().Book.Id, Is.EqualTo(book.Id));
                Assert.That(repository.All().Single().AdditionalInfo.BookIds, Is.EqualTo(new[] { book.Id }));
                var reads = harness.BookReads;
                Assert.That(service.GetPendingQueue().Single().Book.Id, Is.EqualTo(book.Id));
                Assert.That(harness.BookReads, Is.EqualTo(reads));
                book.SetMonitored(false);
                Assert.That(service.GetPending(), Is.Empty);
                Assert.That(repository.All(), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void a_failed_legacy_capture_write_should_preserve_the_pending_row_for_retry(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var book = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns"); book.SetMonitored(true);
                var harness = new ParsingServiceTitleLookupFixture.Harness(new() { book });
                var events = ParsingServiceTitleLookupFixture.Proxy<IEventAggregator>((method, args) => null);
                var repository = new PendingReleaseRepository(database, events);
                var row = Pending(0, ParsingServiceTitleLookupFixture.Parsed(book.Title)); repository.Insert(row);
                var failing = ParsingServiceTitleLookupFixture.Proxy<IPendingReleaseRepository>((method, args) =>
                {
                    if (method.Name == "UpdateMany") throw new InvalidOperationException("Pending capture write failed");
                    return method.Invoke(repository, args);
                });
                Assert.Throws<InvalidOperationException>(() => Service(harness, new(), failing).Service.GetPendingQueue());
                var stored = repository.All().Single();
                Assert.That(stored.Id, Is.EqualTo(row.Id));
                Assert.That(stored.AdditionalInfo.BookIds, Is.Empty);
                Assert.That(Service(harness, new(), repository).Service.GetPendingQueue().Single().Book.Id, Is.EqualTo(book.Id));
                Assert.That(repository.All().Single().AdditionalInfo.BookIds, Is.EqualTo(new[] { book.Id }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void a_multi_book_delete_event_should_prune_all_deleted_ids_and_preserve_the_other_target(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var books = Enumerable.Range(501, 3).Select(id => ParsingServiceTitleLookupFixture.Make(id, "Paper Lanterns")).ToList();
                var harness = new ParsingServiceTitleLookupFixture.Harness(books);
                var events = ParsingServiceTitleLookupFixture.Proxy<IEventAggregator>((method, args) => null);
                var repository = new PendingReleaseRepository(database, events);
                var row = Pending(0, ParsingServiceTitleLookupFixture.Parsed(books[0].Title), books.Select(book => book.Id).ToArray());
                row.AdditionalInfo.AlternativeCopies = true;
                row.AdditionalInfo.TargetEditionIds = books.ToDictionary(book => book.Id, book => book.Editions.Single().Id);
                repository.Insert(row);
                var service = Service(harness, new(), repository).Service;
                if (service is IHandle<BookDeletedEvent> handler)
                    handler.Handle(new BookDeletedEvent(books[0], false, false, true, books.Take(2)));
                var stored = repository.All().Single();
                Assert.That(stored.AdditionalInfo.BookIds, Is.EqualTo(new[] { books[2].Id }));
                Assert.That(stored.AdditionalInfo.TargetEditionIds.Keys, Is.EqualTo(new[] { books[2].Id }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void an_unproved_legacy_pending_row_should_keep_its_owner_context_and_remain_durable(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var book = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns"); book.SetMonitored(true);
                var harness = new ParsingServiceTitleLookupFixture.Harness(new() { book });
                var events = ParsingServiceTitleLookupFixture.Proxy<IEventAggregator>((method, args) => null);
                var repository = new PendingReleaseRepository(database, events);
                var row = Pending(0, ParsingServiceTitleLookupFixture.Parsed("No matching book")); repository.Insert(row);
                var report = Service(harness, new(), repository).Service.GetPending().Single();
                Assert.That(report.PendingContext, Is.Not.Null);
                Assert.That(report.PendingContext.AuthorId, Is.EqualTo(harness.Author.Id));
                Assert.That(report.PendingContext.AdditionalInfo.BookIds, Is.Empty);
                Assert.That(repository.All().Single().Id, Is.EqualTo(row.Id));
                Assert.That(harness.BookReads, Is.EqualTo(1));
            });
        }
    }
}

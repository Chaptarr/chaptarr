using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Core.Test.Common;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class ProcessDownloadDecisionsFixture
    {
        [TestCase(0, 1, false)]
        [TestCase(2, 3, false)]
        [TestCase(1, 1, true)]
        [TestCase(0, 0, true)]
        [TestCase(null, 1, true)]
        public async Task seeder_rule_should_reject_without_pending_and_preserve_manual_grabs(int? seeders, int minimumSeeders, bool approved)
        {
            var factory = InterfaceProxy.Proxy<IIndexerFactory>((method, args) => new IndexerDefinition
            {
                Id = 12,
                Settings = new MyAnonaMouseSettings { MinimumSeeders = minimumSeeders }
            });
            var specification = new TorrentSeedingSpecification(factory, LogManager.GetCurrentClassLogger());
            var remote = BuildDecision(new Book { Id = 1, Title = "Red Country", MediaType = BookMediaType.Audiobook }, "Red Country M4B", Quality.M4B).RemoteBook;
            remote.Release = new TorrentInfo { Title = "Joe Abercrombie Red Country M4B", IndexerId = 12, Seeders = seeders, DownloadProtocol = DownloadProtocol.Torrent };
            var verdict = specification.IsSatisfiedBy(remote, null);
            var decision = verdict.Accepted ? new DownloadDecision(remote) : new DownloadDecision(remote,
                new Rejection(verdict.Reason, specification.Type, verdict.CanBypass, verdict.Category, verdict.Severity));
            var pending = new RecordingPendingReleaseService();
            var downloads = new RecordingDownloadService();
            var processor = new ProcessDownloadDecisions(downloads, new ApprovedFirstPrioritizer(), pending,
                new EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());

            var result = await processor.ProcessDecisions(new List<DownloadDecision> { decision });
            var resource = decision.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(decision.Approved, Is.EqualTo(approved));
                Assert.That(decision.Rejected, Is.EqualTo(!approved));
                Assert.That(decision.TemporarilyRejected, Is.False);
                Assert.That(result.Pending, Is.Empty);
                Assert.That(pending.AddManyCalls, Is.Zero);
                Assert.That(downloads.Downloaded, Has.Count.EqualTo(approved ? 1 : 0));
                Assert.That(result.Rejected, Has.Count.EqualTo(approved ? 0 : 1));
                Assert.That(InteractiveSearchVisibility.IsHidden(decision), Is.False);
                Assert.That(ReleaseController.ShouldBlockInteractiveDownload(decision), Is.False);
                Assert.That(resource.CanBypassFilters, Is.EqualTo(!approved));
                Assert.That(resource.HasHardFilters, Is.False);
            });
        }

        [Test]
        public async Task minimum_age_rejection_should_still_wait_in_pending_queue()
        {
            var config = InterfaceProxy.Proxy<IConfigService>((method, args) => 60);
            var specification = new MinimumAgeSpecification(config, LogManager.GetCurrentClassLogger());
            var remote = BuildDecision(new Book { Id = 1, Title = "Red Country" }, "Red Country M4B", Quality.M4B).RemoteBook;
            remote.Release.DownloadProtocol = DownloadProtocol.Usenet;
            var verdict = specification.IsSatisfiedBy(remote, null);
            var decision = new DownloadDecision(remote, new Rejection(verdict.Reason, specification.Type,
                verdict.CanBypass, verdict.Category, verdict.Severity));
            var pending = new RecordingPendingReleaseService();
            var downloads = new RecordingDownloadService();
            var processor = new ProcessDownloadDecisions(downloads, new ApprovedFirstPrioritizer(), pending,
                new EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());

            var result = await processor.ProcessDecisions(new List<DownloadDecision> { decision });

            Assert.That(verdict.Accepted, Is.False);
            Assert.That(result.Pending, Is.EqualTo(new[] { decision }));
            Assert.That(pending.Added.Single().Reason, Is.EqualTo(PendingReleaseReason.Delay));
            Assert.That(downloads.Downloaded, Is.Empty);
        }

        [Test]
        public async Task should_only_grab_one_release_per_book_row_even_when_second_release_is_unknown_text()
        {
            var downloadService = new RecordingDownloadService();
            var subject = new ProcessDownloadDecisions(
                downloadService,
                new ApprovedFirstPrioritizer(),
                new NoopPendingReleaseService(), new Chaptarr.Core.Test.Download.EmptyQueueSpecification(),
                LogManager.GetLogger("ProcessDownloadDecisionsFixture"));

            var book = new Book { Id = 5792, Title = "Best Served Cold", MediaType = BookMediaType.Ebook };
            var first = BuildDecision(book, "Best Served Cold", Quality.AZW3);
            var duplicateUnknown = BuildDecision(book, "Joe Abercrombie - The First Law Trilogy & Best Served Cold", Quality.Unknown);

            var result = await subject.ProcessDecisions(new List<DownloadDecision> { first, duplicateUnknown });

            Assert.That(result.Grabbed, Has.Count.EqualTo(1));
            Assert.That(downloadService.Downloaded, Has.Count.EqualTo(1));
            Assert.That(downloadService.Downloaded[0].ParsedBookInfo.Quality.Quality, Is.EqualTo(Quality.AZW3));
        }

        [Test]
        public async Task central_mam_slot_race_should_return_the_release_to_the_existing_pending_queue()
        {
            var pending = new RecordingPendingReleaseService();
            var subject = new ProcessDownloadDecisions(
                new MamSlotBlockedDownloadService(),
                new ApprovedFirstPrioritizer(),
                pending, new Chaptarr.Core.Test.Download.EmptyQueueSpecification(),
                LogManager.GetLogger("ProcessDownloadDecisionsFixture"));
            var decision = BuildDecision(new Book { Id = 5792, Title = "Book", MediaType = BookMediaType.Ebook }, "Book", Quality.EPUB);

            var result = await subject.ProcessDecisions(new List<DownloadDecision> { decision }, null);

            Assert.Multiple(() =>
            {
                Assert.That(result.Pending, Is.EqualTo(new[] { decision }));
                Assert.That(pending.Added.Single().Decision, Is.SameAs(decision));
                Assert.That(pending.Added.Single().Reason, Is.EqualTo(PendingReleaseReason.Delay));
            });
        }

        [TestCase(true)] [TestCase(false)]
        public async Task eligible_copy_should_include_only_its_same_release_delay_only_top_choice(bool delayedFirst)
        {
            var transport = new RecordingDownloadService();
            var processor = new ProcessDownloadDecisions(transport, new ApprovedFirstPrioritizer(), new NoopPendingReleaseService(), new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
            var approved = Copy(1, "shared");
            var delayed = new DownloadDecision(Copy(2, "shared").RemoteBook, new Rejection("Waiting", RejectionType.Temporary, false, "Delay"));
            var reports = delayedFirst ? new List<DownloadDecision> { delayed, approved } : new List<DownloadDecision> { approved, delayed };
            var result = await processor.ProcessDecisions(reports, 9);
            Assert.That(result.Grabbed, Has.Count.EqualTo(1));
            Assert.That(result.Pending, Is.Empty);
            Assert.That(transport.Downloaded.Single().Books.Select(book => book.Id), Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(transport.ClientIds, Is.EqualTo(new int?[] { 9 }));
        }

        [TestCase("other_temporary")] [TestCase("permanent")] [TestCase("other_release")]
        [TestCase("other_work")] [TestCase("other_media")] [TestCase("preferred_other_release")]
        public async Task eligible_copy_should_not_bypass_any_other_check_or_copy_preference(string difference)
        {
            var transport = new RecordingDownloadService();
            var processor = new ProcessDownloadDecisions(transport, new ApprovedFirstPrioritizer(), new NoopPendingReleaseService(), new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
            var approved = Copy(1, "shared"); var copy = Copy(2, "shared");
            if (difference == "other_release") copy.RemoteBook.Release.Guid = "different";
            if (difference == "other_work") copy.RemoteBook.Books[0].HardcoverBookId = "hc:other";
            if (difference == "other_media") copy.RemoteBook.Books[0].MediaType = BookMediaType.Audiobook;
            var delayed = new DownloadDecision(copy.RemoteBook, new Rejection("Held", difference == "permanent" ? RejectionType.Permanent : RejectionType.Temporary,
                false, difference == "other_temporary" ? "Queue" : "Delay"));
            var decisions = new List<DownloadDecision> { approved, delayed };
            if (difference == "preferred_other_release") decisions.Insert(0, new DownloadDecision(Copy(2, "preferred").RemoteBook,
                new Rejection("Waiting", RejectionType.Temporary, false, "Delay")));
            var result = await processor.ProcessDecisions(decisions);
            Assert.That(transport.Downloaded.Single().Books.Select(book => book.Id), Is.EqualTo(new[] { 1 }));
        }

        [TestCase("client_unavailable")] [TestCase("slot_pending")] [TestCase("unexpected_skip")]
        public async Task copy_dispatch_failure_should_retain_only_explicit_pending_results(string failure)
        {
            var transport = new RecordingDownloadService
            {
                Failure = failure switch
                {
                    "client_unavailable" => new DownloadClientUnavailableException("Unavailable"),
                    "slot_pending" => new MamUnsatisfiedSlotsUnavailableException("Safety pause"),
                    _ => new InvalidOperationException("Unexpected transport failure")
                }
            };
            var pending = new RecordingPendingReleaseService();
            var processor = new ProcessDownloadDecisions(transport, new ApprovedFirstPrioritizer(), pending, new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
            var delayed = new DownloadDecision(Copy(1, "shared").RemoteBook, new Rejection("Waiting", RejectionType.Temporary, false, "Delay"));
            var eligible = Copy(2, "shared");
            eligible.RemoteBook.Release = delayed.RemoteBook.Release;
            var originalPublishDate = delayed.RemoteBook.Release.PublishDate;
            var result = await processor.ProcessDecisions(new List<DownloadDecision> { delayed, eligible });
            Assert.That(result.Grabbed, Is.Empty);
            Assert.That(transport.Downloaded, Has.Count.EqualTo(1));
            Assert.That(transport.Downloaded.Single().Books.Select(book => book.Id), Is.EquivalentTo(new[] { 1, 2 }));
            if (failure == "unexpected_skip")
            {
                Assert.That(pending.Added, Is.Empty);
                Assert.That(result.Pending, Is.Empty);
            }
            else
            {
                Assert.That(pending.Added, Has.Count.EqualTo(1));
                Assert.That(pending.Added.Single().Decision.RemoteBook.Books.Select(book => book.Id), Is.EquivalentTo(new[] { 1, 2 }));
                Assert.That(pending.Added.Single().Decision.RemoteBook.Release.PublishDate, Is.EqualTo(originalPublishDate));
                Assert.That(pending.Added.Single().Reason, Is.EqualTo(failure == "client_unavailable"
                    ? PendingReleaseReason.DownloadClientUnavailable : PendingReleaseReason.Delay));
                Assert.That(result.Pending.Single().RemoteBook.Books.Select(book => book.Id),
                    Is.EquivalentTo(pending.Added.Single().Decision.RemoteBook.Books.Select(book => book.Id)));
            }
        }

        [Test]
        public async Task successful_grab_should_skip_later_delay_for_the_same_book()
        {
            var transport = new RecordingDownloadService(); var pending = new RecordingPendingReleaseService();
            var processor = new ProcessDownloadDecisions(transport, new ApprovedFirstPrioritizer(), pending, new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
            var delayed = new DownloadDecision(Copy(2, "preferred").RemoteBook, new Rejection("Waiting", RejectionType.Temporary, false, "Delay"));
            var approved = Copy(2, "other");
            var result = await processor.ProcessDecisions(new List<DownloadDecision> { delayed, approved });
            Assert.That(transport.Downloaded.Single().Release.Guid, Is.EqualTo("other"));
            Assert.That(transport.Downloaded.Single().Books.Select(book => book.Id), Is.EqualTo(new[] { 2 }));
            Assert.That(pending.Added, Is.Empty);
            Assert.That(result.Pending, Is.Empty);
        }

        [TestCase(true)] [TestCase(false)]
        public async Task selected_manual_views_should_share_grouping_without_adding_unselected_targets(bool selectBoth)
        {
            var transport = new RecordingDownloadService();
            var processor = new ProcessDownloadDecisions(transport, new ApprovedFirstPrioritizer(), new NoopPendingReleaseService(), new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
            var first = Copy(1, "shared"); var second = Copy(2, "shared");
            first.RemoteBook.DownloadAllowed = false; second.RemoteBook.DownloadAllowed = false;
            await processor.ProcessSelectedDecisions(selectBoth ? new List<RemoteBook> { first.RemoteBook, second.RemoteBook } : new() { first.RemoteBook }, 9);
            Assert.That(transport.Downloaded, Has.Count.EqualTo(1));
            Assert.That(transport.Downloaded.Single().Books.Select(book => book.Id), Is.EquivalentTo(selectBoth ? new[] { 1, 2 } : new[] { 1 }));
            Assert.That(transport.Downloaded.Single().DownloadAllowed, Is.False);
            Assert.That(transport.ClientIds.Single(), Is.EqualTo(9));
        }

        [Test]
        public async Task manual_bulk_should_preserve_distinct_selected_reports_for_the_same_book()
        {
            var transport = new RecordingDownloadService();
            var processor = new ProcessDownloadDecisions(transport, new ApprovedFirstPrioritizer(), new NoopPendingReleaseService(), new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
            await processor.ProcessSelectedDecisions(new List<RemoteBook> { Copy(1, "first").RemoteBook, Copy(1, "second").RemoteBook }, 9);
            Assert.That(transport.Downloaded.Select(remote => remote.Release.Guid), Is.EqualTo(new[] { "first", "second" }));
            Assert.That(transport.ClientIds, Is.EqualTo(new int?[] { 9, 9 }));
        }

        [Test]
        public async Task identical_selected_required_context_should_dispatch_once()
        {
            var transport = new RecordingDownloadService();
            var processor = new ProcessDownloadDecisions(transport, new ApprovedFirstPrioritizer(), new NoopPendingReleaseService(), new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
            var collection = Copy(1, "required").RemoteBook;
            collection.AlternativeCopies = false;
            collection.Books.Add(Copy(2, "required").RemoteBook.Books.Single());
            await processor.ProcessSelectedDecisions(new List<RemoteBook> { collection, collection });
            Assert.That(transport.Downloaded, Has.Count.EqualTo(1));
            Assert.That(transport.Downloaded.Single().Books.Select(book => book.Id), Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public async Task selected_captured_group_should_merge_with_an_explicit_singleton_using_each_current_score()
        {
            var transport = new RecordingDownloadService();
            var processor = new ProcessDownloadDecisions(transport, new ApprovedFirstPrioritizer(), new NoopPendingReleaseService(), new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
            var group = Copy(1, "shared").RemoteBook;
            var second = Copy(2, "shared").RemoteBook;
            group.Books.Add(second.Books.Single());
            group.TargetEditionIds[2] = second.TargetEditionIds[2];
            group.TargetCustomFormatScores = new Dictionary<int, int> { [1] = 11, [2] = 22 };
            group.CustomFormatScore = 77;
            var singleton = Copy(3, "shared").RemoteBook;
            singleton.CustomFormatScore = 99;
            singleton.TargetCustomFormatScores[3] = 10;
            await processor.ProcessSelectedDecisions(new List<RemoteBook> { group, group, singleton }, 9);
            Assert.Multiple(() =>
            {
                Assert.That(transport.Downloaded, Has.Count.EqualTo(1));
                Assert.That(transport.Downloaded.Single().Books.Select(book => book.Id), Is.EquivalentTo(new[] { 1, 2, 3 }));
                Assert.That(transport.Downloaded.Single().TargetCustomFormatScores, Is.EquivalentTo(new Dictionary<int, int> { [1] = 11, [2] = 22, [3] = 99 }));
                Assert.That(transport.Downloaded.Single().TargetEditionIds, Is.EquivalentTo(new Dictionary<int, int> { [1] = 11, [2] = 12, [3] = 13 }));
                Assert.That(transport.ClientIds, Is.EqualTo(new int?[] { 9 }));
                Assert.That(group.TargetCustomFormatScores, Is.EquivalentTo(new Dictionary<int, int> { [1] = 11, [2] = 22 }));
                Assert.That(singleton.TargetCustomFormatScores[3], Is.EqualTo(10));
            });
        }

        [TestCase("other_work")] [TestCase("other_media")] [TestCase("other_author")]
        public async Task every_member_of_a_captured_group_should_pass_the_existing_copy_gate(string difference)
        {
            var transport = new RecordingDownloadService();
            var processor = new ProcessDownloadDecisions(transport, new ApprovedFirstPrioritizer(), new NoopPendingReleaseService(), new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
            var group = Copy(1, "shared").RemoteBook;
            var second = Copy(2, "shared").RemoteBook.Books.Single();
            group.Books.Add(second);
            group.TargetEditionIds[2] = 12;
            if (difference == "other_work") second.HardcoverBookId = "hc:other";
            if (difference == "other_media") second.MediaType = BookMediaType.Audiobook;
            if (difference == "other_author") second.AuthorId = 39;
            await processor.ProcessSelectedDecisions(new List<RemoteBook> { group, Copy(3, "shared").RemoteBook });
            Assert.That(transport.Downloaded.Select(remote => remote.Books.Select(book => book.Id).ToArray()), Is.EqualTo(new[] { new[] { 1, 2 }, new[] { 3 } }));
        }

        [Test]
        public async Task an_unaccepted_source_group_should_not_consume_other_explicitly_selected_copies()
        {
            var transport = new RecordingDownloadService();
            var processor = new ProcessDownloadDecisions(transport, new ApprovedFirstPrioritizer(), new NoopPendingReleaseService(), new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
            var group = Copy(1, "shared").RemoteBook;
            var second = Copy(2, "shared").RemoteBook.Books.Single();
            second.HardcoverBookId = "hc:other";
            group.Books.Add(second);
            group.TargetEditionIds[2] = 12;
            await processor.ProcessSelectedDecisions(new List<RemoteBook> { group, Copy(3, "shared").RemoteBook, Copy(4, "shared").RemoteBook });
            Assert.That(transport.Downloaded.Select(remote => remote.Books.Select(book => book.Id).ToArray()), Is.EqualTo(new[] { new[] { 1, 2 }, new[] { 3, 4 } }));
        }

        [Test]
        public async Task every_member_of_a_captured_group_should_remain_at_its_current_queue_head()
        {
            var transport = new RecordingDownloadService();
            var processor = new ProcessDownloadDecisions(transport, new ApprovedFirstPrioritizer(), new NoopPendingReleaseService(), new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger());
            var group = Copy(1, "shared").RemoteBook;
            group.Books.Add(Copy(2, "shared").RemoteBook.Books.Single());
            group.TargetEditionIds[2] = 12;
            var first = Copy(1, "shared").RemoteBook;
            first.TargetEditionIds[1] = 101;
            await processor.ProcessSelectedDecisions(new List<RemoteBook> { first, group, Copy(3, "shared").RemoteBook });
            Assert.Multiple(() =>
            {
                Assert.That(transport.Downloaded.Select(remote => remote.Books.Select(book => book.Id).ToArray()), Is.EqualTo(new[] { new[] { 1, 3 }, new[] { 1, 2 } }));
                Assert.That(transport.Downloaded[0].TargetEditionIds[1], Is.EqualTo(101));
                Assert.That(transport.Downloaded[1].TargetEditionIds[1], Is.EqualTo(11));
            });
        }

        [TestCase("shared_original")] [TestCase("independent_capture")] [TestCase("other_work")]
        [TestCase("other_author")] [TestCase("other_media")] [TestCase("other_temporary")]
        [TestCase("permanent")] [TestCase("earlier_preferred_release")]
        public void all_delay_capture_must_preserve_current_report_and_per_copy_gates(string difference)
        {
            var first = Copy(1, "shared");
            var second = Copy(2, "shared");
            if (difference != "independent_capture") second.RemoteBook.Release = first.RemoteBook.Release;
            if (difference == "other_work") second.RemoteBook.Books[0].HardcoverBookId = "hc:other";
            if (difference == "other_author") second.RemoteBook.Books[0].AuthorId = 39;
            if (difference == "other_media") second.RemoteBook.Books[0].MediaType = BookMediaType.Audiobook;
            var a = new DownloadDecision(first.RemoteBook, new Rejection("Waiting", RejectionType.Temporary, false, "Delay"));
            var b = new DownloadDecision(second.RemoteBook, new Rejection("Waiting", difference == "permanent" ? RejectionType.Permanent : RejectionType.Temporary, false, difference == "other_temporary" ? "Queue" : "Delay"));
            var reports = new List<DownloadDecision> { a, b };
            if (difference == "earlier_preferred_release") reports.Insert(0, new DownloadDecision(Copy(2, "preferred").RemoteBook, new Rejection("Waiting", RejectionType.Temporary, false, "Delay")));
            var pending = new RecordingPendingReleaseService();
            var written = pending.Added;
            var config = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Configuration.IConfigService>((method, args) => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
            var delays = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Profiles.Delay.IDelayProfileService>((method, args) => new NzbDrone.Core.Profiles.Delay.DelayProfile { PreferredProtocol = DownloadProtocol.Usenet });
            var prioritizer = new DownloadDecisionPriorizationService(config, delays, null, LogManager.GetCurrentClassLogger());
            var downloads = 0;
            var transport = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IDownloadService>((method, args) => { downloads++; return Task.CompletedTask; });
            new ProcessDownloadDecisions(transport, prioritizer, pending, new Chaptarr.Core.Test.Download.EmptyQueueSpecification(), LogManager.GetCurrentClassLogger()).ProcessDecisions(reports).GetAwaiter().GetResult();
            Assert.That(downloads, Is.Zero);
            if (difference == "shared_original")
            {
                Assert.That(written, Has.Count.EqualTo(1));
                Assert.That(written.Single().Item1.RemoteBook.Books.Select(book => book.Id), Is.EqualTo(new[] { 1, 2 }));
                Assert.That(written.Single().Item2, Is.EqualTo(PendingReleaseReason.Delay));
            }
            else if (difference == "earlier_preferred_release")
            {
                Assert.That(written.Single(pair => pair.Item1.RemoteBook.Books.Any(book => book.Id == 1)).Item2, Is.EqualTo(PendingReleaseReason.Delay));
                Assert.That(written.Single(pair => pair.Item1.RemoteBook.Release.Guid == "preferred").Item1.RemoteBook.Books.Select(book => book.Id), Is.EqualTo(new[] { 2 }));
            }
            else
            {
                Assert.That(written.All(pair => pair.Item1.RemoteBook.Books.Count == 1), Is.True);
                Assert.That(written, Has.Count.EqualTo(difference == "permanent" ? 1 : 2));
            }
        }

        [Test]
        public async Task dispatch_uses_current_queue_without_waiting_for_initial_poll()
        {
            var events = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Messaging.Events.IEventAggregator>((method, args) => null);
            var history = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.History.IHistoryService>((method, args) => new List<NzbDrone.Core.History.EntityHistory>());
            var jobReads = 0;
            var tracking = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.MediaFiles.IConversionTrackingService>((method, args) => null);
            var jobs = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.MediaFiles.IConversionJobService>((method, args) => { jobReads++; return null; });
            var queue = new NzbDrone.Core.Queue.QueueService(events, history, null, tracking, null, jobs);
            queue.Handle(new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadRefreshedEvent(new()));
            var downloads = new RecordingDownloadService();
            var pending = new RecordingPendingReleaseService();
            var processor = new ProcessDownloadDecisions(downloads, new ApprovedFirstPrioritizer(), pending, new NzbDrone.Core.DecisionEngine.Specifications.QueueSpecification(queue, null, null, null, LogManager.GetCurrentClassLogger()), LogManager.GetCurrentClassLogger());
            var decision = BuildDecision(new Book { Id = 17, MediaType = BookMediaType.Ebook }, "Book", Quality.EPUB);

            var ready = await processor.ProcessDecisions(new List<DownloadDecision> { decision });
            Assert.That(ready.Grabbed, Has.Count.EqualTo(1));
            Assert.That(downloads.Downloaded, Has.Count.EqualTo(1));
            Assert.That(ready.Pending, Is.Empty);
            Assert.That(pending.Added, Is.Empty);

            queue.Handle(new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadRefreshedEvent(new()
            {
                new NzbDrone.Core.Download.TrackedDownloads.TrackedDownload
                {
                    DownloadClient = 1, IsTrackable = true, RemoteBook = decision.RemoteBook,
                    State = NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadState.Downloading,
                    DownloadItem = new DownloadClientItem { DownloadId = "existing-grab", Title = "Book",
                        DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "Client" } }
                }
            }));
            var readsBeforeDecision = jobReads;
            var staleDecision = await processor.ProcessDecisions(new List<DownloadDecision> { decision });
            Assert.That(jobReads, Is.EqualTo(readsBeforeDecision), "Acquisition must not fetch conversion status for queue rows");
            queue.GetQueue();
            Assert.That(jobReads, Is.EqualTo(readsBeforeDecision + 1), "Queue display must still refresh conversion status");
            Assert.That(staleDecision.Grabbed, Is.Empty, "A decision prepared before the first poll must use the recovered queue");
            Assert.That(staleDecision.Rejected, Has.Count.EqualTo(1));
            Assert.That(downloads.Downloaded, Has.Count.EqualTo(1));
            queue.Handle(new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadRefreshedEvent(new()));
        }

        [Test]
        public async Task explicit_manual_selection_does_not_consult_queue()
        {
            var queue = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Queue.IQueueService>((method, args) => throw new InvalidOperationException("Explicit selection must not consult the queue"));
            var specification = new NzbDrone.Core.DecisionEngine.Specifications.QueueSpecification(queue, null, null, null, LogManager.GetCurrentClassLogger());
            var downloads = new RecordingDownloadService();
            var processor = new ProcessDownloadDecisions(downloads, new ApprovedFirstPrioritizer(), new NoopPendingReleaseService(), specification, LogManager.GetCurrentClassLogger());
            var decision = BuildDecision(new Book { Id = 17, MediaType = BookMediaType.Ebook }, "Book", Quality.EPUB);
            await processor.ProcessSelectedDecisions(new List<RemoteBook> { decision.RemoteBook });
            Assert.That(downloads.Downloaded, Has.Count.EqualTo(1));
        }

        private static DownloadDecision Copy(int id, string guid)
        {
            var decision = BuildDecision(new Book { Id = id, AuthorId = 38, Title = "Paper Lanterns", HardcoverBookId = "hc:8200", MediaType = BookMediaType.Ebook }, "Paper Lanterns", Quality.EPUB);
            decision.RemoteBook.AlternativeCopies = true;
            decision.RemoteBook.Release.Guid = guid; decision.RemoteBook.Release.IndexerId = 7;
            decision.RemoteBook.TargetEditionIds[id] = id + 10;
            decision.RemoteBook.CustomFormatScore = id * 100;
            return decision;
        }

        private static DownloadDecision BuildDecision(Book book, string title, Quality quality)
        {
            var author = new Author { Id = 38, Name = "Joe Abercrombie" };
            return new DownloadDecision(new RemoteBook
            {
                Author = author,
                Books = new List<Book> { book },
                Release = new ReleaseInfo
                {
                    Title = title,
                    DownloadProtocol = DownloadProtocol.Torrent,
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    Quality = new QualityModel(quality)
                }
            });
        }

        private sealed class RecordingDownloadService : IDownloadService
        {
            public List<RemoteBook> Downloaded { get; } = new();
            public List<int?> ClientIds { get; } = new();
            public Exception Failure { get; set; }

            public Task DownloadReport(RemoteBook remoteBook, int? downloadClientId)
            {
                Downloaded.Add(remoteBook);
                ClientIds.Add(downloadClientId);
                if (Failure != null)
                {
                    throw Failure;
                }

                return Task.CompletedTask;
            }
        }

        private sealed class MamSlotBlockedDownloadService : IDownloadService
        {
            public Task DownloadReport(RemoteBook remoteBook, int? downloadClientId)
            {
                throw new MamUnsatisfiedSlotsUnavailableException("MAM safety pause");
            }
        }

        private sealed class ApprovedFirstPrioritizer : IPrioritizeDownloadDecision
        {
            public List<DownloadDecision> PrioritizeDecisions(List<DownloadDecision> decisions)
            {
                return decisions.OrderByDescending(decision => decision.Approved).ToList();
            }
        }

        private class NoopPendingReleaseService : IPendingReleaseService
        {
            public virtual void Add(DownloadDecision decision, PendingReleaseReason reason) { }
            public virtual void AddMany(List<Tuple<DownloadDecision, PendingReleaseReason>> decisions) { }
            public List<ReleaseInfo> GetPending() => new();
            public List<ReleaseInfo> GetPending(out IReadOnlyDictionary<(int AuthorId, int BookId), DateTime> oldestPublishDates)
            {
                oldestPublishDates = new Dictionary<(int AuthorId, int BookId), DateTime>();
                return new();
            }
            public List<RemoteBook> GetPendingRemoteBooks(int authorId) => new();
            public List<NzbDrone.Core.Queue.Queue> GetPendingQueue() => new();
            public NzbDrone.Core.Queue.Queue FindPendingQueueItem(int queueId) => null;
            public void RemovePendingQueueItems(IEnumerable<NzbDrone.Core.Queue.Queue> queueItems) { }
            public RemoteBook OldestPendingRelease(int authorId, int[] bookIds) => null;
        }

        private sealed class RecordingPendingReleaseService : NoopPendingReleaseService
        {
            public List<(DownloadDecision Decision, PendingReleaseReason Reason)> Added { get; } = new();
            public int AddManyCalls { get; private set; }

            public override void AddMany(List<Tuple<DownloadDecision, PendingReleaseReason>> decisions)
            {
                AddManyCalls++;
                Added.AddRange(decisions.Select(pair => (pair.Item1, pair.Item2)));
            }

            public override void Add(DownloadDecision decision, PendingReleaseReason reason)
            {
                Added.Add((decision, reason));
            }
        }
    }
}

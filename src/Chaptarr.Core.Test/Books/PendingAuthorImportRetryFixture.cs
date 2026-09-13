using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class PendingAuthorImportRetryFixture
    {
        private class RepositoryProxy : DispatchProxy
        {
            public PendingAuthorImport Active { get; set; }
            public PendingAuthorImport Inserted { get; private set; }
            public PendingAuthorImport Updated { get; private set; }
            public Func<PendingAuthorImport, long, bool> TryUpdate { get; set; }
            public int UnfencedUpdateCount { get; private set; }
            public int FencedUpdateCount { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IPendingAuthorImportRepository.Find):
                        return Active?.Id == (int)args[0] ? Active : null;
                    case nameof(IPendingAuthorImportRepository.GetActiveByProviderId):
                        return Active?.IsActive() == true &&
                               string.Equals(Active.ProviderId, (string)args[0], StringComparison.OrdinalIgnoreCase)
                            ? Active
                            : null;
                    case nameof(IPendingAuthorImportRepository.Insert):
                        Inserted = (PendingAuthorImport)args[0];
                        Inserted.Id = 123;
                        return Inserted;
                    case nameof(IPendingAuthorImportRepository.Update):
                        UnfencedUpdateCount++;
                        Updated = (PendingAuthorImport)args[0];
                        Active = Updated;
                        return Updated;
                    case nameof(IPendingAuthorImportRepository.TryUpdateRequest):
                        FencedUpdateCount++;
                        var candidate = (PendingAuthorImport)args[0];
                        var expectedVersion = (long)args[1];
                        candidate.Version = expectedVersion + 1;
                        if (TryUpdate != null && !TryUpdate(candidate, expectedVersion))
                        {
                            return false;
                        }

                        Updated = candidate;
                        Active = candidate;
                        return true;
                    case nameof(IPendingAuthorImportRepository.TryDelete):
                        var id = (int)args[0];
                        var deleteVersion = (long)args[1];
                        if (Active?.Id != id || Active.Version != deleteVersion)
                        {
                            return false;
                        }

                        Active = null;
                        return true;
                    default:
                        throw new NotImplementedException($"Repository method {targetMethod?.Name} is not implemented by this test proxy");
                }
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.FindByProviderId))
                {
                    return null;
                }

                throw new NotImplementedException($"Author service method {targetMethod?.Name} is not implemented by this test proxy");
            }
        }

        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        [Test]
        public void schedule_retry_should_not_turn_a_transient_wait_into_failure_at_the_legacy_ceiling()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            var events = new RecordingEventAggregator();
            var subject = new PendingAuthorImportService(repository, null, events, LogManager.GetCurrentClassLogger());
            var item = new PendingAuthorImport
            {
                Id = 42,
                ProviderId = "gr:42",
                AttemptCount = 99,
                MaxAttempts = 100,
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Retrying
            };
            var before = DateTime.UtcNow;

            subject.ScheduleRetry(item, PendingAuthorImportRetryReason.AuthorNotYetAvailable);

            Assert.That(item.AttemptCount, Is.EqualTo(100));
            Assert.That(item.MaxAttempts, Is.Zero);
            Assert.That(item.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
            Assert.That(item.AudiobookStatus, Is.EqualTo(PendingImportStatus.Pending));
            Assert.That(item.EbookStatus, Is.EqualTo(PendingImportStatus.NotRequested));
            Assert.That(item.NextAttemptAt, Is.InRange(before.AddMinutes(3.9), DateTime.UtcNow.AddMinutes(6.1)));
            Assert.That(repositoryProxy.Updated, Is.SameAs(item));
            Assert.That(events.Events, Has.None.InstanceOf<PendingAuthorImportFailedEvent>());
        }

        [Test]
        public void expected_version_status_update_should_leave_a_newer_merged_request_unchanged()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            repositoryProxy.Active = new PendingAuthorImport
            {
                Id = 58,
                ProviderId = "gr:58",
                AuthorName = "Frank Herbert",
                RequestedBy = "UserInterface",
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending,
                LastError = "current merged request",
                Version = 6
            };
            var staleItem = new PendingAuthorImport
            {
                Id = 58,
                ProviderId = "gr:58",
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending,
                Version = 5
            };
            var subject = new PendingAuthorImportService(
                repository,
                null,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            var updated = subject.UpdateStatus(staleItem, PendingImportStatus.Failed, "excluded", expectedVersion: 5);

            Assert.That(updated, Is.False);
            Assert.That(repositoryProxy.Active.AudiobookStatus, Is.EqualTo(PendingImportStatus.Pending));
            Assert.That(repositoryProxy.Active.OverallStatus, Is.EqualTo(PendingImportStatus.Pending));
            Assert.That(repositoryProxy.Active.LastError, Is.EqualTo("current merged request"));
            Assert.That(repositoryProxy.Active.Version, Is.EqualTo(6));
            Assert.That(repositoryProxy.FencedUpdateCount, Is.Zero);
            Assert.That(staleItem.OverallStatus, Is.EqualTo(PendingImportStatus.Pending));
        }

        [Test]
        public void expected_version_status_update_should_fail_if_the_same_provider_request_was_replaced()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            repositoryProxy.Active = new PendingAuthorImport
            {
                Id = 59,
                ProviderId = "gr:58",
                AuthorName = "Frank Herbert",
                RequestedBy = "UserInterface",
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending,
                LastError = "replacement request",
                Version = 5
            };
            var staleItem = new PendingAuthorImport
            {
                Id = 58,
                ProviderId = "gr:58",
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending,
                Version = 5
            };
            var subject = new PendingAuthorImportService(
                repository,
                null,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            var updated = subject.UpdateStatus(staleItem, PendingImportStatus.Failed, "excluded", expectedVersion: 5);

            Assert.That(updated, Is.False);
            Assert.That(repositoryProxy.Active.Id, Is.EqualTo(59));
            Assert.That(repositoryProxy.Active.AudiobookStatus, Is.EqualTo(PendingImportStatus.Pending));
            Assert.That(repositoryProxy.Active.LastError, Is.EqualTo("replacement request"));
            Assert.That(repositoryProxy.Active.Version, Is.EqualTo(5));
            Assert.That(repositoryProxy.FencedUpdateCount, Is.Zero);
        }

        [TestCase(PendingImportStatus.Failed, PendingImportStatus.NotRequested, PendingImportStatus.Failed)]
        [TestCase(PendingImportStatus.NotRequested, PendingImportStatus.Failed, PendingImportStatus.Failed)]
        [TestCase(PendingImportStatus.Failed, PendingImportStatus.Failed, PendingImportStatus.Failed)]
        [TestCase(PendingImportStatus.Succeeded, PendingImportStatus.NotRequested, PendingImportStatus.Succeeded)]
        [TestCase(PendingImportStatus.NotRequested, PendingImportStatus.Succeeded, PendingImportStatus.Succeeded)]
        [TestCase(PendingImportStatus.Succeeded, PendingImportStatus.Failed, PendingImportStatus.PartialSuccess)]
        [TestCase(PendingImportStatus.Failed, PendingImportStatus.Succeeded, PendingImportStatus.PartialSuccess)]
        public void overall_status_only_counts_a_requested_format_as_successful(
            PendingImportStatus audio, PendingImportStatus ebook, PendingImportStatus expected)
        {
            var item = new PendingAuthorImport { AudiobookStatus = audio, EbookStatus = ebook };
            item.UpdateOverallStatus();
            Assert.That(item.OverallStatus, Is.EqualTo(expected));
            Assert.That(item.IsActive(), Is.False);
            if (expected == PendingImportStatus.Failed) Assert.That(item.IsComplete(), Is.True);
        }

        [Test]
        public void expected_version_failure_does_not_retry_against_a_request_merged_during_the_write()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var proxy = (RepositoryProxy)repository;
            var item = new PendingAuthorImport
            {
                Id = 58, ProviderId = "gr:58", AuthorName = "Frank Herbert", RequestedBy = "ImportList:Favorites",
                AudiobookStatus = PendingImportStatus.Pending, EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending, Version = 5
            };
            proxy.Active = new PendingAuthorImport
            {
                Id = item.Id, ProviderId = item.ProviderId, RequestedBy = item.RequestedBy,
                AudiobookStatus = item.AudiobookStatus, EbookStatus = item.EbookStatus,
                OverallStatus = item.OverallStatus, Version = item.Version
            };
            proxy.TryUpdate = (_, _) =>
            {
                proxy.Active = new PendingAuthorImport
                {
                    Id = item.Id, ProviderId = item.ProviderId, RequestedBy = "UserInterface",
                    AudiobookStatus = PendingImportStatus.Pending, EbookStatus = PendingImportStatus.NotRequested,
                    OverallStatus = PendingImportStatus.Pending, Version = 6
                };
                return false;
            };
            var subject = new PendingAuthorImportService(repository, null, new RecordingEventAggregator(), LogManager.GetCurrentClassLogger());

            Assert.That(subject.UpdateStatus(item, PendingImportStatus.Failed, "excluded", item.Version), Is.False);
            Assert.That(proxy.FencedUpdateCount, Is.EqualTo(1));
            Assert.That(proxy.Active.RequestedBy, Is.EqualTo("UserInterface"));
            Assert.That(proxy.Active.OverallStatus, Is.EqualTo(PendingImportStatus.Pending));
            Assert.That(item.OverallStatus, Is.EqualTo(PendingImportStatus.Pending));
        }

        [Test]
        public void expected_version_status_update_should_update_the_matching_request()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            repositoryProxy.Active = new PendingAuthorImport
            {
                Id = 58,
                ProviderId = "gr:58",
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending,
                Version = 5
            };
            var item = new PendingAuthorImport
            {
                Id = 58,
                ProviderId = "gr:58",
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending,
                Version = 5
            };
            var subject = new PendingAuthorImportService(
                repository,
                null,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            var updated = subject.UpdateStatus(item, PendingImportStatus.Failed, "author excluded", expectedVersion: 5);

            Assert.That(updated, Is.True);
            Assert.That(repositoryProxy.Active.AudiobookStatus, Is.EqualTo(PendingImportStatus.Failed));
            Assert.That(repositoryProxy.Active.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
            Assert.That(repositoryProxy.Active.LastError, Is.EqualTo("author excluded"));
            Assert.That(repositoryProxy.Active.Version, Is.EqualTo(6));
            Assert.That(item.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
            Assert.That(item.Version, Is.EqualTo(6));
            Assert.That(repositoryProxy.FencedUpdateCount, Is.EqualTo(1));
        }

        [Test]
        public async Task new_pending_import_should_persist_zero_as_the_unbounded_retry_marker()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new PendingAuthorImportService(
                repository,
                authorService,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            var id = await subject.EnqueueAsync(
                "gr:123",
                new MonitoringConfig { CreateAudiobook = true },
                "test");

            Assert.That(id, Is.EqualTo(123));
            Assert.That(repositoryProxy.Inserted.MaxAttempts, Is.Zero);
            Assert.That(repositoryProxy.Inserted.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
        }

        [Test]
        public async Task pending_import_should_persist_and_update_the_last_requested_media_type()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new PendingAuthorImportService(
                repository,
                authorService,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            await subject.EnqueueAsync(
                "gr:123",
                new MonitoringConfig
                {
                    CreateAudiobook = true,
                    LastSelectedMediaType = "audiobook"
                },
                "test");

            Assert.That(repositoryProxy.Inserted.LastSelectedMediaType, Is.EqualTo("audiobook"));

            repositoryProxy.Active = repositoryProxy.Inserted;
            await subject.EnqueueAsync(
                "gr:123",
                new MonitoringConfig
                {
                    CreateEbook = true,
                    LastSelectedMediaType = "ebook"
                },
                "test");

            Assert.That(repositoryProxy.Updated.LastSelectedMediaType, Is.EqualTo("ebook"));
        }

        [Test]
        public async Task direct_request_should_upgrade_import_list_origin_on_the_same_provider_author()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            repositoryProxy.Active = new PendingAuthorImport
            {
                Id = 58,
                ProviderId = "gr:58",
                AuthorName = "Frank Herbert",
                RequestedBy = "ImportList:Favorites",
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending,
                Version = 1
            };
            var subject = new PendingAuthorImportService(
                repository,
                DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(),
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            var id = await subject.EnqueueAsync(
                "gr:58",
                new MonitoringConfig
                {
                    CreateAudiobook = true
                },
                "UserInterface");

            Assert.That(id, Is.EqualTo(58));
            Assert.That(repositoryProxy.Active.RequestedBy, Is.EqualTo("UserInterface"));
            Assert.That(repositoryProxy.Active.AuthorName, Is.EqualTo("Frank Herbert"));
        }

        [Test]
        public async Task later_import_list_request_should_not_downgrade_direct_demand()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            repositoryProxy.Active = new PendingAuthorImport
            {
                Id = 58,
                ProviderId = "gr:58",
                AuthorName = "Frank Herbert",
                RequestedBy = "UserInterface",
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending,
                Version = 1
            };
            var subject = new PendingAuthorImportService(
                repository,
                DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(),
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            var id = await subject.EnqueueAsync(
                "gr:58",
                new MonitoringConfig
                {
                    CreateAudiobook = true,
                    RequestedBy = "ImportList:Favorites"
                },
                "ImportList");

            Assert.That(id, Is.EqualTo(58));
            Assert.That(repositoryProxy.Active.RequestedBy, Is.EqualTo("UserInterface"));
        }

        [Test]
        public async Task pending_imports_should_remain_scoped_to_the_exact_goodreads_author_id()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            repositoryProxy.Active = new PendingAuthorImport
            {
                Id = 58,
                ProviderId = "gr:58",
                AuthorName = "Frank Herbert",
                RequestedBy = "ImportList:Favorites",
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending,
                Version = 1
            };
            var subject = new PendingAuthorImportService(
                repository,
                DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(),
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            await subject.EnqueueAsync(
                "gr:580",
                new MonitoringConfig
                {
                    CreateAudiobook = true,
                    RequestedBy = "UserInterface"
                },
                "UserInterface");

            Assert.That(repositoryProxy.Active.ProviderId, Is.EqualTo("gr:58"));
            Assert.That(repositoryProxy.Active.RequestedBy, Is.EqualTo("ImportList:Favorites"));
            Assert.That(repositoryProxy.Inserted.ProviderId, Is.EqualTo("gr:580"));
        }

        [Test]
        public async Task pending_import_should_preserve_distinct_media_tags_and_explicit_empty_sets()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new PendingAuthorImportService(
                repository,
                authorService,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            await subject.EnqueueAsync(
                "gr:123",
                new MonitoringConfig
                {
                    CreateAudiobook = true,
                    CreateEbook = true,
                    AudiobookTags = new HashSet<int> { 2, 1 },
                    EbookTags = new HashSet<int>(),
                    Tags = new HashSet<int> { 99 }
                },
                "test");

            Assert.That(repositoryProxy.Inserted.AudiobookTags, Is.EqualTo("[1,2]"));
            Assert.That(repositoryProxy.Inserted.EbookTags, Is.EqualTo("[]"));
            Assert.That(repositoryProxy.Inserted.Tags, Is.EqualTo("[99]"));
        }

        [Test]
        public async Task concurrent_pending_requests_should_union_tags_without_crossing_media_sides()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new PendingAuthorImportService(
                repository,
                authorService,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            await subject.EnqueueAsync(
                "gr:123",
                new MonitoringConfig
                {
                    CreateAudiobook = true,
                    CreateEbook = true,
                    AudiobookTags = new HashSet<int> { 1 },
                    EbookTags = new HashSet<int> { 20 }
                },
                "test");

            repositoryProxy.Active = repositoryProxy.Inserted;
            await subject.EnqueueAsync(
                "gr:123",
                new MonitoringConfig
                {
                    CreateAudiobook = true,
                    AudiobookTags = new HashSet<int> { 2 }
                },
                "test");

            Assert.That(repositoryProxy.Active.AudiobookTags, Is.EqualTo("[1,2]"));
            Assert.That(repositoryProxy.Active.EbookTags, Is.EqualTo("[20]"));
        }

        [Test]
        public async Task pending_import_should_persist_and_merge_media_specific_book_searches()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new PendingAuthorImportService(
                repository,
                authorService,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            var firstId = await subject.EnqueueAsync(
                "gr:123",
                new MonitoringConfig
                {
                    CreateAudiobook = true,
                    CreateEbook = false,
                    AudiobookMonitored = true,
                    AudiobookMonitorNewItems = NewItemMonitorTypes.None,
                    AudiobookBooksToSearch = new List<string> { "gr:1001" },
                    AudiobookBooksToMonitor = new List<string> { "gr:3001" }
                },
                "test");

            repositoryProxy.Active = repositoryProxy.Inserted;
            var secondId = await subject.EnqueueAsync(
                "gr:123",
                new MonitoringConfig
                {
                    CreateAudiobook = true,
                    CreateEbook = true,
                    AudiobookMonitored = true,
                    AudiobookBooksToSearch = new List<string> { "GR:1001", "gr:1002" },
                    AudiobookBooksToMonitor = new List<string> { "GR:3001", "gr:3002" },
                    EbookBooksToSearch = new List<string> { "gr:2001" },
                    SearchForMissingBooks = true
                },
                "test");

            Assert.That(firstId, Is.EqualTo(123));
            Assert.That(secondId, Is.EqualTo(123));
            Assert.That(repositoryProxy.Updated, Is.SameAs(repositoryProxy.Active));
            Assert.That(repositoryProxy.Active.AudiobookBooksToSearch, Is.EqualTo("[\"gr:1001\",\"gr:1002\"]"));
            Assert.That(repositoryProxy.Active.AudiobookBooksToMonitor, Is.EqualTo("[\"gr:3001\",\"gr:3002\"]"));
            Assert.That(repositoryProxy.Active.AudiobookMonitored, Is.True);
            Assert.That(repositoryProxy.Active.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(repositoryProxy.Active.EbookBooksToSearch, Is.EqualTo("[\"gr:2001\"]"));
            Assert.That(repositoryProxy.Active.SearchForMissingBooks, Is.True);
        }

        [Test]
        public async Task pending_import_should_keep_current_book_seed_separate_from_new_item_policy()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new PendingAuthorImportService(
                repository,
                authorService,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            await subject.EnqueueAsync(
                "gr:124",
                new MonitoringConfig
                {
                    CreateAudiobook = true,
                    CreateEbook = true,
                    AudiobookMonitored = true,
                    AudiobookMonitorExistingMode = MonitorTypes.None,
                    AudiobookMonitorNewItems = NewItemMonitorTypes.All,
                    EbookMonitored = true,
                    EbookMonitorExistingMode = MonitorTypes.All,
                    EbookMonitorNewItems = NewItemMonitorTypes.None
                },
                "test");

            Assert.That(repositoryProxy.Inserted.AudiobookMonitored, Is.True);
            Assert.That(repositoryProxy.Inserted.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.None));
            Assert.That(repositoryProxy.Inserted.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
            Assert.That(repositoryProxy.Inserted.EbookMonitored, Is.True);
            Assert.That(repositoryProxy.Inserted.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
            Assert.That(repositoryProxy.Inserted.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));

            // A later exact request may widen only the current-book seed side. It
            // must not overwrite the independently saved new-item policy.
            repositoryProxy.Active = repositoryProxy.Inserted;
            await subject.EnqueueAsync(
                "gr:124",
                new MonitoringConfig
                {
                    CreateAudiobook = true,
                    CreateEbook = false,
                    AudiobookMonitored = true,
                    AudiobookMonitorExistingMode = MonitorTypes.All,
                    AudiobookMonitorNewItems = NewItemMonitorTypes.None
                },
                "test");

            Assert.That(repositoryProxy.Active.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
            Assert.That(repositoryProxy.Active.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
            Assert.That(repositoryProxy.Active.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
            Assert.That(repositoryProxy.Active.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
        }

        [Test]
        public async Task concurrent_requests_should_retry_the_merge_without_losing_either_book_target()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new PendingAuthorImportService(
                repository,
                authorService,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            repositoryProxy.Active = new PendingAuthorImport
            {
                Id = 123,
                ProviderId = "gr:123",
                Version = 4,
                AudiobookStatus = PendingImportStatus.Pending,
                OverallStatus = PendingImportStatus.Pending,
                AudiobookBooksToMonitor = "[\"gr:first\"]"
            };

            var firstAttempt = true;
            repositoryProxy.TryUpdate = (_, _) =>
            {
                if (!firstAttempt)
                {
                    return true;
                }

                firstAttempt = false;
                repositoryProxy.Active = new PendingAuthorImport
                {
                    Id = 123,
                    ProviderId = "gr:123",
                    Version = 5,
                    AudiobookStatus = PendingImportStatus.Pending,
                    OverallStatus = PendingImportStatus.Pending,
                    AudiobookBooksToMonitor = "[\"gr:first\",\"gr:concurrent\"]"
                };
                return false;
            };

            var id = await subject.EnqueueAsync(
                "gr:123",
                new MonitoringConfig
                {
                    CreateAudiobook = true,
                    AudiobookBooksToMonitor = new List<string> { "gr:incoming" }
                },
                "test");

            Assert.That(id, Is.EqualTo(123));
            Assert.That(repositoryProxy.Active.AudiobookBooksToMonitor,
                Is.EqualTo("[\"gr:first\",\"gr:concurrent\",\"gr:incoming\"]"));
            Assert.That(repositoryProxy.Active.Version, Is.EqualTo(6));
        }

        [Test]
        [Timeout(1000)]
        public void retry_should_stop_if_the_pending_row_became_inactive_during_the_update()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            repositoryProxy.TryUpdate = (_, _) => false;
            var subject = new PendingAuthorImportService(
                repository,
                null,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());
            var item = new PendingAuthorImport
            {
                Id = 42,
                ProviderId = "gr:42",
                AudiobookStatus = PendingImportStatus.Pending,
                OverallStatus = PendingImportStatus.Pending
            };

            subject.ScheduleRetry(item, "not ready");

            Assert.That(repositoryProxy.Updated, Is.Null);
        }

        [Test]
        public void cancel_should_survive_a_stale_processing_retry()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            repositoryProxy.Active = new PendingAuthorImport
            {
                Id = 42,
                ProviderId = "gr:42",
                Version = 4,
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending
            };
            var staleProcessingItem = new PendingAuthorImport
            {
                Id = 42,
                ProviderId = "gr:42",
                Version = 4,
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending
            };
            var updateAttempt = 0;
            repositoryProxy.TryUpdate = (_, _) => ++updateAttempt == 1;
            var subject = new PendingAuthorImportService(
                repository,
                null,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            subject.Cancel(42);
            subject.ScheduleRetry(staleProcessingItem, "not ready");

            Assert.That(repositoryProxy.Active.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
            Assert.That(repositoryProxy.Active.LastError, Is.EqualTo("Cancelled by user"));
            Assert.That(repositoryProxy.Active.Version, Is.EqualTo(5));
            Assert.That(repositoryProxy.FencedUpdateCount, Is.EqualTo(2));
            Assert.That(repositoryProxy.UnfencedUpdateCount, Is.Zero);
        }

        [Test]
        public void cancel_should_make_a_stale_success_delete_miss_and_retain_the_cancelled_row()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            repositoryProxy.Active = new PendingAuthorImport
            {
                Id = 42,
                ProviderId = "gr:42",
                Version = 4,
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending
            };
            var staleProcessingItem = new PendingAuthorImport
            {
                Id = 42,
                ProviderId = "gr:42",
                Version = 4,
                AudiobookStatus = PendingImportStatus.Pending,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Pending
            };
            var subject = new PendingAuthorImportService(
                repository,
                null,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            subject.Cancel(42);

            Assert.That(subject.TryDeleteIfUnchanged(staleProcessingItem), Is.False);
            Assert.That(repositoryProxy.Active.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
            Assert.That(repositoryProxy.Active.LastError, Is.EqualTo("Cancelled by user"));
            Assert.That(repositoryProxy.Active.Version, Is.EqualTo(5));
        }

        [Test]
        public void retry_now_should_refetch_after_a_version_miss_and_preserve_concurrent_request_data()
        {
            var repository = DispatchProxy.Create<IPendingAuthorImportRepository, RepositoryProxy>();
            var repositoryProxy = (RepositoryProxy)repository;
            repositoryProxy.Active = new PendingAuthorImport
            {
                Id = 42,
                ProviderId = "gr:42",
                Version = 7,
                AudiobookStatus = PendingImportStatus.Failed,
                EbookStatus = PendingImportStatus.NotRequested,
                OverallStatus = PendingImportStatus.Failed,
                AudiobookBooksToSearch = "[\"gr:first\"]"
            };
            var firstAttempt = true;
            repositoryProxy.TryUpdate = (_, _) =>
            {
                if (!firstAttempt)
                {
                    return true;
                }

                firstAttempt = false;
                repositoryProxy.Active = new PendingAuthorImport
                {
                    Id = 42,
                    ProviderId = "gr:42",
                    Version = 8,
                    AudiobookStatus = PendingImportStatus.Failed,
                    EbookStatus = PendingImportStatus.NotRequested,
                    OverallStatus = PendingImportStatus.Failed,
                    AudiobookBooksToSearch = "[\"gr:first\",\"gr:concurrent\"]"
                };
                return false;
            };
            var subject = new PendingAuthorImportService(
                repository,
                null,
                new RecordingEventAggregator(),
                LogManager.GetCurrentClassLogger());

            subject.RetryNow(42);

            Assert.That(repositoryProxy.Active.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
            Assert.That(repositoryProxy.Active.AudiobookBooksToSearch, Is.EqualTo("[\"gr:first\",\"gr:concurrent\"]"));
            Assert.That(repositoryProxy.Active.Version, Is.EqualTo(9));
            Assert.That(repositoryProxy.FencedUpdateCount, Is.EqualTo(2));
            Assert.That(repositoryProxy.UnfencedUpdateCount, Is.Zero);
        }
    }
}

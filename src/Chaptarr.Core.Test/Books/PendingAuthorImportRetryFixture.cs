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

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IPendingAuthorImportRepository.GetActiveByProviderId):
                        return Active;
                    case nameof(IPendingAuthorImportRepository.Insert):
                        Inserted = (PendingAuthorImport)args[0];
                        Inserted.Id = 123;
                        return Inserted;
                    case nameof(IPendingAuthorImportRepository.Update):
                        Updated = (PendingAuthorImport)args[0];
                        return Updated;
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
            Assert.That(repositoryProxy.Active.EbookBooksToSearch, Is.EqualTo("[\"gr:2001\"]"));
            Assert.That(repositoryProxy.Active.SearchForMissingBooks, Is.True);
        }
    }
}

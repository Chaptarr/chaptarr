using System;
using System.Collections.Generic;
using System.Reflection;
using Chaptarr.Api.V1.Notifications;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class NotificationUpgradeDispatchFixture
    {
        private sealed class DispatchCounts
        {
            public int ReleaseImports { get; set; }
            public int QueueProcesses { get; set; }
        }

        private sealed class RecordingNotification : NotificationBase<TestNotificationSettings>
        {
            private readonly DispatchCounts _counts;

            public RecordingNotification(DispatchCounts counts)
            {
                _counts = counts;
            }

            public override string Name => "Recording notification";
            public override string Link => "https://example.com";

            public override ValidationResult Test()
            {
                return new ValidationResult();
            }

            public override void OnReleaseImport(BookDownloadMessage message)
            {
                _counts.ReleaseImports++;
            }

            public override void ProcessQueue()
            {
                _counts.QueueProcesses++;
            }
        }

        private sealed class TestNotificationSettings : IProviderConfig
        {
            public TestNotificationSettings()
            {
            }

            public NzbDroneValidationResult Validate()
            {
                return new NzbDroneValidationResult();
            }
        }

        private sealed class RecordingProviderContainer : IServiceProvider
        {
            private readonly DispatchCounts _counts;

            public RecordingProviderContainer(DispatchCounts counts)
            {
                _counts = counts;
            }

            public object GetService(Type serviceType)
            {
                return serviceType == typeof(RecordingNotification)
                    ? new RecordingNotification(_counts)
                    : null;
            }
        }

        private class NotificationRepositoryProxy : DispatchProxy
        {
            public IEnumerable<NotificationDefinition> Definitions { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "All")
                {
                    return Definitions;
                }

                throw new NotImplementedException($"Test repository does not implement {targetMethod.Name}");
            }
        }

        private sealed class RecordingNotificationStatusService : INotificationStatusService
        {
            public List<int> SuccessfulProviderIds { get; } = new List<int>();

            public List<NotificationStatus> GetBlockedProviders()
            {
                return new List<NotificationStatus>();
            }

            public void RecordSuccess(int providerId)
            {
                SuccessfulProviderIds.Add(providerId);
            }

            public void RecordFailure(int providerId, TimeSpan minimumBackOff = default(TimeSpan))
            {
                throw new AssertionException("The notification handler should not record a failure.");
            }

            public void RecordConnectionFailure(int providerId)
            {
                throw new AssertionException("The notification handler should not record a connection failure.");
            }
        }

        [Test]
        public void should_round_trip_upgrade_only_enablement_through_notification_resource()
        {
            var mapper = new NotificationResourceMapper();
            var definition = new NotificationDefinition
            {
                Implementation = nameof(RecordingNotification),
                OnUpgrade = true
            };

            var resource = mapper.ToResource(definition);
            var roundTripped = mapper.ToModel(resource);

            Assert.That(resource.OnUpgrade, Is.True);
            Assert.That(resource.Enable, Is.True);
            Assert.That(roundTripped.OnUpgrade, Is.True);
            Assert.That(roundTripped.Enable, Is.True);
        }

        [TestCase(false, false, false, false)]
        [TestCase(true, false, false, true)]
        [TestCase(false, true, false, false)]
        [TestCase(true, true, false, true)]
        [TestCase(false, false, true, false)]
        [TestCase(true, false, true, false)]
        [TestCase(false, true, true, true)]
        [TestCase(true, true, true, true)]
        public void should_dispatch_only_the_selected_import_or_upgrade_notification(
            bool onReleaseImport,
            bool onUpgrade,
            bool isUpgrade,
            bool shouldSend)
        {
            var counts = new DispatchCounts();
            var statusService = new RecordingNotificationStatusService();
            var definition = new NotificationDefinition
            {
                Id = 43,
                Name = "Recording notification",
                Implementation = nameof(RecordingNotification),
                Settings = new TestNotificationSettings(),
                OnReleaseImport = onReleaseImport,
                OnUpgrade = onUpgrade
            };
            var repository = DispatchProxy.Create<INotificationRepository, NotificationRepositoryProxy>();
            ((NotificationRepositoryProxy)repository).Definitions = new[] { definition };
            var provider = new RecordingNotification(counts);
            var factory = new NotificationFactory(
                statusService,
                repository,
                new INotification[] { provider },
                new RecordingProviderContainer(counts),
                null,
                LogManager.GetCurrentClassLogger());
            var service = new NotificationService(factory, statusService, null, LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Id = 11,
                Name = "Test author"
            };
            var importedBook = new BookFile();
            var oldFiles = isUpgrade ? new List<BookFile> { new BookFile() } : new List<BookFile>();
            var message = new BookImportedEvent(
                author,
                new Book { Id = 12, Title = "Test book" },
                new List<BookFile> { importedBook },
                oldFiles,
                true,
                null);

            service.Handle(message);

            Assert.That(counts.ReleaseImports, Is.EqualTo(shouldSend ? 1 : 0));
            Assert.That(statusService.SuccessfulProviderIds, Is.EqualTo(shouldSend ? new[] { definition.Id } : Array.Empty<int>()));
            Assert.That(counts.QueueProcesses, Is.EqualTo(definition.Enable ? 1 : 0));
        }

        [TestCase(1, true)]
        [TestCase(2, false)]
        public void should_keep_author_tag_filtering_and_process_queue_for_upgrade_notifications(int authorTag, bool shouldSend)
        {
            var counts = new DispatchCounts();
            var statusService = new RecordingNotificationStatusService();
            var definition = new NotificationDefinition
            {
                Id = 44,
                Name = "Recording notification",
                Implementation = nameof(RecordingNotification),
                Settings = new TestNotificationSettings(),
                OnUpgrade = true,
                Tags = new HashSet<int> { 1 }
            };
            var repository = DispatchProxy.Create<INotificationRepository, NotificationRepositoryProxy>();
            ((NotificationRepositoryProxy)repository).Definitions = new[] { definition };
            var factory = new NotificationFactory(
                statusService,
                repository,
                new INotification[] { new RecordingNotification(counts) },
                new RecordingProviderContainer(counts),
                null,
                LogManager.GetCurrentClassLogger());
            var service = new NotificationService(factory, statusService, null, LogManager.GetCurrentClassLogger());
            var author = new Author
            {
                Id = 21,
                Name = "Tagged author",
                Tags = new HashSet<int> { authorTag }
            };

            service.Handle(new BookImportedEvent(
                author,
                new Book { Id = 22, Title = "Upgraded book" },
                new List<BookFile> { new BookFile() },
                new List<BookFile> { new BookFile() },
                true,
                null));

            Assert.That(counts.ReleaseImports, Is.EqualTo(shouldSend ? 1 : 0));
            Assert.That(statusService.SuccessfulProviderIds, Is.EqualTo(shouldSend ? new[] { definition.Id } : Array.Empty<int>()));
            Assert.That(counts.QueueProcesses, Is.EqualTo(1));
        }
    }
}

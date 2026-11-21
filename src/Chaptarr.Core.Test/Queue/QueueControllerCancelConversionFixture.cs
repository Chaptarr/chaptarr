using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Chaptarr.Api.V1.Queue;
using Chaptarr.Http.REST;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;

namespace Chaptarr.Core.Test.Queue
{
    [TestFixture]
    public class QueueControllerCancelConversionFixture
    {
        private const int QueueItemId = 42;

        private sealed class NoOpEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        private class QueueServiceProxy : DispatchProxy
        {
            public NzbDrone.Core.Queue.Queue QueueItem { get; set; }
            public int? RequestedId { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IQueueService.Find))
                {
                    RequestedId = (int)args[0];
                    return QueueItem;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class ProfileRepositoryProxy : DispatchProxy
        {
            public IEnumerable<QualityProfile> Profiles { get; set; } = new[]
            {
                new QualityProfile
                {
                    Id = 1,
                    Items = new List<QualityProfileQualityItem>
                    {
                        new() { Quality = Quality.MP3, Allowed = true }
                    }
                }
            };

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IProfileRepository.All))
                {
                    return Profiles;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class ConversionJobServiceProxy : DispatchProxy
        {
            public bool CancelResult { get; set; }
            public List<string> CancelCalls { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IConversionJobService.Cancel))
                {
                    CancelCalls.Add((string)args[0]);
                    return CancelResult;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        [Test]
        public void should_fall_back_to_inline_cancellation_when_no_durable_job_exists()
        {
            var downloadId = Guid.NewGuid().ToString("N");
            using var cancellation = new CancellationTokenSource();
            var tracking = CreateTrackingService();
            tracking.Start(downloadId, 10, "M4B", "Converting to M4B");
            tracking.RegisterCancellation(downloadId, cancellation);
            var durable = CreateJobService(cancelResult: false);
            var setup = BuildController(downloadId, tracking, durable.Service);

            try
            {
                Assert.DoesNotThrow(() => setup.Controller.CancelConversion(QueueItemId));

                Assert.Multiple(() =>
                {
                    Assert.That(setup.QueueProxy.RequestedId, Is.EqualTo(QueueItemId));
                    Assert.That(durable.Proxy.CancelCalls, Is.EqualTo(new[] { downloadId }));
                    Assert.That(cancellation.IsCancellationRequested, Is.True);
                    Assert.That(tracking.Get(downloadId)?.Status, Is.EqualTo("cancelling"));
                });
            }
            finally
            {
                tracking.Complete(downloadId);
            }
        }

        [Test]
        public void should_cancel_durable_job_without_falling_through_to_inline_cancellation()
        {
            var downloadId = Guid.NewGuid().ToString("N");
            using var cancellation = new CancellationTokenSource();
            var tracking = CreateTrackingService();
            tracking.Start(downloadId, 10, "M4B", "Converting to M4B");
            tracking.RegisterCancellation(downloadId, cancellation);
            var durable = CreateJobService(cancelResult: true);
            var setup = BuildController(downloadId, tracking, durable.Service);

            try
            {
                Assert.DoesNotThrow(() => setup.Controller.CancelConversion(QueueItemId));

                Assert.Multiple(() =>
                {
                    Assert.That(durable.Proxy.CancelCalls, Is.EqualTo(new[] { downloadId }));
                    Assert.That(cancellation.IsCancellationRequested, Is.False);
                    Assert.That(tracking.Get(downloadId)?.Status, Is.EqualTo("converting"));
                });
            }
            finally
            {
                tracking.Complete(downloadId);
            }
        }

        [Test]
        public void should_return_bad_request_when_neither_cancellation_path_accepts_the_request()
        {
            var downloadId = Guid.NewGuid().ToString("N");
            var tracking = CreateTrackingService();
            var durable = CreateJobService(cancelResult: false);
            var setup = BuildController(downloadId, tracking, durable.Service);

            Assert.Throws<BadRequestException>(() => setup.Controller.CancelConversion(QueueItemId));

            Assert.That(durable.Proxy.CancelCalls, Is.EqualTo(new[] { downloadId }));
        }

        [Test]
        public void should_keep_inline_cancellation_compatible_when_no_durable_service_is_injected()
        {
            var downloadId = Guid.NewGuid().ToString("N");
            using var cancellation = new CancellationTokenSource();
            var tracking = CreateTrackingService();
            tracking.Start(downloadId, 10, "M4B", "Converting to M4B");
            tracking.RegisterCancellation(downloadId, cancellation);
            var setup = BuildController(downloadId, tracking, conversionJobService: null);

            try
            {
                Assert.DoesNotThrow(() => setup.Controller.CancelConversion(QueueItemId));
                Assert.That(cancellation.IsCancellationRequested, Is.True);
            }
            finally
            {
                tracking.Complete(downloadId);
            }
        }

        [Test]
        public void late_stop_after_inline_conversion_should_return_bad_request_and_preserve_import_progress()
        {
            var downloadId = Guid.NewGuid().ToString("N");
            using var cancellation = new CancellationTokenSource();
            var tracking = CreateTrackingService();
            tracking.Start(downloadId, 10, "M4B", "Converting to M4B");
            tracking.RegisterCancellation(downloadId, cancellation);
            tracking.UnregisterCancellation(downloadId, cancellation);
            cancellation.Dispose();
            tracking.Progress(downloadId, 99m, "Importing M4B");
            var durable = CreateJobService(cancelResult: false);
            var setup = BuildController(downloadId, tracking, durable.Service);
            try
            {
                Assert.Throws<BadRequestException>(() => setup.Controller.CancelConversion(QueueItemId));
                Assert.That(tracking.Get(downloadId).CanCancel, Is.False);
                Assert.That(tracking.Get(downloadId).Message, Is.EqualTo("Importing M4B"));
                Assert.That(tracking.Get(downloadId).Progress, Is.EqualTo(99m));
            }
            finally
            {
                tracking.Complete(downloadId);
            }
        }

        private static IConversionTrackingService CreateTrackingService()
        {
            return new ConversionTrackingService(new NoOpEventAggregator());
        }

        private static (IConversionJobService Service, ConversionJobServiceProxy Proxy) CreateJobService(bool cancelResult)
        {
            var service = DispatchProxy.Create<IConversionJobService, ConversionJobServiceProxy>();
            var proxy = (ConversionJobServiceProxy)(object)service;
            proxy.CancelResult = cancelResult;
            return (service, proxy);
        }

        private static (QueueController Controller, QueueServiceProxy QueueProxy) BuildController(string downloadId,
                                                                                                  IConversionTrackingService trackingService,
                                                                                                  IConversionJobService conversionJobService)
        {
            var queueService = DispatchProxy.Create<IQueueService, QueueServiceProxy>();
            var queueProxy = (QueueServiceProxy)(object)queueService;
            queueProxy.QueueItem = new NzbDrone.Core.Queue.Queue
            {
                Id = QueueItemId,
                DownloadId = downloadId
            };

            var profileRepository = DispatchProxy.Create<IProfileRepository, ProfileRepositoryProxy>();
            var qualityProfileService = new QualityProfileService(
                profileRepository,
                authorService: null,
                importListFactory: null,
                formatService: null,
                rootFolderService: null,
                qualityDefinitionService: null,
                logger: LogManager.GetCurrentClassLogger());

            var controller = new QueueController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IDownloadImportModeResolver>((method, args) => false),
                broadcastSignalRMessage: null,
                queueService: queueService,
                pendingReleaseService: null,
                qualityProfileService: qualityProfileService,
                trackedDownloadService: null,
                failedDownloadService: null,
                ignoredDownloadService: null,
                downloadClientProvider: null,
                blocklistService: null,
                conversionTrackingService: trackingService,
                conversionJobService: conversionJobService);

            return (controller, queueProxy);
        }
    }
}

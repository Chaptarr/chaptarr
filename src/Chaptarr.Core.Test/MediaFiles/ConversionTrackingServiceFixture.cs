using System;
using System.Threading;
using System.Threading.Tasks;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NUnit.Framework;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ConversionTrackingServiceFixture
    {
        private sealed class NoOpEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        [Test]
        public void should_not_allow_late_progress_to_overwrite_failed_status()
        {
            var subject = new ConversionTrackingService(new NoOpEventAggregator());

            subject.Start("download-1", 12, "M4B", "Converting");
            subject.Fail("download-1", "Conversion failed");
            subject.Progress("download-1", 50m, "Converting to M4B - 1 of 2");

            var status = subject.Get("download-1");

            Assert.That(status.Status, Is.EqualTo("failed"));
            Assert.That(status.Message, Is.EqualTo("Conversion failed"));
            Assert.That(status.Progress, Is.Null);
        }

        [Test]
        public void should_cancel_registered_conversion_and_block_late_progress()
        {
            var subject = new ConversionTrackingService(new NoOpEventAggregator());
            using var cancellation = new CancellationTokenSource();

            subject.Start("download-cancel", 12, "M4B", "Converting");
            subject.RegisterCancellation("download-cancel", cancellation);

            var cancelled = subject.Cancel("download-cancel");
            subject.Progress("download-cancel", 50m, "Converting to M4B - 1 of 2");

            var status = subject.Get("download-cancel");

            Assert.That(cancelled, Is.True);
            Assert.That(cancellation.IsCancellationRequested, Is.True);
            Assert.That(status.Status, Is.EqualTo("cancelling"));
            Assert.That(status.Message, Is.EqualTo("Cancelling conversion"));
            Assert.That(status.Progress, Is.Null);

            subject.Complete("download-cancel");
        }

        [Test]
        public void should_leave_the_registered_cancellation_source_usable_for_its_owner()
        {
            // The source is borrowed, not owned: ConversionJobService keeps cancelling through it
            // until its conversion finally runs, which is after the terminal status lands here.
            // Disposing it on a terminal status made ApplicationShutdownRequested throw
            // ObjectDisposedException while the conversion was still in _activeConversions.
            var subject = new ConversionTrackingService(new NoOpEventAggregator());
            using var cancellation = new CancellationTokenSource();

            subject.Start("download-owns-cts", 12, "M4B", "Converting");
            subject.RegisterCancellation("download-owns-cts", cancellation);
            subject.Cancelled("download-owns-cts", "Conversion was cancelled.");

            Assert.DoesNotThrow(() => cancellation.Cancel(), "the owner still holds this source");
            Assert.That(cancellation.IsCancellationRequested, Is.True);

            // Re-registering must not dispose whatever it replaces either.
            using var replacement = new CancellationTokenSource();
            subject.RegisterCancellation("download-owns-cts", cancellation);
            subject.RegisterCancellation("download-owns-cts", replacement);

            Assert.DoesNotThrow(() => cancellation.Cancel(), "the replaced source is still the owner's");
            subject.Complete("download-owns-cts");
        }

        [Test]
        public void should_mark_cancelled_conversion_as_stopped_without_importing_originals()
        {
            var subject = new ConversionTrackingService(new NoOpEventAggregator());
            using var cancellation = new CancellationTokenSource();

            subject.Start("download-cancel", 4, "M4B", "Converting to M4B");
            subject.RegisterCancellation("download-cancel", cancellation);
            subject.Cancel("download-cancel");
            subject.Cancelled("download-cancel", "Target Quality conversion was cancelled.");
            subject.Progress("download-cancel", 50m, "Converting to M4B - 1 of 2");

            var status = subject.Get("download-cancel");

            Assert.That(status.Status, Is.EqualTo("cancelled"));
            Assert.That(status.Message, Is.EqualTo("Target Quality conversion was cancelled."));
            Assert.That(status.Progress, Is.Null);
            Assert.That(status.CanCancel, Is.False);
            Assert.That(subject.Cancel("download-cancel"), Is.False);
        }

        [Test]
        public void unregister_should_wait_for_inflight_cancel_before_owner_disposes_source()
        {
            var subject = new ConversionTrackingService(new NoOpEventAggregator());
            var downloadId = Guid.NewGuid().ToString("N");
            using var cancellation = new CancellationTokenSource();
            using var cancelEntered = new ManualResetEventSlim();
            using var releaseCancel = new ManualResetEventSlim();
            using var removalFinished = new ManualResetEventSlim();
            using var registration = cancellation.Token.Register(() =>
            {
                cancelEntered.Set();
                if (!releaseCancel.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cancellation was not released.");
            });
            subject.Start(downloadId, 12, "M4B");
            subject.RegisterCancellation(downloadId, cancellation);
            var cancel = Task.Run(() => subject.Cancel(downloadId));
            Exception removalError = null;
            var removal = new Thread(() =>
            {
                try
                {
                    subject.UnregisterCancellation(downloadId, cancellation);
                    cancellation.Dispose();
                }
                catch (Exception ex) { removalError = ex; }
                finally { removalFinished.Set(); }
            }) { IsBackground = true };
            var removalStarted = false;
            try
            {
                Assert.That(cancelEntered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                removal.Start();
                removalStarted = true;
                Assert.That(SpinWait.SpinUntil(() => removalFinished.IsSet ||
                    (removal.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(removalFinished.IsSet, Is.False, "Owner disposal must wait for the accepted cancellation call.");
            }
            finally
            {
                releaseCancel.Set();
                Assert.That(cancel.Wait(TimeSpan.FromSeconds(5)), Is.True);
                if (removalStarted) Assert.That(removal.Join(TimeSpan.FromSeconds(5)), Is.True);
                subject.Complete(downloadId);
            }
            Assert.That(cancel.Result, Is.True);
            Assert.That(removalError, Is.Null);
            Assert.That(subject.Cancel(downloadId), Is.False);
        }

        [Test]
        public void older_owner_unregister_should_preserve_replacement_attempt_cancellation()
        {
            var subject = new ConversionTrackingService(new NoOpEventAggregator());
            var downloadId = Guid.NewGuid().ToString("N");
            using var previous = new CancellationTokenSource();
            using var replacement = new CancellationTokenSource();
            var previousToken = previous.Token;
            subject.Start(downloadId, 12, "M4B");
            subject.RegisterCancellation(downloadId, previous);
            subject.Start(downloadId, 12, "M4B", "Retrying conversion");
            subject.RegisterCancellation(downloadId, replacement);
            subject.UnregisterCancellation(downloadId, previous);
            previous.Dispose();
            Assert.That(subject.Get(downloadId).CanCancel, Is.True);
            Assert.That(subject.Cancel(downloadId), Is.True);
            Assert.That(replacement.IsCancellationRequested, Is.True);
            Assert.That(previousToken.IsCancellationRequested, Is.False);
            subject.UnregisterCancellation(downloadId, replacement);
            subject.Complete(downloadId);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void absent_download_is_removed_only_after_a_successful_poll(
            [Values(false, true)] bool postgres,
            [Values(ConversionJobStatus.Queued, ConversionJobStatus.Converting, ConversionJobStatus.ReadyToImport)] ConversionJobStatus status)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "conversion-absent-owner-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(root, "absent-download");
                    request.DownloadClientId = 4;
                    File.WriteAllText(request.OutputPath, "temporary output");
                    SeedClientHistory(database, request.DownloadId);
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    repository.Insert(CreatePersistedJob(request, status, DateTime.UtcNow));
                    var service = CreateService(repository, new UnusedConversionService());
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>>()));
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Not.Null, "A client not polled yet is not removal evidence");
                    Assert.That(File.Exists(request.OutputPath), Is.True);
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() }));
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null, "A successful client poll and no retained download releases the job");
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                    Assert.That(File.ReadAllText(request.Sources.Single().Path), Is.EqualTo("source"));
                }
                finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            });
        }

        [Test]
        public void current_pending_download_wins_over_stale_failed_history([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "conversion-regrab-history-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(root, "download-again");
                    request.DownloadClientId = 4;
                    SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadFailed);
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Queued, DateTime.UtcNow));
                    var service = CreateService(repository, new UnusedConversionService());
                    var tracked = new TrackedDownload
                    {
                        DownloadClient = 4,
                        State = TrackedDownloadState.ImportPending,
                        IsTrackable = true,
                        DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId }
                    };
                    service.Handle(new TrackedDownloadRefreshedEvent(new() { tracked }, new Dictionary<int, HashSet<string>> { [4] = new() }));
                    var kept = repository.FindByDownloadId(request.DownloadId);
                    Assert.That(kept, Is.Not.Null, "A currently tracked import must survive stale failure history");
                    Assert.That(kept.Status, Is.EqualTo(ConversionJobStatus.Queued));
                    Assert.That(Directory.Exists(request.WorkFolder), Is.True);
                }
                finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            });
        }

        [Test]
        public void cleanup_warning_count_includes_every_flagged_job([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "conversion-warning-count-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(root, "warnings");
                    request.DownloadClientId = 4;
                    request.CleanupError = "Workspace is locked";
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var jobs = Enumerable.Range(0, 250).Select(index =>
                    {
                        var job = CreatePersistedJob(request, ConversionJobStatus.Completed, DateTime.UtcNow);
                        job.DownloadId = "warning-" + index;
                        return job;
                    }).ToList();
                    repository.InsertMany(jobs);
                    var localization = InterfaceProxy.Proxy<ILocalizationService>((method, args) =>
                        (string)args[0] == "CountMore" ? "and " + ((Dictionary<string, object>)args[1])["count"] + " more" : "Cleanup warning");
                    Assert.That(repository.CleanupWarnings(), Has.Count.EqualTo(100));
                    Assert.That(repository.CleanupWarningCount(), Is.EqualTo(250));
                    Assert.That(new ConversionCleanupCheck(repository, localization).Check().Message, Does.EndWith("and 245 more"));

                    request.CleanupError = null;
                    jobs[0].RequestJson = JsonSerializer.Serialize(request);
                    repository.SetFields(jobs[0], job => job.RequestJson);
                    Assert.That(repository.CleanupWarningCount(), Is.EqualTo(249), "Partial request writes update the indexed projection too");
                }
                finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            });
        }

        [Test]
        public void failing_cleanup_step_does_not_stop_queued_conversions([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "conversion-step-isolation-" + Guid.NewGuid().ToString("N"));
                ConversionJobService service = null;
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var proxy = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                    {
                        if (method.Name == nameof(IConversionJobRepository.CleanupReviewCandidates)) throw new IOException("Planted cleanup query failure");
                        return method.Invoke(repository, args);
                    });
                    var request = CreateRequest(root, "healthy-job");
                    request.DownloadClientId = 4;
                    SeedClientHistory(database, request.DownloadId);
                    var converter = new RetainedResultConverter { Success = true };
                    service = CreateService(proxy, converter);
                    service.Enqueue(request);
                    service.Handle(new ApplicationStartedEvent());
                    Assert.That(SpinWait.SpinUntil(() => repository.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.ReadyToImport, TimeSpan.FromSeconds(10)), Is.True);
                    Assert.That(converter.Calls, Is.EqualTo(1));
                }
                finally
                {
                    ShutdownAndWait(service);
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            });
        }

        [Test]
        public void unreadable_cleanup_request_never_throws_from_failure_recording([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "conversion-unreadable-cleanup-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var request = CreateRequest(root, "bad-request");
                    request.DownloadClientId = 4;
                    var job = CreatePersistedJob(request, ConversionJobStatus.Completed, DateTime.UtcNow);
                    job.RequestJson = "{";
                    repository.Insert(job);
                    var service = CreateService(repository, new UnusedConversionService());
                    var method = typeof(ConversionJobService).GetMethod("RecordCleanupFailure", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.DoesNotThrow(() => method.Invoke(service, new object[] { request.DownloadId, new IOException("Planted cleanup failure") }));
                    Assert.That(repository.CleanupWarningCount(), Is.EqualTo(1));
                    Assert.That(repository.Get(job.Id).RequestJson, Is.EqualTo("{"));
                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                }
                finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            });
        }

        [Test]
        public void cleanup_rechecks_the_attempt_after_unlocked_ownership_preflight([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "conversion-cleanup-race-" + Guid.NewGuid().ToString("N"));
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                Task<Action> preparation = null;
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var original = CreateRequest(root, "same-download");
                    original.DownloadClientId = 4;
                    File.WriteAllText(original.OutputPath, "old output");
                    repository.Insert(CreatePersistedJob(original, ConversionJobStatus.Completed, DateTime.UtcNow));
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IMediaFileService.GetFilePathOwnership)));
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Ownership preflight was not released");
                        return new List<BookFile>();
                    });
                    var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), media, repository, new UnusedConversionService(), null,
                        DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>(), CreateConfig(), LogManager.GetCurrentClassLogger());
                    preparation = Task.Run(() => service.PrepareArtifactRemoval(original.DownloadId, ConversionRemovalOrigin.UserAction));
                    Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    var next = CreateRequest(Path.Combine(root, "new-attempt"), original.DownloadId);
                    next.DownloadClientId = 4;
                    service.Enqueue(next);
                    release.Set();
                    var remove = preparation.GetAwaiter().GetResult();
                    Assert.Throws<InvalidOperationException>(() => remove());
                    Assert.That(repository.FindByDownloadId(next.DownloadId).WorkFolder, Is.EqualTo(next.WorkFolder));
                    Assert.That(Directory.Exists(next.WorkFolder), Is.True);
                    Assert.That(File.ReadAllText(original.OutputPath), Is.EqualTo("old output"));
                }
                finally
                {
                    release.Set();
                    preparation?.Wait(TimeSpan.FromSeconds(10));
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            });
        }

        [Test]
        public void refresh_cleanup_does_not_block_or_delete_a_new_attempt([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "conversion-refresh-cleanup-race-" + Guid.NewGuid().ToString("N"));
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                Task refresh = null;
                Task<ConversionJob> enqueue = null;
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var original = CreateRequest(root, "same-download");
                    original.DownloadClientId = 4;
                    File.WriteAllText(original.OutputPath, "old output");
                    SeedClientHistory(database, original.DownloadId);
                    repository.Insert(CreatePersistedJob(original, ConversionJobStatus.Completed, DateTime.UtcNow));
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IMediaFileService.GetFilePathOwnership)));
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Ownership read was not released");
                        return new List<BookFile>();
                    });
                    var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), media, repository, new UnusedConversionService(), null,
                        DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>(), CreateConfig(), LogManager.GetCurrentClassLogger());
                    refresh = Task.Run(() => service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() })));
                    Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    var next = CreateRequest(Path.Combine(root, "new-attempt"), original.DownloadId);
                    next.DownloadClientId = 4;
                    enqueue = Task.Run(() => service.Enqueue(next));
                    Assert.That(enqueue.Wait(TimeSpan.FromSeconds(1)), Is.True, "A cleanup inventory read must not hold the worker admission lock");
                    release.Set();
                    refresh.GetAwaiter().GetResult();
                    var kept = repository.FindByDownloadId(next.DownloadId);
                    Assert.That(kept, Is.Not.Null, "Cleanup must recheck the attempt captured before its inventory read");
                    Assert.That(kept.WorkFolder, Is.EqualTo(next.WorkFolder));
                    Assert.That(kept.Status, Is.EqualTo(ConversionJobStatus.Queued));
                    Assert.That(Directory.Exists(next.WorkFolder), Is.True);
                    Assert.That(File.ReadAllText(original.OutputPath), Is.EqualTo("old output"));
                }
                finally
                {
                    release.Set();
                    refresh?.Wait(TimeSpan.FromSeconds(10));
                    enqueue?.Wait(TimeSpan.FromSeconds(10));
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            });
        }
        [Test]
        public void reset_cleanup_does_not_hold_admission_or_remove_other_work([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "conversion-reset-race-" + Guid.NewGuid().ToString("N"));
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                Task<string> reset = null;
                Task<ConversionJob> enqueue = null;
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var original = CreateRequest(root, "reset-download");
                    File.WriteAllText(original.OutputPath, "old output");
                    var captured = repository.Insert(CreatePersistedJob(original, ConversionJobStatus.Completed, DateTime.UtcNow));
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        if (method.Name == nameof(IMediaFileService.GetFilesByBook)) return new List<BookFile>();
                        Assert.That(method.Name, Is.EqualTo(nameof(IMediaFileService.GetFilePathOwnership)));
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Reset cleanup was not released");
                        return new List<BookFile>();
                    });
                    var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), media, repository, new UnusedConversionService(), null,
                        DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>(), CreateConfig(), LogManager.GetCurrentClassLogger());
                    reset = Task.Run(() => service.Reset(original.DownloadId, captured));
                    Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    var next = CreateRequest(Path.Combine(root, "new-attempt"), "other-download");
                    enqueue = Task.Run(() => service.Enqueue(next));
                    Assert.That(enqueue.Wait(TimeSpan.FromSeconds(1)), Is.True, "Reset cleanup must not hold the worker admission lock");
                    Assert.Throws<InvalidOperationException>(() => service.Enqueue(CreateRequest(Path.Combine(root, "replacement"), original.DownloadId)),
                        "A reserved reset must refuse another attempt for the same download");
                    release.Set();
                    Assert.That(reset.GetAwaiter().GetResult(), Is.EqualTo(original.WorkFolder));
                    var kept = repository.FindByDownloadId(next.DownloadId);
                    Assert.That(kept.WorkFolder, Is.EqualTo(next.WorkFolder));
                    Assert.That(kept.Status, Is.EqualTo(ConversionJobStatus.Queued));
                    Assert.That(Directory.Exists(original.WorkFolder), Is.False);
                    Assert.That(Directory.Exists(next.WorkFolder), Is.True);
                }
                finally
                {
                    release.Set();
                    reset?.Wait(TimeSpan.FromSeconds(10));
                    enqueue?.Wait(TimeSpan.FromSeconds(10));
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            });
        }
    }
}

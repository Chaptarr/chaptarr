using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void retained_completed_output_stays_terminal_and_present_after_reconciliation([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-retained-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new RetainedResultConverter { Success = true };
                var service = CreateService(repository, converter);
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                request.DownloadClientId = 4;
                try
                {
                    SeedClientHistory(database, request.DownloadId);
                    RunWorker(service, request);
                    var sources = request.Sources.Select(source => source.Path).ToArray();
                    Assert.That(service.RetainCompletedOutput(request.DownloadId, new[] { Path.Combine(directory, "other-format.epub") }, "existing file now occupies the target").Status, Is.EqualTo(ConversionRetentionStatus.None));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    var failed = repository.FindByDownloadId(request.DownloadId);
                    foreach (var status in new[] { ConversionJobStatus.Converting, ConversionJobStatus.Cancelling })
                    {
                        failed.Status = status;
                        repository.SetFields(failed, job => job.Status);
                        Assert.That(service.RetainCompletedOutput(request.DownloadId, sources, "existing file now occupies the target").Status, Is.EqualTo(ConversionRetentionStatus.Pending));
                        Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(status));
                        Assert.That(File.Exists(request.OutputPath), Is.True);
                    }
                    failed.Status = ConversionJobStatus.Failed;
                    failed.Error = "copy failed";
                    repository.SetFields(failed, job => job.Status, job => job.Error);
                    var blocked = service.RetainCompletedOutput(request.DownloadId, sources, "book became owned");
                    Assert.That(blocked.Status, Is.EqualTo(ConversionRetentionStatus.Blocked));
                    Assert.That(blocked.Message, Is.EqualTo("copy failed"));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Failed));
                    failed.Status = ConversionJobStatus.Cancelled;
                    repository.SetFields(failed, job => job.Status);
                    Assert.That(service.RetainCompletedOutput(request.DownloadId, sources, "book became owned").Status, Is.EqualTo(ConversionRetentionStatus.Blocked));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Cancelled));
                    failed.Status = ConversionJobStatus.ReadyToImport;
                    failed.Error = null;
                    repository.SetFields(failed, job => job.Status, job => job.Error);

                    Assert.That(service.RetainCompletedOutput(request.DownloadId, sources, "existing file now occupies the target").OutputPath, Is.EqualTo(request.OutputPath));
                    var retained = repository.FindByDownloadId(request.DownloadId);
                    Assert.Multiple(() =>
                    {
                        Assert.That(retained.Status, Is.EqualTo(ConversionJobStatus.Completed));
                        Assert.That(retained.Error, Is.Null);
                        Assert.That(retained.Message, Does.Contain(request.OutputPath).And.Contain("Converted file retained at:").And.Not.Contain("imported"));
                        Assert.That(File.Exists(request.OutputPath), Is.True);
                        Assert.That(File.Exists(Path.Combine(request.WorkFolder, "conversion-artifact.json")), Is.True);
                        Assert.That(File.Exists(sources.Single()), Is.True);
                    });
                    Assert.That(service.RetainCompletedOutput(request.DownloadId, sources, "existing file now occupies the target").OutputPath, Is.EqualTo(request.OutputPath));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).UpdatedAt, Is.EqualTo(retained.UpdatedAt), "Repeated refusal must not rewrite the terminal row");

                    var restarted = CreateService(repository, new UnusedConversionService());
                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(restarted, null);
                    typeof(ConversionJobService).GetMethod("DispatchQueuedJobs", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(restarted, null);
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Completed));
                    Assert.That(File.Exists(request.OutputPath), Is.True);
                    Assert.That(converter.Calls, Is.EqualTo(1));
                }
                finally
                {
                    ShutdownAndWait(service);
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void queued_refusal_waits_for_conversion_then_retains_or_discards_only_its_output(
            [Values(false, true)] bool postgres, [Values(false, true)] bool requeued, [Values(false, true)] bool bonus)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-refused-queued-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new RetainedResultConverter { Success = true };
                var service = CreateService(repository, converter);
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                request.DownloadClientId = 4;
                try
                {
                    service.Enqueue(request);
                    var queued = repository.FindByDownloadId(request.DownloadId);
                    if (requeued) { queued.AttemptCount = 1; repository.SetFields(queued, job => job.AttemptCount); }
                    var paths = request.Sources.Select(source => source.Path).ToArray();
                    Assert.That(service.RetainCompletedOutput(request.DownloadId, new[] { Path.Combine(directory, "unrelated.epub") }, "target occupied", bonus).Status,
                        Is.EqualTo(ConversionRetentionStatus.None));
                    Assert.That(service.RetainCompletedOutput(request.DownloadId, paths, "target occupied", bonus).Status, Is.EqualTo(ConversionRetentionStatus.Pending));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Queued));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).UpdatedAt, Is.EqualTo(queued.UpdatedAt));
                    RunWorker(service, request);
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    var result = service.RetainCompletedOutput(request.DownloadId, paths, "target occupied", bonus);
                    Assert.That(result.Status, Is.EqualTo(bonus ? ConversionRetentionStatus.None : ConversionRetentionStatus.Retained));
                    Assert.That(File.Exists(request.OutputPath), Is.EqualTo(!bonus));
                    Assert.That(File.Exists(paths.Single()), Is.True);
                    Assert.That(converter.Calls, Is.EqualTo(1));
                }
                finally
                {
                    ShutdownAndWait(service);
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void refused_bonus_discards_owned_terminal_workspace_and_preserves_originals([Values(false, true)] bool postgres,
            [Values(ConversionJobStatus.ReadyToImport, ConversionJobStatus.Failed, ConversionJobStatus.Cancelled)] ConversionJobStatus status)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-bonus-discard-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new RetainedResultConverter { Success = true };
                var service = CreateService(repository, converter);
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                request.DownloadClientId = 4;
                try
                {
                    RunWorker(service, request);
                    var job = repository.FindByDownloadId(request.DownloadId);
                    job.Status = status;
                    repository.SetFields(job, value => value.Status);
                    var foreign = Path.Combine(directory, "library-recording.m4b");
                    File.WriteAllText(foreign, "managed library file");
                    Assert.That(service.RetainCompletedOutput(request.DownloadId, new[] { foreign }, "bonus refused", discardOutput: true).Status,
                        Is.EqualTo(ConversionRetentionStatus.None));
                    Assert.That(File.Exists(request.OutputPath), Is.True, "An unrelated source must never authorize cleanup");
                    var outcome = service.RetainCompletedOutput(request.DownloadId, request.Sources.Select(source => source.Path).ToArray(), "bonus refused", discardOutput: true);
                    Assert.That(outcome.Status, Is.EqualTo(ConversionRetentionStatus.None));
                    Assert.That(outcome.Message, Does.Contain("workspace discarded").And.Contain(request.WorkFolder));
                    Assert.That(outcome.OutputPath, Is.Null);
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                    Assert.That(File.ReadAllText(foreign), Is.EqualTo("managed library file"));
                    Assert.That(converter.Calls, Is.EqualTo(1));
                    Assert.That(service.RetainCompletedOutput(request.DownloadId, request.Sources.Select(source => source.Path).ToArray(), "bonus refused", discardOutput: true).Status,
                        Is.EqualTo(ConversionRetentionStatus.None));
                }
                finally
                {
                    ShutdownAndWait(service);
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void concurrent_existing_file_refusal_waits_for_running_bonus_then_cleans_only_its_output([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-bonus-running-discard-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new TokenRecordingConversionService();
                var service = CreateService(repository, converter);
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                request.DownloadClientId = 4;
                try
                {
                    service.Enqueue(request);
                    typeof(ConversionJobService).GetMethod("DispatchQueuedJobs", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(request.DownloadId), TimeSpan.FromSeconds(10)), Is.True);
                    var result = service.RetainCompletedOutput(request.DownloadId, request.Sources.Select(source => source.Path).ToArray(), "existing file now occupies the target", discardOutput: true);
                    Assert.That(result.Status, Is.EqualTo(ConversionRetentionStatus.Pending));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Converting));
                    Assert.That(converter.OptionsByDownloadId[request.DownloadId].CancellationToken.IsCancellationRequested, Is.False);
                    converter.Release.Set();
                    Assert.That(SpinWait.SpinUntil(() => !service.IsRunning(request.DownloadId), TimeSpan.FromSeconds(10)), Is.True);
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    Assert.That(service.RetainCompletedOutput(request.DownloadId, request.Sources.Select(source => source.Path).ToArray(), "target occupied", discardOutput: true).Status,
                        Is.EqualTo(ConversionRetentionStatus.None));
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                    Assert.That(converter.CallsByDownloadId[request.DownloadId], Is.EqualTo(1));
                }
                finally
                {
                    converter.Release.Set();
                    ShutdownAndWait(service);
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void refused_invalid_completed_artifact_is_blocked_without_reencoding_or_deletion([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-refused-invalid-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new RetainedResultConverter { Success = true };
                var service = CreateService(repository, converter);
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                request.DownloadClientId = 4;
                try
                {
                    SeedClientHistory(database, request.DownloadId);
                    RunWorker(service, request);
                    File.AppendAllText(request.OutputPath, "changed after completion");
                    var outcome = service.RetainCompletedOutput(request.DownloadId, request.Sources.Select(source => source.Path).ToArray(), "book became owned");
                    Assert.That(outcome.Status, Is.EqualTo(ConversionRetentionStatus.Blocked));
                    Assert.That(outcome.Message, Does.Contain("cannot be verified"));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Failed));
                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    typeof(ConversionJobService).GetMethod("DispatchQueuedJobs", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(converter.Calls, Is.EqualTo(1));
                    Assert.That(File.Exists(request.OutputPath), Is.True);
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                }
                finally
                {
                    ShutdownAndWait(service);
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void original_import_reset_reports_only_output_it_actually_removed([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-original-cleanup-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                using var factory = new LogFactory();
                var sink = new ConcurrentLogTarget { Layout = "${level}|${message}" };
                var logging = new NLog.Config.LoggingConfiguration(factory);
                logging.AddRuleForAllLevels(sink);
                factory.Configuration = logging;
                var service = CreateService(repository, new RetainedResultConverter { Success = true }, logger: factory.GetLogger("original-import-cleanup"));
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                request.DownloadClientId = 4;
                try
                {
                    RunWorker(service, request);
                    service.RetainCompletedOutput(request.DownloadId, request.Sources.Select(source => source.Path).ToArray(), "manual originals selected");
                    var expected = repository.FindByDownloadId(request.DownloadId);
                    Assert.That(service.Reset(request.DownloadId, expected), Is.EqualTo(request.WorkFolder));
                    Assert.That(File.Exists(request.OutputPath), Is.False);
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(sink.Logs.Any(message => message.StartsWith("Info|", StringComparison.Ordinal) &&
                        message.Contains("discarded conversion workspace", StringComparison.Ordinal) && message.Contains(request.WorkFolder, StringComparison.Ordinal)), Is.True);
                    Assert.That(service.Reset(request.DownloadId, expected), Is.Null);
                }
                finally
                {
                    ShutdownAndWait(service);
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void retention_write_failure_preserves_ready_output_for_retry([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-retain-outage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new RetainedResultConverter { Success = true };
                var worker = CreateService(repository, converter);
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                request.DownloadClientId = 4;
                try
                {
                    RunWorker(worker, request);
                    var proxy = DispatchProxy.Create<IConversionJobRepository, FailingRepositoryProxy>();
                    var failure = (FailingRepositoryProxy)(object)proxy;
                    failure.Repository = repository;
                    failure.Method = nameof(IConversionJobRepository.SetFields);
                    failure.FailOnCall = 1;
                    var cleared = 0;
                    var tracking = InterfaceProxy.Proxy<IConversionTrackingService>((method, _) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IConversionTrackingService.Complete)));
                        cleared++;
                        return null;
                    });
                    var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), proxy, new UnusedConversionService(), tracking,
                        DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>(), CreateConfig(), LogManager.GetCurrentClassLogger());
                    var sources = request.Sources.Select(source => source.Path).ToArray();
                    Assert.That(() => service.RetainCompletedOutput(request.DownloadId, sources, "book became owned"), Throws.Exception);
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    Assert.That(cleared, Is.Zero);
                    Assert.That(File.Exists(request.OutputPath), Is.True);
                    Assert.That(service.RetainCompletedOutput(request.DownloadId, sources, "book became owned").OutputPath, Is.EqualTo(request.OutputPath));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Completed));
                    Assert.That(cleared, Is.EqualTo(1));
                    Assert.That(File.Exists(request.OutputPath), Is.True);
                    Assert.That(converter.Calls, Is.EqualTo(1));
                }
                finally
                {
                    ShutdownAndWait(worker);
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }
    }
}

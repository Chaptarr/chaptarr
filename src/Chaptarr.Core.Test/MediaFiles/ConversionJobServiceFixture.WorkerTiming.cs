using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Lifecycle;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void fixture_repository_outcome_write_does_not_wait_for_a_polling_reader()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, Path.GetRandomFileName());
            try
            {
                var request = CreateRequest(root, "reader-writer");
                var repository = CreateRepository(Path.Combine(root, "jobs.db"));
                var job = repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Converting, DateTime.UtcNow));
                var database = (IDatabase)typeof(BasicRepository<ConversionJob>)
                    .GetField("_database", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(repository);
                using var reader = (SqliteConnection)database.OpenConnection();
                using var transaction = reader.BeginTransaction(deferred: true);
                Assert.That(reader.ExecuteScalar<int>("SELECT COUNT(*) FROM ConversionJobs", transaction: transaction), Is.EqualTo(1));
                job.Status = ConversionJobStatus.ReadyToImport;
                var writer = Task.Run(() => repository.Update(job));
                try
                {
                    Assert.That(writer.Wait(TimeSpan.FromSeconds(2)), Is.True,
                        "A test reader must not block the worker's outcome commit; use production SQLite initialization");
                }
                finally
                {
                    transaction.Rollback();
                    writer.GetAwaiter().GetResult();
                }
                Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        internal static void ShutdownAndWait(ConversionJobService service)
        {
            if (service == null) return;
            var stopped = Task.Run(async () =>
            {
                service.Handle(new ApplicationShutdownRequested());
                var completion = service.Completion;
                var workers = Task.WhenAll(completion.Scheduler, completion.Workers);
                try { await workers; }
                catch (OperationCanceledException) when (workers.IsCanceled) { }
            });
            Assert.That(stopped.Wait(TimeSpan.FromSeconds(15)), Is.True,
                "Conversion shutdown did not finish; do not delete a database still used by a worker");
        }

        internal static Task DispatchWorkers(ConversionJobService service)
        {
            typeof(ConversionJobService).GetMethod("DispatchQueuedJobs", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
            return service.Completion.Workers;
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void shutdown_tracks_database_work_after_worker_releases_ownership(bool outliveGracePeriod, bool failFinalization)
        {
            using var logs = new LogFactory();
            var sink = CaptureOrphanLogs(logs);
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, Path.GetRandomFileName());
            var request = CreateRequest(root, "teardown-after-release");
            var repository = CreateRepository(Path.Combine(root, "jobs.db"));
            var database = (IDatabase)typeof(BasicRepository<ConversionJob>)
                .GetField("_database", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(repository);
            using var reading = new ManualResetEventSlim();
            using var releaseRead = new ManualResetEventSlim();
            using var shutdownStarted = new ManualResetEventSlim();
            var converter = new TokenRecordingConversionService();
            ConversionJobService service = null;
            Task completion = null;
            Task shutdown = null;
            var intercepted = 0;
            var observed = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
            {
                if (method.Name == nameof(IConversionJobRepository.FindByDownloadId) &&
                    completion != null && !service.IsRunning(request.DownloadId) &&
                    Interlocked.CompareExchange(ref intercepted, 1, 0) == 0)
                {
                    using var connection = database.OpenConnection();
                    connection.ExecuteScalar<int>("SELECT COUNT(*) FROM ConversionJobs");
                    reading.Set();
                    releaseRead.Wait();
                    if (failFinalization) throw new IOException("Injected final bookkeeping failure");
                }
                return method.Invoke(repository, args);
            });
            service = CreateService(observed, converter, logger: logs.GetLogger("shutdown-lifetime"),
                shutdownWait: outliveGracePeriod ? TimeSpan.FromMilliseconds(50) : null);
            converter.OnStarted = _ => completion = service.Completion.Workers;
            try
            {
                service.Enqueue(request);
                typeof(ConversionJobService).GetMethod("DispatchQueuedJobs", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                converter.Release.Set();
                Assert.That(reading.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(service.IsRunning(request.DownloadId), Is.False);
                Assert.That(completion.IsCompleted, Is.False);
                shutdown = Task.Run(() =>
                {
                    shutdownStarted.Set();
                    service.Handle(new ApplicationShutdownRequested());
                });
                Assert.That(shutdownStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);
                if (outliveGracePeriod)
                {
                    Assert.That(shutdown.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(completion.IsCompleted, Is.False);
                    Assert.That(sink.Logs.Any(line => line.StartsWith("Warn|") &&
                        line.Contains("1 conversion worker") && line.Contains(request.DownloadId)), Is.True,
                        "The shutdown deadline must report the worker still using the database");
                }
                else
                {
                    Assert.That(shutdown.Wait(TimeSpan.FromMilliseconds(200)), Is.False,
                        "Production shutdown returned while a released worker held a database connection");
                    releaseRead.Set();
                    Assert.That(shutdown.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(completion.IsCompleted, Is.True);
                    if (failFinalization) Assert.That(sink.Logs.Any(line => line.StartsWith("Error|") &&
                        line.Contains("finalization failed") && line.Contains(request.DownloadId)), Is.True);
                }
            }
            finally
            {
                converter.Release.Set();
                releaseRead.Set();
                if (shutdown != null) Assert.That(shutdown.Wait(TimeSpan.FromSeconds(15)), Is.True);
                ShutdownAndWait(service);
                SqliteConnection.ClearAllPools();
                Directory.Delete(root, true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void shutdown_stops_admission_before_waiting_for_workers(bool dispatchPassedGate)
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, Path.GetRandomFileName());
            var first = CreateRequest(root, "shutdown-active");
            var second = CreateRequest(root, "shutdown-queued");
            var finishing = CreateRequest(root, "shutdown-finished");
            var repository = CreateRepository(Path.Combine(root, "jobs.db"));
            var converter = new TokenRecordingConversionService { HoldCancellation = true };
            var fast = new TokenRecordingConversionService();
            fast.Release.Set();
            var routed = InterfaceProxy.Proxy<IM4bConversionService>((method, args) =>
                method.Invoke(args.Length > 1 && Equals(args[1], finishing.OutputPath) ? fast : converter, args));
            using var admitting = new ManualResetEventSlim();
            using var releaseAdmission = new ManualResetEventSlim();
            var observed = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
            {
                if (dispatchPassedGate && method.Name == nameof(IConversionJobRepository.SetFields) &&
                    args[0] is ConversionJob job && job.DownloadId == second.DownloadId && job.Status == ConversionJobStatus.Converting)
                {
                    admitting.Set();
                    releaseAdmission.Wait();
                }
                return method.Invoke(repository, args);
            });
            var service = CreateService(observed, routed, concurrentConversions: 2);
            using var entered = new ManualResetEventSlim();
            using var stopping = new ManualResetEventSlim();
            converter.OnStarted = _ => entered.Set();
            Task shutdown = null;
            Task dispatch = null;
            try
            {
                service.Enqueue(first);
                service.Enqueue(finishing);
                DispatchWorkers(service);
                Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(SpinWait.SpinUntil(() => repository.FindByDownloadId(finishing.DownloadId).Status == ConversionJobStatus.ReadyToImport &&
                    !service.IsRunning(finishing.DownloadId), TimeSpan.FromSeconds(5)), Is.True);
                using var registration = converter.OptionsByDownloadId[first.DownloadId].CancellationToken.Register(() => stopping.Set());
                service.Enqueue(second);
                if (dispatchPassedGate)
                {
                    dispatch = Task.Run(() => { DispatchWorkers(service); });
                    Assert.That(admitting.Wait(TimeSpan.FromSeconds(5)), Is.True);
                }
                shutdown = Task.Run(() => service.Handle(new ApplicationShutdownRequested()));
                Assert.That(stopping.Wait(TimeSpan.FromSeconds(5)), Is.True);
                if (dispatchPassedGate)
                {
                    releaseAdmission.Set();
                    Assert.That(dispatch.Wait(TimeSpan.FromSeconds(5)), Is.True);
                }
                else DispatchWorkers(service);
                Assert.That(shutdown.IsCompleted, Is.False);
                converter.Release.Set();
                Assert.That(shutdown.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(repository.FindByDownloadId(second.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Queued));
                Assert.That(repository.FindByDownloadId(second.DownloadId).AttemptCount, Is.EqualTo(dispatchPassedGate ? 1 : 0));
                Assert.That(converter.CallsByDownloadId.ContainsKey(second.DownloadId), Is.False);
                Assert.That(service.Completion.Workers.IsCompleted, Is.True);
            }
            finally
            {
                releaseAdmission.Set();
                converter.Release.Set();
                if (dispatch != null) Assert.That(dispatch.Wait(TimeSpan.FromSeconds(5)), Is.True);
                if (shutdown != null) Assert.That(shutdown.Wait(TimeSpan.FromSeconds(15)), Is.True);
                ShutdownAndWait(service);
                SqliteConnection.ClearAllPools();
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void worker_completion_waits_for_inflight_heartbeat([Values(false, true)] bool postgres)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var ready = new ManualResetEventSlim();
                using var logs = new LogFactory();
                var configuration = new NLog.Config.LoggingConfiguration(logs);
                configuration.AddRule(LogLevel.Debug, LogLevel.Debug, new HeldHeartbeatLogTarget(entered, release));
                logs.Configuration = configuration;
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var observed = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                {
                    // Fail the real timer's write, then hold its error callback outside _sync.
                    // Holding the write itself would also block the worker and mask bad disposal.
                    if (method.Name == nameof(IConversionJobRepository.SetFields) && ((Array)args[1]).Length == 2)
                        throw new IOException("Injected heartbeat write failure");
                    var result = method.Invoke(repository, args);
                    if (method.Name == nameof(IConversionJobRepository.SetFields) &&
                        args[0] is ConversionJob job && job.Status == ConversionJobStatus.ReadyToImport) ready.Set();
                    return result;
                });
                var converter = new TokenRecordingConversionService();
                var service = CreateService(observed, converter, logger: logs.GetLogger("heartbeat-lifetime"),
                    heartbeatInterval: TimeSpan.FromMilliseconds(50));
                scope.Service = service;
                try
                {
                    service.Enqueue(CreateRequest(scope.Root, "heartbeat-lifetime"));
                    var completion = DispatchWorkers(service);
                    Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    converter.Release.Set();
                    Assert.That(ready.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(completion.Wait(TimeSpan.FromMilliseconds(200)), Is.False,
                        "A worker must not complete while its real heartbeat callback is still running");
                    var completedMessage = repository.FindByDownloadId("heartbeat-lifetime").Message;
                    converter.OptionsByDownloadId["heartbeat-lifetime"].ProgressHandler(new ConversionProgressUpdate { Progress = 2m, Message = "late output" });
                    Assert.That(repository.FindByDownloadId("heartbeat-lifetime").Message, Is.EqualTo(completedMessage),
                        "Ready output must not accept progress even while ownership is held to drain a callback");
                    release.Set();
                    Assert.That(completion.Wait(TimeSpan.FromSeconds(5)), Is.True);
                }
                finally
                {
                    release.Set();
                    converter.Release.Set();
                    scope.Stop();
                }
            });
        }

        [Test]
        public void late_application_started_does_not_reopen_shutdown([Values(false, true)] bool postgres)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new TokenRecordingConversionService();
                var service = scope.Service = CreateService(repository, converter);
                var request = CreateRequest(scope.Root, "late-start");
                service.Enqueue(request);
                service.Handle(new ApplicationShutdownRequested());
                service.Handle(new ApplicationStartedEvent());
                Assert.That(DispatchWorkers(service).IsCompleted, Is.True);
                Assert.That(repository.FindByDownloadId(request.DownloadId).AttemptCount, Is.Zero);
                Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Queued));
                Assert.That(converter.CallsByDownloadId, Is.Empty);
            });
        }

        [Test]
        public void late_progress_cannot_change_finished_or_replacement_work([Values(false, true)] bool postgres, [Values(false, true)] bool replacement)
        {
            WithRecovery(postgres, (database, scope) =>
            {
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new TokenRecordingConversionService();
                var service = scope.Service = CreateService(repository, converter);
                var request = CreateRequest(scope.Root, "late-progress");
                request.BookId = 42;
                try
                {
                    service.Enqueue(request);
                    var completion = DispatchWorkers(service);
                    Assert.That(SpinWait.SpinUntil(() => converter.OptionsByDownloadId.ContainsKey(request.DownloadId), TimeSpan.FromSeconds(5)), Is.True);
                    var oldOptions = converter.OptionsByDownloadId[request.DownloadId];
                    oldOptions.ProgressHandler(new ConversionProgressUpdate { Progress = 20m, Message = "current progress" });
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Progress, Is.EqualTo(20m));
                    converter.Release.Set();
                    Assert.That(completion.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    if (replacement)
                    {
                        service.Reset(request.DownloadId, repository.FindByDownloadId(request.DownloadId));
                        var next = CreateRequest(Path.Combine(scope.Root, "next"), request.DownloadId);
                        next.BookId = request.BookId;
                        next.Sources = request.Sources;
                        converter.Release.Reset();
                        var queued = service.Enqueue(next);
                        Assert.That(queued.Status, Is.EqualTo(ConversionJobStatus.Queued), queued.Message);
                        DispatchWorkers(service);
                        Assert.That(SpinWait.SpinUntil(() => !ReferenceEquals(converter.OptionsByDownloadId[request.DownloadId], oldOptions), TimeSpan.FromSeconds(5)), Is.True);
                        converter.OptionsByDownloadId[request.DownloadId].ProgressHandler(new ConversionProgressUpdate { Progress = 30m, Message = "replacement progress" });
                    }
                    var before = repository.FindByDownloadId(request.DownloadId);
                    oldOptions.ProgressHandler(new ConversionProgressUpdate { Progress = 2m, Message = "late old output" });
                    var after = repository.FindByDownloadId(request.DownloadId);
                    Assert.That(after.Message, Is.EqualTo(before.Message));
                    Assert.That(after.Progress, Is.EqualTo(before.Progress));
                    Assert.That(after.UpdatedAt, Is.EqualTo(before.UpdatedAt));
                    Assert.That(new ConversionTrackingService(new StubEventAggregator()).Get(request.DownloadId).Message, Is.EqualTo(before.Message));
                }
                finally
                {
                    converter.Release.Set();
                    scope.Stop();
                }
            });
        }

        private sealed class HeldHeartbeatLogTarget(ManualResetEventSlim entered, ManualResetEventSlim release) : NLog.Targets.Target
        {
            protected override void Write(LogEventInfo logEvent)
            {
                if (!logEvent.Message.StartsWith("Unable to update conversion heartbeat")) return;
                entered.Set();
                release.Wait();
            }
        }

        // These tests cross real database, scheduler and filesystem boundaries. Retain phase
        // timings on failure so native CI can distinguish a blocked write from slow cleanup.
        internal sealed class WorkerTiming : IDisposable
        {
            private readonly Stopwatch _elapsed = Stopwatch.StartNew();
            private readonly ConcurrentQueue<string> _steps = new();
            private readonly LogFactory _factory = new();
            private readonly ConcurrentLogTarget _logs;
            public Logger Logger { get; }

            public WorkerTiming()
            {
                _logs = CaptureOrphanLogs(_factory);
                _logs.Layout = "${longdate}|${threadid}|${level}|${message}|${exception:format=ToString}";
                Logger = _factory.GetLogger("conversion-worker-timing");
            }

            public void Record(string step) => _steps.Enqueue($"{_elapsed.Elapsed.TotalMilliseconds:0.0}ms {step}");
            public string Details => string.Join(Environment.NewLine, _steps) + Environment.NewLine + string.Join(Environment.NewLine, _logs.Logs);

            public IConversionJobRepository Observe(IConversionJobRepository repository) => InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
            {
                var label = method.Name + (args.Length > 0 && args[0] is ConversionJob job ? $" {job.DownloadId} {job.Status}" : "");
                Record("begin " + label);
                try { return method.Invoke(repository, args); }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;
                }
                finally { Record("end " + label); }
            });

            public void Dispose()
            {
                if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed) TestContext.Error.WriteLine(Details);
                _factory.Dispose();
            }
        }
    }
}

using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using Microsoft.Data.Sqlite;
using NLog;
using NLog.Config;
using NLog.Targets;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        internal sealed class UnavailableDatabase : IDisposable
        {
            private readonly IMainDatabase _database;
            private readonly string _unavailableConnection;
            private readonly bool _postgres;
            private int _unavailable;

            internal UnavailableDatabase(IMainDatabase database, string root, bool postgres)
            {
                _database = database;
                _postgres = postgres;
                using var connection = database.OpenConnection();
                _unavailableConnection = postgres
                    ? new NpgsqlConnectionStringBuilder(connection.ConnectionString)
                    {
                        Host = "127.0.0.1", Port = 1, Timeout = 1, Pooling = false
                    }.ConnectionString
                    : new SqliteConnectionStringBuilder
                    {
                        DataSource = Path.Combine(root, "unavailable", "database.db"),
                        Mode = SqliteOpenMode.ReadOnly, Pooling = false
                    }.ConnectionString;
                Database = new MainDatabase(new Database("conversion-outage", Open));
            }

            internal IMainDatabase Database { get; }
            internal bool Unavailable { set => Volatile.Write(ref _unavailable, value ? 1 : 0); }

            private IDbConnection Open()
            {
                if (Volatile.Read(ref _unavailable) == 0) return _database.OpenConnection();
                DbConnection connection = _postgres ? new NpgsqlConnection(_unavailableConnection) : new SqliteConnection(_unavailableConnection);
                try
                {
                    connection.Open();
                    Assert.Fail("The unavailable native connection unexpectedly opened");
                    return connection;
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }
            }

            public void Dispose() => Unavailable = false;
        }

        private sealed class OutcomeConversionService : IM4bConversionService, IDisposable
        {
            public readonly ManualResetEventSlim Started = new(false);
            public readonly ManualResetEventSlim Release = new(false);
            public string Outcome;
            public int Calls;

            public ConversionResult ConvertToM4b(string[] inputs, string output, ConversionOptions options = null)
            {
                Interlocked.Increment(ref Calls);
                File.WriteAllText(output, "partial audio");
                Started.Set();
                WaitHandle.WaitAny(new[] { Release.WaitHandle, options.CancellationToken.WaitHandle });
                options.CancellationToken.ThrowIfCancellationRequested();
                if (Outcome == "throw") throw new IOException("encoder stopped unexpectedly");
                if (Outcome == "failed") return new ConversionResult { Success = false, ErrorMessage = "encoder rejected input" };
                File.WriteAllText(output, "completed recording");
                return new ConversionResult { Success = true };
            }

            public bool CanConvert(string[] inputs) => true;
            public ConversionEstimate EstimateConversion(string[] inputs) => new() { CanConvert = true };
            public void Dispose() { Started.Dispose(); Release.Dispose(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void discard_during_failed_outcome_retry_must_survive_the_stale_worker(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "conversion-discard-failure-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                using var outage = new UnavailableDatabase(database, root, postgres);
                var repository = new ConversionJobRepository(outage.Database, new StubEventAggregator());
                var observer = new ConversionJobRepository(database, new StubEventAggregator());
                using var converter = new OutcomeConversionService { Outcome = "failed" };
                var log = new ConcurrentLogTarget { Layout = "${message}" };
                using var logs = new LogFactory();
                var configuration = new LoggingConfiguration(logs);
                configuration.AddRuleForAllLevels(log);
                logs.Configuration = configuration;
                var service = CreateService(repository, converter, logger: logs.GetLogger("discard-failure"));
                var request = CreateRequest(root, Guid.NewGuid().ToString("N"));
                var worker = System.Threading.Tasks.Task.Run(() => RunWorker(service, request));
                try
                {
                    Assert.That(converter.Started.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    outage.Unavailable = true;
                    converter.Release.Set();
                    Assert.That(SpinWait.SpinUntil(() => log.Logs.Any(message => message.Contains("Unable to save conversion outcome")),
                        TimeSpan.FromSeconds(5)), Is.True);
                    outage.Unavailable = false;
                    service.PrepareArtifactRemoval(request.DownloadId, ConversionRemovalOrigin.UserAction)();
                    Assert.That(JsonSerializer.Deserialize<ConversionJobRequest>(observer.FindByDownloadId(request.DownloadId).RequestJson).DiscardRequested, Is.True);
                    Assert.That(worker.Wait(TimeSpan.FromSeconds(40)), Is.True);
                    var saved = observer.FindByDownloadId(request.DownloadId);
                    Assert.That(saved.Status, Is.EqualTo(ConversionJobStatus.Cancelled));
                    Assert.That(JsonSerializer.Deserialize<ConversionJobRequest>(saved.RequestJson).DiscardRequested, Is.True);
                    Assert.That(service.IsRunning(request.DownloadId), Is.False);
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                    service.PrepareArtifactRemoval(request.DownloadId, ConversionRemovalOrigin.UserAction)();
                    Assert.That(observer.FindByDownloadId(request.DownloadId), Is.Null);
                }
                finally
                {
                    outage.Unavailable = false;
                    converter.Release.Set();
                    ShutdownAndWait(service);
                    try { worker.GetAwaiter().GetResult(); }
                    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
                }
            });
        }

        [TestCase(false, "cancel-acknowledgement")]
        [TestCase(true, "cancel-acknowledgement")]
        [TestCase(false, "acknowledgement")]
        [TestCase(true, "acknowledgement")]
        [TestCase(false, "success")]
        [TestCase(true, "success")]
        [TestCase(false, "failed")]
        [TestCase(true, "failed")]
        [TestCase(false, "throw")]
        [TestCase(true, "throw")]
        public void database_outage_must_not_change_worker_outcome_or_allow_duplicate_work(bool postgres, string outcome)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "conversion-worker-outage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                using var outage = new UnavailableDatabase(database, root, postgres);
                var repository = new ConversionJobRepository(outage.Database, new StubEventAggregator());
                var observer = new ConversionJobRepository(database, new StubEventAggregator());
                using var converter = new OutcomeConversionService { Outcome = outcome };
                var log = new ConcurrentLogTarget { Layout = "${level}|${message}|${exception:format=Message}" };
                using var logs = new LogFactory();
                var config = new LoggingConfiguration(logs);
                config.AddRuleForAllLevels(log);
                logs.Configuration = config;
                var lostAcknowledgement = 0;
                var workerRepository = InterfaceProxy.Proxy<IConversionJobRepository>((method, args) =>
                {
                    try
                    {
                        var result = method.Invoke(repository, args);
                        if ((outcome is "acknowledgement" or "cancel-acknowledgement") && method.Name == nameof(IConversionJobRepository.SetFields) &&
                            args[0] is ConversionJob saved && saved.Status == ConversionJobStatus.ReadyToImport &&
                            Interlocked.Exchange(ref lostAcknowledgement, 1) == 0)
                        {
                            outage.Unavailable = true;
                            using var unavailable = outage.Database.OpenConnection();
                        }
                        return result;
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException != null)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                        throw;
                    }
                });
                var service = CreateService(workerRepository, converter, logger: logs.GetLogger("outage"));
                var request = CreateRequest(root, Guid.NewGuid().ToString("N"));
                var tracking = new ConversionTrackingService(new StubEventAggregator());
                File.WriteAllText(Path.Combine(request.WorkFolder, "conversion-artifact.json"), JsonSerializer.Serialize(new ConversionArtifactManifest
                {
                    OutputPath = request.OutputPath, TargetQualityId = request.TargetQualityId,
                    AudioBitrate = request.AudioBitrate, AudioChannels = request.AudioChannels,
                    TagSignature = request.TagSignature, Sources = request.Sources
                }));
                try
                {
                    service.Enqueue(request);
                    service.Handle(new ApplicationStartedEvent());
                    Assert.That(converter.Started.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    outage.Unavailable = true;
                    Assert.Catch<DbException>(() => service.Reset(request.DownloadId));
                    Assert.That(converter.Calls, Is.EqualTo(1));
                    Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("partial audio"));
                    var ownership = JsonSerializer.Deserialize<ConversionArtifactManifest>(File.ReadAllText(Path.Combine(request.WorkFolder, "conversion-artifact.json")));
                    Assert.That(ownership.DownloadId, Is.EqualTo(request.DownloadId));
                    Assert.That(ownership.EncoderSucceeded, Is.False);
                    outage.Unavailable = false;
                    Assert.Throws<InvalidOperationException>(() => service.Reset(request.DownloadId));
                    service.RecoverExpiredLeases();
                    Assert.That(converter.Calls, Is.EqualTo(1), "A live encoder owns its output even after the database returns");

                    outage.Unavailable = outcome is not ("acknowledgement" or "cancel-acknowledgement");
                    converter.Release.Set();
                    Assert.That(SpinWait.SpinUntil(() => log.Logs.Any(value => value.Contains("Unable to save conversion outcome")),
                        TimeSpan.FromSeconds(5)), Is.True, "A native database failure must occur while saving the real worker outcome");
                    Assert.That(observer.FindByDownloadId(request.DownloadId).Status,
                        Is.EqualTo(outcome is "acknowledgement" or "cancel-acknowledgement" ? ConversionJobStatus.ReadyToImport : ConversionJobStatus.Converting));
                    Assert.That(service.IsRunning(request.DownloadId), Is.True);
                    Assert.Catch<DbException>(() => service.Reset(request.DownloadId));
                    outage.Unavailable = false;
                    Assert.Throws<InvalidOperationException>(() => service.Reset(request.DownloadId), "An unpersisted outcome still belongs to its worker");
                    if (outcome == "cancel-acknowledgement") Assert.That(service.Cancel(request.DownloadId, ConversionRemovalOrigin.UserAction), Is.True);
                    var expected = outcome == "cancel-acknowledgement" ? ConversionJobStatus.Cancelled :
                        outcome is "success" or "acknowledgement" ? ConversionJobStatus.ReadyToImport : ConversionJobStatus.Failed;
                    Assert.That(SpinWait.SpinUntil(() => observer.FindByDownloadId(request.DownloadId).Status == expected &&
                        !service.IsRunning(request.DownloadId), TimeSpan.FromSeconds(40)), Is.True);
                    Assert.That(converter.Calls, Is.EqualTo(1));
                    Assert.That(log.Logs.Any(value => value.Contains("Saved conversion outcome")), Is.True);
                    Assert.That(File.ReadAllText(request.Sources.Single().Path), Is.EqualTo("source"));
                    if (outcome is "success" or "acknowledgement")
                    {
                        Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("completed recording"));
                        Assert.That(File.Exists(Path.Combine(request.WorkFolder, "conversion-artifact.json")), Is.True);
                        Assert.DoesNotThrow(() => service.Reset(request.DownloadId));
                        Assert.That(observer.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    }
                    else
                    {
                        if (outcome != "cancel-acknowledgement")
                        {
                            Assert.That(observer.FindByDownloadId(request.DownloadId).Error,
                                Does.Contain(outcome == "failed" ? "encoder rejected input" : "encoder stopped unexpectedly"));
                        }
                        Assert.That(File.Exists(request.OutputPath), Is.False, "Failed partial output is not importable");
                        Assert.DoesNotThrow(() => service.Reset(request.DownloadId));
                        Assert.That(observer.FindByDownloadId(request.DownloadId), Is.Null);
                        if (outcome is "failed" or "throw")
                        {
                            converter.Outcome = "success";
                            service.Enqueue(request);
                            Assert.That(SpinWait.SpinUntil(() => observer.FindByDownloadId(request.DownloadId)?.Status == ConversionJobStatus.ReadyToImport &&
                                !service.IsRunning(request.DownloadId), TimeSpan.FromSeconds(5)), Is.True);
                            Assert.That(converter.Calls, Is.EqualTo(2), "Only deliberate retry may run a failed conversion again");
                            Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("completed recording"));
                        }
                    }
                }
                finally
                {
                    outage.Unavailable = false;
                    converter.Release.Set();
                    ShutdownAndWait(service);
                    tracking.Clear(request.DownloadId);
                    Directory.Delete(root, true);
                }
            });
        }
        [TestCase(false, "partial")]
        [TestCase(true, "partial")]
        [TestCase(false, "unreadable")]
        [TestCase(true, "unreadable")]
        [TestCase(false, "changed-source")]
        [TestCase(true, "changed-source")]
        public void retry_must_not_treat_unproved_output_as_a_completed_conversion(bool postgres, string evidence)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "conversion-proof-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var service = CreateService(repository, new UnusedConversionService());
                var request = CreateRequest(root, Guid.NewGuid().ToString("N"));
                try
                {
                    File.WriteAllText(request.OutputPath, "unproved audio");
                    var manifest = Path.Combine(request.WorkFolder, "conversion-artifact.json");
                    if (evidence == "unreadable") File.WriteAllText(manifest, "{incomplete");
                    if (evidence == "changed-source")
                    {
                        File.WriteAllText(manifest, JsonSerializer.Serialize(new ConversionArtifactManifest
                        {
                            OutputPath = request.OutputPath, TargetQualityId = request.TargetQualityId,
                            AudioBitrate = request.AudioBitrate, AudioChannels = request.AudioChannels,
                            TagSignature = request.TagSignature, Sources = request.Sources
                        }));
                        File.AppendAllText(request.Sources.Single().Path, " changed");
                    }
                    repository.Insert(CreatePersistedJob(request, ConversionJobStatus.ReadyToImport, DateTime.UtcNow));
                    Assert.Throws<InvalidOperationException>(() => service.Reset(request.DownloadId));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("unproved audio"));
                }
                finally { Directory.Delete(root, true); }
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void pending_success_write_must_honor_stop_or_survive_restart(bool postgres, bool restart)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "conversion-outcome-stop-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                using var outage = new UnavailableDatabase(database, root, postgres);
                var repository = new ConversionJobRepository(outage.Database, new StubEventAggregator());
                var observer = new ConversionJobRepository(database, new StubEventAggregator());
                using var converter = new OutcomeConversionService { Outcome = "success" };
                var log = new ConcurrentLogTarget { Layout = "${message}" };
                using var logs = new LogFactory();
                var config = new LoggingConfiguration(logs);
                config.AddRuleForAllLevels(log);
                logs.Configuration = config;
                var service = CreateService(repository, converter, logger: logs.GetLogger("stop-outage"));
                var request = CreateRequest(root, Guid.NewGuid().ToString("N"));
                try
                {
                    service.Enqueue(request);
                    service.Handle(new ApplicationStartedEvent());
                    Assert.That(converter.Started.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    outage.Unavailable = true;
                    converter.Release.Set();
                    Assert.That(SpinWait.SpinUntil(() => log.Logs.Any(value => value.Contains("Unable to save conversion outcome")), TimeSpan.FromSeconds(5)), Is.True);
                    if (restart)
                    {
                        ShutdownAndWait(service);
                        Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("completed recording"));
                        outage.Unavailable = false;
                        service = CreateService(repository, new UnusedConversionService());
                        service.Handle(new ApplicationStartedEvent());
                    }
                    else
                    {
                        outage.Unavailable = false;
                        Assert.That(service.Cancel(request.DownloadId, ConversionRemovalOrigin.UserAction), Is.True);
                    }
                    var tracking = new ConversionTrackingService(new StubEventAggregator());
                    Assert.That(SpinWait.SpinUntil(() => observer.FindByDownloadId(request.DownloadId).Status ==
                        (restart ? ConversionJobStatus.ReadyToImport : ConversionJobStatus.Cancelled) &&
                        !service.IsRunning(request.DownloadId), TimeSpan.FromSeconds(40)), Is.True);
                    Assert.That(converter.Calls, Is.EqualTo(1));
                    Assert.That(File.Exists(request.OutputPath), Is.EqualTo(restart));
                    Assert.That(File.ReadAllText(request.Sources.Single().Path), Is.EqualTo("source"));
                }
                finally
                {
                    outage.Unavailable = false;
                    converter.Release.Set();
                    ShutdownAndWait(service);
                    new ConversionTrackingService(new StubEventAggregator()).Clear(request.DownloadId);
                    Directory.Delete(root, true);
                }
            });
        }
    }
}

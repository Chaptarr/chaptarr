using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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

        internal static Task GetWorkerCompletion(ConversionJobService service, string downloadId)
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            lock (typeof(ConversionJobService).GetField("_sync", flags).GetValue(service))
            {
                var owners = (IDictionary)typeof(ConversionJobService).GetField("_activeConversions", flags).GetValue(service);
                var owner = owners[downloadId];
                return ((TaskCompletionSource)owner.GetType().GetProperty("Completion").GetValue(owner)).Task;
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

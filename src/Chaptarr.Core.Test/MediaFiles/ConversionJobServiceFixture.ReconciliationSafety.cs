using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        private static void SeedClientHistory(IMainDatabase database, string id, DownloadHistoryEventType type = DownloadHistoryEventType.DownloadGrabbed, bool clientExists = true, int clientId = 4)
        {
            using var connection = database.OpenConnection();
            var identity = database.DatabaseType == DatabaseType.PostgreSQL ? "OVERRIDING SYSTEM VALUE" : string.Empty;
            if (clientExists) connection.Execute($@"INSERT INTO ""DownloadClients""
                (""Id"", ""Enable"", ""Name"", ""Implementation"", ""ConfigContract"")
                {identity} VALUES (@clientId, @enabled, 'Test client', 'Test', 'Test') ON CONFLICT (""Id"") DO NOTHING", new { enabled = true, clientId });
            connection.Execute(@"INSERT INTO ""DownloadHistory""
                (""EventType"", ""AuthorId"", ""BookId"", ""DownloadId"", ""SourceTitle"", ""Date"", ""DownloadClientId"")
                VALUES (@type, 1, 1, @id, 'Download', @now, @clientId)", new { type = (int)type, id = id.ToUpperInvariant(), now = DateTime.UtcNow, clientId });
        }

        [Test]
        public void normalized_download_lookup_preserves_exact_identity_and_refuses_ambiguity([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-normalized-id-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var request = CreateRequest(directory, "SABnzbd_nzo_Case");
                    request.DownloadClientId = 4;
                    var original = repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Completed, DateTime.UtcNow));
                    Assert.That(repository.FindByHistoryDownloadId(request.DownloadId).Id, Is.EqualTo(original.Id));
                    Assert.That(repository.FindByHistoryDownloadId(request.DownloadId.ToUpperInvariant()).Id, Is.EqualTo(original.Id));
                    Assert.That(repository.FindByHistoryDownloadId("missing"), Is.Null);
                    var alternate = CreateRequest(Path.Combine(directory, "alternate"), request.DownloadId.ToLowerInvariant());
                    alternate.DownloadClientId = 4;
                    var second = repository.Insert(CreatePersistedJob(alternate, ConversionJobStatus.Completed, DateTime.UtcNow));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Id, Is.EqualTo(original.Id));
                    Assert.That(repository.FindByDownloadId(alternate.DownloadId).Id, Is.EqualTo(second.Id));
                    Assert.Throws<InvalidOperationException>(() => repository.FindByHistoryDownloadId(request.DownloadId.ToUpperInvariant()));
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void cleanup_failures_keep_retrying_at_a_capped_delay_without_losing_the_conversion_error([Values(false, true)] bool postgres, [Values(false, true)] bool accessDenied)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-cleanup-retry-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "locked");
                    request.DownloadClientId = 4;
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var original = CreatePersistedJob(request, ConversionJobStatus.Completed, DateTime.UtcNow);
                    original.Error = "original encoder error";
                    repository.Insert(original);
                    var calls = 0;
                    var locked = true;
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, _) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IMediaFileService.GetFilePathOwnership)));
                        calls++;
                        if (locked)
                        {
                            if (accessDenied) throw new UnauthorizedAccessException("workspace delete pending");
                            throw new IOException("workspace temporarily locked");
                        }
                        return new List<BookFile>();
                    });
                    var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), media, repository, new UnusedConversionService(), null,
                        DispatchProxy.Create<NzbDrone.Core.Messaging.Commands.IManageCommandQueue, RecordingCommandQueueProxy>(), CreateConfig(), NLog.LogManager.GetCurrentClassLogger());
                    var completed = new DownloadCompletedEvent(new TrackedDownload { DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId } }, 1);
                    for (var attempt = 1; attempt <= 8; attempt++)
                    {
                        service.Handle(completed);
                        service.Handle(completed);
                        Assert.That(calls, Is.EqualTo(attempt), "No repeated I/O before the due time");
                        var job = repository.FindByDownloadId(request.DownloadId);
                        var saved = System.Text.Json.JsonSerializer.Deserialize<ConversionJobRequest>(job.RequestJson);
                        Assert.That(saved.CleanupFailureCount, Is.EqualTo(attempt));
                        Assert.That(job.Error, Is.EqualTo("original encoder error"));
                        Assert.That(saved.CleanupNotBefore, Is.LessThanOrEqualTo(DateTime.UtcNow.AddHours(1)));
                        if (attempt < 8)
                        {
                            Assert.That(saved.CleanupNotBefore, Is.GreaterThan(DateTime.UtcNow));
                            saved.CleanupNotBefore = DateTime.UtcNow.AddSeconds(-1);
                            job.RequestJson = System.Text.Json.JsonSerializer.Serialize(saved);
                            repository.SetFields(job, row => row.RequestJson);
                        }
                    }
                    Assert.That(repository.CleanupWarnings(), Has.Count.EqualTo(1));
                    locked = false;
                    service.Handle(completed);
                    Assert.That(calls, Is.EqualTo(8), "A capped retry still waits for its due time");
                    MakeCleanupDue(repository, request.DownloadId);
                    ReviewAttention(service);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void lifecycle_cleanup_batches_library_ownership_and_pages_candidates([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-cleanup-batch-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    for (var i = 0; i < 101; i++)
                    {
                        var request = CreateRequest(directory, "batch-" + i);
                        request.DownloadClientId = 4;
                        repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Completed, DateTime.UtcNow));
                        SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadImported);
                    }
                    var reads = 0;
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IMediaFileService.GetFilePathOwnership)));
                        reads++;
                        Assert.That(((IReadOnlyCollection<string>)args[1]).Count, Is.LessThanOrEqualTo(100));
                        return new List<BookFile>();
                    });
                    var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), media, repository, new UnusedConversionService(), null,
                        DispatchProxy.Create<NzbDrone.Core.Messaging.Commands.IManageCommandQueue, RecordingCommandQueueProxy>(), CreateConfig(), NLog.LogManager.GetCurrentClassLogger());
                    var refresh = new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() });
                    service.Handle(refresh);
                    Assert.That(repository.All(), Has.Count.EqualTo(1));
                    Assert.That(reads, Is.EqualTo(1));
                    service.Handle(refresh);
                    Assert.That(repository.All(), Is.Empty);
                    Assert.That(reads, Is.EqualTo(2));
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void two_books_in_one_download_share_only_the_active_job_until_it_finishes([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-two-books-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var first = CreateRequest(directory, "same-download");
                    first.DownloadClientId = 4;
                    first.BookId = 1;
                    first.EditionId = 11;
                    var second = CreateRequest(Path.Combine(directory, "second"), "same-download");
                    second.DownloadClientId = 4;
                    second.BookId = 2;
                    second.EditionId = 22;
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var service = CreateService(repository, new UnusedConversionService());
                    var accepted = service.Enqueue(first);
                    var waiting = service.Enqueue(second);
                    Assert.That(waiting.Id, Is.EqualTo(accepted.Id));
                    Assert.That(waiting.WorkFolder, Is.EqualTo(first.WorkFolder));
                    Assert.That(waiting.GetAcceptedRequest().BookId, Is.EqualTo(1));
                    Assert.That(repository.All(), Has.Count.EqualTo(1));
                    accepted.Status = ConversionJobStatus.Completed;
                    repository.SetFields(accepted, job => job.Status);
                    var next = service.Enqueue(second);
                    Assert.That(next.GetAcceptedRequest().BookId, Is.EqualTo(2));
                    Assert.That(next.WorkFolder, Is.EqualTo(second.WorkFolder));
                    Assert.That(File.ReadAllText(first.Sources.Single().Path), Is.EqualTo("source"));
                    Assert.That(File.ReadAllText(second.Sources.Single().Path), Is.EqualTo("source"));
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void client_history_or_category_dropoff_keeps_tracked_conversion(
            [Values(false, true)] bool postgres,
            [Values(ConversionJobStatus.Queued, ConversionJobStatus.Converting, ConversionJobStatus.Cancelling, ConversionJobStatus.ReadyToImport, ConversionJobStatus.Completed)] ConversionJobStatus status,
            [Values("history-window", "category", "deleted-client")] string dropoff)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-tracked-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "SABnzbd_nzo_MixedCase");
                    request.DownloadClientId = 4;
                    File.WriteAllText(request.OutputPath, "keep");
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var job = repository.Insert(CreatePersistedJob(request, status, DateTime.UtcNow));
                    job = repository.Get(job.Id);
                    SeedClientHistory(database, request.DownloadId, clientExists: dropoff != "deleted-client");
                    var service = CreateService(repository, new UnusedConversionService());
                    var tracked = new TrackedDownload { DownloadClient = 4, State = TrackedDownloadState.ImportPending,
                        DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId } };
                    var reported = new HashSet<string>(Enumerable.Range(0, dropoff == "history-window" ? 60 : 0).Select(i => "new-download-" + i));
                    var reports = dropoff == "deleted-client" ? new Dictionary<int, HashSet<string>>()
                        : new Dictionary<int, HashSet<string>> { [4] = reported };
                    for (var i = 0; i < 3; i++) service.Handle(new TrackedDownloadRefreshedEvent(new() { tracked }, reports));
                    Assert.That(repository.Get(job.Id).Status, Is.EqualTo(status));
                    Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("keep"));
                    Assert.That(repository.Get(job.Id).UpdatedAt, Is.EqualTo(job.UpdatedAt));
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void confirmed_queue_absence_cleans_every_job_status_and_preserves_sources(
            [Values(false, true)] bool postgres,
            [Values(ConversionJobStatus.Queued, ConversionJobStatus.Converting, ConversionJobStatus.Cancelling, ConversionJobStatus.ReadyToImport, ConversionJobStatus.Completed)] ConversionJobStatus status,
            [Values(false, true)] bool removedClient)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-gone-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "SABnzbd_nzo_MixedCase");
                    request.DownloadClientId = 4;
                    File.WriteAllText(request.OutputPath, "keep");
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    repository.Insert(CreatePersistedJob(request, status, DateTime.UtcNow));
                    SeedClientHistory(database, request.DownloadId, clientExists: !removedClient);
                    var service = CreateService(repository, new UnusedConversionService());
                    // Repeated successful polls are idempotent after cleanup.
                    for (var pass = 0; pass < 2; pass++)
                        service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() }));
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void automatic_completion_and_refusal_cannot_cancel_in_flight_work(
            [Values(false, true)] bool postgres,
            [Values(ConversionJobStatus.Queued, ConversionJobStatus.Converting, ConversionJobStatus.Cancelling)] ConversionJobStatus status)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-auto-stop-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "queued");
                    request.DownloadClientId = 4;
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    repository.Insert(CreatePersistedJob(request, status, DateTime.UtcNow));
                    var service = CreateService(repository, new UnusedConversionService());
                    service.Handle(new DownloadCompletedEvent(new TrackedDownload { DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId } }, 1));
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Not.Null, "Automatic cleanup cannot discard unproven in-flight work");
                    Assert.That(service.RetainCompletedOutput(request.DownloadId, request.Sources.Select(source => source.Path).ToArray(), "refused", discardOutput: true).Status,
                        Is.EqualTo(ConversionRetentionStatus.Pending));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(status));
                    Assert.That(Directory.Exists(request.WorkFolder), Is.True);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void mixed_case_terminal_history_cleans_the_raw_download_id(
            [Values(false, true)] bool postgres, [Values("failed", "ignored", "reconcile")] string route)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-case-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "SABnzbd_nzo_MixedCase");
                    request.DownloadClientId = 4;
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Completed, DateTime.UtcNow));
                    SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadFailed);
                    var service = CreateService(repository, new UnusedConversionService());
                    if (route == "failed") service.Handle(new DownloadFailedEvent { DownloadId = request.DownloadId.ToUpperInvariant() });
                    else if (route == "ignored") service.Handle(new DownloadIgnoredEvent { DownloadId = request.DownloadId.ToUpperInvariant() });
                    else service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() }));
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }
    }
}

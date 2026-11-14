using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void conversion_identity_migration_uses_unicode_keys_and_does_not_guess_a_client([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                string connectionString;
                using (var connection = database.OpenConnection())
                {
                    connectionString = connection.ConnectionString;
                    connection.Execute(@"INSERT INTO ""ConversionJobs"" (""DownloadId"", ""Status"", ""RequestJson"", ""TargetQualityId"", ""CreatedAt"", ""UpdatedAt"")
                        VALUES ('épisode', 3, '{}', 12, @now, @now), ('ambiguous', 3, '{}', 12, @now, @now);
                        INSERT INTO ""DownloadHistory"" (""EventType"", ""AuthorId"", ""BookId"", ""DownloadId"", ""SourceTitle"", ""Date"", ""DownloadClientId"")
                        VALUES (1, 1, 1, 'ÉPISODE', 'Book', @now, 4), (1, 1, 1, 'AMBIGUOUS', 'Book', @now, 4), (1, 1, 1, 'AMBIGUOUS', 'Book', @now, 9)", new { now = DateTime.UtcNow });
                }
                new NzbDrone.Core.Datastore.Migration.Framework.MigrationController(NLog.LogManager.GetCurrentClassLogger(), null).Migrate(
                    connectionString, new NzbDrone.Core.Datastore.Migration.Framework.MigrationContext(NzbDrone.Core.Datastore.Migration.Framework.MigrationType.Main, 128), database.DatabaseType);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                Assert.That(repository.FindByHistoryDownloadId("ÉPISODE", 4).DownloadId, Is.EqualTo("épisode"));
                Assert.That(repository.FindByHistoryDownloadId("AMBIGUOUS").DownloadClientId, Is.Zero);
                using var migrated = database.OpenConnection();
                Assert.That(migrated.Query<string>(@"SELECT ""DownloadIdKey"" FROM ""ConversionJobs"" ORDER BY ""Id"""), Is.EqualTo(new[] { "ÉPISODE", "AMBIGUOUS" }));
            }, desiredVersion: 127);
        }

        [Test]
        public void conversion_workspace_inventory_uses_the_marker_for_primary_and_replica_paths([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var connection = database.OpenConnection();
                var root = Path.Combine(Path.GetTempPath(), ".Chaptarr-Conversions", "épisode");
                var primary = Path.Combine(root, "attempt", "book.m4b");
                var replica = Path.Combine(root, "attempt", "book.epub");
                connection.Execute(@"INSERT INTO ""BookFiles"" (""Path"", ""ReplicaPaths"", ""Size"", ""Modified"", ""DateAdded"", ""Quality"", ""MediaInfo"", ""EditionId"", ""CalibreId"", ""Part"")
                    VALUES (@primary, '[]', 1, @now, @now, '{}', '{}', 0, 0, 1),
                    (@outside, @replicas, 1, @now, @now, '{}', '{}', 0, 0, 1),
                    (@ordinary, '[]', 1, @now, @now, '{}', '{}', 0, 0, 1)", new
                {
                    primary,
                    outside = Path.Combine(Path.GetTempPath(), "library", "épisode.epub"),
                    replicas = JsonSerializer.Serialize(new[] { replica }),
                    ordinary = Path.Combine(Path.GetTempPath(), "library", "other.m4b"),
                    now = DateTime.UtcNow
                });
                var repository = new MediaFileRepository(database, new StubEventAggregator());
                var inventory = repository.GetFilePathOwnership(Array.Empty<int>(), new[] { root });
                Assert.That(inventory, Has.Count.EqualTo(2));
                Assert.That(inventory.Select(file => file.Path), Does.Contain(primary));
                Assert.That(inventory.SelectMany(file => file.ReplicaPaths ?? new()), Does.Contain(replica));
                const string query = @"SELECT ""Id"", ""Path"", ""ReplicaPaths"" FROM ""BookFiles""
                    WHERE LOWER(""Path"") LIKE '%chaptarr-conversions%' OR LOWER(""ReplicaPaths"") LIKE '%chaptarr-conversions%'";
                if (postgres) connection.Execute("SET enable_seqscan = off");
                var plan = postgres ? string.Join(" ", connection.Query<string>("EXPLAIN " + query)) :
                    string.Join(" ", connection.Query("EXPLAIN QUERY PLAN " + query).Select(row => (string)row.detail));
                Assert.That(plan, Does.Contain("IX_BookFiles_ConversionWorkspace"));
            });
        }

        [Test]
        public void old_conversion_artifacts_follow_client_attachment_and_terminal_history(
            [Values(false, true)] bool postgres,
            [Values(DownloadHistoryEventType.DownloadGrabbed, DownloadHistoryEventType.DownloadImported,
                DownloadHistoryEventType.DownloadFailed, DownloadHistoryEventType.DownloadIgnored)] DownloadHistoryEventType state)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-old-artifact-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "ancien-épisode");
                    request.DownloadClientId = 4;
                    File.WriteAllText(request.OutputPath, "completed conversion");
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    using (var connection = database.OpenConnection())
                    {
                        var data = JsonSerializer.Serialize(new Dictionary<string, string>
                        {
                            ["ConvertedPath"] = request.OutputPath,
                            ["SourcePaths"] = JsonSerializer.Serialize(request.Sources.Select(source => source.Path))
                        });
                        connection.Execute(@"INSERT INTO ""History"" (""AuthorId"", ""BookId"", ""EditionId"", ""Quality"", ""Date"", ""EventType"", ""Data"", ""SourceTitle"", ""DownloadId"")
                            VALUES (1, 1, 1, '{}', @date, @type, @data, 'Converted book', @id)", new
                        {
                            date = DateTime.UtcNow.AddDays(-8),
                            type = (int)EntityHistoryEventType.BookFileConversionFailed,
                            data,
                            id = DownloadIdentity.Key(request.DownloadId)
                        });
                    }
                    SeedClientHistory(database, request.DownloadId, state);
                    var service = CreateService(repository, new UnusedConversionService());
                    var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (state == DownloadHistoryEventType.DownloadGrabbed) reported.Add(request.DownloadId);
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = reported }));
                    typeof(ConversionJobService).GetMethod("ReviewOrphanArtifacts", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(File.Exists(request.OutputPath), Is.EqualTo(state == DownloadHistoryEventType.DownloadGrabbed));
                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                    Assert.That(repository.All(), Is.Empty);
                    if (state == DownloadHistoryEventType.DownloadGrabbed)
                        SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadImported);
                    typeof(ConversionJobService).GetField("_orphanNotBefore", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(service, DateTime.MinValue);
                    typeof(ConversionJobService).GetMethod("ReviewOrphanArtifacts", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(File.Exists(request.OutputPath), Is.False, "The same service must revisit an artifact when its download becomes terminal");
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void resolving_a_structural_cleanup_refusal_retries_without_erasing_the_encoder_error([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-refusal-recovery-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "owned");
                    request.DownloadClientId = 4;
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var job = CreatePersistedJob(request, ConversionJobStatus.Failed, DateTime.UtcNow);
                    job.Error = "encoder stopped unexpectedly";
                    repository.Insert(job);
                    var protectedFile = true;
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, _) =>
                        protectedFile ? new List<BookFile> { new() { Path = request.OutputPath } } : new List<BookFile>());
                    var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), media, repository, new UnusedConversionService(), null,
                        InterfaceProxy.Proxy<NzbDrone.Core.Messaging.Commands.IManageCommandQueue>((_, _) => null), CreateConfig(), NLog.LogManager.GetCurrentClassLogger());
                    service.Handle(new DownloadCompletedEvent(new TrackedDownload
                    {
                        DownloadClient = 4,
                        DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId }
                    }, 1));
                    Assert.That(repository.CleanupWarnings(), Has.Count.EqualTo(1));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Error, Is.EqualTo("encoder stopped unexpectedly"));
                    protectedFile = false;
                    MakeCleanupDue(repository, request.DownloadId);
                    ReviewAttention(service);
                    Assert.That(repository.CleanupWarnings(), Is.Empty);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        private static void MakeCleanupDue(IConversionJobRepository repository, string id)
        {
            var job = repository.FindByDownloadId(id);
            var request = JsonSerializer.Deserialize<ConversionJobRequest>(job.RequestJson);
            request.CleanupNotBefore = DateTime.UtcNow.AddSeconds(-1);
            job.RequestJson = JsonSerializer.Serialize(request);
            repository.SetFields(job, value => value.RequestJson);
        }

        private static void ReviewAttention(ConversionJobService service)
        {
            typeof(ConversionJobService).GetField("_attentionNotBefore", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(service, DateTime.MinValue);
            typeof(ConversionJobService).GetMethod("ReviewCleanupAttention", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
        }

        [Test]
        public void normalized_unicode_identity_keeps_download_clients_separate([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-unicode-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var first = CreateRequest(directory, "Étude-épisode");
                    first.DownloadClientId = 4;
                    var other = CreateRequest(Path.Combine(directory, "other"), first.DownloadId.ToUpperInvariant());
                    other.DownloadClientId = 4;
                    other.DownloadClientId = 9;
                    repository.Insert(CreatePersistedJob(first, ConversionJobStatus.ReadyToImport, DateTime.UtcNow));
                    repository.Insert(CreatePersistedJob(other, ConversionJobStatus.ReadyToImport, DateTime.UtcNow));
                    SeedClientHistory(database, first.DownloadId, DownloadHistoryEventType.DownloadFailed);
                    Assert.That(repository.FindByHistoryDownloadId(first.DownloadId.ToUpperInvariant(), 4).DownloadId, Is.EqualTo(first.DownloadId));
                    Assert.That(repository.FindByHistoryDownloadId(first.DownloadId.ToUpperInvariant(), 9).DownloadId, Is.EqualTo(other.DownloadId));
                    var ambiguity = Assert.Throws<InvalidOperationException>(() => repository.FindByHistoryDownloadId(first.DownloadId));
                    Assert.That(ambiguity.Message, Does.Contain("Ambiguous").And.Contain("client 4").And.Contain("client 9"));
                    var candidates = repository.LifecycleCandidates(0);
                    Assert.That(candidates.Single(job => job.DownloadClientId == 4).EventType, Is.EqualTo(DownloadHistoryEventType.DownloadFailed));
                    Assert.That(candidates.Single(job => job.DownloadClientId == 9).EventType, Is.EqualTo(default(DownloadHistoryEventType)));
                    var service = CreateService(repository, new UnusedConversionService());
                    service.Handle(new DownloadFailedEvent
                    {
                        DownloadId = first.DownloadId.ToUpperInvariant(),
                        TrackedDownload = new TrackedDownload
                        {
                            DownloadClient = 4,
                            DownloadItem = new DownloadClientItem { DownloadId = first.DownloadId }
                        }
                    });
                    Assert.That(repository.FindByDownloadId(first.DownloadId), Is.Null);
                    Assert.That(repository.FindByDownloadId(other.DownloadId).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    Assert.That(Directory.Exists(other.WorkFolder), Is.True);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void ambiguous_history_never_applies_failure_to_another_case_distinct_job([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-ambiguous-history-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var first = CreateRequest(directory, "Étude");
                    first.DownloadClientId = 4;
                    var second = CreateRequest(Path.Combine(directory, "second"), "ÉTUDE");
                    second.DownloadClientId = 4;
                    repository.Insert(CreatePersistedJob(first, ConversionJobStatus.Queued, DateTime.UtcNow));
                    repository.Insert(CreatePersistedJob(second, ConversionJobStatus.ReadyToImport, DateTime.UtcNow));
                    SeedClientHistory(database, first.DownloadId, DownloadHistoryEventType.DownloadFailed);
                    var service = CreateService(repository, new UnusedConversionService());
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() }));
                    Assert.That(repository.FindByDownloadId(first.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Queued));
                    Assert.That(repository.FindByDownloadId(second.DownloadId).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    Assert.That(repository.CleanupWarnings(), Has.Count.EqualTo(2));
                    Assert.That(repository.All().All(job => JsonSerializer.Deserialize<ConversionJobRequest>(job.RequestJson).HistoryIdentityAmbiguous), Is.True);
                    Assert.That(Directory.Exists(first.WorkFolder), Is.True);
                    Assert.That(Directory.Exists(second.WorkFolder), Is.True);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void inferred_removal_without_a_successful_client_poll_cannot_stop_importable_work(
            [Values(false, true)] bool postgres,
            [Values(ConversionJobStatus.Queued, ConversionJobStatus.Converting, ConversionJobStatus.Cancelling, ConversionJobStatus.ReadyToImport)] ConversionJobStatus status)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-inferred-gate-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "protected");
                    request.DownloadClientId = 4;
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    repository.Insert(CreatePersistedJob(request, status, DateTime.UtcNow));
                    var service = CreateService(repository, new UnusedConversionService());
                    Assert.That(service.Cancel(request.DownloadId, ConversionRemovalOrigin.Inferred), Is.False, "No successful poll proves removal");
                    service.PrepareArtifactRemoval(request.DownloadId, ConversionRemovalOrigin.Inferred)();
                    if (status != ConversionJobStatus.ReadyToImport)
                    {
                        service.Fail(request.DownloadId, "late import pass");
                        service.Complete(request.DownloadId);
                    }
                    var protectedJob = repository.FindByDownloadId(request.DownloadId);
                    Assert.That(protectedJob, Is.Not.Null, "The removal guard must keep an unproven job");
                    Assert.That(protectedJob.Status, Is.EqualTo(status));
                    Assert.That(Directory.Exists(request.WorkFolder), Is.True);
                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void a_legacy_artifact_without_a_job_still_reports_its_import_outcome(
            [Values(false, true)] bool postgres, [Values(false, true)] bool success)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = new StubEventAggregator();
                var repository = new ConversionJobRepository(database, events);
                var tracking = new ConversionTrackingService(events);
                var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), ImportLifecycleStubs.NoLibraryMediaFiles(), repository,
                    new UnusedConversionService(), tracking,
                    DispatchProxy.Create<NzbDrone.Core.Messaging.Commands.IManageCommandQueue, RecordingCommandQueueProxy>(),
                    CreateConfig(), NLog.LogManager.GetCurrentClassLogger());
                tracking.Start("legacy-artifact", 12, "M4B");
                if (success) service.Complete("legacy-artifact");
                else service.Fail("legacy-artifact", "Destination is not writable");
                Assert.That(repository.All(), Is.Empty);
                if (success) Assert.That(tracking.Get("legacy-artifact"), Is.Null);
                else
                {
                    Assert.That(tracking.Get("legacy-artifact").Status, Is.EqualTo("failed"));
                    Assert.That(tracking.Get("legacy-artifact").Message, Is.EqualTo("Destination is not writable"));
                }
            });
        }

        [Test]
        public void conversion_removal_never_defaults_to_a_user_action()
        {
            foreach (var type in new[] { typeof(IConversionJobService), typeof(ConversionJobService) })
            {
                foreach (var name in new[] { "Cancel", "PrepareArtifactRemoval" })
                {
                    var methods = type.GetMethods().Where(method => method.Name == name).ToList();
                    Assert.That(methods, Has.Count.EqualTo(1));
                    var origin = methods.Single().GetParameters().Single(parameter => parameter.ParameterType == typeof(ConversionRemovalOrigin));
                    Assert.That(origin.HasDefaultValue, Is.False);
                }
                Assert.That(type.GetMethod("RemoveArtifacts"), Is.Null);
            }
        }

        [Test]
        public void confirmed_missing_download_is_cleaned_without_an_unavailable_health_warning(
            [Values(false, true)] bool postgres,
            [Values(ConversionJobStatus.Queued, ConversionJobStatus.Converting, ConversionJobStatus.ReadyToImport)] ConversionJobStatus status,
            [Values("present", "removed", "unknown")] string client)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-attention-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "dropped-from-client");
                    request.DownloadClientId = 4;
                    request.BookTitle = "A book awaiting import";
                    if (client == "unknown") request.DownloadClientId = 0;
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    repository.Insert(CreatePersistedJob(request, status, DateTime.UtcNow));
                    SeedClientHistory(database, request.DownloadId, clientExists: client == "present");
                    var service = CreateService(repository, new UnusedConversionService());
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), client != "present" ? new() : new Dictionary<int, HashSet<string>> { [4] = new() }));
                    var remaining = repository.FindByDownloadId(request.DownloadId);
                    if (client == "unknown")
                    {
                        Assert.That(remaining, Is.Not.Null, "An unowned legacy job needs removal evidence");
                        Assert.That(remaining.Status, Is.EqualTo(status));
                    }
                    else Assert.That(remaining, Is.Null);
                    Assert.That(Directory.Exists(request.WorkFolder), Is.EqualTo(client == "unknown"));
                    var localization = InterfaceProxy.Proxy<ILocalizationService>((_, _) => throw new AssertionException("No warning is expected"));
                    var check = new ConversionCleanupCheck(repository, localization);
                    Assert.That(check.Check().Type, Is.EqualTo(NzbDrone.Core.HealthCheck.HealthCheckResult.Ok));
                    Assert.That(typeof(ConversionCleanupCheck).GetCustomAttributes(typeof(NzbDrone.Core.HealthCheck.CheckOnAttribute), false).Length, Is.GreaterThanOrEqualTo(5));
                    Assert.That(repository.CleanupWarnings(), Is.Empty);
                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }
    }
}

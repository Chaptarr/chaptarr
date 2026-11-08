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
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void orphan_startup_waits_for_its_client_poll_except_after_import(
            [Values(false, true)] bool postgres,
            [Values(DownloadHistoryEventType.DownloadImported, DownloadHistoryEventType.DownloadFailed,
                DownloadHistoryEventType.DownloadIgnored)] DownloadHistoryEventType state)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-orphan-startup-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "startup-orphan");
                    request.DownloadClientId = 4;
                    File.WriteAllText(request.OutputPath, "completed conversion");
                    var id = SeedOrphanHistory(database, request);
                    SeedClientHistory(database, request.DownloadId, state);
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var config = OrphanConfig(database);
                    var service = CreateService(repository, new UnusedConversionService(), config: config);

                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(File.Exists(request.OutputPath), Is.EqualTo(state != DownloadHistoryEventType.DownloadImported),
                        "Only confirmed import completion can settle an orphan before the first client poll");
                    if (state != DownloadHistoryEventType.DownloadImported)
                    {
                        Assert.That(JsonDocument.Parse(config.GetConversionOrphanReviewProgress()).RootElement.GetProperty("DeferredIds")
                            .EnumerateArray().Select(value => value.GetInt32()), Does.Contain(id));
                        // A processing notification and another client's success cannot establish absence.
                        service.Handle(new TrackedDownloadRefreshedEvent(new()));
                        ReviewOrphans(service);
                        Assert.That(File.Exists(request.OutputPath), Is.True);
                        service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [5] = new() }));
                        ReviewOrphans(service);
                        Assert.That(File.Exists(request.OutputPath), Is.True);
                        service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>>()));
                        ReviewOrphans(service);
                        Assert.That(File.Exists(request.OutputPath), Is.True, "A failed poll supplies no client evidence");

                        service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() }));
                        ReviewOrphans(service);
                        Assert.That(File.Exists(request.OutputPath), Is.False, "A successful poll permits cleanup of the now-unattached terminal download");
                    }

                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                    Assert.That(repository.All(), Is.Empty);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void orphan_with_terminal_history_stays_while_reported_or_actively_tracked(
            [Values(false, true)] bool postgres,
            [Values(DownloadHistoryEventType.DownloadFailed, DownloadHistoryEventType.DownloadIgnored)] DownloadHistoryEventType state,
            [Values(false, true)] bool trackedOnly)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-orphan-attached-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "SABnzbd_orphan_Case");
                    request.DownloadClientId = 4;
                    File.WriteAllText(request.OutputPath, "retained conversion");
                    var id = SeedOrphanHistory(database, request);
                    SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadImported);
                    SeedClientHistory(database, request.DownloadId, state);
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var candidate = repository.OrphanArtifacts(0, Array.Empty<int>()).Single();
                    Assert.That(candidate.EventType, Is.EqualTo(state), "The latest significant event must travel with its client identity");
                    Assert.That(candidate.DownloadClientId, Is.EqualTo(4));
                    var config = OrphanConfig(database);
                    var service = CreateService(repository, new UnusedConversionService(), config: config);
                    var tracked = trackedOnly ? new List<TrackedDownload>
                    {
                        new()
                        {
                            DownloadClient = 4,
                            DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId },
                            State = TrackedDownloadState.ImportPending
                        }
                    } : new List<TrackedDownload>();
                    var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!trackedOnly) reported.Add(request.DownloadId);
                    service.Handle(new TrackedDownloadRefreshedEvent(tracked, new Dictionary<int, HashSet<string>> { [4] = reported }));
                    ReviewOrphans(service);
                    Assert.Multiple(() =>
                    {
                        Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("retained conversion"));
                        Assert.That(repository.All(), Is.Empty, "Retaining an orphan does not invent a conversion job");
                        Assert.That(JsonDocument.Parse(config.GetConversionOrphanReviewProgress()).RootElement.GetProperty("DeferredIds")
                            .EnumerateArray().Select(value => value.GetInt32()), Does.Contain(id));
                    });

                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() }));
                    ReviewOrphans(service);
                    Assert.That(File.Exists(request.OutputPath), Is.False);
                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void orphan_with_multiple_client_histories_cannot_borrow_an_imported_event([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-orphan-client-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "shared-client-id");
                    File.WriteAllText(request.OutputPath, "unresolved ownership");
                    SeedOrphanHistory(database, request);
                    SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadFailed);
                    using (var connection = database.OpenConnection())
                        connection.Execute(@"INSERT INTO ""DownloadHistory"" (""EventType"", ""AuthorId"", ""BookId"", ""DownloadId"", ""SourceTitle"", ""Date"", ""DownloadClientId"")
                            VALUES (@type, 1, 1, @id, 'Download', @now, 5)", new
                            {
                                type = (int)DownloadHistoryEventType.DownloadImported,
                                id = DownloadIdentity.Key(request.DownloadId), now = DateTime.UtcNow
                            });
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var candidate = repository.OrphanArtifacts(0, Array.Empty<int>()).Single();
                    Assert.That(candidate.ClientCount, Is.EqualTo(2));
                    Assert.That(candidate.EventType, Is.Null);
                    Assert.That(candidate.DownloadClientId, Is.Zero);
                    Assert.That(candidate.ClientMissing, Is.False, "A deleted client does not identify the owner of a multi-client artifact");
                    var service = CreateService(repository, new UnusedConversionService(), config: OrphanConfig(database));
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new(), [5] = new() }));
                    ReviewOrphans(service);
                    Assert.That(File.Exists(request.OutputPath), Is.True, "Multiple clients leave artifact ownership unresolved");
                    Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo("unresolved ownership"));
                    Assert.That(File.Exists(request.Sources[0].Path), Is.True);
                    Assert.That(repository.All(), Is.Empty);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }
    }
}

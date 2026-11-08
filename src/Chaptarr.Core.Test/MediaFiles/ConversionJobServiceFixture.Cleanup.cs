using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void pending_discard_cleanup_reads_a_bounded_ordered_batch([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var rows = new ConversionJobRepository(database, new StubEventAggregator());
                var pending = Enumerable.Range(0, ConversionJobRepository.CleanupBatchSize + 3).Select(index => new ConversionJob
                {
                    DownloadId = "cleanup-" + index,
                    Status = ConversionJobStatus.Cancelled,
                    RequestJson = JsonSerializer.Serialize(new ConversionJobRequest { DiscardRequested = true }),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }).ToList();
                rows.InsertMany(pending);
                rows.Insert(new ConversionJob { DownloadId = "not-discarded", Status = ConversionJobStatus.Cancelled,
                    RequestJson = "{}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                Assert.That(rows.PendingCleanup().Select(job => job.Id), Is.EqualTo(pending.Take(ConversionJobRepository.CleanupBatchSize).Select(job => job.Id)));
                rows.DeleteMany(pending.Take(ConversionJobRepository.CleanupBatchSize).Select(job => job.Id));
                Assert.That(rows.PendingCleanup().Select(job => job.Id), Is.EqualTo(pending.Skip(ConversionJobRepository.CleanupBatchSize).Select(job => job.Id)));
            });
        }

        [Test]
        public void cancelled_expiry_preserves_owned_artifacts_pending_discard_and_recent_rows([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-cancelled-expiry-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var rows = new ConversionJobRepository(database, new StubEventAggregator());
                var service = CreateService(rows, new UnusedConversionService());
                ConversionJob Seed(string id, bool discard = false, bool recent = false, bool workspace = false, bool siblingArtifact = false)
                {
                    var root = Path.Combine(directory, ".chaptarr-conversions", id);
                    var folder = Path.Combine(root, "work");
                    if (workspace) Directory.CreateDirectory(folder);
                    if (siblingArtifact)
                    {
                        Directory.CreateDirectory(Path.Combine(root, "earlier-attempt"));
                        File.WriteAllText(Path.Combine(root, "earlier-attempt", "recording.m4b"), "keep owned output");
                    }
                    var request = new ConversionJobRequest { DownloadId = id, DiscardRequested = discard,
                        WorkRoot = root, WorkFolder = folder, OutputPath = Path.Combine(folder, "book.m4b"),
                        Sources = new System.Collections.Generic.List<ConversionArtifactSource>
                        {
                            new() { Path = Path.Combine(directory, id + ".mp3"), Size = 1 }
                        } };
                    return rows.Insert(new ConversionJob { DownloadId = id, Status = ConversionJobStatus.Cancelled,
                        WorkRoot = root, WorkFolder = folder, OutputPath = request.OutputPath, RequestJson = JsonSerializer.Serialize(request),
                        CompletedAt = DateTime.UtcNow.AddDays(recent ? -1 : -8), CreatedAt = DateTime.UtcNow.AddDays(-9), UpdatedAt = DateTime.UtcNow });
                }
                try
                {
                    var expired = Seed("expired");
                    var pending = Seed("pending", discard: true);
                    var recent = Seed("recent", recent: true);
                    var owned = Seed("owned", workspace: true);
                    var earlier = Seed("earlier", siblingArtifact: true);
                    var unknown = Seed("unknown");
                    unknown.RequestJson = "unreadable";
                    rows.SetFields(unknown, job => job.RequestJson);
                    var deserialize = typeof(ConversionJobService).GetMethod("DeserializeRequest", BindingFlags.NonPublic | BindingFlags.Instance);
                    var parsed = (ConversionJobRequest)deserialize.Invoke(service, new object[] { rows.FindByDownloadId(expired.DownloadId) });
                    Assert.That(parsed, Is.Not.Null, "The fixture must satisfy production request validity");
                    Assert.That(parsed.WorkFolder, Is.EqualTo(expired.WorkFolder));
                    Assert.That(parsed.OutputPath, Is.EqualTo(expired.OutputPath));
                    Assert.That(expired.CompletedAt, Is.LessThan(DateTime.UtcNow.AddDays(-7)));
                    var absent = typeof(ConversionJobService).GetMethod("IsAbsentConversionPath", BindingFlags.NonPublic | BindingFlags.Static);
                    foreach (var path in new[] { expired.WorkFolder, expired.OutputPath, parsed.WorkRoot })
                        Assert.That(absent.Invoke(null, new object[] { path }), Is.True, path);

                    var direct = Seed("guarded-delete-positive");
                    rows.DeleteExpiredCancelled(new[] { direct.Id, pending.Id, recent.Id }, DateTime.UtcNow.AddDays(-7));
                    Assert.That(rows.FindByDownloadId(direct.DownloadId), Is.Null, "Guarded SQL must remove an eligible row");
                    Assert.That(rows.FindByDownloadId(pending.DownloadId), Is.Not.Null, "Guarded SQL must preserve discard intent");
                    Assert.That(rows.FindByDownloadId(recent.DownloadId), Is.Not.Null, "Guarded SQL must preserve recent rows");
                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(rows.FindByDownloadId(expired.DownloadId), Is.Null);
                    foreach (var kept in new[] { pending, recent, owned, earlier, unknown })
                        Assert.That(rows.FindByDownloadId(kept.DownloadId), Is.Not.Null, kept.DownloadId);
                    Assert.That(Directory.Exists(owned.WorkFolder), Is.True);
                    Assert.That(File.Exists(Path.Combine(earlier.WorkRoot, "earlier-attempt", "recording.m4b")), Is.True);
                    Assert.That(rows.PendingCleanup().Select(job => job.Id), Does.Contain(pending.Id));

                    rows.DeleteExpiredCancelled(new[] { pending.Id, recent.Id }, DateTime.UtcNow.AddDays(-7));
                    Assert.That(rows.FindByDownloadId(pending.DownloadId), Is.Not.Null, "Conditional delete must preserve durable discard intent");
                    Assert.That(rows.FindByDownloadId(recent.DownloadId), Is.Not.Null, "Conditional delete must recheck age");
                }
                finally
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }
    }
}

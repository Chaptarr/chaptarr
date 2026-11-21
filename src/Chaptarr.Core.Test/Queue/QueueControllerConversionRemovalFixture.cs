using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Text.Json;
using Chaptarr.Api.V1.Queue;
using Chaptarr.Http.REST;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Deluge;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Queue
{
    [TestFixture]
    public class QueueControllerConversionRemovalFixture
    {
        [TestCase(false, "failed")]
        [TestCase(true, "failed")]
        [TestCase(false, "ready")]
        [TestCase(true, "ready")]
        [TestCase(false, "queued")]
        [TestCase(true, "queued")]
        [TestCase(false, "completed")]
        [TestCase(true, "completed")]
        [TestCase(false, "client-missing")]
        [TestCase(true, "client-missing")]
        [TestCase(false, "history-only")]
        [TestCase(true, "history-only")]
        [TestCase(false, "client-fails")]
        [TestCase(true, "client-fails")]
        [TestCase(false, "ignore")]
        [TestCase(true, "ignore")]
        [TestCase(false, "category")]
        [TestCase(true, "category")]
        [TestCase(false, "deluge-category")]
        [TestCase(true, "deluge-category")]
        [TestCase(false, "blocklist-only")]
        [TestCase(true, "blocklist-only")]
        [TestCase(false, "bulk")]
        [TestCase(true, "bulk")]
        [TestCase(false, "bulk-ignore")]
        [TestCase(true, "bulk-ignore")]
        [TestCase(false, "bulk-category")]
        [TestCase(true, "bulk-category")]
        [TestCase(false, "preserved-ignore")]
        [TestCase(true, "preserved-ignore")]
        [TestCase(false, "preserved-category")]
        [TestCase(true, "preserved-category")]
        [TestCase(false, "preserved-remove")]
        [TestCase(true, "preserved-remove")]
        [TestCase(false, "ignore-fails")]
        [TestCase(true, "ignore-fails")]
        [TestCase(false, "category-fails")]
        [TestCase(true, "category-fails")]
        public void explicit_discard_cleans_conversion_only_after_the_selected_action_succeeds(bool postgres, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new RemovalContext(database, clientType: scenario == "deluge-category" ? nameof(Deluge) : "qBittorrent");
                if (scenario != "history-only") context.SaveJob(scenario == "ready" ? ConversionJobStatus.ReadyToImport :
                    scenario == "queued" ? ConversionJobStatus.Queued : scenario == "completed" ? ConversionJobStatus.Completed : ConversionJobStatus.Failed);
                context.RecordOutput(context.Output);
                var previous = Path.Combine(context.WorkRoot, "earlier-attempt", "The Fall.m4b");
                Directory.CreateDirectory(Path.GetDirectoryName(previous));
                File.WriteAllText(previous, "older retained output");
                context.RecordOutput(previous);
                context.ClientFails = scenario is "client-fails" or "category-fails";
                context.ClientMissing = scenario == "client-missing";
                context.IgnoreFails = scenario == "ignore-fails";
                context.Preserved = scenario.StartsWith("preserved-", StringComparison.Ordinal);
                var category = scenario.Contains("category", StringComparison.Ordinal);
                var ignores = scenario.Contains("ignore", StringComparison.Ordinal);
                var removes = !ignores && !category && scenario != "blocklist-only";
                var refused = context.ClientFails || context.ClientMissing || context.IgnoreFails || (context.Preserved && removes);

                void Remove()
                {
                    if (scenario.StartsWith("bulk", StringComparison.Ordinal)) context.Controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 10, 11, 10 } }, removeFromClient: removes, changeCategory: category);
                    else context.Controller.RemoveAction(10, removeFromClient: removes, changeCategory: category, blocklist: scenario == "blocklist-only");
                }

                if (context.ClientFails) Assert.Throws<IOException>(Remove);
                else if (refused) Assert.Throws<BadRequestException>(Remove);
                else Remove();

                var deleted = !refused;
                Assert.Multiple(() =>
                {
                    Assert.That(File.Exists(context.Output), Is.EqualTo(!deleted));
                    Assert.That(File.Exists(previous), Is.EqualTo(!deleted));
                    Assert.That(File.ReadAllText(context.LibraryFile), Is.EqualTo("imported recording"));
                    Assert.That(File.ReadAllText(context.OtherOutput), Is.EqualTo("another download"));
                    Assert.That(context.ClientRemoves, Is.EqualTo(removes && !context.ClientMissing && !context.Preserved ? 1 : 0));
                    Assert.That(context.CategoryChanges, Is.EqualTo(category ? 1 : 0));
                    Assert.That(context.Stops, Is.EqualTo(refused ? 0 : 1));
                    Assert.That(File.ReadAllText(context.Source), Is.EqualTo("source MP3"), "Conversion cleanup must not delete source audio itself");
                });
                TestContext.Progress.WriteLine($"CONVERSION_REMOVE database={database.DatabaseType} case={scenario} clientRemoves={context.ClientRemoves} retained={!deleted}");
            });
        }

        [Test]
        public void preserved_deluge_category_change_discards_conversion_but_keeps_originals(
            [Values(false, true)] bool postgres, [Values(false, true)] bool bulk, [Values(false, true)] bool blocklist)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new RemovalContext(database, clientType: nameof(Deluge));
                context.Preserved = true;
                context.SaveJob(ConversionJobStatus.ReadyToImport);
                context.RecordOutput(context.Output);
                if (bulk) context.Controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 10, 11, 10 } },
                    removeFromClient: false, blocklist: blocklist, changeCategory: true);
                else context.Controller.RemoveAction(10, removeFromClient: false, blocklist: blocklist, changeCategory: true);
                Assert.Multiple(() =>
                {
                    Assert.That(File.Exists(context.Output), Is.False);
                    Assert.That(context.Repository.FindByDownloadId(context.DownloadId), Is.Null);
                    Assert.That(File.ReadAllText(context.Source), Is.EqualTo("source MP3"));
                    Assert.That(File.ReadAllText(context.LibraryFile), Is.EqualTo("imported recording"));
                    Assert.That(File.ReadAllText(context.OtherOutput), Is.EqualTo("another download"));
                    Assert.That(context.ClientRemoves, Is.Zero);
                    Assert.That(context.CategoryChanges, Is.EqualTo(1));
                    Assert.That(context.Stops, Is.EqualTo(1));
                    Assert.That(context.Blocklists, Is.EqualTo(blocklist ? 1 : 0));
                });
            });
        }

        [TestCase(false, "library")]
        [TestCase(true, "library")]
        [TestCase(false, "other-download")]
        [TestCase(true, "other-download")]
        [TestCase(false, "symlink")]
        [TestCase(true, "symlink")]
        public void unsafe_history_paths_must_never_expand_removal_scope(bool postgres, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new RemovalContext(database);
                var path = scenario == "library" ? context.LibraryFile : context.OtherOutput;
                if (scenario == "symlink")
                {
                    var link = Path.Combine(context.WorkRoot, "linked-attempt");
                    try { Directory.CreateSymbolicLink(link, Path.GetDirectoryName(context.LibraryFile)); }
                    catch (UnauthorizedAccessException) { Assert.Ignore("Symbolic link creation is not permitted on this platform."); }
                    path = Path.Combine(link, Path.GetFileName(context.LibraryFile));
                }
                context.RecordOutput(path);
                Assert.Throws<ConversionCleanupRefusedException>(() => context.Controller.RemoveAction(10));
                Assert.Multiple(() =>
                {
                    Assert.That(File.ReadAllText(context.LibraryFile), Is.EqualTo("imported recording"));
                    Assert.That(File.ReadAllText(context.OtherOutput), Is.EqualTo("another download"));
                    Assert.That(File.Exists(context.Output), Is.True);
                    Assert.That(context.Stops, Is.Zero);
                });
            });
        }

        [TestCase(false, "a_b", "a/b")]
        [TestCase(true, "a_b", "a/b")]
        [TestCase(false, "a", " a ")]
        [TestCase(true, "a", " a ")]
        [TestCase(false, "a", "A")]
        [TestCase(true, "a", "A")]
        public void explicit_discard_must_preserve_another_download_whose_identifier_maps_to_the_same_root(bool postgres, string downloadId, string collidingId)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var caseOnly = string.Equals(downloadId, collidingId, StringComparison.OrdinalIgnoreCase);
                if (caseOnly && !OperatingSystem.IsWindows()) Assert.Ignore("The case-only collision requires the Windows case-insensitive directory semantics.");
                using var context = new RemovalContext(database, downloadId: downloadId);
                Assert.That(string.Equals(ConversionWorkPath.SafeName(collidingId), ConversionWorkPath.SafeName(downloadId),
                    caseOnly ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), Is.True);
                context.SaveJob(ConversionJobStatus.Failed);
                var otherFolder = Path.Combine(context.WorkRoot, "colliding-download-attempt");
                var otherOutput = Path.Combine(otherFolder, "other.m4b");
                Directory.CreateDirectory(otherFolder);
                File.WriteAllText(otherOutput, "belongs to the colliding download");
                File.WriteAllText(Path.Combine(otherFolder, "conversion-artifact.json"), JsonSerializer.Serialize(new ConversionArtifactManifest
                {
                    DownloadId = collidingId, OutputPath = otherOutput, EncoderSucceeded = false
                }));
                context.RecordOutput(otherOutput, collidingId);
                var source = new FileInfo(context.Source);
                context.Jobs.Enqueue(new ConversionJobRequest
                {
                    DownloadId = collidingId, WorkRoot = context.WorkRoot,
                    WorkFolder = otherFolder, OutputPath = otherOutput,
                    TargetQualityId = Quality.M4B.Id, TargetQualityName = "M4B",
                    Sources = new List<ConversionArtifactSource> { new() { Path = context.Source, Size = source.Length, ModifiedUtcTicks = source.LastWriteTimeUtc.Ticks } }
                });
                context.Controller.RemoveAction(10);
                Assert.That(File.Exists(context.Output), Is.False);
                Assert.That(File.Exists(otherOutput), Is.True, "A non-lossy identifier does not prove exclusive ownership of its sanitized root");
                Assert.That(File.ReadAllText(otherOutput), Is.EqualTo("belongs to the colliding download"));
                Assert.That(context.Repository.FindByDownloadId(collidingId).Status, Is.EqualTo(ConversionJobStatus.Queued));
                Assert.That(File.ReadAllText(context.LibraryFile), Is.EqualTo("imported recording"));
            });
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public void explicit_discard_cleans_unrecorded_failed_attempts_only_with_exact_attempt_identity(bool postgres, bool lossyId, bool ownershipProof)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new RemovalContext(database, downloadId: lossyId ? "lossy/name" : null);
                var unrecorded = Path.Combine(context.WorkRoot, "old-unrecorded-failed-attempt", "failed.m4b");
                Directory.CreateDirectory(Path.GetDirectoryName(unrecorded));
                File.WriteAllText(unrecorded, "old failed output with no history receipt");
                if (ownershipProof)
                {
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(unrecorded), "conversion-artifact.json"), JsonSerializer.Serialize(new ConversionArtifactManifest
                    {
                        DownloadId = context.DownloadId, OutputPath = unrecorded, EncoderSucceeded = false
                    }));
                }
                context.SaveJob(ConversionJobStatus.Failed);
                // The older worker failure had no history before its row was reused for this attempt.
                context.Controller.RemoveAction(10);
                Assert.That(File.Exists(context.Output), Is.False);
                Assert.That(File.Exists(unrecorded), Is.EqualTo(!ownershipProof), "Only an exact attempt receipt proves ownership beyond captured job/history paths");
                Assert.That(File.ReadAllText(context.OtherOutput), Is.EqualTo("another download"));
                Assert.That(File.ReadAllText(context.LibraryFile), Is.EqualTo("imported recording"));
                Assert.That(File.ReadAllText(context.Source), Is.EqualTo("source MP3"));
                Assert.That(context.Repository.FindByDownloadId(context.DownloadId), Is.Null);
                TestContext.Progress.WriteLine($"DISCARD_SCOPE database={database.DatabaseType} lossyId={lossyId} ownershipProof={ownershipProof} unrecordedRetained={!ownershipProof} siblingRootUntouched=true");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void discard_preflight_must_not_cancel_a_new_attempt_reusing_the_same_job_row(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new RemovalContext(database);
                context.SaveJob(ConversionJobStatus.Failed);
                context.RecordOutput(context.Output);
                var originalId = context.Repository.FindByDownloadId(context.DownloadId).Id;
                var replacementFolder = Path.Combine(context.WorkRoot, "new-attempt");
                var replacementOutput = Path.Combine(replacementFolder, "new.m4b");
                context.AfterClientRemoval = () =>
                {
                    Directory.CreateDirectory(replacementFolder);
                    File.WriteAllText(replacementOutput, "owned by the new attempt");
                    var replacement = context.Repository.FindByDownloadId(context.DownloadId);
                    var request = JsonSerializer.Deserialize<ConversionJobRequest>(replacement.RequestJson);
                    request.WorkFolder = replacementFolder;
                    request.OutputPath = replacementOutput;
                    replacement.WorkFolder = replacementFolder;
                    replacement.OutputPath = replacementOutput;
                    replacement.RequestJson = JsonSerializer.Serialize(request);
                    replacement.Status = ConversionJobStatus.Queued;
                    context.Repository.Update(replacement);
                };
                var error = Assert.Throws<BadRequestException>(() => context.Controller.RemoveAction(10));
                Assert.That(error.Message, Does.Contain("action succeeded"));
                var current = context.Repository.FindByDownloadId(context.DownloadId);
                Assert.That(current.Id, Is.EqualTo(originalId), "Row identity alone cannot establish attempt identity");
                Assert.That(current.Status, Is.EqualTo(ConversionJobStatus.Queued));
                Assert.That(JsonSerializer.Deserialize<ConversionJobRequest>(current.RequestJson).DiscardRequested, Is.False);
                Assert.That(File.ReadAllText(replacementOutput), Is.EqualTo("owned by the new attempt"));
                Assert.That(File.Exists(context.Output), Is.True);
                Assert.That(context.Stops, Is.EqualTo(1));
                Assert.That(context.ClientRemoves, Is.EqualTo(1));
            });
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        public void cleanup_failure_after_client_success_keeps_durable_retry_and_finalizes_tracking(bool postgres, bool historyOnly, bool rowDisappears)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var context = new RemovalContext(database);
                if (!historyOnly) context.SaveJob(ConversionJobStatus.Failed);
                if (!rowDisappears) context.RecordOutput(context.Output);
                var link = Path.Combine(Path.GetDirectoryName(context.Output), "late-link");
                RequireDirectorySymbolicLinkCapability(link, Path.GetDirectoryName(context.LibraryFile));
                context.AfterClientRemoval = () =>
                {
                    if (rowDisappears) context.Repository.DeleteByDownloadId(context.DownloadId);
                    Directory.CreateSymbolicLink(link, Path.GetDirectoryName(context.LibraryFile));
                };
                try
                {
                    Assert.DoesNotThrow(() => context.Controller.RemoveAction(10));
                    Assert.That(context.ClientRemoves, Is.EqualTo(1));
                    Assert.That(context.Stops, Is.EqualTo(1));
                    Assert.That(File.Exists(context.Output), Is.True);
                    var owner = context.Repository.FindByDownloadId(context.DownloadId);
                    Assert.That(owner.Status, Is.EqualTo(ConversionJobStatus.Cancelled));
                    Assert.That(JsonSerializer.Deserialize<ConversionJobRequest>(owner.RequestJson).DiscardRequested, Is.True);
                    Assert.DoesNotThrow(() => context.Jobs.Handle(new BookGrabbedEvent(null) { DownloadId = context.DownloadId }));
                    Assert.That(context.Repository.FindByDownloadId(context.DownloadId).Id, Is.EqualTo(owner.Id), "A new grab must retain the failed-cleanup owner");
                    var source = new FileInfo(context.Source);
                    var nextRequest = new ConversionJobRequest
                    {
                        DownloadId = context.DownloadId, WorkRoot = context.WorkRoot,
                        WorkFolder = Path.Combine(context.WorkRoot, "next-grab"), OutputPath = Path.Combine(context.WorkRoot, "next-grab", "next.m4b"),
                        Sources = new List<ConversionArtifactSource> { new() { Path = context.Source, Size = source.Length, ModifiedUtcTicks = source.LastWriteTimeUtc.Ticks } },
                        TargetQualityId = Quality.M4B.Id, TargetQualityName = "M4B"
                    };
                    Assert.That(Assert.Throws<InvalidOperationException>(() => context.Jobs.Enqueue(nextRequest)).Message, Does.Contain("waiting for cleanup"));
                    Directory.Delete(link);
                    var retryOwner = context.Repository.FindByDownloadId(context.DownloadId);
                    var retry = JsonSerializer.Deserialize<ConversionJobRequest>(retryOwner.RequestJson);
                    Assert.That(retry.CleanupError, Is.Not.Empty);
                    Assert.That(retry.CleanupNotBefore, Is.GreaterThan(DateTime.UtcNow), "Cleanup failure must retain its retry schedule");
                    retry.CleanupNotBefore = DateTime.UtcNow.AddSeconds(-1);
                    retryOwner.RequestJson = JsonSerializer.Serialize(retry);
                    context.Repository.Update(retryOwner);
                    typeof(ConversionJobService).GetMethod("RetryDiscardCleanup", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(context.Jobs, null);
                    Assert.That(File.Exists(context.Output), Is.False);
                    Assert.That(context.Repository.FindByDownloadId(context.DownloadId), Is.Null);
                    Assert.That(context.Jobs.Enqueue(nextRequest).Status, Is.EqualTo(ConversionJobStatus.Queued), "Cleanup completion reopens new conversion admission");
                    Assert.That(File.ReadAllText(context.LibraryFile), Is.EqualTo("imported recording"));
                }
                finally
                {
                    if (Directory.Exists(link)) Directory.Delete(link);
                }
            });
        }

        [Test]
        public void bulk_removal_continues_after_one_offline_conversion_workspace([Values(false, true)] bool postgres)
        {
            VerifyBulkRemovalAfterCleanupFailure(postgres, "offline");
        }

        [Test]
        public void bulk_removal_continues_after_disk_cleanup_failure(
            [Values(false, true)] bool postgres, [Values("availability", "io", "permission")] string failure)
        {
            VerifyBulkRemovalAfterCleanupFailure(postgres, failure);
        }

        private static void VerifyBulkRemovalAfterCleanupFailure(bool postgres, string failure)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var first = new RemovalContext(database);
                using var second = new RemovalContext(database);
                first.SaveJob(ConversionJobStatus.Completed);
                second.SaveJob(ConversionJobStatus.Completed);
                var root = Path.Combine(first.Root, "library");
                var offline = Path.Combine(first.Root, "offline");
                var removed = new List<string>();
                var stopped = new List<string>();
                var failedProbes = 0;
                var disk = Proxy<IDiskProvider>((method, args) =>
                {
                    var path = (string)args[0];
                    if (failure != "offline" && method.Name == nameof(IDiskProvider.FolderExists) && path == Path.GetDirectoryName(first.Output))
                    {
                        Assert.That(removed, Is.EqualTo(new[] { first.DownloadId }), "The client action must finish before cleanup fails");
                        failedProbes++;
                        if (failure == "io") throw new IOException("Host is down");
                        if (failure == "permission") throw new UnauthorizedAccessException("Share access is denied");
                        return false;
                    }
                    return method.Name switch
                    {
                        nameof(IDiskProvider.FolderExists) => Directory.Exists(path),
                        nameof(IDiskProvider.FolderEmpty) => !Directory.EnumerateFileSystemEntries(path).Any(),
                        _ => throw new NotImplementedException(method.Name)
                    };
                });
                var jobs = Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.CreateService(first.Repository, null,
                    roots: Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(root, Path.Combine(second.Root, "library")), disk: disk);
                var items = new[] { first.DownloadId, second.DownloadId }.Select(id => new TrackedDownload
                {
                    DownloadItem = new DownloadClientItem { DownloadId = id, Title = id }, State = TrackedDownloadState.ImportBlocked
                }).ToDictionary(item => item.DownloadItem.DownloadId);
                var client = Proxy<IDownloadClient>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IDownloadClient.RemoveItem)));
                    removed.Add(((DownloadClientItem)args[0]).DownloadId);
                    return null;
                });
                var profiles = new QualityProfileService(Proxy<IProfileRepository>((_, _) => new List<QualityProfile> { new() { Id = 1, Items = new List<QualityProfileQualityItem> { new() { Quality = Quality.MP3, Allowed = true } } } }),
                    null, null, null, null, null, LogManager.GetCurrentClassLogger());
                var controller = new QueueController(Proxy<NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new AssertionException("Unexpected series query")),
                    Proxy<IDownloadImportModeResolver>((_, _) => false), null,
                    Proxy<IQueueService>((_, args) => new NzbDrone.Core.Queue.Queue { Id = (int)args[0], DownloadId = (int)args[0] == 10 ? first.DownloadId : second.DownloadId }),
                    Proxy<IPendingReleaseService>((method, _) => method.Name == nameof(IPendingReleaseService.GetPendingQueue) ? new List<NzbDrone.Core.Queue.Queue>() : null),
                    profiles, Proxy<ITrackedDownloadService>((method, args) =>
                    {
                        if (method.Name == nameof(ITrackedDownloadService.Find)) return items[(string)args[0]];
                        if (method.Name == nameof(ITrackedDownloadService.StopTracking)) { stopped.Add((string)args[0]); return null; }
                        throw new AssertionException(method.Name);
                    }), null, null, Proxy<IProvideDownloadClient>((_, _) => client), null, null, jobs);
                if (failure == "offline")
                {
                    Directory.Move(root, offline);
                    Directory.CreateDirectory(root);
                }
                Assert.DoesNotThrow(() => controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 10, 11 } }));
                Assert.That(removed, Is.EqualTo(new[] { first.DownloadId, second.DownloadId }));
                Assert.That(stopped, Is.EqualTo(removed));
                var retained = first.Repository.FindByDownloadId(first.DownloadId);
                Assert.That(retained, Is.Not.Null);
                var retry = JsonSerializer.Deserialize<ConversionJobRequest>(retained.RequestJson);
                var expectedError = failure switch
                {
                    "offline" => "missing or empty",
                    "availability" => "Unable to verify conversion workspace availability",
                    "io" => "Host is down",
                    "permission" => "Share access is denied",
                    _ => throw new AssertionException(failure)
                };
                Assert.That(retry.CleanupError, Does.Contain(expectedError));
                Assert.That(retry.DiscardRequested, Is.True);
                Assert.That(retry.DiscardCurrentAttemptOnly, Is.False);
                Assert.That(retry.CleanupNotBefore, Is.GreaterThan(DateTime.UtcNow));
                Assert.That(retained.CleanupPending, Is.True);
                Assert.That(failedProbes, Is.EqualTo(failure == "offline" ? 0 : 1));
                Assert.That(second.Repository.FindByDownloadId(second.DownloadId), Is.Null);
                Assert.That(File.Exists(second.Output), Is.False);
                Assert.That(File.Exists(failure == "offline" ? Path.Combine(offline, Path.GetRelativePath(root, first.Output)) : first.Output), Is.True);
                Assert.That(File.ReadAllText(failure == "offline" ? Path.Combine(offline, Path.GetRelativePath(root, first.LibraryFile)) : first.LibraryFile), Is.EqualTo("imported recording"));
                Assert.That(File.ReadAllText(second.LibraryFile), Is.EqualTo("imported recording"));
                Assert.That(File.Exists(first.Source), Is.True);
                Assert.That(File.Exists(second.Source), Is.True);
            });
        }

        private static void RequireDirectorySymbolicLinkCapability(string link, string target)
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (UnauthorizedAccessException)
            {
                Assert.Ignore("This symlink-specific cleanup case requires permission to create directory symbolic links.");
            }
            catch (PlatformNotSupportedException)
            {
                Assert.Ignore("This symlink-specific cleanup case is unsupported on this platform.");
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 50 or 1314)
            {
                Assert.Ignore("This symlink-specific cleanup case requires filesystem symbolic link support and privileges.");
            }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link);
            }
        }

        [TestCase(false, "remove", false)]
        [TestCase(true, "remove", false)]
        [TestCase(false, "ignore", false)]
        [TestCase(true, "ignore", false)]
        [TestCase(false, "category", false)]
        [TestCase(true, "category", false)]
        [TestCase(false, "ignore", true)]
        [TestCase(true, "ignore", true)]
        public void active_encoder_must_stop_before_its_workspace_is_cleaned(bool postgres, string action, bool lateSuccess)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var converter = Proxy<IM4bConversionService>((method, args) =>
                {
                    if (method.Name != nameof(IM4bConversionService.ConvertToM4b)) throw new NotImplementedException(method.Name);
                    var options = (ConversionOptions)args[2];
                    entered.Set();
                    Assert.That(options.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(release.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    return lateSuccess ? new ConversionResult { Success = true } :
                        new ConversionResult { FailureCategory = ConversionFailureCategory.Cancelled, ErrorMessage = "Cancelled" };
                });
                using var context = new RemovalContext(database, converter);
                context.Preserved = action != "remove";
                context.SaveJob(ConversionJobStatus.Queued);
                context.Jobs.Handle(new ApplicationStartedEvent());
                try
                {
                    Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    context.Controller.RemoveAction(10, removeFromClient: action == "remove", changeCategory: action == "category");
                    Assert.That(File.Exists(context.Output), Is.True, "The encoder still owns this folder until it returns");
                    release.Set();
                    Assert.That(SpinWait.SpinUntil(() => !Directory.Exists(context.WorkRoot) && context.Repository.FindByDownloadId(context.DownloadId) == null, TimeSpan.FromSeconds(5)), Is.True);
                    Assert.That(context.Repository.FindByDownloadId(context.DownloadId), Is.Null);
                    Assert.That(File.ReadAllText(context.LibraryFile), Is.EqualTo("imported recording"));
                    Assert.That(File.ReadAllText(context.Source), Is.EqualTo("source MP3"));
                    Assert.That(context.ClientRemoves, Is.EqualTo(action == "remove" ? 1 : 0));
                }
                finally
                {
                    release.Set();
                    context.Jobs.Handle(new ApplicationShutdownRequested());
                }
            });
        }

        private sealed class RemovalContext : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "queue-conversion-removal-" + Guid.NewGuid().ToString("N"));
            public readonly string DownloadId = Guid.NewGuid().ToString("N");
            public string WorkRoot { get; }
            public string Output { get; }
            public string Source { get; }
            public string LibraryFile { get; }
            public string OtherOutput { get; }
            public ConversionJobRepository Repository { get; }
            public ConversionJobService Jobs { get; }
            public QueueController Controller { get; }
            public Action AfterClientRemoval { get; set; }
            public bool ClientFails { get; set; }
            public bool ClientMissing { get; set; }
            public bool IgnoreFails { get; set; }
            public bool Preserved { get; set; }
            public int ClientRemoves { get; private set; }
            public int CategoryChanges { get; private set; }
            public int Stops { get; private set; }
            public int Blocklists { get; private set; }
            private readonly HistoryService _history;
            private readonly ConversionTrackingService _tracking;

            public RemovalContext(IMainDatabase database, IM4bConversionService converter = null, string downloadId = null, string clientType = "qBittorrent")
            {
                if (downloadId != null) DownloadId = downloadId;
                WorkRoot = Path.Combine(Root, "library", ".chaptarr-conversions", ConversionWorkPath.SafeName(DownloadId));
                Output = Path.Combine(WorkRoot, "attempt", "The Fall.m4b");
                Source = Path.Combine(Root, "The Fall - Part 01.mp3");
                LibraryFile = Path.Combine(Root, "library", "The Fall.m4b");
                OtherOutput = Path.Combine(Root, "library", ".chaptarr-conversions", "another-download", "attempt", "Other Book.m4b");
                Directory.CreateDirectory(Path.GetDirectoryName(Output));
                Directory.CreateDirectory(Path.GetDirectoryName(OtherOutput));
                File.WriteAllText(Output, "retained converted audio");
                File.WriteAllText(Source, "source MP3");
                File.WriteAllText(LibraryFile, "imported recording");
                File.WriteAllText(OtherOutput, "another download");
                var events = Proxy<IEventAggregator>((method, args) => null);
                var logger = LogManager.GetCurrentClassLogger();
                _history = new HistoryService(new HistoryRepository(database, events), logger);
                _tracking = new ConversionTrackingService(events);
                Repository = new ConversionJobRepository(database, events);
                var config = Proxy<IConfigService>((method, args) => method.Name == "get_AudiobookConversionMaxCpuThreads" ? 2 : 1);
                Jobs = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), Repository, converter, _tracking, Proxy<IManageCommandQueue>((method, args) => null), config, logger, _history);
                var tracked = new TrackedDownload
                {
                    DownloadItem = new DownloadClientItem
                    {
                        DownloadId = DownloadId, Title = "Ryan Cahill - The Fall",
                        DownloadClientInfo = new DownloadClientItemClientInfo { Type = clientType, Name = "Deluge" }
                    },
                    State = TrackedDownloadState.ImportBlocked
                };
                var queue = Proxy<IQueueService>((method, args) => new NzbDrone.Core.Queue.Queue { Id = (int)args[0], DownloadId = DownloadId });
                var pending = Proxy<IPendingReleaseService>((method, args) => method.Name == nameof(IPendingReleaseService.GetPendingQueue) ? new List<NzbDrone.Core.Queue.Queue>() : null);
                var trackedService = Proxy<ITrackedDownloadService>((method, args) =>
                {
                    if (method.Name == nameof(ITrackedDownloadService.Find)) return tracked;
                    if (method.Name == nameof(ITrackedDownloadService.StopTracking)) { Stops++; return null; }
                    throw new NotImplementedException(method.Name);
                });
                var client = Proxy<IDownloadClient>((method, args) =>
                {
                    if (method.Name == nameof(IDownloadClient.RemoveItem))
                    {
                        ClientRemoves++;
                        Assert.That(File.Exists(Output), Is.True, "Client deletion must succeed before retained output is deleted");
                        if (ClientFails) throw new IOException("Download client is unreachable");
                        AfterClientRemoval?.Invoke();
                    }
                    else if (method.Name == nameof(IDownloadClient.MarkItemAsImported))
                    {
                        CategoryChanges++;
                        if (ClientFails) throw new IOException("Download client is unreachable");
                        AfterClientRemoval?.Invoke();
                    }
                    else throw new NotImplementedException(method.Name);
                    return null;
                });
                var providers = Proxy<IProvideDownloadClient>((method, args) => ClientMissing ? null : client);
                var profiles = new QualityProfileService(Proxy<IProfileRepository>((method, args) => new List<QualityProfile>
                {
                    new() { Id = 1, Items = new List<QualityProfileQualityItem> { new() { Quality = Quality.MP3, Allowed = true } } }
                }), null, null, null, null, null, logger);
                Controller = new QueueController(global::Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<global::NzbDrone.Core.Books.ISeriesBookLinkService>((_, _) => throw new global::System.InvalidOperationException("Series batch loading was not expected in this fixture")), Proxy<IDownloadImportModeResolver>((method, args) => Preserved), null, queue, pending, profiles, trackedService,
                    Proxy<IFailedDownloadService>((method, args) => { Blocklists++; return null; }), Proxy<IIgnoredDownloadService>((method, args) => !IgnoreFails),
                    providers, null, _tracking, Jobs);
            }

            public void SaveJob(ConversionJobStatus status)
            {
                var info = new FileInfo(Source);
                var job = Jobs.Enqueue(new ConversionJobRequest
                {
                    DownloadId = DownloadId, WorkRoot = WorkRoot, WorkFolder = Path.GetDirectoryName(Output), OutputPath = Output,
                    TargetQualityId = Quality.M4B.Id, TargetQualityName = "M4B",
                    Sources = new List<ConversionArtifactSource> { new() { Path = Source, Size = info.Length, ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks } }
                });
                job.Status = status;
                Repository.Update(job);
            }

            public void RecordOutput(string path, string downloadId = null)
            {
                _history.Handle(new BookFileConvertedEvent(new LocalBook
                {
                    Path = path, IsGeneratedConversion = true, GeneratedConversionOutputPath = path,
                    GeneratedConversionSourcePaths = new List<string> { Source }, Quality = new QualityModel(Quality.M4B)
                }, null, null, null, new DownloadClientItem { DownloadId = downloadId ?? DownloadId }, "Import blocked"));
            }

            public void Dispose()
            {
                _tracking.Clear(DownloadId);
                Directory.Delete(Root, true);
            }
        }
    }
}

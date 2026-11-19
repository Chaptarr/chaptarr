using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void attention_preserves_saved_discard_scope_with_committed_or_absent_output(
            [Values(false, true)] bool postgres, [Values(false, true)] bool absent, [Values(false, true)] bool currentAttemptOnly)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-attention-scope-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "scope");
                    request.BookId = 42;
                    request.DiscardRequested = true;
                    request.DiscardCurrentAttemptOnly = currentAttemptOnly;
                    request.DiscardReason = "removed";
                    request.CleanupError = "Earlier cleanup was unavailable";
                    if (absent) Directory.Delete(request.WorkFolder, true);
                    else File.WriteAllText(request.OutputPath, "latest conversion");
                    var other = Path.Combine(request.WorkRoot, "other-format", "book.m4b");
                    Directory.CreateDirectory(Path.GetDirectoryName(other));
                    File.WriteAllText(other, "another attempt");
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(other), "conversion-artifact.json"),
                        System.Text.Json.JsonSerializer.Serialize(new ConversionArtifactManifest
                        {
                            DownloadId = request.DownloadId, OutputPath = other, EncoderSucceeded = true,
                            OutputSize = new FileInfo(other).Length,
                            Sources = new List<ConversionArtifactSource> { new() { Path = Path.Combine(directory, "other-source.mp3"), Size = 1 } }
                        }));
                    var library = Path.Combine(directory, "book.m4b");
                    File.WriteAllText(library, "committed recording");
                    var committed = new BookFile { Id = 123, Path = library, Quality = new QualityModel(Quality.M4B),
                        MatchProvenance = new MatchProvenance { ImportDownloadId = request.DownloadId,
                            ImportSourcePaths = request.Sources.Select(source => source.Path).ToList() } };
                    var recoveryReads = 0;
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        if (method.Name == nameof(IMediaFileService.GetFilesByBook)) { recoveryReads++; return new List<BookFile> { committed }; }
                        if (method.Name == nameof(IMediaFileService.GetFilePathOwnership)) return new List<BookFile>();
                        throw new AssertionException("Unexpected media call: " + method.Name);
                    });
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    repository.Insert(CreatePersistedJob(request, ConversionJobStatus.ReadyToImport, DateTime.UtcNow));
                    var service = new ConversionJobService(TestCleanupDisk(), TestRootFolders(directory), media, repository,
                        new UnusedConversionService(), new ConversionTrackingService(new StubEventAggregator()),
                        InterfaceProxy.Proxy<IManageCommandQueue>((_, _) => null), CreateConfig(), LogManager.GetCurrentClassLogger());

                    typeof(ConversionJobService).GetMethod("ReviewCleanupAttention", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);

                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                    Assert.That(File.Exists(other), Is.EqualTo(currentAttemptOnly), "Saved removal scope must survive recovery and an already-absent workspace");
                    Assert.That(File.ReadAllText(committed.Path), Is.EqualTo("committed recording"));
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                    Assert.That(recoveryReads, Is.Zero, "An outstanding discard precedes recovery of a committed import");
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void absent_workspace_outside_configured_roots_settles_after_import_or_root_removal(
            [Values(false, true)] bool postgres, [Values(false, true)] bool removedRoot)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-outside-roots-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var request = CreateRequest(directory, "outside-roots");
                    File.WriteAllText(request.OutputPath, "converted recording");
                    File.WriteAllText(Path.Combine(request.WorkFolder, "conversion-artifact.json"),
                        System.Text.Json.JsonSerializer.Serialize(new ConversionArtifactManifest
                        {
                            DownloadId = request.DownloadId, OutputPath = request.OutputPath, EncoderSucceeded = true,
                            OutputSize = new FileInfo(request.OutputPath).Length, Sources = request.Sources
                        }));
                    var roots = new List<RootFolder> { new() { Path = removedRoot ? directory : Path.Combine(directory, "other-library") } };
                    var rootService = new RootFolderService(InterfaceProxy.Proxy<IRootFolderRepository>((method, _) => method.Name == nameof(IRootFolderRepository.All)
                        ? roots : throw new AssertionException(method.Name)), null, null, null, LogManager.GetCurrentClassLogger());
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    repository.Insert(CreatePersistedJob(request, ConversionJobStatus.Completed, DateTime.UtcNow));
                    var service = CreateService(repository, new UnusedConversionService(), roots: rootService);
                    if (removedRoot)
                    {
                        roots.Clear();
                        Directory.Delete(request.WorkRoot, true);
                        service.PrepareArtifactRemoval(request.DownloadId, ConversionRemovalOrigin.UserAction)();
                    }
                    else
                    {
                        service.Complete(request.DownloadId);
                        Assert.That(Directory.Exists(request.WorkFolder), Is.False, "Import must remove the attempt before its completion event checks the absent path");
                        service.Handle(new DownloadCompletedEvent(new TrackedDownload { DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId } }, 0));
                    }
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(Directory.Exists(request.WorkRoot), Is.False);
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void startup_prune_loads_and_probes_each_root_once([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-prune-batch-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    foreach (var id in new[] { "first", "second" })
                    {
                        var request = CreateRequest(directory, id);
                        var job = CreatePersistedJob(request, ConversionJobStatus.Cancelled, DateTime.UtcNow.AddDays(-9));
                        job.CompletedAt = DateTime.UtcNow.AddDays(-9);
                        repository.Insert(job);
                        Directory.Delete(request.WorkRoot, true);
                    }
                    var reads = 0;
                    var probes = 0;
                    var roots = new RootFolderService(InterfaceProxy.Proxy<IRootFolderRepository>((method, _) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IRootFolderRepository.All)));
                        reads++;
                        return new List<RootFolder> { new() { Path = directory } };
                    }), null, null, null, LogManager.GetCurrentClassLogger());
                    var service = CreateService(repository, new UnusedConversionService(), roots: roots, disk: TestCleanupDisk(() => probes++));
                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(repository.All(), Is.Empty);
                    Assert.That(reads, Is.EqualTo(1));
                    Assert.That(probes, Is.EqualTo(2), "One FolderExists and one FolderEmpty check serve every row under this root");
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void committed_import_recovery_keeps_cleanup_owed_until_the_mount_returns([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-recovery-mount-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var root = Path.Combine(directory, "library");
                    var request = CreateRequest(root, "recovery-mount");
                    request.BookId = 42;
                    File.WriteAllText(request.OutputPath, "converted output");
                    var library = Path.Combine(directory, "committed.m4b");
                    File.WriteAllText(library, "committed recording");
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, _) => method.Name == nameof(IMediaFileService.GetFilesByBook)
                        ? new List<BookFile> { new() { Path = library, Quality = new QualityModel(Quality.M4B),
                            MatchProvenance = new MatchProvenance { ImportDownloadId = request.DownloadId,
                                ImportSourcePaths = request.Sources.Select(source => source.Path).ToList() } } }
                        : method.Name == nameof(IMediaFileService.GetFilePathOwnership) ? new List<BookFile>() : throw new AssertionException(method.Name));
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    repository.Insert(CreatePersistedJob(request, ConversionJobStatus.ReadyToImport, DateTime.UtcNow));
                    var service = new ConversionJobService(TestCleanupDisk(), TestRootFolders(root), media, repository,
                        new UnusedConversionService(), new ConversionTrackingService(new StubEventAggregator()),
                        InterfaceProxy.Proxy<IManageCommandQueue>((_, _) => null), CreateConfig(), LogManager.GetCurrentClassLogger());
                    Directory.Move(root, Path.Combine(directory, "offline"));
                    Directory.CreateDirectory(root);
                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    var kept = repository.FindByDownloadId(request.DownloadId);
                    Assert.That(kept, Is.Not.Null);
                    var saved = System.Text.Json.JsonSerializer.Deserialize<ConversionJobRequest>(kept.RequestJson);
                    Assert.That(saved.DiscardRequested, Is.True);
                    Assert.That(saved.DiscardCurrentAttemptOnly, Is.True);
                    Assert.That(saved.CleanupError, Does.Contain("missing or empty"));
                    Directory.Delete(root);
                    Directory.Move(Path.Combine(directory, "offline"), root);
                    MakeCleanupDue(repository, request.DownloadId);
                    typeof(ConversionJobService).GetMethod("RetryDiscardCleanup", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                    Assert.That(File.ReadAllText(library), Is.EqualTo("committed recording"));
                }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
        }
    }
}

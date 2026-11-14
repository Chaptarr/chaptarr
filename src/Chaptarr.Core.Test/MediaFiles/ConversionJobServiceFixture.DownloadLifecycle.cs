using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void terminal_download_events_remove_only_owned_conversion_workspaces(
            [Values(false, true)] bool postgres,
            [Values("imported", "failed", "ignored", "removed", "gone")] string terminal)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-terminal-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                request.DownloadClientId = 4;
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new RetainedResultConverter { Success = true };
                var worker = CreateService(repository, converter);
                SeedClientHistory(database, request.DownloadId);
                var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), repository, new UnusedConversionService(), null,
                    DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>(), CreateConfig(), LogManager.GetCurrentClassLogger());
                var item = new TrackedDownload { DownloadClient = 4, DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId } };
                var libraryFile = Path.Combine(directory, "library.m4b");
                File.WriteAllText(libraryFile, "library recording");
                try
                {
                    RunWorker(worker, request);
                    worker.RetainCompletedOutput(request.DownloadId, request.Sources.Select(source => source.Path).ToArray(), "replacement refused");
                    var retained = repository.FindByDownloadId(request.DownloadId);
                    retained.CompletedAt = DateTime.UtcNow.AddDays(-8);
                    repository.SetFields(retained, job => job.CompletedAt);
                    // An empty cache at startup or another client's successful poll cannot prove removal.
                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [5] = new() }));
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() { request.DownloadId } }));
                    Assert.That(File.Exists(request.OutputPath), Is.True);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Not.Null);
                    void Finish()
                    {
                        switch (terminal)
                        {
                            case "imported": service.Handle(new DownloadCompletedEvent(item, 1)); break;
                            case "failed": service.Handle(new DownloadFailedEvent { DownloadId = request.DownloadId }); break;
                            case "ignored": service.Handle(new DownloadIgnoredEvent { DownloadId = request.DownloadId }); break;
                            case "removed": service.Handle(new TrackedDownloadsRemovedEvent(new() { item })); break;
                            case "gone": service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() })); break;
                        }
                    }
                    Finish();
                    Finish();
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(File.ReadAllText(request.Sources.Single().Path), Is.EqualTo("source"));
                    Assert.That(File.ReadAllText(libraryFile), Is.EqualTo("library recording"));
                    Assert.That(converter.Calls, Is.EqualTo(1));
                }
                finally
                {
                    ShutdownAndWait(worker);
                    ShutdownAndWait(service);
                    Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void terminal_cleanup_keeps_a_tracked_file_inside_the_conversion_workspace([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-tracked-output-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                request.DownloadClientId = 4;
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var worker = CreateService(repository, new RetainedResultConverter { Success = true });
                try
                {
                    RunWorker(worker, request);
                    var ownershipReads = 0;
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        ownershipReads++;
                        Assert.That(method.Name, Is.EqualTo(nameof(IMediaFileService.GetFilePathOwnership)));
                        Assert.That((IReadOnlyCollection<string>)args[1], Does.Contain(request.WorkFolder));
                        return new List<BookFile> { new() { Id = 1, Path = request.OutputPath } };
                    });
                    var cleanup = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), media, repository, new UnusedConversionService(), null,
                        DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>(), CreateConfig(), LogManager.GetCurrentClassLogger());
                    var bytes = File.ReadAllBytes(request.OutputPath);
                    cleanup.Handle(new DownloadIgnoredEvent { DownloadId = request.DownloadId });
                    for (var i = 0; i < 3; i++) cleanup.Handle(new DownloadCompletedEvent(new TrackedDownload
                        { DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId } }, 1));
                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(cleanup, null);
                    Assert.That(ownershipReads, Is.EqualTo(1), "A permanent safety refusal must not repeat the ownership scan");
                    Assert.That(System.Text.Json.JsonSerializer.Deserialize<ConversionJobRequest>(repository.CleanupWarnings().Single().RequestJson).CleanupError, Does.Contain("tracked library file"));
                    Assert.That(File.ReadAllBytes(request.OutputPath), Is.EqualTo(bytes));
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Not.Null, "Cleanup ownership survives the refusal");
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                }
                finally
                {
                    ShutdownAndWait(worker);
                    Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void a_new_grab_cleans_the_previous_terminal_attempt_before_removing_its_row([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-regrab-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                request.DownloadClientId = 4;
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var worker = CreateService(repository, new RetainedResultConverter { Success = true });
                try
                {
                    RunWorker(worker, request);
                    worker.RetainCompletedOutput(request.DownloadId, request.Sources.Select(source => source.Path).ToArray(), "replacement refused");
                    worker.Handle(new BookGrabbedEvent(new NzbDrone.Core.Parser.Model.RemoteBook()) { DownloadId = request.DownloadId });
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                }
                finally
                {
                    ShutdownAndWait(worker);
                    Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void reconcile_retries_terminal_cleanup_after_restart([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-terminal-restart-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                request.DownloadClientId = 4;
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var worker = CreateService(repository, new RetainedResultConverter { Success = true });
                try
                {
                    RunWorker(worker, request);
                    SeedClientHistory(database, request.DownloadId, DownloadHistoryEventType.DownloadImported);
                    var restarted = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), repository, new UnusedConversionService(), null,
                        DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>(), CreateConfig(), LogManager.GetCurrentClassLogger());
                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(restarted, null);
                    Assert.That(File.Exists(request.OutputPath), Is.True, "Restart must wait for a successful client poll");
                    restarted.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() }));
                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(restarted, null);
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                    Assert.That(repository.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                }
                finally
                {
                    ShutdownAndWait(worker);
                    Directory.Delete(directory, true);
                }
            });
        }
    }
}

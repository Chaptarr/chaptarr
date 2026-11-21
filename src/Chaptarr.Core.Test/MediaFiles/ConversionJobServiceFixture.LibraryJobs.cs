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
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.MediaFiles
{
    // Library conversion jobs: owned by a book and edition, not by a download.
    public partial class ConversionJobServiceFixture
    {
        private static ConversionJobRequest CreateLibraryRequest(string directory, int editionId)
        {
            var key = LibraryConversionJob.Key(editionId);
            var request = CreateRequest(directory, key);
            request.DownloadId = null;
            request.BookId = editionId + 1000;
            request.EditionId = editionId;
            return request;
        }

        private static (ConversionJobService Service, RecordingCommandQueueProxy Commands) CreateLibraryService(
            IConversionJobRepository repository, IM4bConversionService converter, IHistoryService history = null)
        {
            var commands = DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>();
            var service = new ConversionJobService(TestCleanupDisk(), TestRootFolders(), ImportLifecycleStubs.NoLibraryMediaFiles(), repository, converter,
                new ConversionTrackingService(new StubEventAggregator()), commands, CreateConfig(), LogManager.GetCurrentClassLogger(), history,
                outcomeRetryDelay: TimeSpan.FromSeconds(2));
            return (service, (RecordingCommandQueueProxy)(object)commands);
        }

        private static IHistoryService NoDownloadHistory() => InterfaceProxy.Proxy<IHistoryService>((method, _) =>
            throw new AssertionException($"A library conversion job must not read download history ({method.Name})."));

        [Test]
        public void library_job_is_keyed_by_its_edition_and_queues_its_own_import([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "library-conversion-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var (service, commands) = CreateLibraryService(repository, new RetainedResultConverter { Success = true }, NoDownloadHistory());
                try
                {
                    var request = CreateLibraryRequest(directory, 41);
                    request.DownloadId = "client-supplied-id";
                    request.DownloadClientId = 7;

                    var job = service.EnqueueLibrary(request);

                    Assert.That(job.Origin, Is.EqualTo(ConversionJobOrigin.Library));
                    Assert.That(job.DownloadId, Is.EqualTo("library-edition-41"), "the key comes from the edition, never from the caller");
                    Assert.That(job.DownloadClientId, Is.EqualTo(0));
                    Assert.That(job.BookId, Is.EqualTo(1041));
                    Assert.That(job.EditionId, Is.EqualTo(41));
                    Assert.That(service.GetLibraryJob(41)?.Id, Is.EqualTo(job.Id));
                    Assert.That(service.GetActiveLibraryJobs().Select(active => active.Id), Is.EqualTo(new[] { job.Id }));

                    Assert.That(DispatchWorkers(service).Wait(TimeSpan.FromSeconds(45)), Is.True);

                    Assert.That(service.GetLibraryJob(41).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    Assert.That(service.GetCompletedConversion("library-edition-41")?.OutputPath, Is.EqualTo(request.OutputPath));
                    Assert.That(commands.Commands.OfType<ImportLibraryConversionCommand>().Select(command => command.EditionId).Distinct(), Is.EqualTo(new[] { 41 }),
                        "the download queue never imports a library job, so the worker asks for its own import");
                }
                finally
                {
                    ShutdownAndWait(service);
                    Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void download_events_never_reach_a_library_job([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "library-conversion-events-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var (service, _) = CreateLibraryService(repository, new RetainedResultConverter { Success = true }, NoDownloadHistory());
                try
                {
                    var request = CreateLibraryRequest(directory, 42);
                    service.EnqueueLibrary(request);
                    Assert.That(DispatchWorkers(service).Wait(TimeSpan.FromSeconds(45)), Is.True);
                    var key = LibraryConversionJob.Key(42);
                    var item = new TrackedDownload { DownloadClient = 4, DownloadItem = new DownloadClientItem { DownloadId = key } };

                    // Whatever a download client reports under this key, the library job is not its download.
                    service.Handle(new BookGrabbedEvent(new NzbDrone.Core.Parser.Model.RemoteBook()) { DownloadId = key });
                    service.Handle(new DownloadCompletedEvent(item, 1));
                    service.Handle(new DownloadFailedEvent { DownloadId = key });
                    service.Handle(new DownloadIgnoredEvent { DownloadId = key });
                    service.Handle(new TrackedDownloadsRemovedEvent(new() { item }));
                    service.Handle(new TrackedDownloadRefreshedEvent(new(), new Dictionary<int, HashSet<string>> { [4] = new() }));
                    typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, null);

                    var job = repository.FindLibraryJob(42);
                    Assert.That(job, Is.Not.Null);
                    Assert.That(job.Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    Assert.That(File.Exists(request.OutputPath), Is.True, "the converted file waits for its import");
                    Assert.That(repository.LifecycleCandidates(0), Is.Empty, "download reconciliation does not page library jobs");
                }
                finally
                {
                    ShutdownAndWait(service);
                    Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void library_job_cleanup_and_completion_use_only_the_job_and_its_receipt([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "library-conversion-cleanup-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var (service, _) = CreateLibraryService(repository, new RetainedResultConverter { Success = true }, NoDownloadHistory());
                try
                {
                    var completed = CreateLibraryRequest(directory, 43);
                    service.EnqueueLibrary(completed);
                    var discarded = CreateLibraryRequest(directory, 44);
                    service.EnqueueLibrary(discarded);
                    Assert.That(DispatchWorkers(service).Wait(TimeSpan.FromSeconds(45)), Is.True);
                    Assert.That(DispatchWorkers(service).Wait(TimeSpan.FromSeconds(45)), Is.True);

                    service.Complete(LibraryConversionJob.Key(43));
                    Assert.That(repository.FindLibraryJob(43).Status, Is.EqualTo(ConversionJobStatus.Completed));
                    Assert.That(Directory.Exists(completed.WorkFolder), Is.False, "an imported conversion's workspace is removed");

                    service.PrepareArtifactRemoval(LibraryConversionJob.Key(44), ConversionRemovalOrigin.UserAction)();
                    Assert.That(repository.FindLibraryJob(44), Is.Null);
                    Assert.That(Directory.Exists(discarded.WorkFolder), Is.False);
                    Assert.That(File.ReadAllText(discarded.Sources.Single().Path), Is.EqualTo("source"), "library files are never touched by cleanup");
                }
                finally
                {
                    ShutdownAndWait(service);
                    Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void cancelling_a_queued_library_job_needs_no_download([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "library-conversion-cancel-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var (service, _) = CreateLibraryService(repository, new UnusedConversionService(), NoDownloadHistory());
                try
                {
                    var request = CreateLibraryRequest(directory, 45);
                    service.EnqueueLibrary(request);

                    Assert.That(service.Cancel(LibraryConversionJob.Key(45), ConversionRemovalOrigin.UserAction), Is.True);
                    Assert.That(repository.FindLibraryJob(45).Status, Is.EqualTo(ConversionJobStatus.Cancelled));
                    Assert.That(service.GetActiveLibraryJobs(), Is.Empty);
                    Assert.That(File.ReadAllText(request.Sources.Single().Path), Is.EqualTo("source"));
                }
                finally
                {
                    ShutdownAndWait(service);
                    Directory.Delete(directory, true);
                }
            });
        }

        [Test]
        public void library_cancel_leaves_a_finished_conversion_for_its_import([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "library-conversion-ready-cancel-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var (service, _) = CreateLibraryService(repository, new RetainedResultConverter { Success = true }, NoDownloadHistory());
                try
                {
                    var request = CreateLibraryRequest(directory, 47);
                    service.EnqueueLibrary(request);
                    Assert.That(DispatchWorkers(service).Wait(TimeSpan.FromSeconds(45)), Is.True);
                    Assert.That(repository.FindLibraryJob(47).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));

                    // The progress task saw the job converting, but the worker finished it before the cancel arrived.
                    Assert.That(service.CancelLibrary(47, ConversionRemovalOrigin.UserAction), Is.False);

                    Assert.That(repository.FindLibraryJob(47).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    Assert.That(File.Exists(request.OutputPath), Is.True, "the converted file stays for the import that is starting");
                }
                finally
                {
                    ShutdownAndWait(service);
                    Directory.Delete(directory, true);
                }
            });
        }

        [TestCase(-5, false)]
        [TestCase(5, true)]
        public void library_import_proof_must_come_from_the_current_attempt(int addedSecondsAfterQueue, bool committed)
        {
            var directory = Path.Combine(Path.GetTempPath(), "library-conversion-proof-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var request = CreateLibraryRequest(directory, 48);
                request.Origin = ConversionJobOrigin.Library;
                request.DownloadId = LibraryConversionJob.Key(48);
                request.QueuedAtUtc = new DateTime(2026, 11, 20, 12, 0, 0, 500, DateTimeKind.Utc);
                var kept = Path.Combine(directory, "Kept From Earlier.m4b");
                File.WriteAllText(kept, "m4b");
                var file = new BookFile
                {
                    Path = kept,
                    DateAdded = request.QueuedAtUtc.Value.AddSeconds(addedSecondsAfterQueue),
                    Quality = new NzbDrone.Core.Qualities.QualityModel(NzbDrone.Core.Qualities.Quality.M4B),
                    MatchProvenance = new MatchProvenance { ImportDownloadId = request.DownloadId, ImportSourcePaths = request.Sources.Select(source => source.Path).ToList() }
                };
                var media = InterfaceProxy.Proxy<IMediaFileService>((method, _) =>
                    method.Name == nameof(IMediaFileService.GetFilesByBook) ? new List<BookFile> { file } : null);
                var commands = DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>();
                var service = new ConversionJobService(TestCleanupDisk(), TestRootFolders(), media, InterfaceProxy.Proxy<IConversionJobRepository>((_, _) => null),
                    new UnusedConversionService(), new ConversionTrackingService(new StubEventAggregator()), commands, CreateConfig(),
                    LogManager.GetCurrentClassLogger(), NoDownloadHistory());
                var job = new ConversionJob { Origin = ConversionJobOrigin.Library, DownloadId = request.DownloadId, BookId = request.BookId, EditionId = 48 };

                var proof = (bool)typeof(ConversionJobService).GetMethod("HasCommittedImport", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(service, new object[] { job, request });

                Assert.That(proof, Is.EqualTo(committed), committed
                    ? "a crash after this attempt's import still settles the job"
                    : "an M4B kept from an earlier conversion of the same edition is not this attempt's import");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void library_jobs_lookup_handles_more_editions_than_sqlite_parameters([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                repository.Insert(new ConversionJob
                {
                    DownloadId = LibraryConversionJob.Key(1500), Origin = ConversionJobOrigin.Library, BookId = 2500, EditionId = 1500,
                    Status = ConversionJobStatus.Completed, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                    WorkRoot = "/w", WorkFolder = "/w/a", OutputPath = "/w/a/o.m4b", RequestJson = "{}"
                });

                var found = repository.LibraryJobs(Enumerable.Range(1, 2000));

                Assert.That(found.Select(job => job.EditionId), Is.EqualTo(new[] { 1500 }));
            });
        }

        [Test]
        public void library_enqueue_requires_a_persisted_book_and_edition()
        {
            var (service, _) = CreateLibraryService(InterfaceProxy.Proxy<IConversionJobRepository>((_, _) => null), new UnusedConversionService());
            var request = CreateRequest(Path.GetTempPath(), "unused");

            request.BookId = 1;
            request.EditionId = 0;
            Assert.Throws<ArgumentException>(() => service.EnqueueLibrary(request));

            request.BookId = 0;
            request.EditionId = 1;
            Assert.Throws<ArgumentException>(() => service.EnqueueLibrary(request));
        }
    }
}

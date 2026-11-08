using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.Json;
using Chaptarr.Api.V1.ManualImport;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void manual_preview_uses_conversion_success_not_import_status(
            [Values(false, true)] bool postgres,
            [Values("ready", "import-failed", "encoder-failed", "cancelled", "missing", "changed-output", "queued", "missing-source")] string outcome,
            [Values("queue-folder", "queue-file", "folder")] string origin)
        {
            RunManualPreview(postgres, outcome, origin);
        }

        [Test]
        public void folder_preview_requires_unique_download_and_preserves_exact_file_scope(
            [Values(false, true)] bool postgres, [Values("folder-ambiguous", "folder-untracked", "folder-exact")] string origin)
        {
            RunManualPreview(postgres, "ready", origin);
        }

        [Test]
        public void parent_folder_preview_offers_each_owned_successful_conversion_in_one_batch(
            [Values(false, true)] bool postgres,
            [Values("ready", "import-failed", "encoder-failed", "queued", "ambiguous", "outside")] string secondState)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "manual-parent-preview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    var repository = new ConversionJobRepository(database, new StubEventAggregator());
                    var batchReads = 0;
                    var singleReads = 0;
                    var countedRepository = Proxy<IConversionJobRepository>((method, args) =>
                    {
                        if (method.Name == nameof(IConversionJobRepository.ByDownloadIds)) batchReads++;
                        if (method.Name == nameof(IConversionJobRepository.FindByDownloadId)) singleReads++;
                        return method.Invoke(repository, args);
                    });
                    var converter = new RetainedResultConverter { Success = true };
                    var jobs = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), countedRepository, converter, null,
                        Proxy<IManageCommandQueue>((_, _) => null), CreateConfig(), LogManager.GetCurrentClassLogger());
                    var requests = new[] { CreateRequest(Path.Combine(directory, "Dune"), "dune"),
                        CreateRequest(Path.Combine(directory, "Dune Messiah"), "messiah") };
                    RunWorker(jobs, requests[0]);
                    converter.Success = secondState != "encoder-failed";
                    RunWorker(jobs, requests[1]);
                    if (secondState == "import-failed") jobs.Fail("messiah", "Destination unavailable");
                    if (secondState == "queued")
                    {
                        var job = repository.FindByDownloadId("messiah");
                        job.Status = ConversionJobStatus.Queued;
                        repository.Update(job);
                    }

                    var tracks = requests.Select(request => new TrackedDownload
                    {
                        State = TrackedDownloadState.ImportBlocked,
                        DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId,
                            FilePaths = request.Sources.Select(source => source.Path).ToList() }
                    }).ToList();
                    if (secondState == "ambiguous") tracks.Add(new TrackedDownload
                    {
                        State = TrackedDownloadState.ImportBlocked,
                        DownloadItem = new DownloadClientItem { DownloadId = "other-owner", FilePaths = tracks[1].DownloadItem.FilePaths }
                    });
                    var scope = secondState == "outside" ? Path.Combine(directory, "Dune") : directory;
                    var unrelated = Path.Combine(scope, "Untracked book.mp3");
                    File.WriteAllText(unrelated, "untracked recording");
                    var fs = new FileSystem();
                    var queueReads = 0;
                    var matchingBatches = 0;
                    var decisions = Proxy<IMakeImportDecision>((_, args) =>
                    {
                        matchingBatches++;
                        return ((List<IFileInfo>)args[0]).Select(file => new ImportDecision<LocalBook>(new LocalBook
                        { Path = file.FullName, Size = file.Length, Quality = new QualityModel(file.Extension == ".m4b" ? Quality.M4B : Quality.MP3) })).ToList();
                    });
                    var dependencies = new Dictionary<Type, object>
                    {
                        [typeof(Logger)] = LogManager.GetCurrentClassLogger(),
                        [typeof(IDiskProvider)] = Proxy<IDiskProvider>((method, args) => method.Name switch
                        {
                            nameof(IDiskProvider.FolderExists) => Directory.Exists((string)args[0]),
                            nameof(IDiskProvider.FileExists) => File.Exists((string)args[0]),
                            nameof(IDiskProvider.GetFileInfo) => fs.FileInfo.FromFileName((string)args[0]),
                            _ => throw new NotImplementedException(method.Name)
                        }),
                        [typeof(IDiskScanService)] = Proxy<IDiskScanService>((_, _) => requests.SelectMany(request => request.Sources)
                            .Select(source => source.Path).Append(unrelated).Where(path => path.StartsWith(scope + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                            .Select(fs.FileInfo.FromFileName).ToArray()),
                        [typeof(IMakeImportDecision)] = decisions,
                        [typeof(ITrackedDownloadService)] = Proxy<ITrackedDownloadService>((method, args) =>
                        {
                            if (method.Name == nameof(ITrackedDownloadService.Find))
                                return tracks.SingleOrDefault(track => track.DownloadItem.DownloadId == (string)args[0]);
                            Assert.That(method.Name, Is.EqualTo(nameof(ITrackedDownloadService.GetTrackedDownloads)));
                            queueReads++;
                            return tracks;
                        }),
                        [typeof(IDownloadClientFileSnapshotService)] = Proxy<IDownloadClientFileSnapshotService>((_, _) => null),
                        [typeof(IHistoryService)] = new HistoryService(new HistoryRepository(database, new StubEventAggregator()),
                            LogManager.GetCurrentClassLogger(), new DownloadHistoryRepository(database, new StubEventAggregator())),
                        [typeof(IConversionJobService)] = jobs
                    };
                    var constructor = typeof(ManualImportService).GetConstructors().Single();
                    var preview = (ManualImportService)constructor.Invoke(constructor.GetParameters().Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
                    batchReads = singleReads = 0;
                    var items = preview.GetMediaFiles(scope, null, new Author { Id = 7, Name = "Frank Herbert" }, FilterFilesType.None, false);

                    Assert.That(items.Single(item => item.Path == requests[0].OutputPath).SelectByDefault, Is.True);
                    Assert.That(items.Single(item => item.Path == requests[0].Sources.Single().Path).SelectByDefault, Is.False);
                    var secondShown = secondState is "ready" or "import-failed";
                    Assert.That(items.Any(item => item.Path == requests[1].OutputPath), Is.EqualTo(secondShown));
                    foreach (var request in requests.Where(request => items.Any(item => item.Path == request.OutputPath)))
                    {
                        Assert.That(items.Single(item => item.Path == request.OutputPath).DownloadId, Is.EqualTo(request.DownloadId));
                        Assert.That(items.Single(item => item.Path == request.Sources.Single().Path).DownloadId, Is.EqualTo(request.DownloadId));
                    }
                    Assert.That(items.Single(item => item.Path == unrelated).DownloadId, Is.Null);
                    Assert.That(items.Single(item => item.Path == unrelated).SelectByDefault, Is.Null);
                    Assert.That(batchReads, Is.EqualTo(1));
                    Assert.That(singleReads, Is.Zero);
                    Assert.That(queueReads, Is.EqualTo(1));
                    Assert.That(matchingBatches, Is.EqualTo(1));
                    Assert.That(converter.Calls, Is.EqualTo(2));
                    Assert.That(requests.SelectMany(request => request.Sources).All(source => File.Exists(source.Path)), Is.True);
                }
                finally
                {
                    Directory.Delete(directory, true);
                }
            });
        }

        private void RunManualPreview(bool postgres, string outcome, string origin)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "manual-converted-preview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var repository = new ConversionJobRepository(database, new StubEventAggregator());
                var converter = new RetainedResultConverter { Success = outcome != "encoder-failed" };
                var request = CreateRequest(directory, "dune-download");
                var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), repository, converter, null,
                    Proxy<IManageCommandQueue>((_, _) => null), CreateConfig(), LogManager.GetCurrentClassLogger());
                try
                {
                    var singleFile = origin == "queue-file";
                    var folderPreview = origin.StartsWith("folder", StringComparison.Ordinal);
                    RunWorker(service, request);
                    if (outcome == "import-failed") service.Fail(request.DownloadId, "Destination is unavailable");
                    if (outcome == "cancelled") service.Cancel(request.DownloadId, ConversionRemovalOrigin.UserAction);
                    if (outcome == "missing") File.Delete(request.OutputPath);
                    if (outcome == "changed-output") File.AppendAllText(request.OutputPath, "changed");
                    if (outcome == "missing-source") File.Delete(request.Sources.Single().Path);
                    if (outcome == "queued")
                    {
                        var job = repository.FindByDownloadId(request.DownloadId);
                        job.Status = ConversionJobStatus.Queued;
                        repository.Update(job);
                    }

                    var fs = new FileSystem();
                    var source = request.Sources.Single().Path;
                    var unrelated = Path.Combine(directory, "Another book.mp3");
                    File.WriteAllText(unrelated, "another recording");
                    var tracked = new TrackedDownload
                    {
                        State = TrackedDownloadState.ImportBlocked,
                        DownloadItem = new DownloadClientItem { DownloadId = request.DownloadId, OutputPath = new OsPath(singleFile ? source : directory),
                            FilePaths = new List<string> { source, unrelated } }
                    };
                    tracked.ImportItem = tracked.DownloadItem;
                    var disk = Proxy<IDiskProvider>((method, args) => method.Name switch
                    {
                        nameof(IDiskProvider.FolderExists) => Directory.Exists((string)args[0]),
                        nameof(IDiskProvider.FileExists) => File.Exists((string)args[0]),
                        nameof(IDiskProvider.GetFileInfo) => fs.FileInfo.FromFileName((string)args[0]),
                        _ => throw new NotImplementedException(method.Name)
                    });
                    var scans = Proxy<IDiskScanService>((method, _) => method.Name == nameof(IDiskScanService.GetBookFiles)
                        ? new[] { source, unrelated }.Where(File.Exists).Select(fs.FileInfo.FromFileName).ToArray()
                        : throw new NotImplementedException(method.Name));
                    var pathsSeen = new List<string>();
                    var decisions = Proxy<IMakeImportDecision>((method, args) =>
                    {
                        if (method.Name != nameof(IMakeImportDecision.GetImportDecisions)) throw new NotImplementedException(method.Name);
                        var files = (List<IFileInfo>)args[0];
                        pathsSeen.AddRange(files.Select(file => file.FullName));
                        return files.Select(file => new ImportDecision<LocalBook>(new LocalBook
                        {
                            Path = file.FullName, Size = file.Length,
                            Quality = new QualityModel(file.Extension == ".m4b" ? Quality.M4B : Quality.MP3)
                        })).ToList();
                    });
                    var preview = new ManualImportService(disk, null, null, scans, decisions,
                        null, null, null, null, null, null, null, null, null, null, null,
                        Proxy<ITrackedDownloadService>((method, _) => method.Name switch
                        {
                            nameof(ITrackedDownloadService.Find) => tracked,
                            nameof(ITrackedDownloadService.GetTrackedDownloads) => origin == "folder-untracked" ? new List<TrackedDownload>() :
                                origin == "folder-ambiguous" ? new List<TrackedDownload> { tracked, new()
                                { State = tracked.State, DownloadItem = new DownloadClientItem { DownloadId = "different-download", FilePaths = tracked.DownloadItem.FilePaths } } }
                                : new List<TrackedDownload> { tracked },
                            _ => throw new NotImplementedException(method.Name)
                        }),
                        Proxy<IDownloadClientFileSnapshotService>((_, _) => null),
                        new HistoryService(new HistoryRepository(database, new StubEventAggregator()), LogManager.GetCurrentClassLogger(),
                            new DownloadHistoryRepository(database, new StubEventAggregator())), null, null, null,
                        LogManager.GetCurrentClassLogger(), null, service);

                    var before = repository.FindByDownloadId(request.DownloadId);
                    var items = preview.GetMediaFiles(folderPreview ? directory : null,
                        folderPreview ? null : request.DownloadId, new Author { Id = 7, Name = "Frank Herbert" }, FilterFilesType.None, false,
                        exactPaths: origin == "folder-exact" ? new[] { source } : null);
                    var expectedOutput = (outcome is "ready" or "import-failed" or "missing-source") && !origin.StartsWith("folder-", StringComparison.Ordinal);
                    Assert.That(pathsSeen.Contains(request.OutputPath), Is.EqualTo(expectedOutput));
                    Assert.That(items.Any(item => item.Path == request.OutputPath), Is.EqualTo(expectedOutput));
                    if (outcome == "missing-source") Assert.That(items.Any(item => item.Path == source), Is.False);
                    else Assert.That(items.Single(item => item.Path == source).ToResource().SelectByDefault,
                        Is.EqualTo(expectedOutput ? false : (bool?)null));
                    if (expectedOutput)
                    {
                        Assert.That(items.Single(item => item.Path == request.OutputPath).ToResource().SelectByDefault, Is.True);
                        Assert.That(items.Single(item => item.Path == request.OutputPath).DownloadId, Is.EqualTo(request.DownloadId));
                    }
                    if (origin == "folder-exact") Assert.That(items.Select(item => item.Path), Is.EqualTo(new[] { source }));
                    else if (!singleFile) Assert.That(items.Single(item => item.Path == unrelated).SelectByDefault, Is.Null);
                    Assert.That(converter.Calls, Is.EqualTo(1));
                    Assert.That(repository.FindByDownloadId(request.DownloadId).Status, Is.EqualTo(before.Status), "Opening preview cannot change the job");
                    Assert.That(File.Exists(source), Is.EqualTo(outcome != "missing-source"));
                }
                finally
                {
                    Directory.Delete(directory, true);
                }
            });
        }

        [TestCase("successful", true)]
        [TestCase("missing", false)]
        [TestCase("changed", false)]
        [TestCase("failed", false)]
        [TestCase("tracked", false)]
        [TestCase("ordinary", false)]
        public void replacement_preview_only_treats_verified_untracked_conversion_as_incoming(string state, bool replaces)
        {
            var directory = Path.Combine(Path.GetTempPath(), "manual-converted-selector-" + Guid.NewGuid().ToString("N"));
            var work = Path.Combine(directory, ".chaptarr-conversions", "download", "attempt");
            Directory.CreateDirectory(work);
            try
            {
                var output = Path.Combine(state == "ordinary" ? directory : work, "Dune.m4b");
                File.WriteAllText(output, "converted audio");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(output), "conversion-artifact.json"), JsonSerializer.Serialize(new ConversionArtifactManifest
                { EncoderSucceeded = state != "failed", OutputPath = output, OutputSize = new FileInfo(output).Length }));
                if (state == "missing") File.Delete(output);
                if (state == "changed") File.AppendAllText(output, "changed");
                var old = new BookFile { Id = 1, Path = Path.Combine(directory, "old.m4b"), Edition = new Edition { BookId = 10 } };
                var selected = new LocalBook { Path = output, ExistingFile = true, Book = new Book { Id = 10 } };
                var stored = state == "tracked" ? new[] { new BookFile { Path = output } } : Array.Empty<BookFile>();
                Assert.That(ManualImportReplacementFiles.Get(new[] { old }, new[] { selected }, stored),
                    Is.EqualTo(replaces ? new[] { old } : Array.Empty<BookFile>()));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RemotePathMappings;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        [TestCase(false, "converting-artifact")]
        [TestCase(true, "converting-artifact")]
        [TestCase(false, "queued")]
        [TestCase(true, "queued")]
        [TestCase(false, "converting")]
        [TestCase(true, "converting")]
        [TestCase(false, "destination")]
        [TestCase(true, "destination")]
        [TestCase(false, "pinned-copy")]
        [TestCase(true, "pinned-copy")]
        [TestCase(false, "additional-copy")]
        [TestCase(true, "additional-copy")]
        [TestCase(false, "already-tracked")]
        [TestCase(true, "already-tracked")]
        [TestCase(false, "transfer")]
        [TestCase(true, "transfer")]
        [TestCase(false, "lookup-outage")]
        [TestCase(true, "lookup-outage")]
        [TestCase(false, "database-outage")]
        [TestCase(true, "database-outage")]
        [TestCase(false, "commit")]
        [TestCase(true, "commit")]
        [TestCase(false, "commit-retry")]
        [TestCase(true, "commit-retry")]
        [TestCase(false, "expired")]
        [TestCase(true, "expired")]
        [TestCase(false, "committed-event")]
        [TestCase(true, "committed-event")]
        public void conversion_import_retry_must_reuse_successful_m4b(bool postgres, string refusal)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "conversion-import-retry-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    using var outage = new ConversionJobServiceFixture.UnavailableDatabase(database, root, postgres);
                    var events = new ConversionBoundaryEvents();
                    var tracking = new ConversionTrackingService(events);
                    var converter = new SuccessfulM4bConversionService();
                    var repository = new ConversionJobRepository(outage.Database, events);
                    var jobs = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), repository, converter, tracking, Proxy<IManageCommandQueue>(), CreateConfigService(), logger);
                    var (profile, author, book, edition) = CreateAudiobookConversionGraph(299);
                    author.AudiobookRootFolderPath = Path.Combine(root, "library");
                    author.AudiobookPath = author.AudiobookRootFolderPath;
                    book.AnyEditionOk = true;
                    book.Editions = new List<Edition> { edition };
                    Directory.CreateDirectory(author.AudiobookPath);
                    var source = Path.Combine(root, "source.mp3");
                    var destination = Path.Combine(author.AudiobookPath, "recording.m4b");
                    File.WriteAllText(source, "original source audio");
                    var files = new StubMediaFileService();
                    var mover = new StubMoveBookFiles { DestinationPath = destination, TransferFilesOnDisk = true };
                    var (books, _) = CreateBookService(new List<Book> { book });
                    var editions = InterfaceProxy.Proxy<IEditionService>((method, args) =>
                    {
                        if (method.Name == nameof(IEditionService.InsertMany))
                        {
                            new EditionRepository(database, events).InsertMany((List<Edition>)args[0]);
                            return null;
                        }

                        return method.Invoke(CreateEditionService(book.Editions), args);
                    });
                    var fileRows = new MediaFileRepository(outage.Database, events);
                    var commitAttempts = 0;
                    void RejectFileInsert()
                    {
                        using var connection = database.OpenConnection();
                        connection.Execute(postgres
                            ? "CREATE FUNCTION reject_converted_file() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'converted file save refused'; END $$; CREATE TRIGGER reject_converted_file BEFORE INSERT ON \"BookFiles\" FOR EACH ROW EXECUTE FUNCTION reject_converted_file();"
                            : "CREATE TRIGGER reject_converted_file BEFORE INSERT ON \"BookFiles\" BEGIN SELECT RAISE(ABORT, 'converted file save refused'); END;");
                    }

                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        if (method.Name == nameof(IMediaFileService.ReplaceMany))
                        {
                            commitAttempts++;
                            if (refusal == "database-outage" && commitAttempts == 1) outage.Unavailable = true;
                            var additions = (List<BookFile>)args[0];
                            if (refusal == "commit-retry" && commitAttempts == 1)
                            {
                                var concurrent = new BookFile
                                {
                                    Path = additions[0].Path, EditionId = edition.Id, Edition = edition,
                                    Quality = new QualityModel(Quality.M4B), MediaInfo = new MediaInfoModel(),
                                    DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow
                                };
                                fileRows.Insert(concurrent);
                                files.FilesByPath[concurrent.Path] = concurrent;
                            }


                            if (refusal is "commit" or "commit-retry" or "committed-event" or "database-outage")
                            {
                                fileRows.ReplaceMany(additions, (List<BookFile>)args[1]);
                                foreach (var old in (List<BookFile>)args[1]) files.Delete(old, (DeleteMediaFileReason)args[2]);
                                files.AddMany(additions);
                                return null;
                            }

                            files.ReplaceMany(additions, (List<BookFile>)args[1], (DeleteMediaFileReason)args[2]);
                            return null;
                        }

                        return method.Invoke(files, args);
                    });
                    var history = new HistoryService(new HistoryRepository(outage.Database, events), logger);
                    var importEvents = InterfaceProxy.Proxy<IEventAggregator>((method, args) =>
                    {
                        if (args[0] is BookFileConvertedEvent converted) history.Handle(converted);
                        if (args[0] is BookFileConversionFailedEvent failed && refusal != "lookup-outage") history.Handle(failed);
                        if (refusal == "committed-event" && args[0] is TrackImportedEvent) throw new InvalidOperationException("import observer unavailable");
                        return null;
                    });
                    var service = new ImportApprovedBooks(
                        media, new StubMetadataTagService(), new StubMediaInfoExtractor(), Proxy<IAuthorService>(), books, editions,
                        Proxy<IRecycleBinProvider>(), Proxy<IExtraService>(), mover, history,
                        Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), importEvents, Proxy<IManageCommandQueue>(),
                        Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(), Proxy<IQualityProfileService>(), converter, logger,
                        tracking, configService: CreateConfigService(), conversionJobService: jobs);
                    var item = new DownloadClientItem { DownloadId = "retained-conversion", Status = DownloadItemStatus.Completed, OutputPath = new OsPath(root) };
                    List<ImportResult> Import() => service.Import(new List<ImportDecision<LocalBook>>
                    {
                        new(new LocalBook
                        {
                            Path = source, Author = author, Book = book, Edition = edition, Quality = new QualityModel(Quality.MP3)
                        })
                    }, true, item, ImportMode.Move, CancellationToken.None);

                    Assert.That(Import().Single().Result, Is.EqualTo(ImportResultType.Pending));
                    var job = repository.FindByDownloadId(item.DownloadId);
                    Assert.That(job.Status, Is.EqualTo(ConversionJobStatus.Queued));
                    if (refusal is "queued" or "converting")
                    {
                        job.Status = refusal == "queued" ? ConversionJobStatus.Queued : ConversionJobStatus.Converting;
                        repository.Update(job);
                        Directory.CreateDirectory(job.WorkFolder);
                        var activeOutput = Path.Combine(job.WorkFolder, "active-conversion.tmp");
                        File.WriteAllText(activeOutput, "conversion in progress");
                        Directory.SetLastWriteTimeUtc(job.WorkFolder, DateTime.UtcNow.AddDays(-8));
                        Assert.That(Import().Single().Result, Is.EqualTo(ImportResultType.Pending));
                        Assert.That(repository.FindByDownloadId(item.DownloadId).Status, Is.EqualTo(job.Status));
                        Assert.That(converter.ConvertCalls, Is.Zero);
                        Assert.That(File.ReadAllText(activeOutput), Is.EqualTo("conversion in progress"));
                        Assert.Throws<InvalidOperationException>(() => jobs.Reset(item.DownloadId));
                        return;
                    }

                    var request = JsonSerializer.Deserialize<ConversionJobRequest>(job.RequestJson);
                    Directory.CreateDirectory(request.WorkFolder);
                    const string convertedBytes = "completed M4B that must survive import refusal";
                    File.WriteAllText(request.OutputPath, convertedBytes);
                    var manifestPath = Path.Combine(request.WorkFolder, "conversion-artifact.json");
                    File.WriteAllText(manifestPath, JsonSerializer.Serialize(new ConversionArtifactManifest
                    {
                        EncoderSucceeded = true, OutputSize = new FileInfo(request.OutputPath).Length,
                        CreatedUtc = DateTime.UtcNow, OutputPath = request.OutputPath,
                        TargetQualityId = request.TargetQualityId, TargetQualityName = request.TargetQualityName,
                        AudioBitrate = request.AudioBitrate, AudioChannels = request.AudioChannels,
                        TagMode = request.TagOptions?.Mode, TagSignature = request.TagSignature, Sources = request.Sources
                    }));
                    job.Status = ConversionJobStatus.ReadyToImport;
                    repository.Update(job);
                    var manifest = File.ReadAllText(manifestPath);

                    if (refusal == "converting-artifact")
                    {
                        job.Status = ConversionJobStatus.Converting;
                        repository.Update(job);
                        Assert.That(Import().Single().Result, Is.EqualTo(ImportResultType.Pending));
                        Assert.That(repository.FindByDownloadId(item.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Converting));
                        Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo(convertedBytes));
                        Assert.That(File.Exists(destination), Is.False);
                        Assert.That(converter.ConvertCalls, Is.Zero);
                        return;
                    }

                    if (refusal == "commit") RejectFileInsert();
                    if (refusal == "expired") Directory.SetLastWriteTimeUtc(request.WorkFolder, DateTime.UtcNow.AddDays(-8));
                    if (refusal == "destination") File.WriteAllText(destination, "occupied destination");
                    if (refusal is "transfer" or "expired") Directory.CreateDirectory(destination);
                    if (refusal == "pinned-copy")
                    {
                        book.AnyEditionOk = false;
                        book.Editions.Add(new Edition { Id = edition.Id + 1, BookId = book.Id, Book = book, Monitored = true, ManualAdd = true, Title = book.Title });
                        using var connection = database.OpenConnection();
                        connection.Execute(postgres
                            ? "CREATE FUNCTION reject_copy_edition() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'copy destination refused'; END $$; CREATE TRIGGER reject_copy_edition BEFORE INSERT ON \"Editions\" FOR EACH ROW EXECUTE FUNCTION reject_copy_edition();"
                            : "CREATE TRIGGER reject_copy_edition BEFORE INSERT ON \"Editions\" BEGIN SELECT RAISE(ABORT, 'copy destination refused'); END;");
                    }

                    if (refusal is "additional-copy" or "already-tracked")
                    {
                        var existing = new BookFile { Id = 123, EditionId = edition.Id, Edition = edition, Path = Path.Combine(root, "old.m4b"), Quality = new QualityModel(Quality.M4B) };
                        files.FilesByBook.Add(existing);
                        if (refusal == "already-tracked")
                        {
                            existing.Path = request.OutputPath;
                            files.FilesByPath[existing.Path] = existing;
                        }
                        else events.QueueUpdated = () =>
                        {
                            if (tracking.Get(item.DownloadId)?.Message == "Using retained M4B") File.WriteAllText(destination, "occupied after conversion preflight");
                        };
                    }

                    var siblingArtifacts = new Dictionary<string, string>();
                    if (refusal == "expired")
                    {
                        foreach (var status in new[] { ConversionJobStatus.Converting, ConversionJobStatus.Failed })
                        {
                            var otherRequest = JsonSerializer.Deserialize<ConversionJobRequest>(job.RequestJson);
                            otherRequest.DownloadId = status.ToString();
                            otherRequest.WorkRoot = Path.Combine(Path.GetDirectoryName(request.WorkRoot), otherRequest.DownloadId);
                            otherRequest.WorkFolder = Path.Combine(otherRequest.WorkRoot, "attempt");
                            otherRequest.OutputPath = Path.Combine(otherRequest.WorkFolder, "other.m4b");
                            Directory.CreateDirectory(otherRequest.WorkFolder);
                            File.WriteAllText(otherRequest.OutputPath, status.ToString());
                            siblingArtifacts.Add(otherRequest.OutputPath, status.ToString());
                            Directory.SetLastWriteTimeUtc(otherRequest.WorkFolder, DateTime.UtcNow.AddDays(-8));
                            var otherJob = jobs.Enqueue(otherRequest);
                            otherJob.Status = status;
                            repository.Update(otherJob);
                        }
                    }

                    if (refusal == "committed-event")
                    {
                        var failure = Assert.Throws<InvalidOperationException>(() => Import());
                        Assert.That(failure.Message, Is.EqualTo("import observer unavailable"));
                        Assert.That(repository.FindByDownloadId(item.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Completed));
                        Assert.That(fileRows.All().Single().Path, Is.EqualTo(destination));
                        Assert.That(File.ReadAllText(destination), Is.EqualTo(convertedBytes));
                        Assert.That(Directory.Exists(request.WorkRoot), Is.False);
                        Assert.That(converter.ConvertCalls, Is.Zero);
                        return;
                    }

                    var results = new List<ImportResult>();
                    if (refusal is "database-outage" or "lookup-outage")
                    {
                        if (refusal == "lookup-outage") outage.Unavailable = true;
                        Assert.Catch<DbException>(() => Import());
                        outage.Unavailable = false;
                        Assert.That(File.Exists(destination), Is.False);
                        Assert.That(fileRows.All(), Is.Empty);
                    }
                    else if (refusal is "commit" or "commit-retry")
                    {
                        if (refusal == "commit")
                        {
                            var failure = Assert.Catch<DbException>(() => Import());
                            Assert.That(failure.Message, Does.Contain("converted file save refused"));
                        }
                        else
                        {
                            results = Import();
                            Assert.That(results.Single().Errors.Single(), Does.Contain("Existing files changed"));
                        }
                        Assert.That(commitAttempts, Is.EqualTo(1));
                        Assert.That(File.Exists(destination), Is.False, "The failed transaction must return the M4B to its work folder");
                        var retainedHistory = history.FindByDownloadId(item.DownloadId).Single(row =>
                            row.EventType == EntityHistoryEventType.BookFileConverted && string.IsNullOrWhiteSpace(row.GetDataValue("ImportedPath")));
                        Assert.That(retainedHistory.GetDataValue("ConvertedPath"), Is.EqualTo(request.OutputPath),
                            "Explicit removal must be able to find retained output after a failed file commit");
                        Assert.That(fileRows.All().Count, Is.EqualTo(refusal == "commit" ? 0 : 1));
                    }
                    else
                    {
                        results = Import();
                    }
                    Assert.Multiple(() =>
                    {
                        Assert.That(results.Any(result => result.Result == ImportResultType.Imported), Is.False);
                        Assert.That(repository.FindByDownloadId(item.DownloadId).Status, Is.EqualTo(refusal is "database-outage" or "lookup-outage" ? ConversionJobStatus.ReadyToImport : ConversionJobStatus.Failed), refusal);
                        Assert.That(tracking.Get(item.DownloadId)?.Status, Is.EqualTo(refusal is "database-outage" or "lookup-outage" ? "converting" : "failed"));
                        Assert.That(File.Exists(request.OutputPath), Is.True, "Import refusal must retain the completed recording");
                        Assert.That(File.Exists(manifestPath), Is.True, "Retry needs the existing artifact validation record");
                        Assert.That(converter.ConvertCalls, Is.Zero);
                    });
                    Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo(convertedBytes));
                    Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(manifest));
                    foreach (var artifact in siblingArtifacts)
                    {
                        Assert.That(File.ReadAllText(artifact.Key), Is.EqualTo(artifact.Value));
                    }

                    if (refusal == "transfer")
                    {
                        jobs.Reset(item.DownloadId);
                        Assert.That(Import().Single().Result, Is.Not.EqualTo(ImportResultType.Imported));
                        Assert.That(repository.FindByDownloadId(item.DownloadId), Is.Null);
                        Assert.That(tracking.Get(item.DownloadId)?.Status, Is.EqualTo("failed"));
                        Assert.That(File.ReadAllText(request.OutputPath), Is.EqualTo(convertedBytes));
                        Assert.That(File.ReadAllText(manifestPath), Is.EqualTo(manifest));
                        Assert.That(converter.ConvertCalls, Is.Zero);
                    }

                    if (refusal is "commit" or "commit-retry")
                    {
                        using var connection = database.OpenConnection();
                        if (refusal == "commit") connection.Execute(postgres
                            ? "DROP TRIGGER reject_converted_file ON \"BookFiles\"; DROP FUNCTION reject_converted_file();"
                            : "DROP TRIGGER reject_converted_file;");
                        fileRows.DeleteMany(fileRows.All().Select(file => file.Id));
                    }

                    events.QueueUpdated = null;
                    if (File.Exists(destination)) File.Delete(destination);
                    if (Directory.Exists(destination)) Directory.Delete(destination);
                    files.FilesByBook.Clear();
                    files.FilesByPath.Clear();
                    book.AnyEditionOk = true;
                    book.Editions.RemoveAll(value => value.Id != edition.Id);
                    var tracked = new TrackedDownload { DownloadItem = item, State = TrackedDownloadState.ImportBlocked };
                    var retryImports = 0;
                    var completed = InterfaceProxy.Proxy<ICompletedDownloadService>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(ICompletedDownloadService.Import)));
                        retryImports++;
                        var retried = Import();
                        Assert.That(retried.Single().Result, Is.EqualTo(ImportResultType.Imported), string.Join("; ", retried.Single().Errors));
                        tracked.State = TrackedDownloadState.Imported;
                        return null;
                    });
                    var retry = new RetryFailedImportService(
                        InterfaceProxy.Proxy<ITrackedDownloadService>((method, args) => method.Name == nameof(ITrackedDownloadService.GetTrackedDownloads) ? new List<TrackedDownload> { tracked } : tracked),
                        InterfaceProxy.Proxy<IProvideDownloadClient>((method, args) => null),
                        InterfaceProxy.Proxy<IRemotePathMappingService>((method, args) => item.OutputPath),
                        completed, Proxy<IDownloadClientFileSnapshotService>(), tracking, Proxy<ICommandResultReporter>(), events, logger, jobs);
                    retry.Execute(new RetryFailedImportCommand { DownloadId = item.DownloadId });
                    Assert.Multiple(() =>
                    {
                        Assert.That(retryImports, Is.EqualTo(1));
                        Assert.That(converter.ConvertCalls, Is.Zero, "Retry must only import the validated M4B");
                        if (refusal is "database-outage" or "lookup-outage")
                        {
                            Assert.That(repository.FindByDownloadId(item.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Completed));
                        }
                        else
                        {
                            Assert.That(repository.FindByDownloadId(item.DownloadId), Is.Null, "No replacement conversion may be queued");
                        }
                        Assert.That(File.ReadAllText(destination), Is.EqualTo(convertedBytes));
                        Assert.That(File.ReadAllText(source), Is.EqualTo("original source audio"));
                        Assert.That(tracking.Get(item.DownloadId), Is.Null);
                        Assert.That(Directory.Exists(request.WorkRoot), Is.False, "Successful import releases its retained workspace");
                    });
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }
    }
}

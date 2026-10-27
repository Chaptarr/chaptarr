using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using Chaptarr.Api.V1.ManualImport;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.RootFolders;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        private sealed class ConversionBoundaryEvents : IEventAggregator
        {
            public Action QueueUpdated { get; set; }

            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
            {
                if (@event is QueueUpdatedEvent) QueueUpdated?.Invoke();
            }
        }

        private sealed class BoundaryM4bConverter : IM4bConversionService
        {
            public Action<ConversionOptions> BeforeReturn { get; set; }
            public bool Success { get; set; } = true;
            public bool RetainOutputOnFailure { get; set; }
            public ConversionFailureCategory FailureCategory { get; set; }
            public int Calls { get; private set; }
            public string OutputPath { get; private set; }

            public bool CanConvert(string[] inputFiles) => true;
            public ConversionEstimate EstimateConversion(string[] inputFiles) => new()
            {
                CanConvert = true, InputFileCount = inputFiles.Length, TotalInputSize = 1024, EstimatedOutputSize = 1024
            };

            public ConversionResult ConvertToM4b(string[] inputFiles, string outputFile, ConversionOptions options = null)
            {
                Calls++;
                OutputPath = outputFile;
                Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
                if (Success || RetainOutputOnFailure) File.WriteAllText(outputFile, "constructed converted audio");
                BeforeReturn?.Invoke(options);
                return new ConversionResult
                {
                    Success = Success, RetainOutputOnFailure = RetainOutputOnFailure, FailureCategory = FailureCategory, OutputFile = outputFile,
                    ErrorMessage = Success ? null : "constructed conversion failure",
                    OutputFileSize = Success ? new FileInfo(outputFile).Length : 0
                };
            }
        }

        private sealed class WorkerConversionBoundary : IDisposable
        {
            private readonly string _directory;
            private readonly string _source;
            private readonly Author _author;
            private readonly Book _book;
            private readonly Edition _edition;
            private readonly ImportApprovedBooks _service;
            public readonly string DownloadId = Guid.NewGuid().ToString("N");
            public readonly ConversionTrackingService Tracking;
            public readonly BoundaryM4bConverter Converter = new();
            public readonly StubMoveBookFiles Mover;
            public readonly ConversionJobService Jobs;
            public readonly StubMediaFileService MediaFiles = new();
            public readonly List<(decimal? Progress, bool CanCancel, bool LateAccepted, Exception LateError)> ImportBoundaries = new();
            public bool FailTransfer;

            public WorkerConversionBoundary(NzbDrone.Core.Datastore.IMainDatabase database = null, int bookId = 91)
            {
                _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "conversion-stop-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_directory);
                _source = Path.Combine(_directory, "source.mp3");
                File.WriteAllText(_source, "constructed source audio");
                (_, _author, _book, _edition) = CreateAudiobookConversionGraph(bookId);
                _book.Editions = new List<Edition> { _edition };
                _author.AudiobookRootFolderPath = _directory;
                _author.AudiobookPath = _directory;
                _book.CleanTitle = "black sheep";
                Mover = new StubMoveBookFiles { DestinationPath = Path.Combine(_directory, "output.m4b") };
                var events = new ConversionBoundaryEvents();
                Tracking = new ConversionTrackingService(events);
                events.QueueUpdated = () =>
                {
                    var status = Tracking.Get(DownloadId);
                    if (status?.Message != "Importing M4B") return;
                    var canCancel = status.CanCancel;
                    var accepted = false;
                    Exception error = null;
                    try { accepted = Tracking.Cancel(DownloadId); }
                    catch (Exception ex) { error = ex; }
                    ImportBoundaries.Add((status.Progress, canCancel, accepted, error));
                    Mover.FailSourcePath = FailTransfer ? Converter.OutputPath : null;
                };
                Jobs = CreateConversionJobs(Converter, Tracking, null, MediaFiles, database);
                _service = new ImportApprovedBooks(
                    MediaFiles, new StubMetadataTagService(), new StubMediaInfoExtractor(),
                    Proxy<IAuthorService>(), CreateBookService(new List<Book> { _book }).Service, CreateEditionService(new List<Edition> { _edition }),
                    new StubRecycleBinProvider(), Proxy<IExtraService>(), Mover, Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(), Converter, LogManager.GetCurrentClassLogger(), Tracking,
                    conversionJobService: Jobs);
            }

            public List<ImportResult> Import(bool manual = false) => manual ? ImportSelectedOriginals() : ImportWithConversionWorker(_service, new List<ImportDecision<LocalBook>>
            {
                new(new LocalBook
                {
                    Path = _source, Author = _author, Book = _book, Edition = _edition,
                    Quality = new QualityModel(Quality.MP3), IsManualImport = false
                })
            }, false, new DownloadClientItem { DownloadId = DownloadId }, ImportMode.Move, CancellationToken.None, Jobs);

            private List<ImportResult> ImportSelectedOriginals() => _service.Import(new List<ImportDecision<LocalBook>>
            {
                new(new LocalBook { Path = _source, Author = _author, Book = _book, Edition = _edition,
                    Quality = new QualityModel(Quality.MP3), IsManualImport = true })
            }, false, new DownloadClientItem { DownloadId = DownloadId }, ImportMode.Move, CancellationToken.None);

            public List<ImportResult> ImportSelectedOutput(bool replace = false, IReadOnlyCollection<ManualImportReplacementFile> confirmed = null, bool associateDownload = true) => _service.Import(new List<ImportDecision<LocalBook>>
            {
                new(new LocalBook { Path = Converter.OutputPath, Author = _author, Book = _book, Edition = _edition,
                    Quality = new QualityModel(Quality.M4B), IsManualImport = true, ExistingFile = replace,
                    ConfirmedReplacementFiles = confirmed })
            }, replace, associateDownload ? new DownloadClientItem { DownloadId = DownloadId } : null, ImportMode.Move, CancellationToken.None);

            public List<ImportResult> ImportOutputAndSource(bool differentBook) => _service.Import(new List<ImportDecision<LocalBook>>
            {
                new(new LocalBook { Path = _source, Author = _author, Book = _book, Edition = _edition,
                    Quality = new QualityModel(Quality.MP3), IsManualImport = true }),
                new(new LocalBook { Path = Converter.OutputPath, Author = _author,
                    Book = differentBook ? new Book { Id = _book.Id + 1 } : _book, Edition = _edition,
                    Quality = new QualityModel(Quality.M4B), IsManualImport = true })
            }, true, new DownloadClientItem { DownloadId = DownloadId }, ImportMode.Move, CancellationToken.None);

            public BookFile AddOldRecording()
            {
                var path = Path.Combine(_directory, "old-recording.m4b");
                File.WriteAllText(path, "old recording must survive a failed replacement");
                return MediaFiles.Add(new BookFile { Path = path, EditionId = _edition.Id, Edition = _edition,
                    Quality = new QualityModel(Quality.M4B) });
            }

            public List<ManualImportReplacementFile> PreviewOutputReplacement()
            {
                var media = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IMediaFileService>((method, args) => method.Name switch
                {
                    nameof(IMediaFileService.GetFilesByBooks) => MediaFiles.FilesByBook,
                    nameof(IMediaFileService.GetFileWithPath) => MediaFiles.GetFileWithPath((List<string>)args[0]),
                    _ => throw new NotImplementedException(method.Name)
                });
                var roots = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IRootFolderService>((method, _) => method.Name == nameof(IRootFolderService.All)
                    ? new List<RootFolder> { new() { Path = _directory } } : new RootFolder { Path = _directory });
                var descriptions = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IManualImportReplacementFiles>((_, args) =>
                    ((IReadOnlyCollection<BookFile>)args[0]).Select(file => new ManualImportReplacementFile
                    { Id = file.Id, BookId = file.Edition.BookId, Path = file.Path }).ToList());
                var controller = new ManualImportController(null, null, null, null, media, roots, LogManager.GetCurrentClassLogger(), descriptions);
                return controller.GetReplacementFiles(new ManualImportCommand { Files = new List<ManualImportFile>
                { new() { Path = Converter.OutputPath, BookId = _book.Id } } });
            }

            public void ExecuteOutputWithoutDownloadId(bool selectSources, WorkerConversionBoundary other = null, bool importOriginals = false)
            {
                var boundaries = other == null ? new[] { this } : new[] { this, other };
                var downloads = boundaries.Select(boundary => new TrackedDownload { State = TrackedDownloadState.ImportBlocked,
                    DownloadItem = new DownloadClientItem { DownloadId = boundary.DownloadId, FilePaths = new List<string> { boundary._source } } }).ToList();
                var tracked = downloads[0];
                var dependencies = new Dictionary<Type, object>
                {
                    [typeof(Logger)] = LogManager.GetCurrentClassLogger(),
                    [typeof(IDiskProvider)] = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IDiskProvider>((_, args) => new System.IO.Abstractions.FileSystem().FileInfo.FromFileName((string)args[0])),
                    [typeof(IRootFolderService)] = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IRootFolderService>((_, _) => new RootFolder { Path = _directory }),
                    [typeof(IAuthorService)] = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IAuthorService>((_, args) => boundaries.First(boundary => boundary._author.Id == (int)args[0])._author),
                    [typeof(IBookService)] = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((_, args) => boundaries.First(boundary => boundary._book.Id == (int)args[0])._book),
                    [typeof(IEditionService)] = CreateEditionService(boundaries.Select(boundary => boundary._edition).ToList()),
                    [typeof(IMetadataTagService)] = new StubMetadataTagService(),
                    [typeof(ITrackedDownloadService)] = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<ITrackedDownloadService>((method, args) => method.Name == nameof(ITrackedDownloadService.Find)
                        ? downloads.Single(download => download.DownloadItem.DownloadId == (string)args[0]) : downloads),
                    [typeof(IDownloadClientFileSnapshotService)] = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IDownloadClientFileSnapshotService>((_, _) => null),
                    [typeof(IDownloadImportModeResolver)] = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IDownloadImportModeResolver>((_, _) => ImportMode.Move),
                    [typeof(IImportApprovedBooks)] = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IImportApprovedBooks>((method, args) =>
                    {
                        var downloadId = ((DownloadClientItem)args[2])?.DownloadId;
                        Assert.That(downloadId, Is.Not.Null, "Every selected conversion must retain its own download context");
                        var boundary = boundaries.Single(candidate => candidate.DownloadId == downloadId);
                        Assert.That(((List<ImportDecision<LocalBook>>)args[0]).All(decision => decision.Item.Book.Id == boundary._book.Id), Is.True);
                        return method.Invoke(boundary._service, args);
                    }),
                    [typeof(IHistoryService)] = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IHistoryService>((_, _) => new List<EntityHistory>()),
                    [typeof(ITrackedDownloadAlreadyImported)] = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<ITrackedDownloadAlreadyImported>((_, args) =>
                        ((List<ImportResult>)args[2]).All(result => result.Result == ImportResultType.Imported)),
                    [typeof(IEventAggregator)] = new NoOpEventAggregator()
                };
                var constructor = typeof(ManualImportService).GetConstructors().Single();
                var manual = (ManualImportService)constructor.Invoke(constructor.GetParameters().Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
                var command = new ManualImportCommand { ReplaceExistingFiles = false, ImportMode = ImportMode.Move,
                    Files = boundaries.Select(boundary => new ManualImportFile { Path = importOriginals ? boundary._source : boundary.Converter.OutputPath,
                        AuthorId = boundary._author.Id, BookId = boundary._book.Id, EditionId = boundary._edition.Id,
                        DownloadId = other == null ? null : boundary.DownloadId,
                        Quality = new QualityModel(importOriginals ? Quality.MP3 : Quality.M4B) }).ToList() };
                if (selectSources)
                {
                    command.Files.Add(new ManualImportFile { Path = _source, AuthorId = _author.Id, BookId = _book.Id,
                        EditionId = _edition.Id, Quality = new QualityModel(Quality.MP3) });
                    Assert.That(Assert.Throws<CommandFailedException>(() => manual.Execute(command)).Message, Does.Contain("not both"));
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                }
                else
                {
                    manual.Execute(command);
                    Assert.That(downloads.All(download => download.State == TrackedDownloadState.Imported), Is.True);
                }
            }

            public void Dispose()
            {
                Tracking.Complete(DownloadId);
                if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
            }
        }

        [Test]
        public void parent_manual_import_keeps_each_download_context_and_explicit_choice(
            [Values(false, true)] bool postgres, [Values(false, true)] bool importOriginals)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, database =>
            {
                using var first = new WorkerConversionBoundary(database);
                using var second = new WorkerConversionBoundary(database, 92);
                foreach (var boundary in new[] { first, second })
                {
                    boundary.FailTransfer = true;
                    Assert.That(boundary.Import().Single().Result, Is.EqualTo(ImportResultType.Skipped));
                    boundary.FailTransfer = false;
                    boundary.Mover.FailSourcePath = null;
                    boundary.Mover.TransferFilesOnDisk = true;
                }
                first.ExecuteOutputWithoutDownloadId(false, second, importOriginals);
                foreach (var boundary in new[] { first, second })
                {
                    Assert.That(boundary.MediaFiles.FilesByBook.Single().Quality.Quality, Is.EqualTo(importOriginals ? Quality.MP3 : Quality.M4B));
                    Assert.That(boundary.Jobs.GetCompletedConversion(boundary.DownloadId), Is.Null);
                    Assert.That(boundary.Converter.Calls, Is.EqualTo(1), "Manual import must not restart conversion");
                }
            });
        }

        [Test]
        public void manual_import_rejects_converted_output_with_its_sources_before_any_transfer(
            [Values(false, true)] bool postgres, [Values(false, true)] bool differentBook)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new WorkerConversionBoundary(database) { FailTransfer = true };
                Assert.That(h.Import().Single().Result, Is.EqualTo(ImportResultType.Skipped));
                var request = h.Jobs.GetCompletedConversion(h.DownloadId);
                Assert.That(request, Is.Not.Null);
                var moves = h.Mover.MoveCalls;
                var result = h.ImportOutputAndSource(differentBook);
                Assert.That(result, Has.Count.EqualTo(2));
                Assert.That(result.All(item => item.Result == ImportResultType.Skipped && item.Errors.Any(error => error.Contains("not both"))), Is.True);
                Assert.That(h.Mover.MoveCalls, Is.EqualTo(moves));
                Assert.That(h.MediaFiles.ReplaceCalls, Is.Zero);
                Assert.That(h.MediaFiles.FilesByBook, Is.Empty);
                Assert.That(h.Jobs.GetCompletedConversion(h.DownloadId), Is.Not.Null);
                Assert.That(request.Sources.All(source => File.Exists(source.Path)), Is.True);
                Assert.That(File.Exists(request.OutputPath), Is.True);
            });
        }

        [Test]
        public void successful_temporary_output_replacement_agrees_with_preview_and_preserves_old_file_on_failure(
            [Values(false, true)] bool postgres, [Values("success", "transfer-fails", "unconfirmed")] string outcome,
            [Values(false, true)] bool associateDownload)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new WorkerConversionBoundary(database) { FailTransfer = true };
                Assert.That(h.Import().Single().Result, Is.EqualTo(ImportResultType.Skipped));
                var old = h.AddOldRecording();
                var originalBytes = File.ReadAllBytes(old.Path);
                var confirmed = h.PreviewOutputReplacement();
                Assert.That(confirmed.Single().Id, Is.EqualTo(old.Id));
                h.FailTransfer = outcome == "transfer-fails";
                h.Mover.FailSourcePath = h.FailTransfer ? h.Converter.OutputPath : null;
                h.Mover.TransferFilesOnDisk = true;
                var result = h.ImportSelectedOutput(true, outcome == "unconfirmed" ? new List<ManualImportReplacementFile>() : confirmed, associateDownload);
                if (outcome == "success")
                {
                    Assert.That(result.Single().Result, Is.EqualTo(ImportResultType.Imported));
                    Assert.That(h.MediaFiles.DeletedFiles.Single().File.Id, Is.EqualTo(old.Id));
                    Assert.That(h.MediaFiles.FilesByBook.Single().Path, Is.EqualTo(h.Mover.DestinationPath));
                    Assert.That(File.Exists(h.Mover.DestinationPath), Is.True);
                    Assert.That(File.Exists(old.Path), Is.False);
                }
                else
                {
                    Assert.That(result.Single().Result, Is.EqualTo(ImportResultType.Skipped));
                    Assert.That(File.ReadAllBytes(old.Path), Is.EqualTo(originalBytes));
                    Assert.That(h.MediaFiles.FilesByBook.Single().Id, Is.EqualTo(old.Id));
                    Assert.That(h.MediaFiles.DeletedFiles, Is.Empty);
                    Assert.That(h.Jobs.GetCompletedConversion(h.DownloadId), Is.Not.Null);
                }
            });
        }

        [Test]
        public void folder_manual_import_of_successful_output_recovers_download_from_recorded_sources(
            [Values(false, true)] bool postgres, [Values(false, true)] bool selectSources)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new WorkerConversionBoundary(database) { FailTransfer = true };
                Assert.That(h.Import().Single().Result, Is.EqualTo(ImportResultType.Skipped));
                h.FailTransfer = false;
                h.Mover.FailSourcePath = null;
                h.Mover.TransferFilesOnDisk = true;
                h.ExecuteOutputWithoutDownloadId(selectSources);
                Assert.That(h.Converter.Calls, Is.EqualTo(1));
                if (selectSources)
                {
                    Assert.That(h.MediaFiles.FilesByBook, Is.Empty);
                    var request = h.Jobs.GetCompletedConversion(h.DownloadId);
                    Assert.That(request, Is.Not.Null);
                    Assert.That(File.Exists(request.OutputPath), Is.True);
                    Assert.That(request.Sources.All(source => File.Exists(source.Path)), Is.True);
                }
                else
                {
                    Assert.That(h.MediaFiles.FilesByBook.Single().Quality.Quality, Is.EqualTo(Quality.M4B));
                    Assert.That(h.Jobs.GetCompletedConversion(h.DownloadId), Is.Null);
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_manual_original_import_never_starts_a_conversion(bool postgres)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new WorkerConversionBoundary(database);
                h.Mover.DestinationPath = Path.ChangeExtension(h.Mover.DestinationPath, ".mp3");
                Assert.That(h.Import(manual: true).Single().Result, Is.EqualTo(ImportResultType.Imported));
                Assert.That(h.Converter.Calls, Is.Zero);
                Assert.That(h.MediaFiles.FilesByBook.Single().Quality.Quality, Is.EqualTo(Quality.MP3));
                Assert.That(h.Jobs.Get(h.DownloadId), Is.Null);
            });
        }

        [Test]
        public void manual_selection_after_successful_conversion_and_failed_import_is_honored(
            [Values(false, true)] bool postgres, [Values(false, true)] bool selectConverted)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new WorkerConversionBoundary(database);
                h.FailTransfer = true;
                Assert.That(h.Import().Single().Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(h.Jobs.GetCompletedConversion(h.DownloadId), Is.Not.Null);
                h.FailTransfer = false;
                h.Mover.FailSourcePath = null;
                h.Mover.DestinationPath = Path.ChangeExtension(h.Mover.DestinationPath, selectConverted ? ".m4b" : ".mp3");

                var result = selectConverted ? h.ImportSelectedOutput() : h.Import(manual: true);
                Assert.That(result.Single().Result, Is.EqualTo(ImportResultType.Imported));
                Assert.That(h.Converter.Calls, Is.EqualTo(1), "Manual import must not rerun the encoder");
                Assert.That(h.MediaFiles.FilesByBook.Single().Quality.Quality, Is.EqualTo(selectConverted ? Quality.M4B : Quality.MP3));
                Assert.That(h.Jobs.GetCompletedConversion(h.DownloadId), Is.Null, "Imported or deliberately superseded output is retired");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void repeated_stop_retry_then_manual_original_import_retires_only_the_stopped_attempt(bool postgres)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new WorkerConversionBoundary(database);
                h.Converter.BeforeReturn = _ => Assert.That(h.Jobs.Cancel(h.DownloadId), Is.True);
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (attempt > 0) h.Jobs.Reset(h.DownloadId);
                    Assert.That(h.Import().Single().Result, Is.EqualTo(ImportResultType.Skipped));
                    Assert.That(h.Jobs.Get(h.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Cancelled));
                }
                h.Mover.DestinationPath = Path.ChangeExtension(h.Mover.DestinationPath, ".mp3");
                Assert.That(h.Import(manual: true).Single().Result, Is.EqualTo(ImportResultType.Imported));
                Assert.That(h.Converter.Calls, Is.EqualTo(3));
                Assert.That(h.MediaFiles.FilesByBook.Single().Quality.Quality, Is.EqualTo(Quality.MP3));
                Assert.That(h.Jobs.Get(h.DownloadId), Is.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void readable_failed_conversion_can_be_explicitly_imported_without_becoming_automatic_success(bool postgres)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new WorkerConversionBoundary(database);
                h.Converter.Success = false;
                h.Converter.RetainOutputOnFailure = true;
                h.Converter.FailureCategory = ConversionFailureCategory.OutputInvalid;
                Assert.That(h.Import().Single().Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(h.Jobs.Get(h.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Failed));
                Assert.That(h.MediaFiles.FilesByBook, Is.Empty);
                Assert.That(File.Exists(h.Converter.OutputPath), Is.True);
                h.Mover.TransferFilesOnDisk = true;
                Assert.That(h.ImportSelectedOutput().Single().Result, Is.EqualTo(ImportResultType.Imported));
                Assert.That(h.Converter.Calls, Is.EqualTo(1));
                Assert.That(h.Jobs.Get(h.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Completed));
                Assert.That(h.MediaFiles.FilesByBook.Single().Quality.Quality, Is.EqualTo(Quality.M4B));
                Assert.That(File.Exists(h.Mover.DestinationPath), Is.True);
            });
        }

        [Test]
        public void worker_conversion_should_detach_stop_before_import_while_retaining_progress()
        {
            using var h = new WorkerConversionBoundary();
            h.Converter.BeforeReturn = _ => Assert.That(h.Tracking.Get(h.DownloadId).CanCancel, Is.True);
            var result = h.Import();
            Assert.That(result.Single().Result, Is.EqualTo(ImportResultType.Imported));
            var boundary = h.ImportBoundaries.Single();
            Assert.Multiple(() =>
            {
                Assert.That(boundary.Progress, Is.EqualTo(99m));
                Assert.That(boundary.CanCancel, Is.False);
                Assert.That(boundary.LateAccepted, Is.False);
                Assert.That(boundary.LateError, Is.Null);
                Assert.That(h.Tracking.Get(h.DownloadId), Is.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void worker_conversion_should_honor_accepted_stop_even_if_converter_returns_success(bool converterReturnsSuccess)
        {
            using var h = new WorkerConversionBoundary();
            h.Converter.Success = converterReturnsSuccess;
            h.Converter.FailureCategory = ConversionFailureCategory.Cancelled;
            h.Converter.BeforeReturn = options =>
            {
                Assert.That(h.Tracking.Cancel(h.DownloadId), Is.True);
                Assert.That(options.CancellationToken.IsCancellationRequested, Is.True);
            };
            var result = h.Import();
            Assert.Multiple(() =>
            {
                Assert.That(result.Single().Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(result.Single().Errors.Single(), Does.Contain("cancelled"));
                Assert.That(h.MediaFiles.FilesByBook, Is.Empty);
                Assert.That(h.Mover.MoveCalls + h.Mover.CopyCalls, Is.Zero);
                Assert.That(h.ImportBoundaries, Is.Empty);
                Assert.That(h.Tracking.Get(h.DownloadId).Status, Is.EqualTo("cancelled"));
                Assert.That(h.Tracking.Get(h.DownloadId).CanCancel, Is.False);
                Assert.That(h.Tracking.Cancel(h.DownloadId), Is.False);
            });
        }

        [TestCase("cancelled")]
        [TestCase("failed")]
        [TestCase("import-failed")]
        public void manual_import_after_worker_stop_or_failure_should_use_originals_or_proven_success(string firstOutcome)
        {
            using var h = new WorkerConversionBoundary();
            h.FailTransfer = firstOutcome == "import-failed";
            h.Converter.Success = firstOutcome == "import-failed";
            h.Converter.FailureCategory = firstOutcome == "cancelled" ? ConversionFailureCategory.Cancelled : ConversionFailureCategory.None;
            h.Converter.BeforeReturn = _ =>
            {
                Assert.That(h.Tracking.Get(h.DownloadId).CanCancel, Is.True);
                if (firstOutcome == "cancelled") Assert.That(h.Tracking.Cancel(h.DownloadId), Is.True);
            };
            Assert.That(h.Import().Single().Result, Is.EqualTo(ImportResultType.Skipped));
            Assert.That(h.Tracking.Get(h.DownloadId).Status, Is.EqualTo(firstOutcome == "cancelled" ? "cancelled" : "failed"));
            Assert.That(h.Tracking.Cancel(h.DownloadId), Is.False);
            h.FailTransfer = false;
            h.Converter.Success = true;
            h.Converter.FailureCategory = ConversionFailureCategory.None;
            h.Converter.BeforeReturn = _ => Assert.That(h.Tracking.Get(h.DownloadId).CanCancel, Is.True);
            Assert.That(h.Import(manual: firstOutcome != "import-failed").Single().Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(h.Converter.Calls, Is.EqualTo(1));
            Assert.That(h.MediaFiles.FilesByBook.Single().Quality.Quality, Is.EqualTo(firstOutcome == "import-failed" ? Quality.M4B : Quality.MP3));
            Assert.That(h.ImportBoundaries.All(boundary => !boundary.CanCancel && !boundary.LateAccepted && boundary.LateError == null), Is.True);
            Assert.That(h.Tracking.Get(h.DownloadId), Is.Null);
            if (firstOutcome == "import-failed") Assert.That(h.Jobs.Get(h.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Completed));
            else Assert.That(h.Jobs.Get(h.DownloadId), Is.Null, "Successful original-file import retires the obsolete stopped/failed conversion");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_retry_after_worker_failure_or_stop_restarts_canonical_conversion(bool cancelled)
        {
            using var h = new WorkerConversionBoundary();
            h.Converter.Success = false;
            h.Converter.FailureCategory = cancelled ? ConversionFailureCategory.Cancelled : ConversionFailureCategory.None;
            Assert.That(h.Import().Single().Result, Is.EqualTo(ImportResultType.Skipped));
            h.Jobs.Reset(h.DownloadId);
            h.Converter.Success = true;
            h.Converter.FailureCategory = ConversionFailureCategory.None;
            Assert.That(h.Import().Single().Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(h.Converter.Calls, Is.EqualTo(2));
            Assert.That(h.MediaFiles.FilesByBook.Single().Quality.Quality, Is.EqualTo(Quality.M4B));
        }

        [Test]
        public void manual_import_during_owned_worker_conversion_refuses_without_another_encode()
        {
            using var h = new WorkerConversionBoundary();
            List<ImportResult> nested = null;
            h.Converter.BeforeReturn = _ => nested = h.Import(manual: true);
            Assert.That(h.Import().Single().Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(nested.Single().Result, Is.EqualTo(ImportResultType.Skipped));
            Assert.That(nested.Single().Errors.Single(), Does.Contain("active conversion"));
            Assert.That(h.Converter.Calls, Is.EqualTo(1));
        }

        [Test]
        public void genuinely_new_grab_does_not_inherit_worker_cancelled_outcome()
        {
            using var h = new WorkerConversionBoundary();
            h.Converter.Success = false;
            h.Converter.FailureCategory = ConversionFailureCategory.Cancelled;
            Assert.That(h.Import().Single().Result, Is.EqualTo(ImportResultType.Skipped));
            h.Jobs.Handle(new BookGrabbedEvent(null) { DownloadId = h.DownloadId });
            h.Converter.Success = true;
            h.Converter.FailureCategory = ConversionFailureCategory.None;
            Assert.That(h.Import().Single().Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(h.Converter.Calls, Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void successful_converted_import_cleans_only_its_attempt_when_download_roots_collide(bool reuseRetained)
        {
            using var h = new WorkerConversionBoundary();
            h.Mover.TransferFilesOnDisk = true;
            h.FailTransfer = reuseRetained;
            var otherDownloadId = " " + h.DownloadId + " ";
            string otherOutput = null;
            ConversionJobRequest firstRequest = null;
            h.Converter.BeforeReturn = _ =>
            {
                firstRequest = JsonSerializer.Deserialize<ConversionJobRequest>(h.Jobs.Get(h.DownloadId).RequestJson);
                var folder = Path.Combine(firstRequest.WorkRoot, "other-download-attempt");
                otherOutput = Path.Combine(folder, "other.m4b");
                Directory.CreateDirectory(folder);
                File.WriteAllText(otherOutput, "another download owns this recording");
                File.WriteAllText(Path.Combine(folder, "conversion-artifact.json"), JsonSerializer.Serialize(new ConversionArtifactManifest
                {
                    DownloadId = otherDownloadId, OutputPath = otherOutput, EncoderSucceeded = true,
                    OutputSize = new FileInfo(otherOutput).Length, Sources = firstRequest.Sources,
                    TargetQualityId = firstRequest.TargetQualityId, AudioBitrate = firstRequest.AudioBitrate,
                    AudioChannels = firstRequest.AudioChannels, TagSignature = firstRequest.TagSignature
                }));
                h.Jobs.Enqueue(new ConversionJobRequest
                {
                    DownloadId = otherDownloadId, WorkRoot = firstRequest.WorkRoot, WorkFolder = folder,
                    OutputPath = otherOutput, Sources = firstRequest.Sources, TargetQualityId = Quality.M4B.Id, TargetQualityName = "M4B"
                });
            };
            var result = h.Import();
            if (reuseRetained)
            {
                Assert.That(result.Single().Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(File.Exists(h.Converter.OutputPath), Is.True);
                h.FailTransfer = false;
                File.SetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(otherOutput), "conversion-artifact.json"), DateTime.UtcNow.AddMinutes(1));
                result = h.Import();
            }
            Assert.That(result.Single().Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(h.Converter.Calls, Is.EqualTo(1), "A retained successful output must be reused");
            Assert.That(File.Exists(h.Mover.DestinationPath), Is.True);
            Assert.That(File.ReadAllText(otherOutput), Is.EqualTo("another download owns this recording"));
            Assert.That(h.Jobs.Get(otherDownloadId).Status, Is.EqualTo(ConversionJobStatus.Queued));
            Assert.That(Directory.Exists(firstRequest.WorkFolder), Is.False, "Only the consumed attempt should be cleaned");
            Assert.That(File.Exists(firstRequest.Sources.Single().Path), Is.True);
        }

        [Test]
        public void original_import_must_not_retire_a_newer_different_book_attempt()
        {
            using var h = new WorkerConversionBoundary();
            h.Converter.Success = false;
            Assert.That(h.Import().Single().Result, Is.EqualTo(ImportResultType.Skipped));
            var previous = JsonSerializer.Deserialize<ConversionJobRequest>(h.Jobs.Get(h.DownloadId).RequestJson);
            var destination = Path.ChangeExtension(h.Mover.DestinationPath, ".mp3");
            var root = Path.GetDirectoryName(previous.Sources.Single().Path);
            var newerSource = Path.Combine(root, "another-book.mp3");
            File.WriteAllText(newerSource, "another source");
            var newerFolder = Path.Combine(root, "OtherBook", ".chaptarr-conversions", h.DownloadId, "new-attempt");
            var newerOutput = Path.Combine(newerFolder, "other.m4b");
            var admitted = false;
            h.Mover.DestinationForFile = _ =>
            {
                if (!admitted)
                {
                    admitted = true;
                    Directory.CreateDirectory(newerFolder);
                    File.WriteAllText(newerOutput, "newer different-book artifact");
                    var info = new FileInfo(newerSource);
                    h.Jobs.Enqueue(new ConversionJobRequest
                    {
                        DownloadId = h.DownloadId, BookId = previous.BookId + 1,
                        WorkRoot = Path.GetDirectoryName(newerFolder), WorkFolder = newerFolder, OutputPath = newerOutput,
                        Sources = new List<ConversionArtifactSource> { new() { Path = newerSource, Size = info.Length, ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks } },
                        TargetQualityId = Quality.M4B.Id, TargetQualityName = "M4B"
                    });
                    h.Jobs.Fail(h.DownloadId, "newer different-book failure");
                }
                return destination;
            };
            h.Mover.TransferFilesOnDisk = true;
            Assert.That(h.Import(manual: true).Single().Result, Is.EqualTo(ImportResultType.Imported));
            Assert.That(File.Exists(destination), Is.True);
            Assert.That(File.ReadAllText(newerOutput), Is.EqualTo("newer different-book artifact"));
            Assert.That(h.Jobs.Get(h.DownloadId).WorkFolder, Is.EqualTo(newerFolder));
            Assert.That(h.Jobs.Get(h.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Failed));
            Assert.That(h.Tracking.Get(h.DownloadId).Message, Is.EqualTo("newer different-book failure"));
        }
    }
}

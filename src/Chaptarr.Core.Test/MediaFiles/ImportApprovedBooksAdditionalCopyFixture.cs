using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ImportApprovedBooksAdditionalCopyFixture
    {
        private sealed class StaticCustomFormatService : ICustomFormatService
        {
            public List<CustomFormat> Formats { get; init; } = new();

            public List<CustomFormat> All() => Formats;
            public void Update(CustomFormat customFormat) => throw new NotImplementedException();
            public CustomFormat Insert(CustomFormat customFormat) => throw new NotImplementedException();
            public CustomFormat GetById(int id) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
        }
        private sealed class StubMediaFileService : IMediaFileService
        {
            public List<BookFile> FilesByBook { get; } = new();
            public Dictionary<string, BookFile> FilesByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<(BookFile File, DeleteMediaFileReason Reason)> DeletedFiles { get; } = new();
            public List<BookFile> UpdatedFiles { get; } = new();
            public bool AllowUpdates { get; set; }
            public bool ThrowOnReplace { get; set; }
            public Action BeforeReplace { get; set; }
            public int ReplaceCalls { get; private set; }

            public BookFile Add(BookFile bookFile)
            {
                AddMany(new List<BookFile> { bookFile });
                return bookFile;
            }
            public void AddMany(List<BookFile> bookFiles)
            {
                foreach (var bookFile in bookFiles)
                {
                    if (bookFile.Id <= 0)
                    {
                        bookFile.Id = FilesByBook.Select(f => f.Id).DefaultIfEmpty(1000).Max() + 1;
                    }

                    FilesByBook.Add(bookFile);
                    if (!string.IsNullOrWhiteSpace(bookFile.Path))
                    {
                        FilesByPath[bookFile.Path] = bookFile;
                    }
                }
            }
            public void Update(BookFile bookFile)
            {
                if (!AllowUpdates)
                {
                    throw new NotImplementedException();
                }

                UpdatedFiles.Add(bookFile);
                FilesByPath[bookFile.Path] = bookFile;

                var existingIndex = FilesByBook.FindIndex(file => file.Id == bookFile.Id);
                if (existingIndex >= 0)
                {
                    FilesByBook[existingIndex] = bookFile;
                }
            }

            public void Update(List<BookFile> bookFiles)
            {
                foreach (var bookFile in bookFiles ?? new List<BookFile>())
                {
                    Update(bookFile);
                }
            }
            public void ReplaceMany(List<BookFile> bookFiles, List<BookFile> replacedFiles, DeleteMediaFileReason reason)
            {
                ReplaceCalls++;
                BeforeReplace?.Invoke();
                if (ThrowOnReplace)
                {
                    throw new InvalidOperationException("synthetic atomic replacement failure");
                }

                foreach (var replacedFile in replacedFiles ?? new List<BookFile>())
                {
                    Delete(replacedFile, reason);
                }

                AddMany(bookFiles ?? new List<BookFile>());
            }
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason)
            {
                DeletedFiles.Add((bookFile, reason));
                FilesByPath.Remove(bookFile.Path);
                FilesByBook.Remove(bookFile);
            }
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => FilesByBook.Where(f => f.Edition?.BookId == bookId || f.EditionId == bookId || f.Edition?.Book?.Id == bookId).ToList();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles() => throw new NotImplementedException();
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> path) => throw new NotImplementedException();
            public BookFile GetFileWithPath(string path) => FilesByPath.TryGetValue(path, out var file) ? file : null;
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private sealed class StubMoveBookFiles : IMoveBookFiles
        {
            public string DestinationPath { get; set; }
            public int MoveCalls { get; private set; }
            public int CopyCalls { get; private set; }
            public int PreviewCalls { get; private set; }
            public bool TransferFilesOnDisk { get; set; }

            public BookFile MoveBookFile(BookFile bookFile, Author author, RenameBatchContext renameBatchContext = null)
            {
                MoveCalls++;
                bookFile.Path = DestinationPath ?? bookFile.Path;
                return bookFile;
            }

            public BookFile MoveBookFile(BookFile bookFile, LocalBook localBook)
            {
                MoveCalls++;
                bookFile.Path = DestinationPath ?? bookFile.Path;
                TransferFile(localBook.Path, bookFile.Path, copy: false);
                return bookFile;
            }

            public BookFile CopyBookFile(BookFile bookFile, LocalBook localBook)
            {
                CopyCalls++;
                bookFile.Path = DestinationPath ?? bookFile.Path;
                TransferFile(localBook.Path, bookFile.Path, copy: true);
                return bookFile;
            }

            public string GetImportDestinationPath(BookFile bookFile, LocalBook localBook)
            {
                PreviewCalls++;
                return DestinationPath;
            }

            private void TransferFile(string sourcePath, string destinationPath, bool copy)
            {
                if (!TransferFilesOnDisk || string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                if (copy)
                {
                    File.Copy(sourcePath, destinationPath, overwrite: true);
                }
                else
                {
                    File.Move(sourcePath, destinationPath, overwrite: true);
                }
            }
        }

        private sealed class StubMetadataTagService : IMetadataTagService
        {
            public List<(BookFile File, bool NewDownload)> Writes { get; } = new();
            public bool ThrowOnWrite { get; set; }

            public Dictionary<string, List<string>> ReadAllTags(System.IO.Abstractions.IFileInfo file) => new();
            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(System.IO.Abstractions.IFileInfo file) => (new Dictionary<string, List<string>>(), null);
            public string ReadAllTagsAsJson(System.IO.Abstractions.IFileInfo file) => "{}";
            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false)
            {
                Writes.Add((trackfile, newDownload));
                if (ThrowOnWrite)
                {
                    throw new InvalidOperationException("synthetic tag write failure");
                }
            }
            public void SyncTags(List<Edition> books) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int authorId) => throw new NotImplementedException();
        }

        private sealed class FailingM4bConversionService : IM4bConversionService
        {
            public int ConvertCalls { get; private set; }
            public string[] LastInputFiles { get; private set; }
            public string LastOutputFile { get; private set; }
            public ConversionOptions LastOptions { get; private set; }

            public bool CanConvert(string[] inputFiles) => true;

            public ConversionEstimate EstimateConversion(string[] inputFiles) => new ConversionEstimate
            {
                CanConvert = true,
                InputFileCount = inputFiles?.Length ?? 0,
                TotalInputSize = 1024,
                EstimatedOutputSize = 1024
            };

            public ConversionResult ConvertToM4b(string[] inputFiles, string outputFile, ConversionOptions options = null)
            {
                ConvertCalls++;
                LastInputFiles = inputFiles;
                LastOutputFile = outputFile;
                LastOptions = options;
                return new ConversionResult
                {
                    Success = false,
                    ErrorMessage = "synthetic conversion failure"
                };
            }
        }

        private sealed class SuccessfulM4bConversionService : IM4bConversionService
        {
            public int ConvertCalls { get; private set; }
            public string[] LastInputFiles { get; private set; }
            public string LastOutputFile { get; private set; }
            public ConversionOptions LastOptions { get; private set; }

            public bool CanConvert(string[] inputFiles) => true;

            public ConversionEstimate EstimateConversion(string[] inputFiles) => new ConversionEstimate
            {
                CanConvert = true,
                InputFileCount = inputFiles?.Length ?? 0,
                TotalInputSize = 1024,
                EstimatedOutputSize = 1024
            };

            public ConversionResult ConvertToM4b(string[] inputFiles, string outputFile, ConversionOptions options = null)
            {
                ConvertCalls++;
                LastInputFiles = inputFiles;
                LastOutputFile = outputFile;
                LastOptions = options;
                Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
                File.WriteAllText(outputFile, "converted m4b");
                return new ConversionResult
                {
                    Success = true,
                    OutputFile = outputFile,
                    OutputFileSize = new FileInfo(outputFile).Length
                };
            }
        }

        private sealed class StubMediaInfoExtractor : IMediaInfoExtractor
        {
            public Dictionary<string, TimeSpan> Durations { get; } = new(StringComparer.OrdinalIgnoreCase);

            public MediaInfoModel ExtractMediaInfo(string filePath) => new();

            public TimeSpan GetDuration(string filePath)
            {
                return Durations.TryGetValue(filePath, out var duration) ? duration : TimeSpan.Zero;
            }

            public bool IsAudiobookFile(string filePath, MediaInfoModel mediaInfo = null) => true;
        }

        private sealed class NoOpEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, NzbDrone.Common.Messaging.IEvent
            {
            }
        }

        private sealed class StubRecycleBinProvider : IRecycleBinProvider
        {
            public readonly List<string> DeletedFiles = new();

            public void DeleteFolder(string path)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }

            public void DeleteFile(string path, string subfolder = "")
            {
                DeletedFiles.Add(path);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            public void Empty()
            {
            }

            public void Cleanup()
            {
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            public List<Edition> Editions { get; set; } = new();
            public List<Edition> InsertedEditions { get; } = new();
            public List<int> MonitoredEditionIds { get; } = new();
            private int _nextId = 9000;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEditionsByBook) &&
                    args?.Length == 1 &&
                    args[0] is int bookId)
                {
                    return Editions.Where(e => e.BookId == bookId).ToList();
                }

                if (targetMethod?.Name == nameof(IEditionService.GetEdition) &&
                    args?.Length == 1 &&
                    args[0] is int editionId)
                {
                    return Editions.FirstOrDefault(e => e.Id == editionId);
                }

                if (targetMethod?.Name == nameof(IEditionService.InsertMany) &&
                    args?.Length == 1 &&
                    args[0] is List<Edition> editions)
                {
                    foreach (var edition in editions)
                    {
                        if (edition.Id <= 0)
                        {
                            edition.Id = _nextId++;
                        }

                        InsertedEditions.Add(edition);
                        Editions.Add(edition);
                    }

                    return null;
                }

                if (targetMethod?.Name == nameof(IEditionService.SetMonitored) &&
                    args?.Length == 2 &&
                    args[0] is Edition monitoredEdition)
                {
                    foreach (var edition in Editions.Where(e => e.BookId == monitoredEdition.BookId))
                    {
                        edition.Monitored = edition.Id == monitoredEdition.Id;
                    }

                    MonitoredEditionIds.Add(monitoredEdition.Id);
                    return Editions.Where(e => e.BookId == monitoredEdition.BookId).ToList();
                }

                throw new NotImplementedException($"Unexpected call to IEditionService.{targetMethod?.Name}");
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> ExistingBooks { get; set; } = new();
            public List<Book> InsertedBooks { get; } = new();
            private int _nextId = 8000;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthorId) &&
                    args?.Length == 1 &&
                    args[0] is int authorId)
                {
                    return ExistingBooks.Where(b => b.AuthorId == authorId).ToList();
                }
                if (targetMethod?.Name == nameof(IBookService.GetBook) &&
                    args?.Length == 1 &&
                    args[0] is int bookId)
                {
                    return ExistingBooks.FirstOrDefault(book => book.Id == bookId);
                }

                if (targetMethod?.Name == nameof(IBookService.UpdateBook) &&
                    args?.Length == 1 &&
                    args[0] is Book updatedBook)
                {
                    var index = ExistingBooks.FindIndex(book => book.Id == updatedBook.Id);
                    if (index >= 0)
                    {
                        ExistingBooks[index] = updatedBook;
                    }

                    return updatedBook;
                }

                if (targetMethod?.Name == nameof(IBookService.InsertMany) &&
                    args?.Length == 1 &&
                    args[0] is List<Book> books)
                {
                    foreach (var book in books)
                    {
                        if (book.Id <= 0)
                        {
                            book.Id = _nextId++;
                        }

                        InsertedBooks.Add(book);
                        ExistingBooks.Add(book);
                    }

                    return null;
                }

                throw new NotImplementedException($"Unexpected call to IBookService.{targetMethod?.Name}");
            }
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Unexpected call to {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public long? AvailableSpace { get; set; }
            public string LastPath { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.GetAvailableSpace))
                {
                    LastPath = (string)args[0];
                    return AvailableSpace;
                }

                if (targetMethod?.Name == nameof(IDiskProvider.GetFileInfo))
                {
                    return new System.IO.Abstractions.FileSystem().FileInfo.FromFileName((string)args[0]);
                }

                throw new NotImplementedException($"Unexpected call to IDiskProvider.{targetMethod?.Name}");
            }
        }

        private sealed class StubCoverMapper : IMapCoversToLocal
        {
            public string CoverPath { get; set; }
            public int EnsureBookCoversCalls { get; private set; }

            public void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IEnumerable<NzbDrone.Core.MediaCover.MediaCover> covers, string selectedAuthorImageHash = null)
            {
            }

            public string GetCoverPath(int entityId, MediaCoverEntity coverEntity, MediaCoverTypes coverType, string extension, int? height = null)
            {
                return EnsureBookCoversCalls > 0 ? CoverPath : null;
            }

            public void EnsureAuthorCovers(Author author)
            {
            }

            public void EnsureBookCovers(Book book)
            {
                EnsureBookCoversCalls++;
            }

            public System.Threading.Tasks.Task<EnsureImageResult> EnsureAuthorImage(Author author, NzbDrone.Core.MediaCover.MediaCover cover)
            {
                throw new NotImplementedException();
            }
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            public bool SkipFreeSpaceCheckWhenImporting { get; set; }
            public int MinimumFreeSpaceWhenImporting { get; set; } = 100;
            public int AudiobookConversionConcurrentConversions { get; set; } = 1;
            public int AudiobookConversionMaxCpuThreads { get; set; } = 4;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_SkipFreeSpaceCheckWhenImporting")
                {
                    return SkipFreeSpaceCheckWhenImporting;
                }

                if (targetMethod?.Name == "get_MinimumFreeSpaceWhenImporting")
                {
                    return MinimumFreeSpaceWhenImporting;
                }

                if (targetMethod?.Name == "get_AudiobookConversionConcurrentConversions")
                {
                    return AudiobookConversionConcurrentConversions;
                }

                if (targetMethod?.Name == "get_AudiobookConversionMaxCpuThreads")
                {
                    return AudiobookConversionMaxCpuThreads;
                }

                if (targetMethod?.Name == "get_AudiobookConversionMaxBitrate")
                {
                    return 64;
                }

                if (targetMethod?.Name == "get_AudiobookConversionNoUpscale")
                {
                    return false;
                }

                if (targetMethod?.Name == "get_AudiobookConversionAudioChannels")
                {
                    return "keep";
                }

                if (targetMethod?.Name == "get_AudiobookConversionTagMode")
                {
                    return "preserve";
                }

                throw new NotImplementedException($"Unexpected call to IConfigService.{targetMethod?.Name}");
            }
        }

        private sealed class RecordingConversionJobService : IConversionJobService
        {
            public int WorkerConcurrency => 1;
            public ConversionJobRequest Request { get; private set; }
            public ConversionJob Job { get; private set; }
            public string CompletedDownloadId { get; private set; }
            public string FailedDownloadId { get; private set; }

            public ConversionJob Get(string downloadId) => Job;

            public ConversionJob Enqueue(ConversionJobRequest request)
            {
                Request = request;
                Job = new ConversionJob
                {
                    Id = 1,
                    DownloadId = request.DownloadId,
                    Status = ConversionJobStatus.Queued,
                    WorkRoot = request.WorkRoot,
                    WorkFolder = request.WorkFolder,
                    OutputPath = request.OutputPath
                };
                return Job;
            }

            public void SetJob(string downloadId, ConversionJobStatus status, string workRoot = null, string workFolder = null, string outputPath = null)
            {
                Job = new ConversionJob
                {
                    Id = 1,
                    DownloadId = downloadId,
                    Status = status,
                    WorkRoot = workRoot,
                    WorkFolder = workFolder,
                    OutputPath = outputPath
                };
            }

            public bool IsActive(string downloadId) => Job != null &&
                                                       (Job.Status == ConversionJobStatus.Queued ||
                                                        Job.Status == ConversionJobStatus.Converting ||
                                                        Job.Status == ConversionJobStatus.ReadyToImport ||
                                                        Job.Status == ConversionJobStatus.Cancelling);
            public bool Cancel(string downloadId) => false;

            public void Complete(string downloadId)
            {
                CompletedDownloadId = downloadId;
                Job.Status = ConversionJobStatus.Completed;
            }

            public void Fail(string downloadId, string error)
            {
                FailedDownloadId = downloadId;
                Job.Status = ConversionJobStatus.Failed;
                Job.Error = error;
            }
            public void Reset(string downloadId) => Job = null;
        }

        private static T Proxy<T>() where T : class
        {
            return DispatchProxy.Create<T, ThrowingProxy<T>>();
        }

        private static IConfigService CreateConfigService(int concurrentConversions = 1, int maxCpuThreads = 4)
        {
            var proxy = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            var config = (ConfigServiceProxy)(object)proxy;
            config.AudiobookConversionConcurrentConversions = concurrentConversions;
            config.AudiobookConversionMaxCpuThreads = maxCpuThreads;
            return proxy;
        }

        private static IEditionService CreateEditionService(List<Edition> editions)
        {
            var proxy = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)proxy).Editions = editions;
            return proxy;
        }

        private static (IBookService Service, BookServiceProxy Proxy) CreateBookService(List<Book> books)
        {
            var proxy = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookProxy = (BookServiceProxy)(object)proxy;
            bookProxy.ExistingBooks = books;
            return (proxy, bookProxy);
        }

        private static (QualityProfile QualityProfile, Author Author, Book Book, Edition Edition) CreateAudiobookConversionGraph(int seed = 1)
        {
            var qualityProfile = new QualityProfile
            {
                Id = 100 + seed,
                Name = "Audiobooks",
                ProfileType = ProfileType.Audiobook,
                ConvertToQualityId = Quality.M4B.Id,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = true, Quality = Quality.MP3 },
                    new() { Allowed = true, Quality = Quality.M4B }
                }
            };

            var author = new Author
            {
                Id = 200 + seed,
                Name = "David Archer",
                AudiobookRootFolderPath = "/audiobooks",
                AudiobookPath = "/audiobooks/David Archer",
                AudiobookQualityProfileId = qualityProfile.Id,
                AudiobookQualityProfile = qualityProfile
            };

            var book = new Book
            {
                Id = 300 + seed,
                Title = "Black Sheep",
                TitleSlug = "black-sheep",
                MediaType = BookMediaType.Audiobook,
                Author = author,
                AuthorId = author.Id
            };

            var edition = new Edition
            {
                Id = 400 + seed,
                BookId = book.Id,
                Book = book,
                Title = "Black Sheep",
                Format = "audiobook",
                ReadingFormatId = 2,
                NarratorNames = new List<string> { "Adam Verner" },
                ForeignEditionId = $"az:B0746T3XVR-audiobook-{seed}"
            };

            return (qualityProfile, author, book, edition);
        }

        [Test]
        public void manual_import_should_keep_gui_pinned_narratorless_edition()
        {
            AssertSelectedAudiobookEditionRemainsFinal(anyEditionOk: false, isManualImport: true, selectedEditionInitiallyMonitored: true);
        }

        [Test]
        public void automatic_import_should_keep_matcher_selected_narratorless_edition()
        {
            AssertSelectedAudiobookEditionRemainsFinal(anyEditionOk: true, isManualImport: false, selectedEditionInitiallyMonitored: false);
        }

        [Test]
        public void automatic_import_should_accept_matcher_selected_narrator_edition()
        {
            AssertSelectedAudiobookEditionRemainsFinal(
                anyEditionOk: true,
                isManualImport: false,
                selectedEditionInitiallyMonitored: false,
                selectedNarrator: "Proven Narrator",
                unrelatedNarrator: null);
        }

        [Test]
        public void automatic_import_should_persist_tagless_tracked_release_evidence_for_future_scoring()
        {
            AssertSelectedAudiobookEditionRemainsFinal(
                anyEditionOk: true,
                isManualImport: false,
                selectedEditionInitiallyMonitored: false,
                selectedNarrator: "James Marsters",
                includeTrackedReleaseEvidence: true);
        }

        [Test]
        public void automatic_import_should_copy_a_match_that_conflicts_with_the_gui_pinned_edition()
        {
            AssertSelectedAudiobookEditionRemainsFinal(
                anyEditionOk: false,
                isManualImport: false,
                selectedEditionInitiallyMonitored: false,
                selectedNarrator: "Proven Narrator",
                unrelatedNarrator: "Pinned Narrator",
                expectSeparateCopy: true);
        }

        private static void AssertSelectedAudiobookEditionRemainsFinal(
            bool anyEditionOk,
            bool isManualImport,
            bool selectedEditionInitiallyMonitored,
            string selectedNarrator = null,
            string unrelatedNarrator = "Unrelated Narrator",
            bool expectSeparateCopy = false,
            bool includeTrackedReleaseEvidence = false)
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"edition-finality-{Guid.NewGuid():N}");
            var sourcePath = Path.Combine(tempDir, "incoming.m4b");
            var destinationPath = Path.Combine(tempDir, "Example Book.m4b");
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(sourcePath, "fake m4b");

            try
            {
                var qualityProfile = new QualityProfile
                {
                    Id = 120,
                    Name = "Audiobooks",
                    ProfileType = ProfileType.Audiobook,
                    Items = new List<QualityProfileQualityItem>
                    {
                        new() { Allowed = true, Quality = Quality.M4B }
                    }
                };

                var author = new Author
                {
                    Id = 220,
                    Name = "Example Author",
                    AudiobookRootFolderPath = "/audiobooks",
                    AudiobookPath = "/audiobooks/Example Author",
                    AudiobookQualityProfileId = qualityProfile.Id,
                    AudiobookQualityProfile = qualityProfile
                };

                var book = new Book
                {
                    Id = 320,
                    Title = "Example Book",
                    TitleSlug = "example-book",
                    MediaType = BookMediaType.Audiobook,
                    AnyEditionOk = anyEditionOk,
                    Author = author,
                    AuthorId = author.Id
                };

                var selectedEdition = new Edition
                {
                    Id = 420,
                    BookId = book.Id,
                    Book = book,
                    Title = "Example Book",
                    Format = "audiobook",
                    ReadingFormatId = 2,
                    Monitored = selectedEditionInitiallyMonitored,
                    ManualAdd = false,
                    NarratorNames = selectedNarrator == null ? null : new List<string> { selectedNarrator },
                    ForeignEditionId = "hc:edition:420"
                };

                var unrelatedNarratorEdition = new Edition
                {
                    Id = 421,
                    BookId = book.Id,
                    Book = book,
                    Title = "Example Book",
                    Format = "audiobook",
                    ReadingFormatId = 2,
                    Monitored = !selectedEditionInitiallyMonitored,
                    ManualAdd = false,
                    NarratorNames = unrelatedNarrator == null ? null : new List<string> { unrelatedNarrator },
                    Ratings = new Ratings { Votes = 10000, Value = 5.0m },
                    ForeignEditionId = "hc:edition:421"
                };

                book.Editions = new List<Edition> { selectedEdition, unrelatedNarratorEdition };

                var mediaFileService = new StubMediaFileService();
                var (bookService, bookProxy) = CreateBookService(new List<Book> { book });
                var editionService = CreateEditionService(new List<Edition> { selectedEdition, unrelatedNarratorEdition });
                var editionProxy = (EditionServiceProxy)(object)editionService;
                var service = new ImportApprovedBooks(
                    mediaFileService,
                    new StubMetadataTagService(),
                    new StubMediaInfoExtractor(),
                    Proxy<IAuthorService>(),
                    bookService,
                    editionService,
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    new StubMoveBookFiles { DestinationPath = destinationPath },
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    Proxy<IM4bConversionService>(),
                    LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var localBook = new LocalBook
                {
                    Path = sourcePath,
                    Book = book,
                    Author = author,
                    Edition = selectedEdition,
                    Quality = new QualityModel { Quality = Quality.M4B, Revision = new Revision() },
                    IsManualImport = isManualImport,
                    Size = new FileInfo(sourcePath).Length,
                    Modified = File.GetLastWriteTimeUtc(sourcePath),
                    DownloadClientBookInfo = includeTrackedReleaseEvidence ? new ParsedBookInfo { ReleaseTitle = "Example Release" } : null,
                    SceneName = includeTrackedReleaseEvidence ? "Example.Author.Example.Book.James.Marsters" : null,
                    ReleaseGroup = includeTrackedReleaseEvidence ? "NarratorGroup" : null,
                    IndexerFlags = includeTrackedReleaseEvidence ? IndexerFlags.Freeleech : 0,
                    Narrator = includeTrackedReleaseEvidence ? "James Marsters" : null,
                    IsGraphicAudio = includeTrackedReleaseEvidence,
                    AudioProductionType = includeTrackedReleaseEvidence ? "Full Cast" : null
                };

                var results = service.Import(
                    new List<ImportDecision<LocalBook>> { new(localBook) },
                    replaceExisting: false,
                    downloadClientItem: null,
                    importMode: ImportMode.Copy,
                    cancellationToken: CancellationToken.None);

                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Imported));
                var importedFile = mediaFileService.FilesByBook.Single();
                if (expectSeparateCopy)
                {
                    var copyBook = bookProxy.InsertedBooks.Single();
                    var copyEdition = editionProxy.InsertedEditions.Single();
                    Assert.That(copyBook.AnyEditionOk, Is.True);
                    Assert.That(copyEdition.BookId, Is.EqualTo(copyBook.Id));
                    Assert.That(copyEdition.ForeignEditionId, Is.EqualTo(selectedEdition.ForeignEditionId));
                    Assert.That(importedFile.EditionId, Is.EqualTo(copyEdition.Id));
                    Assert.That(importedFile.Edition, Is.SameAs(copyEdition));
                }
                else
                {
                    Assert.That(importedFile.EditionId, Is.EqualTo(selectedEdition.Id));
                    Assert.That(importedFile.Edition, Is.SameAs(selectedEdition));
                }
                Assert.That(importedFile.Narrator, Is.EqualTo(selectedNarrator));
                if (includeTrackedReleaseEvidence)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(importedFile.SceneName, Is.EqualTo("Example.Author.Example.Book.James.Marsters"));
                        Assert.That(importedFile.ReleaseGroup, Is.EqualTo("NarratorGroup"));
                        Assert.That(importedFile.IndexerFlags, Is.EqualTo(IndexerFlags.Freeleech));
                        Assert.That(importedFile.IsGraphicAudio, Is.True);
                        Assert.That(importedFile.AudioProductionType, Is.EqualTo("Full Cast"));
                    });

                    var freeleech = new CustomFormat
                    {
                        Id = 700,
                        Name = "Freeleech",
                        Specifications = new List<ICustomFormatSpecification>
                        {
                            new IndexerFlagSpecification
                            {
                                Name = "Freeleech",
                                Value = (int)IndexerFlags.Freeleech
                            }
                        }
                    };
                    qualityProfile.PreferCustomFormatsOverQuality = true;
                    qualityProfile.FormatItems = new List<NzbDrone.Core.Profiles.ProfileFormatItem>
                    {
                        new()
                        {
                            Format = freeleech,
                            Score = 50
                        }
                    };

                    var formatCalculator = new CustomFormatCalculationService(
                        new StaticCustomFormatService { Formats = new List<CustomFormat> { freeleech } },
                        LogManager.GetLogger("ImportApprovedBooksScoreStability"));
                    var onDiskFormats = formatCalculator.ParseCustomFormat(importedFile);
                    var equivalentFutureRelease = new RemoteBook
                    {
                        Author = author,
                        Books = new List<Book> { book },
                        Release = new ReleaseInfo
                        {
                            Title = importedFile.SceneName,
                            IndexerFlags = IndexerFlags.Freeleech
                        },
                        ParsedBookInfo = new ParsedBookInfo
                        {
                            Quality = new QualityModel(Quality.M4B),
                            ReleaseTitle = importedFile.SceneName
                        }
                    };
                    var futureFormats = formatCalculator.ParseCustomFormat(equivalentFutureRelease, importedFile.Size);
                    var upgradable = new UpgradableSpecification(ConfigServiceTestProxy.Create(), LogManager.GetCurrentClassLogger());

                    Assert.That(onDiskFormats, Does.Contain(freeleech));
                    Assert.That(futureFormats, Does.Contain(freeleech));
                    Assert.That(upgradable.IsUpgradable(
                        qualityProfile,
                        importedFile.Quality,
                        onDiskFormats,
                        equivalentFutureRelease.ParsedBookInfo.Quality,
                        futureFormats), Is.False);
                }

                Assert.That(importedFile.MatchProvenance?.ConflictingSignals?.Any(signal => signal.Type == "edition_retarget") == true,
                    Is.False);
                Assert.That(book.AnyEditionOk, Is.EqualTo(anyEditionOk));
                Assert.That(selectedEdition.Monitored, Is.EqualTo(!expectSeparateCopy));
                Assert.That(unrelatedNarratorEdition.Monitored, Is.EqualTo(expectSeparateCopy));
                Assert.That(editionProxy.MonitoredEditionIds, Does.Not.Contain(unrelatedNarratorEdition.Id));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void clone_creation_should_stamp_copy_slug_unit_key_hash_and_provider_aliases()
        {
            var author = new Author
            {
                Id = 42,
                Name = "Joe Abercrombie",
                EbookRootFolderPath = "/ebooks",
                EbookPath = "/ebooks/Joe Abercrombie"
            };

            var book = new Book
            {
                Id = 5047,
                Title = "Best Served Cold",
                TitleSlug = "best-served-cold",
                CleanTitle = "bestservedcold",
                MediaType = BookMediaType.Ebook,
                Author = author,
                AuthorId = author.Id,
                BaseBookId = "hc:242555",
                RemoteProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "hc:242555",
                    "gr:231260754"
                }
            };

            var edition = new Edition
            {
                Id = 30425,
                BookId = book.Id,
                Book = book,
                Title = "Best Served Cold",
                IsEbook = true,
                ReadingFormatId = 3,
                ForeignEditionId = "hc:edition:30425"
            };

            var (bookService, bookProxy) = CreateBookService(new List<Book> { book });
            var editionService = CreateEditionService(new List<Edition> { edition });
            var service = new ImportApprovedBooks(
                new StubMediaFileService(),
                new StubMetadataTagService(),
                Proxy<IMediaInfoExtractor>(),
                Proxy<IAuthorService>(),
                bookService,
                editionService,
                Proxy<IRecycleBinProvider>(),
                Proxy<IExtraService>(),
                new StubMoveBookFiles(),
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                new NoOpEventAggregator(),
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

            var localBook = new LocalBook
            {
                Path = "/downloads/Best Served Cold/Best Served Cold.epub",
                Book = book,
                Author = author,
                Edition = edition,
                Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() },
                Part = 1,
                PartCount = 1
            };

            var method = typeof(ImportApprovedBooks).GetMethod("CreateNewBookInstanceForBatch", BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(service, new object[] { new ImportDecision<LocalBook>(localBook), book, author, true });

            var insertedBook = bookProxy.InsertedBooks.Single();
            Assert.That(insertedBook.TitleSlug, Does.StartWith("best-served-cold_copy_"));
            Assert.That(insertedBook.UnitKeyHash, Is.Not.Null.And.Not.Empty);
            Assert.That(insertedBook.UnitKeyHash, Has.Length.EqualTo(64));
            Assert.That(insertedBook.MediaType, Is.EqualTo(BookMediaType.Ebook));
            Assert.That(insertedBook.RemoteProviderIds, Is.EquivalentTo(book.RemoteProviderIds));
            Assert.That(insertedBook.RemoteProviderIds, Is.Not.SameAs(book.RemoteProviderIds));
        }

        [TestCase(true, false, false, TestName = "manual_replace_should_commit_when_optional_tag_write_fails")]
        [TestCase(false, true, false, TestName = "manual_replace_should_restore_old_files_and_rows_when_atomic_persistence_fails")]
        [TestCase(false, false, true, TestName = "interactive_grab_should_replace_despite_upgrade_profile_and_lower_revision")]
        public void explicit_user_replace_should_be_failure_safe_across_tagging_and_persistence(bool throwOnTagWrite, bool throwOnReplace, bool downloadForced)
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"manual-replace-{Guid.NewGuid():N}");
            var sourcePath = Path.Combine(tempDir, "incoming.epub");
            var destinationPath = Path.Combine(tempDir, "Best Served Cold.epub");
            var oldEpub = Path.Combine(tempDir, "old.epub");
            var oldMobi = Path.Combine(tempDir, "old.mobi");
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(sourcePath, "incoming");
            File.WriteAllText(oldEpub, "old epub");
            File.WriteAllText(oldMobi, "old mobi");

            try
            {
                var qualityProfile = new QualityProfile
                {
                    Id = 1,
                    Name = "Ebooks",
                    ProfileType = ProfileType.Ebook,
                    UpgradeAllowed = false,
                    Items = new List<QualityProfileQualityItem>
                    {
                        new() { Allowed = true, Quality = Quality.EPUB },
                        new() { Allowed = true, Quality = Quality.MOBI }
                    }
                };

                var author = new Author
                {
                    Id = 42,
                    Name = "Joe Abercrombie",
                    EbookRootFolderPath = "/ebooks",
                    EbookPath = "/ebooks/Joe Abercrombie",
                    EbookQualityProfileId = qualityProfile.Id,
                    EbookQualityProfile = qualityProfile
                };

                var book = new Book
                {
                    Id = 5792,
                    Title = "Best Served Cold",
                    TitleSlug = "best-served-cold_2",
                    CleanTitle = "bestservedcold",
                    MediaType = BookMediaType.Ebook,
                    Author = author,
                    AuthorId = author.Id
                };

                var existingEdition = new Edition
                {
                    Id = 15629,
                    BookId = book.Id,
                    Book = book,
                    Title = "Best Served Cold",
                    IsEbook = true,
                    ReadingFormatId = 3
                };

                var incomingEdition = new Edition
                {
                    Id = 15632,
                    BookId = book.Id,
                    Book = book,
                    Title = "Best Served Cold",
                    IsEbook = true,
                    ReadingFormatId = 3
                };

                var mediaFileService = new StubMediaFileService { ThrowOnReplace = throwOnReplace };
                mediaFileService.FilesByBook.AddRange(new[]
                {
                    new BookFile
                    {
                        Id = 1492,
                        Path = oldEpub,
                        EditionId = existingEdition.Id,
                        Edition = existingEdition,
                        Quality = new QualityModel { Quality = Quality.EPUB, Revision = downloadForced ? new Revision(2) : new Revision() }
                    },
                    new BookFile
                    {
                        Id = 1493,
                        Path = oldMobi,
                        EditionId = existingEdition.Id,
                        Edition = existingEdition,
                        Quality = new QualityModel { Quality = Quality.MOBI, Revision = downloadForced ? new Revision(2) : new Revision() }
                    }
                });

	                var recycleBin = new StubRecycleBinProvider();
	                var metadataTagService = new StubMetadataTagService { ThrowOnWrite = throwOnTagWrite };
	                var mover = new StubMoveBookFiles
	                {
	                    DestinationPath = destinationPath,
	                    TransferFilesOnDisk = true
	                };
	                var service = new ImportApprovedBooks(
	                    mediaFileService,
	                    metadataTagService,
                    new StubMediaInfoExtractor(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { existingEdition, incomingEdition }),
                    recycleBin,
                    Proxy<IExtraService>(),
	                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    Proxy<IM4bConversionService>(),
                    LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var localBook = new LocalBook
                {
                    Path = sourcePath,
                    Book = book,
                    Author = author,
                    Edition = incomingEdition,
                    Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() },
                    IsManualImport = !downloadForced,
                    Size = new FileInfo(sourcePath).Length,
                    Modified = File.GetLastWriteTimeUtc(sourcePath)
                };

                if (throwOnReplace)
                {
                    Assert.Throws<InvalidOperationException>(() => service.Import(
                        new List<ImportDecision<LocalBook>> { new(localBook) },
                        replaceExisting: true,
                        downloadClientItem: downloadForced ? new DownloadClientItem { DownloadId = "interactive-grab", DownloadForced = true } : null,
                        importMode: ImportMode.Copy,
                        cancellationToken: CancellationToken.None));

                    Assert.That(mediaFileService.ReplaceCalls, Is.EqualTo(1));
                    Assert.That(mediaFileService.DeletedFiles, Is.Empty);
                    Assert.That(mediaFileService.FilesByBook.Select(f => f.Id), Is.EquivalentTo(new[] { 1492, 1493 }));
                    Assert.That(File.Exists(oldEpub), Is.True);
                    Assert.That(File.Exists(oldMobi), Is.True);
                    Assert.That(File.Exists(destinationPath), Is.False);
                    Assert.That(File.Exists(sourcePath), Is.True);
                    Assert.That(recycleBin.DeletedFiles, Is.Empty);
                    Assert.That(metadataTagService.Writes, Is.Empty);
                }
                else
                {
                    var results = service.Import(
                        new List<ImportDecision<LocalBook>> { new(localBook) },
                        replaceExisting: true,
                        downloadClientItem: downloadForced ? new DownloadClientItem { DownloadId = "interactive-grab", DownloadForced = true } : null,
                        importMode: ImportMode.Copy,
                        cancellationToken: CancellationToken.None);

                    Assert.That(results, Has.Count.EqualTo(1));
                    Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Imported));
                    Assert.That(mediaFileService.ReplaceCalls, Is.EqualTo(1));
                    Assert.That(mediaFileService.DeletedFiles.Select(d => d.File.Id), Is.EquivalentTo(new[] { 1492, 1493 }));
                    Assert.That(mediaFileService.FilesByBook.Select(f => f.Id), Does.Not.Contain(1492));
                    Assert.That(mediaFileService.FilesByBook.Select(f => f.Id), Does.Not.Contain(1493));
	                    Assert.That(mediaFileService.FilesByBook.Single().EditionId, Is.EqualTo(incomingEdition.Id));
	                    Assert.That(mediaFileService.FilesByBook.Single().Path, Is.EqualTo(destinationPath));
                    Assert.That(File.Exists(destinationPath), Is.True);
	                    Assert.That(recycleBin.DeletedFiles, Has.Count.EqualTo(2));
	                    Assert.That(metadataTagService.Writes.Single().NewDownload, Is.True);
                }
	            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void new_move_database_failure_should_restore_source_or_keep_destination_visible(bool sourcePathReoccupied)
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"move-rollback-{Guid.NewGuid():N}");
            var sourcePath = Path.Combine(tempDir, "downloads", "incoming.epub");
            var destinationPath = Path.Combine(tempDir, "library", "Best Served Cold.epub");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath));
            File.WriteAllText(sourcePath, "incoming");

            try
            {
                var qualityProfile = new QualityProfile
                {
                    Id = 1,
                    Name = "Ebooks",
                    ProfileType = ProfileType.Ebook,
                    UpgradeAllowed = true,
                    Items = new List<QualityProfileQualityItem>
                    {
                        new() { Allowed = true, Quality = Quality.EPUB }
                    }
                };

                var author = new Author
                {
                    Id = 42,
                    Name = "Joe Abercrombie",
                    EbookRootFolderPath = Path.Combine(tempDir, "library"),
                    EbookPath = Path.Combine(tempDir, "library", "Joe Abercrombie"),
                    EbookQualityProfileId = qualityProfile.Id,
                    EbookQualityProfile = qualityProfile
                };

                var book = new Book
                {
                    Id = 5792,
                    Title = "Best Served Cold",
                    TitleSlug = "best-served-cold",
                    CleanTitle = "bestservedcold",
                    MediaType = BookMediaType.Ebook,
                    Author = author,
                    AuthorId = author.Id
                };

                var edition = new Edition
                {
                    Id = 15632,
                    BookId = book.Id,
                    Book = book,
                    Title = book.Title,
                    IsEbook = true,
                    ReadingFormatId = 3,
                    Monitored = true
                };

                var mediaFileService = new StubMediaFileService
                {
                    ThrowOnReplace = true,
                    BeforeReplace = sourcePathReoccupied
                        ? () => File.WriteAllText(sourcePath, "new source occupant")
                        : null
                };
                var mover = new StubMoveBookFiles
                {
                    DestinationPath = destinationPath,
                    TransferFilesOnDisk = true
                };
                var metadataTagService = new StubMetadataTagService();
                var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
                var service = new ImportApprovedBooks(
                    mediaFileService,
                    metadataTagService,
                    new StubMediaInfoExtractor(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    Proxy<IM4bConversionService>(),
                    LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    diskProvider: diskProvider);

                var localBook = new LocalBook
                {
                    Path = sourcePath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() },
                    Size = new FileInfo(sourcePath).Length,
                    Modified = File.GetLastWriteTimeUtc(sourcePath)
                };

                Assert.Throws<InvalidOperationException>(() => service.Import(
                    new List<ImportDecision<LocalBook>> { new(localBook) },
                    replaceExisting: false,
                    downloadClientItem: null,
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None));

                Assert.That(mover.MoveCalls, Is.EqualTo(1));
                Assert.That(mediaFileService.ReplaceCalls, Is.EqualTo(1));
                Assert.That(File.Exists(sourcePath), Is.True);
                Assert.That(metadataTagService.Writes, Is.Empty);

                if (sourcePathReoccupied)
                {
                    Assert.That(File.Exists(destinationPath), Is.True);
                    var visibleRecoveryRow = mediaFileService.FilesByBook.Single();
                    Assert.That(visibleRecoveryRow.Path, Is.EqualTo(destinationPath));
                    Assert.That(visibleRecoveryRow.EditionId, Is.Zero);
                }
                else
                {
                    Assert.That(File.Exists(destinationPath), Is.False);
                    Assert.That(mediaFileService.FilesByBook, Is.Empty);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_reject_additional_copy_when_destination_path_is_already_occupied()
        {
            var qualityProfile = new QualityProfile
            {
                Id = 1,
                Name = "Ebooks",
                ProfileType = ProfileType.Ebook,
                UpgradeAllowed = false,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = true, Quality = Quality.EPUB }
                }
            };

            var author = new Author
            {
                Id = 42,
                Name = "David Baldacci",
                EbookRootFolderPath = "/ebooks",
                EbookPath = "/ebooks/David Baldacci",
                EbookQualityProfileId = qualityProfile.Id,
                EbookQualityProfile = qualityProfile
            };

            var book = new Book
            {
                Id = 5047,
                Title = "A Minute to Midnight",
                TitleSlug = "a-minute-to-midnight",
                MediaType = BookMediaType.Ebook,
                Author = author
            };

            var edition = new Edition
            {
                Id = 30425,
                BookId = book.Id,
                Book = book,
                Title = "A Minute to Midnight (Atlee Pine Book 2)",
                IsEbook = true,
                ForeignEditionId = "hc:edition:30425"
            };

            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"additional-copy-destination-{Guid.NewGuid():N}");
            var existingManagedPath = Path.Combine(destinationDir, "David Baldacci - [Atlee Pine 02] - A Minute to Midnight (retail) (epub).epub");
            Directory.CreateDirectory(destinationDir);
            File.WriteAllText(existingManagedPath, "existing epub");
            var existingManagedFile = new BookFile
            {
                Id = 700,
                Path = existingManagedPath,
                EditionId = edition.Id,
                Edition = edition,
                Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() }
            };

            var mediaFileService = new StubMediaFileService();
            mediaFileService.FilesByBook.Add(existingManagedFile);
            mediaFileService.FilesByPath[existingManagedPath] = existingManagedFile;

            var mover = new StubMoveBookFiles
            {
                DestinationPath = existingManagedPath
            };

            var service = new ImportApprovedBooks(
                mediaFileService,
                new StubMetadataTagService(),
                Proxy<IMediaInfoExtractor>(),
                Proxy<IAuthorService>(),
                Proxy<IBookService>(),
                CreateEditionService(new List<Edition> { edition }),
                Proxy<IRecycleBinProvider>(),
                Proxy<IExtraService>(),
                mover,
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                new NoOpEventAggregator(),
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

            var localBook = new LocalBook
            {
                Path = "/data/books/David Baldacci - [Atlee Pine 02] - A Minute to Midnight (retail) (epub)/David Baldacci - [Atlee Pine 02] - A Minute to Midnight (retail) (epub).epub",
                Book = book,
                Author = author,
                Edition = edition,
                Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() },
                Part = 1,
                PartCount = 1
            };

            try
            {
                var results = service.Import(
                    new List<ImportDecision<LocalBook>> { new(localBook) },
                    replaceExisting: true,
                    downloadClientItem: new DownloadClientItem(),
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(results[0].Errors.Single(), Does.Contain("managed destination is already occupied"));
                Assert.That(mover.PreviewCalls, Is.EqualTo(1));
                Assert.That(mover.MoveCalls, Is.EqualTo(0));
                Assert.That(mover.CopyCalls, Is.EqualTo(0));
            }
            finally
            {
                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_not_convert_tracked_download_when_any_file_was_rejected_by_matching()
        {
            var tempPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-match-gate-{Guid.NewGuid():N}.mp3");
            File.WriteAllText(tempPath, "fake mp3");

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(50);
                var conversion = new FailingM4bConversionService();
                var mover = new StubMoveBookFiles
                {
                    DestinationPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-match-gate-destination-{Guid.NewGuid():N}", "Black Sheep.m4b")
                };

                var service = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var matched = new LocalBook
                {
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var unmatched = new LocalBook
                {
                    Path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-match-gate-unmatched-{Guid.NewGuid():N}.mp3"),
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var results = service.Import(
                    new List<ImportDecision<LocalBook>>
                    {
                        new(matched),
                        new(unmatched, new Rejection("NO_MATCH"))
                    },
                    replaceExisting: true,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-match-gate" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(results, Has.Count.EqualTo(2));
                Assert.That(results.SelectMany(r => r.Errors), Has.Some.Contains("Fix the match failure"));
                Assert.That(conversion.ConvertCalls, Is.EqualTo(0));
                Assert.That(mover.PreviewCalls, Is.EqualTo(0));
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        [Test]
        public void should_convert_tracked_manual_confirmed_download()
        {
            var tempPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-manual-confirm-{Guid.NewGuid():N}.mp3");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-manual-confirm-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            File.WriteAllText(tempPath, "fake mp3");

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(51);
                var conversion = new FailingM4bConversionService();
                var mover = new StubMoveBookFiles
                {
                    DestinationPath = destinationPath
                };

                var service = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var localBook = new LocalBook
                {
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() },
                    IsManualImport = true
                };

                var results = service.Import(
                    new List<ImportDecision<LocalBook>> { new(localBook) },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-manual-confirm" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(results[0].Errors.Single(), Is.EqualTo("synthetic conversion failure"));
                Assert.That(conversion.ConvertCalls, Is.EqualTo(1));
                Assert.That(conversion.LastInputFiles, Is.EquivalentTo(new[] { tempPath }));
                Assert.That(conversion.LastOutputFile, Does.StartWith(Path.Combine(destinationDir, ".chaptarr-conversions")));
                Assert.That(conversion.LastOutputFile, Does.EndWith("Black Sheep.m4b"));
                Assert.That(mover.PreviewCalls, Is.EqualTo(1));
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_not_create_duplicate_book_instance_when_conversion_fails()
        {
            var tempPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-fails-{Guid.NewGuid():N}.mp3");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            File.WriteAllText(tempPath, "fake mp3");

            try
            {
                var qualityProfile = new QualityProfile
                {
                    Id = 2,
                    Name = "Audiobooks",
                    ProfileType = ProfileType.Audiobook,
                    ConvertToQualityId = Quality.M4B.Id,
                    Items = new List<QualityProfileQualityItem>
                    {
                        new() { Allowed = true, Quality = Quality.MP3 },
                        new() { Allowed = true, Quality = Quality.M4B }
                    }
                };

                var author = new Author
                {
                    Id = 43,
                    Name = "David Archer",
                    AudiobookRootFolderPath = "/audiobooks",
                    AudiobookPath = "/audiobooks/David Archer",
                    AudiobookQualityProfileId = qualityProfile.Id,
                    AudiobookQualityProfile = qualityProfile
                };

                var book = new Book
                {
                    Id = 1300,
                    Title = "Black Sheep",
                    TitleSlug = "black-sheep",
                    MediaType = BookMediaType.Audiobook,
                    Author = author,
                    AuthorId = author.Id
                };

                var edition = new Edition
                {
                    Id = 2832,
                    BookId = book.Id,
                    Book = book,
                    Title = "Black Sheep",
                    Format = "audiobook",
                    ReadingFormatId = 2,
                    NarratorNames = new List<string> { "Adam Verner" },
                    ForeignEditionId = "az:B0746T3XVR-audiobook"
                };

                var conversion = new FailingM4bConversionService();
                var mover = new StubMoveBookFiles
                {
                    DestinationPath = destinationPath
                };

                var service = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var localBook = new LocalBook
                {
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var results = service.Import(
                    new List<ImportDecision<LocalBook>> { new(localBook) },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-fails" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(results[0].Errors.Single(), Is.EqualTo("synthetic conversion failure"));
                Assert.That(conversion.ConvertCalls, Is.EqualTo(1));
                Assert.That(conversion.LastInputFiles, Is.EquivalentTo(new[] { tempPath }));
                Assert.That(conversion.LastOutputFile, Does.StartWith(Path.Combine(destinationDir, ".chaptarr-conversions")));
                Assert.That(conversion.LastOutputFile, Does.EndWith("Black Sheep.m4b"));
                Assert.That(conversion.LastOptions.TempDirectory, Does.StartWith(Path.Combine(destinationDir, ".chaptarr-conversions")));
                Assert.That(Directory.Exists(Path.Combine(destinationDir, ".chaptarr-conversions")), Is.False);
                Assert.That(mover.PreviewCalls, Is.EqualTo(1));
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [TestCase(20, 1, 12, 12, 1)]
        [TestCase(20, 2, 12, 12, 1)]
        [TestCase(4, 1, 12, 4, 3)]
        [TestCase(1, 1, 12, 1, 12)]
        public void should_allocate_conversion_jobs_within_user_cpu_budget(
            int inputFileCount,
            int concurrentDownloads,
            int maxCpuThreads,
            int expectedJobs,
            int expectedFfmpegThreads)
        {
            var sourceDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-thread-plan-source-{Guid.NewGuid():N}");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-thread-plan-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(destinationDir);

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(30 + inputFileCount + concurrentDownloads + maxCpuThreads);
                var paths = Enumerable.Range(1, inputFileCount)
                    .Select(i => Path.Combine(sourceDir, $"part-{i:D2}.mp3"))
                    .ToArray();

                foreach (var path in paths)
                {
                    File.WriteAllText(path, "fake mp3");
                }

                var conversion = new FailingM4bConversionService();
                var service = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    new StubMoveBookFiles { DestinationPath = destinationPath },
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    configService: CreateConfigService(concurrentDownloads, maxCpuThreads));

                var decisions = paths.Select(path => new ImportDecision<LocalBook>(new LocalBook
                {
                    Path = path,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                })).ToList();

                var results = service.Import(
                    decisions,
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = $"conversion-thread-plan-{Guid.NewGuid():N}" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(results, Has.Count.EqualTo(inputFileCount));
                Assert.That(conversion.ConvertCalls, Is.EqualTo(1));
                Assert.That(conversion.LastOptions.Jobs, Is.EqualTo(expectedJobs));
                Assert.That(conversion.LastOptions.FfmpegThreads, Is.EqualTo(expectedFfmpegThreads));
                Assert.That(conversion.LastOptions.Jobs * conversion.LastOptions.FfmpegThreads, Is.LessThanOrEqualTo(maxCpuThreads));
            }
            finally
            {
                if (Directory.Exists(sourceDir))
                {
                    Directory.Delete(sourceDir, recursive: true);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_check_free_space_at_converted_destination_before_conversion()
        {
            var tempPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-space-{Guid.NewGuid():N}.mp3");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            File.WriteAllText(tempPath, "fake mp3");
            Directory.CreateDirectory(destinationDir);

            try
            {
                var qualityProfile = new QualityProfile
                {
                    Id = 3,
                    Name = "Audiobooks",
                    ProfileType = ProfileType.Audiobook,
                    ConvertToQualityId = Quality.M4B.Id,
                    Items = new List<QualityProfileQualityItem>
                    {
                        new() { Allowed = true, Quality = Quality.MP3 },
                        new() { Allowed = true, Quality = Quality.M4B }
                    }
                };

                var author = new Author
                {
                    Id = 44,
                    Name = "David Archer",
                    AudiobookRootFolderPath = "/audiobooks",
                    AudiobookPath = "/audiobooks/David Archer",
                    AudiobookQualityProfileId = qualityProfile.Id,
                    AudiobookQualityProfile = qualityProfile
                };

                var book = new Book
                {
                    Id = 1301,
                    Title = "Black Sheep",
                    TitleSlug = "black-sheep",
                    MediaType = BookMediaType.Audiobook,
                    Author = author,
                    AuthorId = author.Id
                };

                var edition = new Edition
                {
                    Id = 2833,
                    BookId = book.Id,
                    Book = book,
                    Title = "Black Sheep",
                    Format = "audiobook",
                    ReadingFormatId = 2,
                    NarratorNames = new List<string> { "Adam Verner" },
                    ForeignEditionId = "az:B0746T3XVR-audiobook"
                };

                var conversion = new FailingM4bConversionService();
                var mover = new StubMoveBookFiles
                {
                    DestinationPath = destinationPath
                };

                var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
                var diskProxy = (DiskProviderProxy)(object)diskProvider;
                diskProxy.AvailableSpace = 1;

                var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
                ((ConfigServiceProxy)(object)configService).MinimumFreeSpaceWhenImporting = 100;

                var service = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    null,
                    diskProvider,
                    configService);

                var localBook = new LocalBook
                {
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var results = service.Import(
                    new List<ImportDecision<LocalBook>> { new(localBook) },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-space" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(results[0].Errors.Single(), Does.Contain("Not enough free space in conversion destination folder"));
                Assert.That(conversion.ConvertCalls, Is.EqualTo(0));
                Assert.That(diskProxy.LastPath, Is.EqualTo(destinationDir));
                Assert.That(mover.PreviewCalls, Is.EqualTo(1));
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_skip_conversion_when_converted_destination_already_exists()
        {
            var tempPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-exists-{Guid.NewGuid():N}.mp3");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            File.WriteAllText(tempPath, "fake mp3");
            Directory.CreateDirectory(destinationDir);
            File.WriteAllText(destinationPath, "already imported m4b");

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(20);
                var conversion = new FailingM4bConversionService();
                var mover = new StubMoveBookFiles
                {
                    DestinationPath = destinationPath
                };

                var service = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var localBook = new LocalBook
                {
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var results = service.Import(
                    new List<ImportDecision<LocalBook>> { new(localBook) },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-destination-exists" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(results[0].Errors.Single(), Does.Contain("untracked file already exists at the destination"));
                Assert.That(conversion.ConvertCalls, Is.EqualTo(0));
                Assert.That(Directory.Exists(Path.Combine(destinationDir, ".chaptarr-conversions")), Is.False);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_skip_conversion_when_converted_destination_is_unmapped()
        {
            var tempPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-unmapped-{Guid.NewGuid():N}.mp3");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            File.WriteAllText(tempPath, "fake mp3");
            Directory.CreateDirectory(destinationDir);
            File.WriteAllText(destinationPath, "unmapped m4b");

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(20);
                var mediaFileService = new StubMediaFileService();
                mediaFileService.FilesByPath[destinationPath] = new BookFile
                {
                    Id = 123,
                    Path = destinationPath,
                    EditionId = 0,
                    Quality = new QualityModel(Quality.M4B)
                };

                var conversion = new FailingM4bConversionService();
                var mover = new StubMoveBookFiles
                {
                    DestinationPath = destinationPath
                };

                var service = new ImportApprovedBooks(
                    mediaFileService,
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var localBook = new LocalBook
                {
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var results = service.Import(
                    new List<ImportDecision<LocalBook>> { new(localBook) },
                    replaceExisting: true,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-destination-unmapped" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(results[0].Errors.Single(), Does.Contain("unmapped file already exists at the destination"));
                Assert.That(mediaFileService.DeletedFiles, Is.Empty);
                Assert.That(conversion.ConvertCalls, Is.EqualTo(0));
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_preserve_stale_tracked_destination_row_when_conversion_fails()
        {
            var tempPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-stale-{Guid.NewGuid():N}.mp3");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            File.WriteAllText(tempPath, "fake mp3");
            Directory.CreateDirectory(destinationDir);

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(20);
                var mediaFileService = new StubMediaFileService();
                mediaFileService.FilesByPath[destinationPath] = new BookFile
                {
                    Id = 456,
                    Path = destinationPath,
                    EditionId = edition.Id,
                    Quality = new QualityModel(Quality.M4B)
                };

                var conversion = new FailingM4bConversionService();
                var mover = new StubMoveBookFiles
                {
                    DestinationPath = destinationPath
                };

                var service = new ImportApprovedBooks(
                    mediaFileService,
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var localBook = new LocalBook
                {
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var results = service.Import(
                    new List<ImportDecision<LocalBook>> { new(localBook) },
                    replaceExisting: true,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-destination-stale" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Errors.Single(), Does.Contain("synthetic conversion failure"));
                Assert.That(conversion.ConvertCalls, Is.EqualTo(1));
                Assert.That(mediaFileService.DeletedFiles, Is.Empty);
                Assert.That(mediaFileService.GetFileWithPath(destinationPath)?.Id, Is.EqualTo(456));
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_reuse_stale_tracked_destination_row_when_conversion_succeeds()
        {
            var tempPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-reuse-{Guid.NewGuid():N}.mp3");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            File.WriteAllText(tempPath, "fake mp3");
            Directory.CreateDirectory(destinationDir);

            try
            {
                var (qualityProfile, author, book, edition) = CreateAudiobookConversionGraph(21);
                qualityProfile.UpgradeAllowed = true;
                book.CleanTitle = "black sheep";
                var staleRow = new BookFile
                {
                    Id = 456,
                    Path = destinationPath,
                    EditionId = edition.Id,
                    Edition = edition,
                    Quality = new QualityModel(Quality.M4B)
                };
                var mediaFileService = new StubMediaFileService { AllowUpdates = true };
                mediaFileService.FilesByPath[destinationPath] = staleRow;
                mediaFileService.FilesByBook.Add(staleRow);

                var conversion = new SuccessfulM4bConversionService();
                var mover = new StubMoveBookFiles
                {
                    DestinationPath = destinationPath
                };

                var service = new ImportApprovedBooks(
                    mediaFileService,
                    new StubMetadataTagService(),
                    new StubMediaInfoExtractor(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                    LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var localBook = new LocalBook
                {
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() },
                    MatchProvenance = new MatchProvenance
                    {
                        DecisionId = "conversion-provenance",
                        Mode = "Balanced",
                        Route = "global/embedded_tags"
                    }
                };

                var results = service.Import(
                    new List<ImportDecision<LocalBook>> { new(localBook) },
                    replaceExisting: true,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-destination-reuse" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Errors, Is.Empty);
                Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Imported));
                Assert.That(results[0].ImportDecision.Item.MatchProvenance?.DecisionId, Is.EqualTo("conversion-provenance"), "generated LocalBook should retain the matcher decision");
                Assert.That(conversion.ConvertCalls, Is.EqualTo(1));
                Assert.That(mediaFileService.DeletedFiles, Is.Empty);
                Assert.That(mediaFileService.UpdatedFiles.Select(file => file.Id), Is.EqualTo(new[] { 456 }));
                Assert.That(mediaFileService.GetFileWithPath(destinationPath)?.Id, Is.EqualTo(456));
                Assert.That(mediaFileService.GetFileWithPath(destinationPath)?.EditionId, Is.EqualTo(edition.Id));
                Assert.That(mediaFileService.GetFileWithPath(destinationPath)?.MatchProvenance?.DecisionId, Is.EqualTo("conversion-provenance"), "persisted converted file should retain the matcher decision");
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_pass_multi_file_conversion_sources_in_natural_order()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-order-{Guid.NewGuid():N}");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(destinationDir);

            var part10 = Path.Combine(tempDir, "10-track.mp3");
            var part2 = Path.Combine(tempDir, "2-track.mp3");
            var part1 = Path.Combine(tempDir, "1-track.mp3");
            File.WriteAllText(part10, "fake mp3");
            File.WriteAllText(part2, "fake mp3");
            File.WriteAllText(part1, "fake mp3");

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(23);
                var conversion = new FailingM4bConversionService();
                var service = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    new StubMoveBookFiles { DestinationPath = destinationPath },
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                LocalBook LocalBookFor(string path) => new()
                {
                    Path = path,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                service.Import(
                    new List<ImportDecision<LocalBook>>
                    {
                        new(LocalBookFor(part10)),
                        new(LocalBookFor(part2)),
                        new(LocalBookFor(part1))
                    },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-order" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(conversion.ConvertCalls, Is.EqualTo(1));
                Assert.That(conversion.LastInputFiles, Is.EqualTo(new[] { part1, part2, part10 }));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_pass_matched_book_cover_as_conversion_fallback()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-cover-{Guid.NewGuid():N}");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(destinationDir);

            var sourceDir = Path.Combine(tempDir, "source");
            var cacheDir = Path.Combine(tempDir, "cover-cache");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(cacheDir);

            var sourcePath = Path.Combine(sourceDir, "Black Sheep.mp3");
            var coverPath = Path.Combine(cacheDir, "cover.jpg");
            File.WriteAllText(sourcePath, "fake mp3");
            File.WriteAllText(coverPath, "fake cover");

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(26);
                edition.Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Cover, "https://example.com/black-sheep.jpg")
                };
                book.Editions = new List<Edition> { edition };

                var coverMapper = new StubCoverMapper { CoverPath = coverPath };
                var conversion = new FailingM4bConversionService();
                var service = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    new StubMoveBookFiles { DestinationPath = destinationPath },
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    coverMapper: coverMapper);

                var localBook = new LocalBook
                {
                    Path = sourcePath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                service.Import(
                    new List<ImportDecision<LocalBook>> { new(localBook) },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-cover" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(conversion.ConvertCalls, Is.EqualTo(1));
                Assert.That(conversion.LastOptions.TagOptions.Cover, Is.EqualTo(coverPath));
                Assert.That(conversion.LastOptions.TagOptions.CoverPolicySignature, Does.Contain("source-cover-v1"));
                Assert.That(conversion.LastOptions.TagOptions.CoverPolicySignature, Does.Contain(coverPath));
                Assert.That(coverMapper.EnsureBookCoversCalls, Is.EqualTo(1));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_stage_matched_provider_chapters_when_source_duration_matches()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-provider-chapters-{Guid.NewGuid():N}");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(destinationDir);

            var part1 = Path.Combine(tempDir, "Black Sheep (1).mp3");
            var part2 = Path.Combine(tempDir, "Black Sheep (2).mp3");
            File.WriteAllText(part1, "fake mp3");
            File.WriteAllText(part2, "fake mp3");

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(24);
                edition.DurationSeconds = 1200;
                edition.HasChapters = true;
                edition.ChapterCount = 3;
                edition.Chapters = new List<EditionChapter>
                {
                    new() { Title = "Opening", StartOffsetMs = 0, LengthMs = 300000 },
                    new() { Title = "Chapter 1", StartOffsetMs = 300000, LengthMs = 600000 },
                    new() { Title = "Chapter 2", StartOffsetMs = 900000, LengthMs = 300000 }
                };

                var mediaInfo = new StubMediaInfoExtractor();
                mediaInfo.Durations[part1] = TimeSpan.FromMinutes(10);
                mediaInfo.Durations[part2] = TimeSpan.FromMinutes(10);

                var conversion = new SuccessfulM4bConversionService();
                var service = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    mediaInfo,
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    new StubMoveBookFiles { DestinationPath = destinationPath },
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                LocalBook LocalBookFor(string path) => new()
                {
                    Path = path,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                service.Import(
                    new List<ImportDecision<LocalBook>>
                    {
                        new(LocalBookFor(part1)),
                        new(LocalBookFor(part2))
                    },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-provider-chapters" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(conversion.ConvertCalls, Is.EqualTo(1));
                Assert.That(conversion.LastInputFiles, Has.Length.EqualTo(2));
                Assert.That(conversion.LastInputFiles[0], Is.Not.EqualTo(part1));
                Assert.That(Path.GetDirectoryName(conversion.LastInputFiles[0]), Does.EndWith("provider-chapters"));

                var chaptersPath = Path.Combine(Path.GetDirectoryName(conversion.LastInputFiles[0]), "chapters.txt");
                Assert.That(File.Exists(chaptersPath), Is.True);
                var chaptersTxt = File.ReadAllText(chaptersPath);
                Assert.That(chaptersTxt, Does.Contain("## total-length 00:20:00.000"));
                Assert.That(chaptersTxt, Does.Contain("00:00:00.000 Opening"));
                Assert.That(chaptersTxt, Does.Contain("00:05:00.000 Chapter 1"));
                Assert.That(chaptersTxt, Does.Contain("00:15:00.000 Chapter 2"));
                Assert.That(conversion.LastOptions.TagOptions.ProviderChapterCount, Is.EqualTo(3));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_not_stage_provider_chapters_when_source_duration_does_not_match()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-provider-chapters-mismatch-{Guid.NewGuid():N}");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(destinationDir);

            var part1 = Path.Combine(tempDir, "Black Sheep (1).mp3");
            File.WriteAllText(part1, "fake mp3");

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(25);
                edition.DurationSeconds = 1200;
                edition.HasChapters = true;
                edition.ChapterCount = 2;
                edition.Chapters = new List<EditionChapter>
                {
                    new() { Title = "Opening", StartOffsetMs = 0, LengthMs = 600000 },
                    new() { Title = "Chapter 1", StartOffsetMs = 600000, LengthMs = 600000 }
                };

                var mediaInfo = new StubMediaInfoExtractor();
                mediaInfo.Durations[part1] = TimeSpan.FromMinutes(5);

                var conversion = new FailingM4bConversionService();
                var service = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    mediaInfo,
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    new StubMoveBookFiles { DestinationPath = destinationPath },
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var localBook = new LocalBook
                {
                    Path = part1,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                service.Import(
                    new List<ImportDecision<LocalBook>> { new(localBook) },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-provider-chapters-mismatch" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(conversion.ConvertCalls, Is.EqualTo(1));
                Assert.That(conversion.LastInputFiles, Is.EqualTo(new[] { part1 }));
                Assert.That(conversion.LastOptions.TagOptions.ProviderChapterCount, Is.Null);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_retain_and_reuse_converted_artifact_when_import_fails_after_conversion()
        {
            var tempPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-retain-{Guid.NewGuid():N}.mp3");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            File.WriteAllText(tempPath, "fake mp3");
            Directory.CreateDirectory(destinationDir);

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(21);
                var mover = new StubMoveBookFiles
                {
                    DestinationPath = destinationPath
                };

                var firstConversion = new SuccessfulM4bConversionService();
                var firstService = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    firstConversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var firstLocalBook = new LocalBook
                {
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var firstResults = firstService.Import(
                    new List<ImportDecision<LocalBook>> { new(firstLocalBook) },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-retain" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(firstResults[0].Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(firstConversion.ConvertCalls, Is.EqualTo(1));
                Assert.That(File.Exists(firstConversion.LastOutputFile), Is.True);
                Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(firstConversion.LastOutputFile), "conversion-artifact.json")), Is.True);

                var secondConversion = new SuccessfulM4bConversionService();
                var secondService = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    secondConversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var secondLocalBook = new LocalBook
                {
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                secondService.Import(
                    new List<ImportDecision<LocalBook>> { new(secondLocalBook) },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-retain" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(secondConversion.ConvertCalls, Is.EqualTo(0));
                Assert.That(File.Exists(firstConversion.LastOutputFile), Is.True);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void should_not_reuse_expired_retained_conversion_artifact()
        {
            var tempPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-retain-expired-{Guid.NewGuid():N}.mp3");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-destination-{Guid.NewGuid():N}");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            File.WriteAllText(tempPath, "fake mp3");
            Directory.CreateDirectory(destinationDir);

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(22);
                var mover = new StubMoveBookFiles
                {
                    DestinationPath = destinationPath
                };

                var firstConversion = new SuccessfulM4bConversionService();
                var firstService = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    firstConversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                firstService.Import(
                    new List<ImportDecision<LocalBook>>
                    {
                        new(new LocalBook
                        {
                            Path = tempPath,
                            Book = book,
                            Author = author,
                            Edition = edition,
                            Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                        })
                    },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-retain-expired" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                var oldOutput = firstConversion.LastOutputFile;
                var oldAttemptFolder = Path.GetDirectoryName(oldOutput);
                Directory.SetLastWriteTimeUtc(oldAttemptFolder, DateTime.UtcNow.AddDays(-8));

                var secondConversion = new SuccessfulM4bConversionService();
                var secondService = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    secondConversion,
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                secondService.Import(
                    new List<ImportDecision<LocalBook>>
                    {
                        new(new LocalBook
                        {
                            Path = tempPath,
                            Book = book,
                            Author = author,
                            Edition = edition,
                            Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                        })
                    },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-retain-expired" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(secondConversion.ConvertCalls, Is.EqualTo(1));
                Assert.That(File.Exists(oldOutput), Is.False);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void completed_download_sweep_should_skip_existing_conversion_job_and_return_pending()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-sweep-skip-{Guid.NewGuid():N}");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-sweep-skip-destination-{Guid.NewGuid():N}");
            var sourcePath = Path.Combine(tempDir, "Black Sheep.mp3");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(destinationDir);
            File.WriteAllText(sourcePath, "fake mp3");

            try
            {
                const string downloadId = "sweep-skip-conversion-download";
                var (_, author, book, edition) = CreateAudiobookConversionGraph(92);
                var conversion = new SuccessfulM4bConversionService();
                var jobs = new RecordingConversionJobService();
                jobs.SetJob(downloadId, ConversionJobStatus.Converting);
                var service = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    new StubMoveBookFiles { DestinationPath = destinationPath },
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                    LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    configService: CreateConfigService(concurrentConversions: 2, maxCpuThreads: 8),
                    conversionJobService: jobs);

                var results = service.Import(
                    new List<ImportDecision<LocalBook>>
                    {
                        new(new LocalBook
                        {
                            Path = sourcePath,
                            Book = book,
                            Author = author,
                            Edition = edition,
                            Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                        })
                    },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = downloadId },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(results.Single().Result, Is.EqualTo(ImportResultType.Pending));
                Assert.That(conversion.ConvertCalls, Is.Zero);
                Assert.That(jobs.Job.Status, Is.EqualTo(ConversionJobStatus.Converting));
                Assert.That(jobs.Request, Is.Null);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void ready_to_import_artifact_should_run_normal_serialized_tail_and_complete_job()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-ready-tail-{Guid.NewGuid():N}");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-ready-tail-destination-{Guid.NewGuid():N}");
            var sourcePath = Path.Combine(tempDir, "Black Sheep.mp3");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(destinationDir);
            File.WriteAllText(sourcePath, "fake mp3");

            try
            {
                const string downloadId = "ready-tail-conversion-download";
                var (_, author, book, edition) = CreateAudiobookConversionGraph(93);
                var (bookService, _) = CreateBookService(new List<Book> { book });
                var mediaFiles = new StubMediaFileService();
                var mover = new StubMoveBookFiles { DestinationPath = destinationPath };
                var conversion = new SuccessfulM4bConversionService();
                var jobs = new RecordingConversionJobService();
                var service = new ImportApprovedBooks(
                    mediaFiles,
                    new StubMetadataTagService(),
                    new StubMediaInfoExtractor(),
                    Proxy<IAuthorService>(),
                    bookService,
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    mover,
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                    LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    configService: CreateConfigService(concurrentConversions: 2, maxCpuThreads: 8),
                    conversionJobService: jobs);

                List<ImportDecision<LocalBook>> Decisions()
                {
                    return new List<ImportDecision<LocalBook>>
                    {
                        new(new LocalBook
                        {
                            Path = sourcePath,
                            Book = book,
                            Author = author,
                            Edition = edition,
                            Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                        })
                    };
                }

                var firstResults = service.Import(
                    Decisions(),
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = downloadId },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(firstResults.Single().Result, Is.EqualTo(ImportResultType.Pending));
                Assert.That(jobs.Request, Is.Not.Null);
                var request = jobs.Request;
                Directory.CreateDirectory(request.WorkFolder);
                File.WriteAllText(request.OutputPath, "converted m4b");
                var manifest = new ConversionArtifactManifest
                {
                    CreatedUtc = DateTime.UtcNow,
                    OutputPath = request.OutputPath,
                    TargetQualityId = request.TargetQualityId,
                    TargetQualityName = request.TargetQualityName,
                    AudioBitrate = request.AudioBitrate,
                    AudioChannels = request.AudioChannels,
                    TagMode = request.TagOptions?.Mode,
                    TagSignature = request.TagSignature,
                    Sources = request.Sources
                };
                File.WriteAllText(
                    Path.Combine(request.WorkFolder, "conversion-artifact.json"),
                    JsonSerializer.Serialize(manifest));
                jobs.SetJob(downloadId, ConversionJobStatus.ReadyToImport, request.WorkRoot, request.WorkFolder, request.OutputPath);

                var secondResults = service.Import(
                    Decisions(),
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = downloadId },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(secondResults.Single().Result, Is.EqualTo(ImportResultType.Imported),
                    string.Join(" | ", secondResults.Single().Errors));
                Assert.That(conversion.ConvertCalls, Is.Zero);
                Assert.That(mover.MoveCalls, Is.EqualTo(1));
                Assert.That(mediaFiles.FilesByBook, Has.Count.EqualTo(1));
                Assert.That(jobs.CompletedDownloadId, Is.EqualTo(downloadId));
                Assert.That(jobs.Job.Status, Is.EqualTo(ConversionJobStatus.Completed));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }

        [Test]
        public void completed_download_conversion_should_enqueue_and_return_pending_without_running_converter()
        {
            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-detached-{Guid.NewGuid():N}");
            var destinationDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-detached-destination-{Guid.NewGuid():N}");
            var sourcePath = Path.Combine(tempDir, "Black Sheep.mp3");
            var destinationPath = Path.Combine(destinationDir, "Black Sheep.m4b");
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(destinationDir);
            File.WriteAllText(sourcePath, "fake mp3");

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(91);
                var conversion = new SuccessfulM4bConversionService();
                var jobs = new RecordingConversionJobService();
                var service = new ImportApprovedBooks(
                    new StubMediaFileService(),
                    new StubMetadataTagService(),
                    Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(),
                    Proxy<IBookService>(),
                    CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(),
                    Proxy<IExtraService>(),
                    new StubMoveBookFiles { DestinationPath = destinationPath },
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    conversion,
                    LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    configService: CreateConfigService(concurrentConversions: 2, maxCpuThreads: 8),
                    conversionJobService: jobs);

                var results = service.Import(
                    new List<ImportDecision<LocalBook>>
                    {
                        new(new LocalBook
                        {
                            Path = sourcePath,
                            Book = book,
                            Author = author,
                            Edition = edition,
                            Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                        })
                    },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "detached-conversion-download" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Pending));
                Assert.That(conversion.ConvertCalls, Is.Zero);
                Assert.That(jobs.Request, Is.Not.Null);
                Assert.That(jobs.Request.DownloadId, Is.EqualTo("detached-conversion-download"));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }

                if (Directory.Exists(destinationDir))
                {
                    Directory.Delete(destinationDir, recursive: true);
                }
            }
        }
    }
}

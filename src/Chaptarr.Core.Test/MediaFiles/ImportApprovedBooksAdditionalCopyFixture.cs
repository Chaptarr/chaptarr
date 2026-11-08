using NzbDrone.Core.Profiles;
using System;
using System.Data;
using NzbDrone.Common.Extensions;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public partial class ImportApprovedBooksAdditionalCopyFixture
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
            public Action BeforeOwnershipRead { get; set; }

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
            public (int Relinked, int Reclassified) RelinkAndClassifyFilesByBook(int bookId, int? editionId, NzbDrone.Core.Books.BookMediaType mediaType, NzbDrone.Core.MediaFiles.AudioProductionCorrection correction, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();

            public void Update(List<BookFile> bookFiles)
            {
                foreach (var bookFile in bookFiles ?? new List<BookFile>())
                {
                    Update(bookFile);
                }
            }
            public void ReplaceMany(List<BookFile> bookFiles, List<BookFile> replacedFiles, DeleteMediaFileReason reason, IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFile> confirmedFiles = null)
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

                Update((bookFiles ?? new List<BookFile>()).Where(file => file.Id > 0).ToList());
                AddMany((bookFiles ?? new List<BookFile>()).Where(file => file.Id == 0).ToList());
            }
            public void ValidateReplacementFiles(List<BookFile> replacedFiles, IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFile> confirmedFiles)
            {
                if ((replacedFiles ?? new()).Any(file => !NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFiles.IsConfirmed(file, confirmedFiles)))
                    throw new InvalidOperationException("Existing files changed since confirmation");
            }
            public void ReplaceMany(List<BookFile> bookFiles, List<BookFile> replacedFiles, DeleteMediaFileReason reason,
                IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportReplacementFile> confirmedFiles,
                Action<Action<IDbConnection, IDbTransaction>> persist)
            {
                persist((connection, transaction) => ReplaceMany(bookFiles, replacedFiles, reason, confirmedFiles));
            }
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason)
            {
                DeletedFiles.Add((bookFile, reason));
                FilesByPath.Remove(bookFile.Path);
                FilesByBook.Remove(bookFile);
            }
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => throw new NotImplementedException();
            public List<BookFile> GetFilePathOwnership(IReadOnlyCollection<int> authorIds, IReadOnlyCollection<string> folders)
            {
                BeforeOwnershipRead?.Invoke();
                return FilesByBook.Where(file => folders.Any(folder => folder.IsParentPath(file.Path) || folder.PathEquals(file.Path))).ToList();
            }
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => FilesByBook.Where(f => f.Edition?.BookId == bookId || f.Edition?.Book?.Id == bookId).ToList();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles() => throw new NotImplementedException();
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> paths) => paths.Select(GetFileWithPath).Where(file => file != null).ToList();
            public BookFile GetFileWithPath(string path) => FilesByPath.TryGetValue(path, out var file) ? file : null;
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private sealed class StubMoveBookFiles : IMoveBookFiles
        {
            public string DestinationPath { get; set; }
            public Func<LocalBook, string> DestinationForFile { get; set; }
            public string FailSourcePath { get; set; }
            public int MoveCalls { get; private set; }
            public int CopyCalls { get; private set; }
            public int PreviewCalls { get; private set; }
            public bool TransferFilesOnDisk { get; set; }

            public BookFileMovePlan GetOrganizeDestination(BookFile bookFile, Author author, bool moveToCanonicalAuthorFolder, RenameBatchContext renameBatchContext = null) => throw new NotImplementedException();

            public BookFile MoveBookFile(BookFile bookFile, Author author, BookFileMovePlan plan, RenameBatchContext renameBatchContext = null)
            {
                MoveCalls++;
                bookFile.Path = DestinationPath ?? bookFile.Path;
                return bookFile;
            }

            public BookFile MoveBookFile(BookFile bookFile, LocalBook localBook)
            {
                MoveCalls++;
                bookFile.Path = DestinationForFile?.Invoke(localBook) ?? DestinationPath ?? bookFile.Path;
                TransferFile(localBook.Path, bookFile.Path, copy: false);
                return bookFile;
            }

            public BookFile CopyBookFile(BookFile bookFile, LocalBook localBook)
            {
                CopyCalls++;
                bookFile.Path = DestinationForFile?.Invoke(localBook) ?? DestinationPath ?? bookFile.Path;
                TransferFile(localBook.Path, bookFile.Path, copy: true);
                return bookFile;
            }

            public bool CompleteImportReplicas(BookFile bookFile, LocalBook localBook) => false;

            public string GetImportDestinationPath(BookFile bookFile, LocalBook localBook)
            {
                PreviewCalls++;
                return DestinationForFile?.Invoke(localBook) ?? DestinationPath;
            }

            private void TransferFile(string sourcePath, string destinationPath, bool copy)
            {
                if (sourcePath == FailSourcePath)
                {
                    throw new IOException("Source unavailable during transfer");
                }

                if (!TransferFilesOnDisk || string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                if (copy)
                {
                    File.Copy(sourcePath, destinationPath, overwrite: false);
                }
                else
                {
                    File.Move(sourcePath, destinationPath, overwrite: false);
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
            public bool ThrowOnInsert { get; set; }
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
                    if (ThrowOnInsert) { throw new InvalidOperationException("Edition insert failed"); }
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
            public List<int> DeletedBookIds { get; } = new();
            private int _nextId = 8000;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.DeleteBook))
                {
                    var id = (int)args[0];
                    Assert.That(args[1], Is.False);
                    DeletedBookIds.Add(id);
                    ExistingBooks.RemoveAll(book => book.Id == id);
                    return null;
                }

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

                if (targetMethod?.Name == nameof(IBookService.UpdateBook) && args?.Length == 4)
                {
                    ((Action<IDbConnection, IDbTransaction>)args[3])?.Invoke(null, null);
                    var book = (Book)args[0];
                    var index = ExistingBooks.FindIndex(existing => existing.Id == book.Id);
                    if (index >= 0) ExistingBooks[index] = book;
                    return book;
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
                if (typeof(T) == typeof(IHistoryService) && targetMethod.Name == nameof(IHistoryService.FindByDownloadId))
                {
                    return new List<EntityHistory>();
                }

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
            public void EnsureBookCoversForBooks(List<Book> books) => throw new NotImplementedException();
            public void EnsureBookCoversForImport(Book book) => EnsureBookCovers(book);
            public void ConvertBookCoversToLocalUrls(IEnumerable<KeyValuePair<int, List<NzbDrone.Core.MediaCover.MediaCover>>> covers)
            {
                foreach (var book in covers)
                {
                    ConvertToLocalUrls(book.Key, MediaCoverEntity.Book, book.Value);
                }
            }

            public void DeleteBookCovers(int bookId) => throw new NotImplementedException();

            public string CoverPath { get; set; }
            public int EnsureBookCoversCalls { get; private set; }

            public void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IList<NzbDrone.Core.MediaCover.MediaCover> covers, string selectedAuthorImageHash = null)
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

                if (targetMethod?.Name == "get_DownloadPropersAndRepacks")
                {
                    return ProperDownloadTypes.PreferAndUpgrade;
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
            public ConversionJobRequest GetCompletedConversion(string downloadId) =>
                Job?.Status == ConversionJobStatus.ReadyToImport && Job.DownloadId == downloadId && Request?.DownloadId == downloadId
                    ? Request : null;
            public List<ConversionJobRequest> GetCompletedConversions(IEnumerable<string> downloadIds) => new();
            public List<ConversionJob> Get(IEnumerable<string> downloadIds) => Job == null ? new() : new() { Job };
            public Action PrepareArtifactRemoval(string downloadId, ConversionRemovalOrigin origin) => () => throw new NotImplementedException();

            public ConversionJob Enqueue(ConversionJobRequest request)
            {
                Request = request;
                Job = new ConversionJob
                {
                    Id = 1,
                    DownloadId = request.DownloadId,
                    Status = ConversionJobStatus.Queued,
                    RequestJson = JsonSerializer.Serialize(request),
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
                    RequestJson = Request == null ? null : JsonSerializer.Serialize(Request),
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
            public bool IsRunning(string downloadId) => false;
            public bool Cancel(string downloadId, ConversionRemovalOrigin origin) => false;

            public ConversionRetentionResult RetainCompletedOutput(string downloadId, IReadOnlyCollection<string> sourcePaths, string reason, bool discardOutput = false)
            {
                if (Job == null) return new(ConversionRetentionStatus.None, reason);
                throw new NotSupportedException("Retained output tests must use the real conversion job service.");
            }

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
            public string Reset(string downloadId, ConversionJob expectedJob = null)
            {
                Job = null;
                return null;
            }
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

        [TestCase(false, false, false, true)]
        [TestCase(false, true, false, true)]
        [TestCase(false, true, true, true)]
        [TestCase(true, false, false, true)]
        [TestCase(true, true, false, true)]
        [TestCase(true, true, true, true)]
        [TestCase(false, true, false, false)]
        [TestCase(true, true, false, false)]
        [TestCase(false, true, true, false)]
        [TestCase(true, true, true, false)]
        [TestCase(false, true, true, true, 3)]
        [TestCase(true, true, true, true, 3)]
        [TestCase(false, true, true, false, 2, true)]
        [TestCase(true, true, true, false, 2, true)]
        public void multipart_import_must_keep_old_recording_on_failed_replace_and_resume_new_downloads(bool postgres, bool retry, bool publishHistory, bool existingRecording, int parts = 2, bool moveSources = false)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "multipart-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var noEvents = new NoOpEventAggregator();
                    var histories = new HistoryRepository(database, noEvents);
                    var history = new HistoryService(histories, logger);
                    var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) =>
                    {
                        if (publishHistory && args[0] is NzbDrone.Core.MediaFiles.Events.TrackImportedEvent imported)
                        {
                            history.Handle(imported);
                        }

                        return null;
                    });
                    var (profile, author, book, edition) = CreateAudiobookConversionGraph(199);
                    profile.UpgradeAllowed = false;
                    author.Id = 0;
                    author.Path = root;
                    author.CleanName = "testwriter";
                    author.SortName = "testwriter";
                    author.TitleSlug = "test-writer";
                    author.Added = DateTime.UtcNow;
                    new AuthorRepository(database, noEvents, logger).Insert(author);
                    book.Id = 0;
                    book.CleanTitle = "blacksheep";
                    book.AnyEditionOk = true;
                    book.AuthorId = author.Id;
                    book.Added = DateTime.UtcNow;
                    var books = new BookRepository(database, noEvents);
                    books.Insert(book);
                    edition.Id = 0;
                    edition.BookId = book.Id;
                    var editions = new EditionRepository(database, noEvents);
                    editions.Insert(edition);
                    book.Editions = new List<Edition> { edition };
                    var sourceDir = Path.Combine(root, "download");
                    var libraryDir = Path.Combine(root, "library");
                    Directory.CreateDirectory(sourceDir);
                    Directory.CreateDirectory(libraryDir);
                    var sources = Enumerable.Range(1, parts).Select(part => Path.Combine(sourceDir, $"Book Part {part:00}.m4b")).ToList();
                    var files = new MediaFileRepository(database, noEvents);
                    for (var i = 0; i < sources.Count; i++)
                    {
                        File.WriteAllText(sources[i], "new part " + (i + 1));
                        if (!existingRecording)
                        {
                            continue;
                        }

                        var destination = Path.Combine(libraryDir, Path.GetFileName(sources[i]));
                        File.WriteAllText(destination, "old part " + (i + 1));
                        files.Insert(new BookFile { Path = destination, EditionId = edition.Id, Edition = edition,
                            Quality = new QualityModel(Quality.M4B), Part = i + 1, PartCount = parts,
                            MediaInfo = new MediaInfoModel(), DateAdded = DateTime.UtcNow.AddDays(-1), Modified = DateTime.UtcNow });
                    }

                    var reads = 0;
                    var replaces = 0;
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        switch (method.Name)
                        {
                            case nameof(IMediaFileService.GetFilesByBook): reads++; return files.GetFilesByBook((int)args[0]);
                            case nameof(IMediaFileService.GetFilesByBooks): return files.GetFilesByBooks((List<int>)args[0]);
                            case nameof(IMediaFileService.GetFileWithPath): return args[0] is List<string> paths ? files.GetFileWithPath(paths) : files.GetFileWithPath((string)args[0]);
                            case nameof(IMediaFileService.GetFilesByEdition): return files.GetFilesByEdition((int)args[0]);
                            case nameof(IMediaFileService.ReplaceMany):
                                replaces++;
                                if (args.Length == 5)
                                    ((Action<Action<IDbConnection, IDbTransaction>>)args[4])((connection, transaction) =>
                                        files.ReplaceMany((List<BookFile>)args[0], (List<BookFile>)args[1], connection, transaction));
                                else
                                    files.ReplaceMany((List<BookFile>)args[0], (List<BookFile>)args[1]);
                                return null;
                            case nameof(IMediaFileService.Update): files.Update((BookFile)args[0]); return null;
                            default: throw new InvalidOperationException("Unexpected file operation " + method.Name);
                        }
                    });
                    var mover = new StubMoveBookFiles { TransferFilesOnDisk = true,
                        DestinationForFile = local => Path.Combine(libraryDir, Path.GetFileName(local.Path)),
                        FailSourcePath = retry ? sources[1] : null };
                    var checkedRecordingSizes = new List<long>();
                    var sizeFormat = new CustomFormat("Multipart source size", new SizeSpecification
                    {
                        Min = 15d / (1024 * 1024 * 1024), Max = 1
                    }) { Id = 95 };
                    var calculator = RecordingFormats(sizeFormat);
                    if (moveSources)
                    {
                        profile.MinFormatScore = 100;
                        profile.FormatItems = new List<ProfileFormatItem> { new() { Format = sizeFormat, Score = 100 } };
                    }
                    var customFormats = moveSources ? InterfaceProxy.Proxy<ICustomFormatCalculationService>((_, args) =>
                    {
                        var input = (LocalBook)args[0];
                        checkedRecordingSizes.Add(input.Size);
                        return calculator.ParseCustomFormat(input);
                    }) : null;
                    var recycle = new StubRecycleBinProvider();
                    var editionService = new EditionService(editions, events, logger);
                    var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name == nameof(IAuthorService.GetAuthors) ? new List<Author> { author } : author);
                    var bookService = new BookService(books, editionService, events, authorService, media,
                        Proxy<NzbDrone.Core.RootFolders.IRootFolderService>(), null, null, logger);
                    ImportApprovedBooks CreateService() => new ImportApprovedBooks(media, new StubMetadataTagService(), new StubMediaInfoExtractor(),
                        authorService, bookService, editionService,
                        recycle, Proxy<IExtraService>(), mover, history,
                        Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), events,
                        Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(),
                        Proxy<IQualityProfileService>(), Proxy<IM4bConversionService>(), logger, customFormatCalculationService: customFormats);
                    ImportDecision<LocalBook> Decision(string path, bool manual) => new(new LocalBook
                    {
                        Path = path, Author = author, Book = book, Edition = edition, IsManualImport = manual,
                        DisableReleaseSwitching = manual,
                        Quality = new QualityModel(Quality.M4B), Size = new FileInfo(path).Length, Modified = File.GetLastWriteTimeUtc(path)
                    });
                    var download = new DownloadClientItem { DownloadId = "multipart", DownloadForced = !moveSources, Title = "Multipart download",
                        FilePaths = sources.Append(Path.Combine(sourceDir, "cover.jpg")).ToList() };
                    var results = CreateService().Import(sources.Select(path => Decision(path, !moveSources)).ToList(), true, download,
                        moveSources ? ImportMode.Move : ImportMode.Copy);
                    Assert.That(results.Count(result => result.Result == ImportResultType.Imported), Is.EqualTo(retry ? (existingRecording ? 0 : parts - 1) : parts));
                    Assert.That(reads, Is.EqualTo(1), "Load existing files once for the book group, not once for every part");
                    Assert.That(replaces, Is.EqualTo(retry && existingRecording ? 0 : 1));
                    var hasSuccessfulCommit = !retry || !existingRecording;
                    Assert.That(books.Get(book.Id).AnyEditionOk, Is.EqualTo(moveSources || !hasSuccessfulCommit),
                        "Only a successful manual selection disables automatic edition selection");
                    Assert.That(editions.Get(edition.Id).Monitored, Is.EqualTo(hasSuccessfulCommit),
                        "A failed complete replacement must not select its edition");
                    var first = files.GetFilesByBook(book.Id).Single(file => file.Part == 1);
                    if (retry && existingRecording)
                    {
                        Assert.That(files.GetFilesByBook(book.Id).OrderBy(file => file.Part).Select(file => File.ReadAllText(file.Path)),
                            Is.EqualTo(Enumerable.Range(1, parts).Select(part => "old part " + part)));
                        Assert.That(histories.All(), Is.Empty);
                        Assert.That(Directory.EnumerateFiles(libraryDir, "*.chaptarr-upgrade~*"), Is.Empty);
                    }
                    else
                    {
                        Assert.That(File.ReadAllText(first.Path), Is.EqualTo("new part 1"));
                        Assert.That(first.MatchProvenance.ImportDownloadId, Is.EqualTo(download.DownloadId));
                        Assert.That(first.MatchProvenance.ImportSourcePaths, Is.EqualTo(new[] { sources[0] }));
                    }
                    var tracked = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownload
                    {
                        DownloadItem = download,
                        RemoteBook = new RemoteBook { Books = new List<Book> { book }, ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) } }
                    };
                    var completion = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(logger, media);
                    Assert.That(completion.IsImported(tracked, history.FindByDownloadId(download.DownloadId)), Is.EqualTo(!retry));
                    if (retry)
                    {
                        Assert.That(tracked.StatusMessages.Any(message => message.Title == Path.GetFileName(sources[1])), Is.True, "Name the missing part");
                        Assert.That(tracked.StatusMessages.Any(message => message.SourcePath == sources[1]), Is.True, "Keep the missing source identity");
                        mover.FailSourcePath = null;
                        // Recreate the import service and read committed rows, with no in-memory first-pass results.
                        if (moveSources)
                        {
                            Assert.That(File.Exists(sources[0]), Is.False, "The first source was moved; retry must use its stored size");
                            Assert.That(File.Exists(sources[1]), Is.True);
                        }
                        var resumed = CreateService().Import(sources.Where(path => !moveSources || File.Exists(path))
                            .Select(path => Decision(path, existingRecording)).ToList(), true, download,
                            moveSources ? ImportMode.Move : ImportMode.Copy);
                        Assert.That(resumed.Count(result => result.Result == ImportResultType.Imported), Is.EqualTo(existingRecording ? parts : 1));
                        Assert.That(resumed.Count(result => result.Result == ImportResultType.Skipped), Is.EqualTo(existingRecording || moveSources ? 0 : 1));
                        if (moveSources)
                        {
                            Assert.That(mover.MoveCalls, Is.EqualTo(parts + 1));
                            Assert.That(mover.CopyCalls, Is.Zero);
                            Assert.That(checkedRecordingSizes, Is.EqualTo(new long[] { 20, 20 }),
                                "Both first attempt and source-gone retry must evaluate the full recording, not remaining ten bytes");
                        }
                        else Assert.That(mover.CopyCalls, Is.EqualTo(existingRecording ? parts * 2 : parts + 1));
                        if (!existingRecording)
                        {
                            Assert.That(files.Get(first.Id).Path, Is.EqualTo(first.Path), "A retry must retain its own earlier file row");
                        }
                        Assert.That(completion.IsImported(tracked, history.FindByDownloadId(download.DownloadId)), Is.True);
                    }

                    var saved = files.GetFilesByBook(book.Id).OrderBy(file => file.Part).ToList();
                    Assert.That(saved.Select(file => file.Part), Is.EqualTo(Enumerable.Range(1, parts)));
                    PartAssignmentHelper.NormalizeBookFilesByEdition(saved);
                    Assert.That(saved.Select(file => file.PartCount), Is.All.EqualTo(parts));
                    Assert.That(saved.Select(file => File.ReadAllText(file.Path)), Is.EqualTo(Enumerable.Range(1, parts).Select(part => "new part " + part)));
                    Assert.That(books.All(), Has.Count.EqualTo(1), "Resuming the grab must not create an alternative book row");
                    Assert.That(books.Get(book.Id).AnyEditionOk, Is.EqualTo(moveSources));
                    Assert.That(editions.Get(edition.Id).Monitored, Is.True);
                    Assert.That(saved.SelectMany(file => file.MatchProvenance.ImportSourcePaths), Is.EquivalentTo(sources));
                    if (!publishHistory)
                    {
                        Assert.That(histories.All(), Is.Empty, "Coverage must survive a restart between the file commit and history publication");
                    }
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
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

        [TestCase("hc:edition:30425", false, false)]
        [TestCase("hc:edition:30425", false, true)]
        [TestCase("gr:30425-ebook", false, false)]
        [TestCase("gr:30425-ebook", false, true)]
        [TestCase("hc:edition:30425", true, false)]
        public void clone_creation_should_stamp_identity_and_preserve_book_monitoring_while_selecting_the_edition(string foreignEditionId, bool failInsert, bool postgres)
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
                Subtitle = "Preserved subtitle", OriginalTitle = "Preserved original title", IsOmnibus = true,
                RelatedBooks = new List<int> { 1, 2 },
                LastSearchTime = DateTime.UtcNow,
                AddOptions = new AddBookOptions { SearchForNewBook = true },
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
                ForeignEditionId = foreignEditionId,
                Asins = new List<string> { "B000000001", "B000000002" }
            };

            var (bookService, bookProxy) = CreateBookService(new List<Book> { book });
            var editionService = CreateEditionService(new List<Edition> { edition });
            ((EditionServiceProxy)(object)editionService).ThrowOnInsert = failInsert;
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
            var result = method.Invoke(service, new object[] { new ImportDecision<LocalBook>(localBook), book, author, true });
            if (failInsert)
            {
                Assert.That(result, Is.Null);
                Assert.That(bookProxy.DeletedBookIds, Is.EqualTo(bookProxy.InsertedBooks.Select(copy => copy.Id)));
                Assert.That(bookProxy.ExistingBooks, Is.EqualTo(new[] { book }));
                return;
            }

            var insertedBook = bookProxy.InsertedBooks.Single();
            var insertedEdition = ((EditionServiceProxy)(object)editionService).InsertedEditions.Single();
            Assert.That(insertedBook.TitleSlug, Does.StartWith("best-served-cold_copy_"));
            Assert.That(insertedBook.UnitKeyHash, Is.Not.Null.And.Not.Empty);
            Assert.That(insertedBook.UnitKeyHash, Has.Length.EqualTo(64));
            Assert.That(insertedBook.MediaType, Is.EqualTo(BookMediaType.Ebook));
            Assert.That(insertedBook.AudiobookMonitored, Is.False);
            Assert.That(insertedBook.EbookMonitored, Is.False);
            Assert.That(insertedEdition.Monitored, Is.True);
            Assert.That(insertedEdition.ForeignEditionId, Is.EqualTo(foreignEditionId));
            Assert.That(insertedEdition.Asins, Is.EqualTo(edition.Asins));
            Assert.That(insertedEdition.Asins, Is.Not.SameAs(edition.Asins));
            Assert.That(insertedBook.RemoteProviderIds, Is.EquivalentTo(book.RemoteProviderIds));
            Assert.That(insertedBook.RemoteProviderIds, Is.Not.SameAs(book.RemoteProviderIds));
            Assert.That(insertedBook.Subtitle, Is.EqualTo(book.Subtitle));
            Assert.That(insertedBook.OriginalTitle, Is.EqualTo(book.OriginalTitle));
            Assert.That(insertedBook.IsOmnibus, Is.True);
            Assert.That(insertedBook.RelatedBooks, Is.EqualTo(book.RelatedBooks).And.Not.SameAs(book.RelatedBooks));
            Assert.That(insertedBook.BaseBookId, Is.EqualTo(book.BaseBookId));
            Assert.That(insertedBook.AddOptions, Is.Null);
            Assert.That(insertedBook.LastSearchTime, Is.Null);
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = new NoOpEventAggregator();
                var savedAuthor = new AuthorRepository(database, events, LogManager.GetCurrentClassLogger()).Insert(new Author
                {
                    Name = author.Name, CleanName = "joeabercrombie", TitleSlug = "joe-abercrombie",
                    Path = @"C:\Ebooks\Joe Abercrombie".AsOsAgnostic(), Added = DateTime.UtcNow
                });
                var repository = new BookRepository(database, events);
                insertedBook.Id = 0;
                insertedBook.AuthorId = savedAuthor.Id;
                repository.Insert(insertedBook);
                var saved = repository.Get(insertedBook.Id);
                Assert.That(saved.Subtitle, Is.EqualTo(book.Subtitle));
                Assert.That(saved.OriginalTitle, Is.EqualTo(book.OriginalTitle));
                Assert.That(saved.RelatedBooks, Is.EqualTo(book.RelatedBooks));
                Assert.That(saved.IsOmnibus, Is.True);
                Assert.That(saved.BaseBookId, Is.EqualTo(book.BaseBookId));
            });

        }

        [TestCase(false, false, BookMediaType.Audiobook)]
        [TestCase(false, true, BookMediaType.Audiobook)]
        [TestCase(true, false, BookMediaType.Audiobook)]
        [TestCase(true, true, BookMediaType.Audiobook)]
        [TestCase(false, false, BookMediaType.Ebook)]
        [TestCase(false, true, BookMediaType.Ebook)]
        [TestCase(true, false, BookMediaType.Ebook)]
        [TestCase(true, true, BookMediaType.Ebook)]
        public void hand_picked_grab_can_schedule_conversion_without_a_blanket_confirmation_hold(bool upgradeAllowed, bool targetHasFiles, BookMediaType siblingMedia)
        {
            var temp = Path.Combine(TestContext.CurrentContext.WorkDirectory, "grab-confirmation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var source = Path.Combine(temp, "incoming.mp3");
                var oldPath = Path.Combine(temp, "existing.m4b");
                File.WriteAllText(source, "incoming audio");
                File.WriteAllText(oldPath, "existing recording");
                var (profile, author, book, edition) = CreateAudiobookConversionGraph(97);
                profile.UpgradeAllowed = upgradeAllowed;
                book.HardcoverBookId = "hc:100";
                var sibling = new Book { Id = book.Id + 1, AuthorId = author.Id, Author = author, HardcoverBookId = book.HardcoverBookId, MediaType = siblingMedia };
                var siblingEdition = new Edition { Id = edition.Id + 1, BookId = sibling.Id, Book = sibling, ForeignEditionId = edition.ForeignEditionId };
                var media = new StubMediaFileService();
                media.Add(new BookFile { Id = 21, EditionId = siblingEdition.Id, Edition = siblingEdition, Path = oldPath, Quality = new QualityModel(Quality.M4B) });
                if (targetHasFiles)
                {
                    media.Add(new BookFile { Id = 22, EditionId = edition.Id, Edition = edition, Path = Path.Combine(temp, "target.m4b"), Quality = new QualityModel(Quality.MP3) });
                    File.WriteAllText(media.FilesByBook.Last().Path, "target recording");
                }

                var mover = new StubMoveBookFiles { DestinationPath = Path.Combine(temp, "output.m4b") };
                var jobs = new RecordingConversionJobService();
                var converter = new SuccessfulM4bConversionService();
                var service = new ImportApprovedBooks(media, new StubMetadataTagService(), Proxy<IMediaInfoExtractor>(),
                    Proxy<IAuthorService>(), Proxy<IBookService>(), CreateEditionService(new List<Edition> { edition, siblingEdition }),
                    new StubRecycleBinProvider(), Proxy<IExtraService>(), mover, Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(), converter, LogManager.GetCurrentClassLogger(), conversionJobService: jobs);
                var local = new LocalBook { Path = source, Author = author, Book = book, Edition = edition,
                    Quality = new QualityModel(Quality.MP3), Size = new FileInfo(source).Length, Modified = File.GetLastWriteTimeUtc(source) };

                var result = service.Import(new List<ImportDecision<LocalBook>> { new(local) }, true,
                    new DownloadClientItem { DownloadId = "manual-grab", DownloadForced = true }, ImportMode.Copy).Single();

                // Hand-picked grabs are automatic; conversion precedes the eventual same-edition swap.
                Assert.That(result.Result, Is.EqualTo(ImportResultType.Pending));
                Assert.That(jobs.Request, Is.Not.Null);
                if (targetHasFiles)
                {
                    Assert.That(File.ReadAllText(Path.Combine(temp, "target.m4b")), Is.EqualTo("target recording"));
                }

                Assert.That(media.DeletedFiles, Is.Empty);
                Assert.That(media.ReplaceCalls, Is.Zero);
                Assert.That(mover.CopyCalls + mover.MoveCalls, Is.Zero);
                Assert.That(converter.ConvertCalls, Is.Zero);
                Assert.That(File.ReadAllText(source), Is.EqualTo("incoming audio"));
                Assert.That(File.ReadAllText(oldPath), Is.EqualTo("existing recording"));
            }
            finally
            {
                Directory.Delete(temp, true);
            }
        }

        [TestCase(false, "independent", BookMediaType.Audiobook)]
        [TestCase(true, "independent", BookMediaType.Audiobook)]
        [TestCase(false, "independent", BookMediaType.Ebook)]
        [TestCase(true, "independent", BookMediaType.Ebook)]
        [TestCase(false, "stale", BookMediaType.Audiobook)]
        [TestCase(true, "stale", BookMediaType.Audiobook)]
        [TestCase(false, "concurrent", BookMediaType.Audiobook)]
        [TestCase(true, "concurrent", BookMediaType.Audiobook)]
        public void confirmed_replacement_must_preserve_other_local_books(bool postgres, string collision, BookMediaType otherMedia)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var temp = Path.Combine(TestContext.CurrentContext.WorkDirectory, "replacement-ownership-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var events = new NoOpEventAggregator();
                    var (profile, author, book, edition) = CreateAudiobookConversionGraph(98);
                    profile.ConvertToQualityId = null;
                    profile.UpgradeAllowed = false;
                    author.Id = 0;
                    author.Path = temp;
                    author.CleanName = "testwriter";
                    author.SortName = "testwriter";
                    author.TitleSlug = "test-writer";
                    author.Added = DateTime.UtcNow;
                    new AuthorRepository(database, events, logger).Insert(author);
                    book.Id = 0;
                    book.CleanTitle = "blacksheep";
                    book.AuthorId = author.Id;
                    book.Added = DateTime.UtcNow;
                    book.HardcoverBookId = "hc:100";
                    var books = new BookRepository(database, events);
                    books.Insert(book);
                    var other = books.Insert(new Book { AuthorId = author.Id, Author = author, Title = book.Title,
                        TitleSlug = "other-copy", CleanTitle = book.CleanTitle, MediaType = otherMedia, Added = DateTime.UtcNow,
                        HardcoverBookId = book.HardcoverBookId });
                    edition.Id = 0;
                    edition.BookId = book.Id;
                    var editions = new EditionRepository(database, events);
                    editions.Insert(edition);
                    var otherEdition = editions.Insert(new Edition { BookId = other.Id, Book = other, Title = edition.Title,
                        ForeignEditionId = edition.ForeignEditionId, ReadingFormatId = otherMedia == BookMediaType.Audiobook ? 2 : 3 });
                    var source = Path.Combine(temp, "incoming.m4b");
                    var old = Path.Combine(temp, "old.m4b");
                    var destination = Path.Combine(temp, "new.m4b");
                    var independent = Path.Combine(temp, otherMedia == BookMediaType.Audiobook ? "other.m4b" : "other.epub");
                    File.WriteAllText(source, "incoming recording");
                    File.WriteAllText(old, "old target recording");
                    File.WriteAllText(independent, "other recording");
                    var files = new MediaFileRepository(database, events);
                    BookFile FileRow(Edition owner, string path) => new()
                    {
                        Edition = owner, EditionId = owner.Id, Path = path, Size = 18,
                        Quality = new QualityModel(owner.Book.MediaType == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB),
                        MediaType = owner.Book.MediaType == BookMediaType.Audiobook ? "audiobook" : "ebook", DurationSeconds = 120,
                        MediaInfo = new MediaInfoModel(),
                        DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow
                    };
                    var oldRow = files.Insert(FileRow(edition, old));
                    var otherRow = FileRow(otherEdition, collision == "independent" ? independent : destination);
                    if (collision != "concurrent")
                    {
                        files.Insert(otherRow);
                    }

                    var replaceCalls = 0;
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        switch (method.Name)
                        {
                            case nameof(IMediaFileService.GetFilesByBook): return files.GetFilesByBook((int)args[0]);
                            case nameof(IMediaFileService.GetFilesByBooks): return files.GetFilesByBooks((List<int>)args[0]);
                            case nameof(IMediaFileService.GetFileWithPath): return args[0] is List<string> paths ? files.GetFileWithPath(paths) : files.GetFileWithPath((string)args[0]);
                            case nameof(IMediaFileService.GetFilesByEdition): return files.GetFilesByEdition((int)args[0]);
                            case nameof(IMediaFileService.ReplaceMany):
                                replaceCalls++;
                                if (collision == "concurrent" && replaceCalls == 1)
                                {
                                    files.Insert(otherRow);
                                }

                                if (args.Length == 5)
                                    ((Action<Action<IDbConnection, IDbTransaction>>)args[4])((connection, transaction) =>
                                        files.ReplaceMany((List<BookFile>)args[0], (List<BookFile>)args[1], connection, transaction));
                                else
                                    files.ReplaceMany((List<BookFile>)args[0], (List<BookFile>)args[1]);
                                return null;
                            case nameof(IMediaFileService.Update): files.Update((BookFile)args[0]); return null;
                            default: throw new InvalidOperationException("Unexpected file operation " + method.Name);
                        }
                    });
                    var mover = new StubMoveBookFiles { DestinationPath = destination, TransferFilesOnDisk = true };
                    var recycle = new StubRecycleBinProvider();
                    var editionService = new EditionService(editions, events, logger);
                    var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name == nameof(IAuthorService.GetAuthors) ? new List<Author> { author } : author);
                    var bookService = new BookService(books, editionService, events, authorService, media,
                        Proxy<NzbDrone.Core.RootFolders.IRootFolderService>(), null, null, logger);
                    var service = new ImportApprovedBooks(media, new StubMetadataTagService(), new StubMediaInfoExtractor(),
                        authorService, bookService, editionService,
                        recycle, Proxy<IExtraService>(), mover, Proxy<IHistoryService>(),
                        Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), events,
                        Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(),
                        Proxy<IQualityProfileService>(), Proxy<IM4bConversionService>(), logger);
                    var local = new LocalBook { Path = source, Author = author, Book = book, Edition = edition,
                        Quality = new QualityModel(Quality.M4B), IsManualImport = true,
                        Size = new FileInfo(source).Length, Modified = File.GetLastWriteTimeUtc(source) };
                    List<NzbDrone.Core.MediaFiles.BookImport.ImportResult> Apply() => service.Import(new List<ImportDecision<LocalBook>> { new(local) }, true,
                        new DownloadClientItem { DownloadId = "confirmed-manual", DownloadForced = true }, ImportMode.Copy);

                    if (collision == "independent")
                    {
                        Assert.That(Apply().Single().Result, Is.EqualTo(ImportResultType.Imported));
                        Assert.That(files.GetFilesByBook(book.Id).Single().Path, Is.EqualTo(destination));
                        Assert.That(File.Exists(old), Is.False);
                        Assert.That(File.ReadAllText(destination), Is.EqualTo("incoming recording"));
                    }
                    else
                    {
                        Assert.That(Apply().Single().Errors.Single(), Does.Contain("another book"));

                        Assert.That(files.GetFilesByBook(book.Id).Single().Id, Is.EqualTo(oldRow.Id));
                        Assert.That(File.ReadAllText(old), Is.EqualTo("old target recording"));
                        Assert.That(File.Exists(destination), Is.False);
                        Assert.That(recycle.DeletedFiles, Is.Empty);
                    }

                    Assert.That(files.Get(otherRow.Id).EditionId, Is.EqualTo(otherEdition.Id));
                    Assert.That(files.GetFilesByBook(other.Id).Single().Id, Is.EqualTo(otherRow.Id));
                    Assert.That(File.ReadAllText(independent), Is.EqualTo("other recording"));
                    Assert.That(File.ReadAllText(source), Is.EqualTo("incoming recording"));
                }
                finally
                {
                    Directory.Delete(temp, true);
                }
            });
        }

        [TestCase(true, false, TestName = "manual_replace_should_commit_when_optional_tag_write_fails")]
        [TestCase(false, true, TestName = "manual_replace_should_restore_old_files_and_rows_when_atomic_persistence_fails")]
        public void explicit_user_replace_should_be_failure_safe_across_tagging_and_persistence(bool throwOnTagWrite, bool throwOnReplace)
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
                    Monitored = true,
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

                book.Editions = new List<Edition> { existingEdition, incomingEdition };
                var mediaFileService = new StubMediaFileService { ThrowOnReplace = throwOnReplace };
                mediaFileService.FilesByBook.AddRange(new[]
                {
                    new BookFile
                    {
                        Id = 1492,
                        Path = oldEpub,
                        EditionId = existingEdition.Id,
                        Edition = existingEdition,
                        Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() }
                    },
                    new BookFile
                    {
                        Id = 1493,
                        Path = oldMobi,
                        EditionId = existingEdition.Id,
                        Edition = existingEdition,
                        Quality = new QualityModel { Quality = Quality.MOBI, Revision = new Revision() }
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
                    CreateBookService(new List<Book> { book }).Service,
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
                    IsManualImport = true,
                    Size = new FileInfo(sourcePath).Length,
                    Modified = File.GetLastWriteTimeUtc(sourcePath)
                };

                localBook.ConfirmedReplacementFiles = mediaFileService.FilesByBook.Select(file => new ManualImportReplacementFile
                {
                    Id = file.Id, BookId = file.Edition.BookId, Path = file.Path
                }).ToList();

                if (throwOnReplace)
                {
                    Assert.Throws<InvalidOperationException>(() => service.Import(
                        new List<ImportDecision<LocalBook>> { new(localBook) },
                        replaceExisting: true,
                        downloadClientItem: null,
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
                        downloadClientItem: null,
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
        public void conversion_requires_every_source_for_its_book_but_not_another_book(
            [Values(false, true)] bool postgres, [Values(0, 50, 51)] int failedBookId)
        {
            Chaptarr.Core.Test.Common.DualDatabaseFixture.Run(postgres, database =>
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
                            LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                        conversionJobService: CreateConversionJobs(conversion, null, null, new StubMediaFileService(), database));

                    var matched = new LocalBook
                    {
                        IsManualImport = false,
                        Path = tempPath,
                        Book = book,
                        Author = author,
                        Edition = edition,
                        Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                    };

                    var unmatched = new LocalBook
                    {
                        IsManualImport = false,
                        Path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"conversion-match-gate-unmatched-{Guid.NewGuid():N}.mp3"),
                        Book = failedBookId == 0 ? null : new Book { Id = failedBookId, MediaType = BookMediaType.Audiobook },
                        Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                    };

                    var results = ImportWithConversionWorker(service,
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
                    Assert.That(results.SelectMany(r => r.Errors), Has.Some.Contains("NO_MATCH"));
                    var blocked = failedBookId == 0 || failedBookId == book.Id;
                    Assert.That(results.SelectMany(r => r.Errors).Any(error => error.Contains("required file")), Is.EqualTo(blocked));
                    if (blocked)
                    {
                        var error = results.SelectMany(result => result.Errors).Single(message => message.Contains("required file"));
                        Assert.That(error, Does.Contain(unmatched.Path).And.Contain("NO_MATCH"));
                        Assert.That(error, Does.Not.Contain("delete").IgnoreCase);
                    }
                    Assert.That(conversion.ConvertCalls, Is.EqualTo(blocked ? 0 : 1));
                    if (blocked) Assert.That(mover.PreviewCalls, Is.Zero);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
            });
        }

        [Test]
        public void should_convert_tracked_automatic_download()
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
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    conversionJobService: CreateConversionJobs(conversion, null, null, new StubMediaFileService()));

                var localBook = new LocalBook
                {
                    IsManualImport = false,
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var results = ImportWithConversionWorker(service,
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
                Assert.That(mover.PreviewCalls, Is.EqualTo(2), "The initial enqueue and the post-worker import sweep each preview the destination.");
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
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    conversionJobService: CreateConversionJobs(conversion, null, null, new StubMediaFileService()));

                var localBook = new LocalBook
                {
                    IsManualImport = false,
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var results = ImportWithConversionWorker(service,
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
                Assert.That(mover.PreviewCalls, Is.EqualTo(2), "The initial enqueue and the post-worker import sweep each preview the destination.");
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
                    configService: CreateConfigService(concurrentDownloads, maxCpuThreads),
                    conversionJobService: CreateConversionJobs(conversion, null, CreateConfigService(concurrentDownloads, maxCpuThreads), new StubMediaFileService()));

                var decisions = paths.Select(path => new ImportDecision<LocalBook>(new LocalBook
                {
                    IsManualImport = false,
                    Path = path,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                })).ToList();

                var results = ImportWithConversionWorker(service,
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
                    configService,
                    conversionJobService: CreateConversionJobs(conversion, null, configService, new StubMediaFileService()));

                var localBook = new LocalBook
                {
                    IsManualImport = false,
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var results = ImportWithConversionWorker(service,
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
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    conversionJobService: CreateConversionJobs(conversion, null, null, new StubMediaFileService()));

                var localBook = new LocalBook
                {
                    IsManualImport = false,
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var results = ImportWithConversionWorker(service,
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
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    conversionJobService: CreateConversionJobs(conversion, null, null, mediaFileService));

                var localBook = new LocalBook
                {
                    IsManualImport = false,
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var results = ImportWithConversionWorker(service,
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
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    conversionJobService: CreateConversionJobs(conversion, null, null, mediaFileService));

                var localBook = new LocalBook
                {
                    IsManualImport = false,
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var results = ImportWithConversionWorker(service,
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
                book.Editions = new List<Edition> { edition };
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
                    CreateBookService(new List<Book> { book }).Service,
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
                    conversionJobService: CreateConversionJobs(conversion, null, null, mediaFileService));

                var localBook = new LocalBook
                {
                    IsManualImport = false,
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

                var results = ImportWithConversionWorker(service,
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
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    conversionJobService: CreateConversionJobs(conversion, null, null, new StubMediaFileService()));

                LocalBook LocalBookFor(string path) => new()
                {
                    IsManualImport = false,
                    Path = path,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                ImportWithConversionWorker(service,
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
                    coverMapper: coverMapper,
                    conversionJobService: CreateConversionJobs(conversion, null, null, new StubMediaFileService()));

                var localBook = new LocalBook
                {
                    IsManualImport = false,
                    Path = sourcePath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                ImportWithConversionWorker(service,
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
        public void should_pass_matched_provider_chapters_to_converter_without_staging_in_import()
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
                    CreateBookService(new List<Book> { book }).Service,
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
                    conversionJobService: CreateConversionJobs(conversion, null, null, new StubMediaFileService()));

                LocalBook LocalBookFor(string path) => new()
                {
                    IsManualImport = false,
                    Path = path,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                ImportWithConversionWorker(service,
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
                Assert.That(conversion.LastInputFiles, Is.EqualTo(new[] { part1, part2 }));
                Assert.That(Directory.Exists(Path.Combine(conversion.LastOptions.TempDirectory, "provider-chapters")), Is.False);
                var chaptersTxt = conversion.LastOptions.TagOptions.ChaptersTxtContent;
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
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    conversionJobService: CreateConversionJobs(conversion, null, null, new StubMediaFileService()));

                var localBook = new LocalBook
                {
                    IsManualImport = false,
                    Path = part1,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                ImportWithConversionWorker(service,
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

        [TestCase(0)]
        [TestCase(8)]
        public void should_retain_and_reuse_converted_artifact_when_import_fails_after_conversion(int ageInDays)
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
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    conversionJobService: CreateConversionJobs(firstConversion, null, null, new StubMediaFileService()));

                var firstLocalBook = new LocalBook
                {
                    IsManualImport = false,
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                var firstResults = ImportWithConversionWorker(firstService,
                    new List<ImportDecision<LocalBook>> { new(firstLocalBook) },
                    replaceExisting: false,
                    downloadClientItem: new DownloadClientItem { DownloadId = "conversion-retain" },
                    importMode: ImportMode.Move,
                    cancellationToken: CancellationToken.None);

                Assert.That(firstResults[0].Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(firstConversion.ConvertCalls, Is.EqualTo(1));
                Assert.That(File.Exists(firstConversion.LastOutputFile), Is.True);
                Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(firstConversion.LastOutputFile), "conversion-artifact.json")), Is.True);

                Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(firstConversion.LastOutputFile), DateTime.UtcNow.AddDays(-ageInDays));

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
                        LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"),
                    conversionJobService: CreateConversionJobs(secondConversion, null, null, new StubMediaFileService()));

                var secondLocalBook = new LocalBook
                {
                    IsManualImport = false,
                    Path = tempPath,
                    Book = book,
                    Author = author,
                    Edition = edition,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
                };

                ImportWithConversionWorker(secondService,
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
                    EncoderSucceeded = true, OutputSize = new FileInfo(request.OutputPath).Length,
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

        [TestCase(false)]
        [TestCase(true)]
        public void completed_download_conversion_should_enqueue_and_return_pending_without_running_converter(bool providerChapters)
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
                var mediaInfo = new StubMediaInfoExtractor();
                if (providerChapters)
                {
                    mediaInfo.Durations[sourcePath] = TimeSpan.FromMinutes(20);
                    edition.DurationSeconds = 1200;
                    edition.Chapters = new List<EditionChapter>
                    {
                        new() { Title = "Opening", StartOffsetMs = 0, LengthMs = 600000 },
                        new() { Title = "Closing", StartOffsetMs = 600000, LengthMs = 600000 }
                    };
                }

                var conversion = new SuccessfulM4bConversionService();
                var jobs = new RecordingConversionJobService();
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
                Assert.That(jobs.Request.Sources.Select(source => source.Path), Is.EqualTo(new[] { sourcePath }));
                Assert.That(Directory.Exists(Path.Combine(jobs.Request.WorkFolder, "provider-chapters")), Is.False);
                if (providerChapters)
                {
                    Assert.That(jobs.Request.TagOptions.ChaptersTxtContent, Does.Contain("Opening"));
                }
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
        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public void sent_grab_should_import_and_record_history_on_the_original_unmonitored_book(bool postgres, BookMediaType media) => RunCapturedSentImport(postgres, media, false);

        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public void alternative_sent_grab_should_preserve_original_row_edition_and_later_pin(bool postgres, BookMediaType media) => RunCapturedSentImport(postgres, media, true);

        private static void RunCapturedSentImport(bool postgres, BookMediaType media, bool alternative)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var temp = Path.Combine(TestContext.CurrentContext.WorkDirectory, "sent-grab-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    var extension = media == BookMediaType.Audiobook ? ".m4b" : ".epub";
                    var source = Path.Combine(temp, "incoming" + extension);
                    var destination = Path.Combine(temp, "library" + extension);
                    File.WriteAllText(source, "controlled import contents");
                    var noop = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IEventAggregator>((method, args) => null);
                    var author = new AuthorRepository(database, noop, logger).Insert(new Author
                    {
                        Name = "Test Writer", CleanName = "testwriter", SortName = "testwriter", TitleSlug = "test-writer", Added = DateTime.UtcNow,
                        Path = temp, AudiobookPath = temp, EbookPath = temp, AudiobookRootFolderPath = temp, EbookRootFolderPath = temp,
                        AudiobookMonitored = true, EbookMonitored = true
                    });
                    var quality = media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB;
                    var profile = new QualityProfile { Id = 1, Name = "Controlled profile", ProfileType = media == BookMediaType.Audiobook ? ProfileType.Audiobook : ProfileType.Ebook,
                        Items = new List<QualityProfileQualityItem> { new() { Quality = quality, Allowed = true } } };
                    author.AudiobookQualityProfile = profile; author.EbookQualityProfile = profile;
                    var books = new BookRepository(database, noop);
                    Book InsertBook(string slug, BookMediaType type)
                    {
                        var book = new Book { AuthorId = author.Id, Author = author, Title = "Paper Lanterns", CleanTitle = "paperlanterns", TitleSlug = slug,
                            Added = DateTime.UtcNow, MediaType = type, HardcoverBookId = "hc:shared-work", BaseBookId = "hc:shared-work" };
                        book.SetMonitored(true); return books.Insert(book);
                    }
                    var x = InsertBook("grabbed-x", media); var y = InsertBook("independent-y", media);
                    var other = InsertBook("opposite-side", media == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook);
                    var editions = new EditionRepository(database, noop);
                    var edition = editions.Insert(new Edition { BookId = x.Id, Book = x, Title = x.Title, ForeignEditionId = "hc:edition:controlled",
                        Format = media == BookMediaType.Audiobook ? "audiobook" : "ebook", ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3, Monitored = true });
                    x.Editions = new List<Edition> { edition };
                    var yEdition = alternative ? editions.Insert(new Edition { BookId = y.Id, Book = y, Title = y.Title,
                        ForeignEditionId = "hc:edition:alternative", ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3, Monitored = true }) : null;
                    y.Editions = yEdition == null ? new List<Edition>() : new List<Edition> { yEdition };
                    var histories = new HistoryRepository(database, noop);
                    var history = new HistoryService(histories, logger);
                    var remote = new RemoteBook { Author = author, Books = new List<Book> { x }, ReleaseSource = ReleaseSourceType.Rss,
                        Release = new ReleaseInfo { Title = "Test Writer - Paper Lanterns", Indexer = "Controlled Indexer", PublishDate = DateTime.UtcNow,
                            DownloadProtocol = DownloadProtocol.Usenet },
                        ParsedBookInfo = new ParsedBookInfo { AuthorName = author.Name, BookTitle = x.Title, Quality = new QualityModel(quality) } };
                    if (alternative) { remote.Books.Add(y); remote.AlternativeCopies = true; remote.TargetEditionIds = new Dictionary<int,int> { [x.Id]=edition.Id,[y.Id]=yEdition.Id }; }
                    var client = new DownloadClientItem { DownloadId = "c20-sent-grab", Title = "renamed-client-file", Status = DownloadItemStatus.Completed,
                        DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "Controlled Client", Type = "ControlledClient" }, CanMoveFiles = false };
                    history.Handle(new BookGrabbedEvent(remote) { DownloadId = client.DownloadId, DownloadClient = "ControlledClient", DownloadClientName = "Controlled Client" });
                    books.SetMonitored(new[] { x.Id }, false); x.SetMonitored(false);
                    Edition laterPin = null;
                    if (alternative)
                    {
                        edition.Monitored = false; editions.Update(edition);
                        laterPin = editions.Insert(new Edition { BookId=x.Id,Book=x,Title=x.Title,ForeignEditionId="hc:edition:later-pin",ReadingFormatId=edition.ReadingFormatId,Monitored=true,ManualAdd=true });
                        x.AnyEditionOk = false; books.Update(x); x.Editions.Add(laterPin);
                    }
                    var tracked = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadService(
                        Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Parser.IParsingService>((method, args) =>
                        {
                            Assert.That(method.Name, Is.EqualTo(nameof(NzbDrone.Core.Parser.IParsingService.Map)));
                            Assert.That(args.Length, Is.EqualTo(3));
                            Assert.That(args[1], Is.EqualTo(author.Id));
                            var ids = ((IEnumerable<int>)args[2]).ToList();
                            Assert.That(ids, Is.EqualTo(alternative ? new[] { y.Id,x.Id } : new[] { x.Id }));
                            return new RemoteBook { Author = author, Books = books.FindExisting(ids).ToList(), ParsedBookInfo = (ParsedBookInfo)args[0] };
                        }),
                        new NzbDrone.Common.Cache.CacheManager(), history, noop,
                        Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>((method, args) => null),
                        Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<ICustomFormatCalculationService>((method, args) => new List<CustomFormat>()), logger,
                        new Chaptarr.Core.Test.Download.NoopDownloadClientFileSnapshotService()).TrackDownload(
                            new DownloadClientDefinition { Id = 1, Protocol = DownloadProtocol.Usenet }, client);
                    Assert.That(tracked.RemoteBook.Books.Select(book => book.Id), Is.EquivalentTo(alternative ? new[] { x.Id,y.Id } : new[] { x.Id }));
                    Assert.That(tracked.RemoteBook.Books.First(book=>book.Id==x.Id).IsMonitored(), Is.False);
                    var local = new LocalBook { Path = source, Book = x, Author = author, Edition = edition, Quality = new QualityModel(quality),
                        Size = new FileInfo(source).Length, Modified = File.GetLastWriteTimeUtc(source), DownloadClientBookInfo = tracked.RemoteBook.ParsedBookInfo,
                        SceneName = tracked.RemoteBook.Release.Title, Part = 1, PartCount = 1 };
                    if (alternative) local.AlternativeCopyIncompatibleEditionIds = new Dictionary<int,int>();
                    var decision = new ImportDecision<LocalBook>(local);
                    var downloadImporter = typeof(DownloadedBooksImportService).GetConstructors().Single();
                    var args = downloadImporter.GetParameters().Select(parameter => parameter.ParameterType == typeof(Logger) ? (object)logger :
                        parameter.ParameterType == typeof(NzbDrone.Core.Queue.IQueueService) ? ImportLifecycleStubs.EmptyQueue() :
                        parameter.ParameterType == typeof(IConversionJobService) ? ImportLifecycleStubs.NoConversionJobs() : null).ToArray();
                    var downloadService = (DownloadedBooksImportService)downloadImporter.Invoke(args);
                    typeof(DownloadedBooksImportService).GetMethod("EnforceTrackedDownloadTargetBooks", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(downloadService, new object[] { new List<ImportDecision<LocalBook>> { decision }, tracked.RemoteBook, client });
                    Assert.That(decision.Approved, Is.True);
                    var files = new MediaFileRepository(database, noop);
                    var mediaFiles = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        if (method.Name == nameof(IMediaFileService.ReplaceMany)) { files.ReplaceMany((List<BookFile>)args[0], (List<BookFile>)args[1]); return null; }
                        if (method.Name == nameof(IMediaFileService.GetFilesByBook)) return files.GetFilesByBook((int)args[0]);
                        if (method.Name == nameof(IMediaFileService.GetFilesByEdition)) return files.GetFilesByEdition((int)args[0]);
                        if (method.Name == nameof(IMediaFileService.GetFileWithPath)) return args[0] is List<string> paths ? files.GetFileWithPath(paths) : files.GetFileWithPath((string)args[0]);
                        throw new InvalidOperationException("Unexpected media file operation " + method.Name);
                    });
                    var eventWriter = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IEventAggregator>((method, args) =>
                    {
                        if (args[0] is NzbDrone.Core.MediaFiles.Events.TrackImportedEvent message) history.Handle(message);
                        return null;
                    });
                    var (bookService, bookProxy) = CreateBookService(new List<Book> { x, y, other });
                    if (alternative)
                    {
                        var legacy = bookService;
                        bookService = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((method,args) =>
                        {
                            if (method.Name == nameof(IBookService.InsertMany)) { books.InsertMany((List<Book>)args[0]); return null; }
                            if (method.Name == nameof(IBookService.UpdateBook)) { books.Update((Book)args[0]); return args[0]; }
                            if (method.Name == nameof(IBookService.DeleteBook)) { books.Delete((int)args[0]); return null; }
                            if (method.Name == nameof(IBookService.GetBooksByAuthorId)) return books.All().Where(book=>book.AuthorId==(int)args[0]).ToList();
                            if (method.Name == nameof(IBookService.GetBook)) return books.Get((int)args[0]);
                            return method.Invoke(legacy,args);
                        });
                    }
                    var importEditions = alternative ? (IEditionService)new EditionService(editions,noop,logger) : CreateEditionService(new List<Edition> { edition });
                    var importer = new ImportApprovedBooks(mediaFiles, new StubMetadataTagService(), new StubMediaInfoExtractor(), Proxy<IAuthorService>(),
                        bookService, importEditions, Proxy<IRecycleBinProvider>(), Proxy<IExtraService>(),
                        new StubMoveBookFiles { DestinationPath = destination, TransferFilesOnDisk = true }, history,
                        Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>((method, args) => null), eventWriter,
                        Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(), Proxy<IQualityProfileService>(), Proxy<IM4bConversionService>(), logger);
                    var result = importer.Import(new List<ImportDecision<LocalBook>> { decision }, false, client, ImportMode.Copy);
                    Assert.That(result.Single().Result, Is.EqualTo(ImportResultType.Imported));
                    Assert.That(File.Exists(destination), Is.True);
                    Assert.That(files.All().Single().EditionId, Is.EqualTo(edition.Id));
                    Assert.That(files.GetFilesByBook(x.Id), Has.Count.EqualTo(1));
                    Assert.That(files.GetFilesByBook(y.Id), Is.Empty); Assert.That(files.GetFilesByBook(other.Id), Is.Empty);
                    Assert.That(bookProxy.InsertedBooks, Is.Empty);
                    Assert.That(books.Get(x.Id).IsMonitored(), Is.False);
                    var saved = history.FindByDownloadId(client.DownloadId);
                    Assert.That(saved.Where(item=>item.EventType==EntityHistoryEventType.BookFileImported).Select(item => item.BookId), Is.EqualTo(new[] { x.Id }));
                    Assert.That(saved.Where(item=>item.EventType==EntityHistoryEventType.Grabbed).Select(item=>item.BookId), Is.EquivalentTo(alternative?new[]{x.Id,y.Id}:new[]{x.Id}));
                    if (alternative)
                    {
                        Assert.That(saved.Single(item=>item.EventType==EntityHistoryEventType.BookFileImported).EditionId, Is.EqualTo(edition.Id));
                        Assert.That(editions.Get(edition.Id).Monitored, Is.False); Assert.That(editions.Get(laterPin.Id).Monitored, Is.True);
                        Assert.That(editions.Get(laterPin.Id).ManualAdd, Is.True);
                        Assert.That(books.All(), Has.Count.EqualTo(3));
                    }
                    TestContext.Progress.WriteLine($"C20_SENT database={database.DatabaseType} media={media} originalBook={x.Id} monitoredAfter={books.Get(x.Id).IsMonitored()} fileBook={files.All().Single().Edition.BookId} historyRows={saved.Count} targetBookIds={string.Join(',', saved.Select(item => item.BookId).Distinct())}");
                }
                finally { Directory.Delete(temp, true); }
            });
        }

    }
}

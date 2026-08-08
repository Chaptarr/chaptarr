using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;

namespace NzbDrone.Core.MediaFiles
{
    internal sealed class RenameFilesResult
    {
        public int SelectedCount { get; set; }
        public int AttemptedCount { get; set; }
        public int RenamedCount { get; set; }
        public int CollisionSkippedCount { get; set; }
        public int AlreadyInPlaceCount { get; set; }
        public int FailedCount { get; set; }
    }

    public interface IRenameBookFileService
    {
        List<RenameBookFilePreview> GetRenamePreviews(int authorId, string mediaType = null);
        List<RenameBookFilePreview> GetRenamePreviews(int authorId, int bookId);
    }

    public class RenameBookFileService : IRenameBookFileService, IExecute<RenameFilesCommand>, IExecute<RenameAuthorCommand>
    {
        private readonly IAuthorService _authorService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMoveBookFiles _bookFileMover;
        private readonly IEventAggregator _eventAggregator;
        private readonly IBuildFileNames _filenameBuilder;
        private readonly INamingConfigService _namingConfigService;
        private readonly IAuthorFolderPathResolver _authorFolderPathResolver;
        private readonly IEbookColocationPlanner _ebookColocationPlanner;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public RenameBookFileService(IAuthorService authorService,
                                        IMediaFileService mediaFileService,
                                        IMoveBookFiles bookFileMover,
                                        IEventAggregator eventAggregator,
                                        IBuildFileNames filenameBuilder,
                                        INamingConfigService namingConfigService,
                                        IAuthorFolderPathResolver authorFolderPathResolver,
                                        IEbookColocationPlanner ebookColocationPlanner,
                                        IDiskProvider diskProvider,
                                        Logger logger)
        {
            _authorService = authorService;
            _mediaFileService = mediaFileService;
            _bookFileMover = bookFileMover;
            _eventAggregator = eventAggregator;
            _filenameBuilder = filenameBuilder;
            _namingConfigService = namingConfigService;
            _authorFolderPathResolver = authorFolderPathResolver;
            _ebookColocationPlanner = ebookColocationPlanner;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public List<RenameBookFilePreview> GetRenamePreviews(int authorId, string mediaType = null)
        {
            var author = _authorService.GetAuthor(authorId);
            var files = _mediaFileService.GetFilesByAuthor(authorId);

            if (IsMediaTypeScoped(mediaType))
            {
                files = FilterByMediaType(files, mediaType).ToList();
            }

            _logger.Trace($"got {files.Count} files");

            return GetPreviews(author, files)
                .OrderByDescending(e => e.BookId)
                .ThenBy(e => e.ExistingPath)
                .ToList();
        }

        public List<RenameBookFilePreview> GetRenamePreviews(int authorId, int bookId)
        {
            var author = _authorService.GetAuthor(authorId);
            var files = _mediaFileService.GetFilesByBook(bookId);

            return GetPreviews(author, files)
                .OrderBy(e => e.ExistingPath).ToList();
        }

        private IEnumerable<RenameBookFilePreview> GetPreviews(Author author, List<BookFile> files)
        {
            var renameFiles = files.Where(x => x.CalibreId == 0).ToList();
            EnsurePartNumbers(renameFiles);
            var baseNamingConfig = _namingConfigService.GetConfig();
            var audiobookNamingConfig = CloneNamingConfig(baseNamingConfig);
            audiobookNamingConfig.RenameBooks = true;
            var ebookNamingConfig = CloneNamingConfig(baseNamingConfig);
            ebookNamingConfig.EbookRenameBooks = true;

            // Pass 1: compute target directories for audiobook files that are part of this rename batch.
            var batchContext = new RenameBatchContext();

            foreach (var f in renameFiles)
            {
                var file = f;

                var edition = file.Edition;
                if (edition?.Book == null)
                {
                    continue;
                }

                var mediaType = GetEffectiveMediaType(file);
                if (string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (file.Quality?.Quality == null)
                {
                    continue;
                }

                var rootFolderPath = author.GetRootFolderForQuality(file.Quality.Quality);
                if (rootFolderPath.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var newName = _filenameBuilder.BuildBookFileName(author, edition, file, audiobookNamingConfig);
                var authorFolderPath = _authorFolderPathResolver.GetAuthorPath(rootFolderPath, author, mediaType);
                var newPath = Path.Combine(authorFolderPath, newName + Path.GetExtension(file.Path));

                var newFolder = Path.GetDirectoryName(newPath);
                var oldFolder = file.Path.IsNotNullOrWhiteSpace() ? Path.GetDirectoryName(file.Path) : null;
                if (!newFolder.IsNullOrWhiteSpace() && !oldFolder.IsNullOrWhiteSpace())
                {
                    batchContext.AddAudiobookFolderRemap(oldFolder, newFolder);
                }
            }

            // Pass 2: compute final preview paths (ebooks may be clamped to audiobook target folders).
            foreach (var f in renameFiles)
            {
                var file = f;

                var book = file.Edition;
                var bookFilePath = file.Path;

                if (book == null)
                {
                    _logger.Warn("File ({0}) is not linked to a book", bookFilePath);
                    continue;
                }

                var mediaType = GetEffectiveMediaType(file);

                var namingConfig = string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase)
                    ? ebookNamingConfig
                    : audiobookNamingConfig;

                var newName = _filenameBuilder.BuildBookFileName(author, book, file, namingConfig);

                _logger.Trace($"got name {newName}");

                if (file.Quality?.Quality == null)
                {
                    _logger.Warn("File ({0}) has no quality, cannot preview rename", bookFilePath);
                    continue;
                }

                var rootFolderPath = author.GetRootFolderForQuality(file.Quality.Quality);
                if (rootFolderPath.IsNullOrWhiteSpace())
                {
                    _logger.Warn("No root folder configured for '{0}' ({1}), cannot preview rename for file: {2}",
                        author.Name, mediaType ?? "unknown", bookFilePath);
                    continue;
                }
                var authorFolderPath = _authorFolderPathResolver.GetAuthorPath(rootFolderPath, author, mediaType);
                var extension = Path.GetExtension(bookFilePath);
                var newPath = Path.Combine(authorFolderPath, newName + extension);

                if (string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase))
                {
                    var filenameOnly = Path.GetFileName(newName) + extension;
                    var colocationPlan = _ebookColocationPlanner.Plan(file, author, book, filenameOnly, batchContext);
                    if (colocationPlan.Applies)
                    {
                        newPath = colocationPlan.PrimaryPath;
                    }
                }

                _logger.Trace($"got path {newPath}");

                if (!bookFilePath.PathEquals(newPath, StringComparison.Ordinal))
                {
                    yield return new RenameBookFilePreview
                    {
                        AuthorId = author.Id,
                        BookId = book.Id,
                        BookFileId = file.Id,
                        ExistingPath = file.Path,
                        NewPath = newPath
                    };
                }
            }
        }

        private static string GetEffectiveMediaType(BookFile file)
        {
            var mediaType = file.MediaType;
            if (mediaType.IsNullOrWhiteSpace() && file.Quality != null)
            {
                mediaType = BookFile.DetermineMediaType(file.Quality);
            }

            return mediaType;
        }

        private static bool IsMediaTypeScoped(string mediaType)
        {
            return mediaType.IsNotNullOrWhiteSpace() && !string.Equals(mediaType, "all", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<BookFile> FilterByMediaType(IEnumerable<BookFile> files, string mediaType)
        {
            if (!IsMediaTypeScoped(mediaType))
            {
                return files ?? Enumerable.Empty<BookFile>();
            }

            return (files ?? Enumerable.Empty<BookFile>())
                .Where(f => string.Equals(GetEffectiveMediaType(f), mediaType, StringComparison.OrdinalIgnoreCase));
        }

        private static NamingConfig CloneNamingConfig(NamingConfig source)
        {
            return new NamingConfig
            {
                Id = source.Id,

                RenameBooks = source.RenameBooks,
                ReplaceIllegalCharacters = source.ReplaceIllegalCharacters,
                ColonReplacementFormat = source.ColonReplacementFormat,
                StandardBookFormat = source.StandardBookFormat,
                AuthorFolderFormat = source.AuthorFolderFormat,

                EbookRenameBooks = source.EbookRenameBooks,
                EbookReplaceIllegalCharacters = source.EbookReplaceIllegalCharacters,
                EbookColonReplacementFormat = source.EbookColonReplacementFormat,
                EbookStandardBookFormat = source.EbookStandardBookFormat,
                EbookAuthorFolderFormat = source.EbookAuthorFolderFormat
            };
        }

        private static void EnsurePartNumbers(List<BookFile> files)
        {
            PartAssignmentHelper.NormalizeBookFilesByEdition(files);
        }

        internal static string FormatRenameResultMessage(RenameFilesResult result, string authorName)
        {
            if (result == null || result.SelectedCount == 0)
            {
                return $"No files selected to rename for {authorName}.";
            }

            var message = $"Renamed {result.RenamedCount} of {result.SelectedCount} {Pluralize(result.SelectedCount, "file")} for {authorName}";
            var details = new List<string>();

            if (result.CollisionSkippedCount > 0)
            {
                details.Add($"{result.CollisionSkippedCount} skipped because destination already exists");
            }

            if (result.AlreadyInPlaceCount > 0)
            {
                details.Add($"{result.AlreadyInPlaceCount} already in place");
            }

            if (result.FailedCount > 0)
            {
                details.Add($"{result.FailedCount} failed");
            }

            var notEligibleCount = Math.Max(0, result.SelectedCount - result.AttemptedCount);
            if (notEligibleCount > 0)
            {
                details.Add($"{notEligibleCount} not eligible for rename");
            }

            if (details.Any())
            {
                message += "; " + string.Join("; ", details);
            }

            return message + ".";
        }

        private static string Pluralize(int count, string singular)
        {
            return count == 1 ? singular : singular + "s";
        }

        private RenameFilesResult RenameFiles(List<BookFile> bookFiles, Author author, string mediaType = null)
        {
            bookFiles = FilterByMediaType(bookFiles, mediaType).ToList();

            var result = new RenameFilesResult
            {
                SelectedCount = bookFiles?.Count ?? 0
            };

            if (bookFiles == null || bookFiles.Count == 0)
            {
                return result;
            }

            // Fetch all author files so EnsurePartNumbers can assign sequential Part values
            // across complete edition groups, then filter to the requested subset for renaming.
            var allFiles = _mediaFileService.GetFilesByAuthor(author.Id);
            EnsurePartNumbers(allFiles);

            var requestedIds = new HashSet<int>(bookFiles.Select(f => f.Id));
            var filesToRename = allFiles.Where(f => requestedIds.Contains(f.Id)).ToList();

            var renamed = new List<RenamedBookFile>();

            // Don't rename Calibre files.
            // Ensure audiobook files are renamed first so mixed-root ebook colocation can clamp to the updated audiobook folders.
            var ordered = filesToRename
                .Where(x => x.CalibreId == 0)
                .OrderBy(x =>
                {
                    var mt = x.MediaType;
                    if (mt.IsNullOrWhiteSpace() && x.Quality != null)
                    {
                        mt = BookFile.DetermineMediaType(x.Quality);
                    }

                    return string.Equals(mt, "ebook", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                })
                .ThenBy(x => x.Path ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            result.AttemptedCount = ordered.Count;
            var batchContext = new RenameBatchContext();

            foreach (var bookFile in ordered)
            {
                var previousPath = bookFile.Path;

                try
                {
                    _logger.Debug("Renaming book file: {0}", bookFile);
                    _bookFileMover.MoveBookFile(bookFile, author, true, batchContext);

                    _mediaFileService.Update(bookFile);
                    TrackAudiobookFolderMove(batchContext, bookFile, previousPath);

                    if (previousPath.PathEquals(bookFile.Path))
                    {
                        result.AlreadyInPlaceCount++;
                        _logger.Debug("Book file already in place after organize: {0}", bookFile);
                        continue;
                    }

                    renamed.Add(new RenamedBookFile
                    {
                        BookFile = bookFile,
                        PreviousPath = previousPath
                    });
                    result.RenamedCount++;

                    _logger.Debug("Renamed book file: {0}", bookFile);

                    _eventAggregator.PublishEvent(new BookFileRenamedEvent(author, bookFile, previousPath));
                }
                catch (FileAlreadyExistsException ex)
                {
                    result.CollisionSkippedCount++;
                    _logger.Warn("File not renamed, there is already a file at the destination: {0}", ex.Filename);
                }
                catch (DestinationAlreadyExistsException ex)
                {
                    result.CollisionSkippedCount++;
                    _logger.Warn("File not renamed because the destination already exists (naming collision). Source: {0}. {1} Adjust your naming settings (e.g., include subtitle/series/disambiguation) or remove the existing destination file.", previousPath, ex.Message);
                }
                catch (SameFilenameException ex)
                {
                    result.AlreadyInPlaceCount++;
                    _logger.Debug("File not renamed, source and destination are the same: {0}", ex.Filename);
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    _logger.Error(ex, "Failed to rename file {0}", previousPath);
                }
            }

            if (renamed.Any())
            {
                _eventAggregator.PublishEvent(new AuthorRenamedEvent(author, renamed));

                var cleanupRoots = new[]
                {
                    author.AudiobookPath,
                    author.EbookPath,
                    author.Path
                }
                .Where(p => p.IsNotNullOrWhiteSpace())
                .Distinct(PathEqualityComparer.Instance)
                .ToList();

                foreach (var root in cleanupRoots)
                {
                    if (!_diskProvider.FolderExists(root))
                    {
                        continue;
                    }

                    _logger.Debug("Removing Empty Subfolders from: {0}", root);
                    _diskProvider.RemoveEmptySubfolders(root);
                }
            }

            return result;
        }

        private static void TrackAudiobookFolderMove(RenameBatchContext batchContext, BookFile bookFile, string previousPath)
        {
            if (batchContext == null || !string.Equals(GetEffectiveMediaType(bookFile), "audiobook", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var oldFolder = previousPath.IsNotNullOrWhiteSpace() ? Path.GetDirectoryName(previousPath) : null;
            var newFolder = bookFile.Path.IsNotNullOrWhiteSpace() ? Path.GetDirectoryName(bookFile.Path) : null;
            batchContext.AddAudiobookFolderRemap(oldFolder, newFolder);
        }

        public void Execute(RenameFilesCommand message)
        {
            var author = _authorService.GetAuthor(message.AuthorId);
            var bookFiles = message.Files?.Count > 0
                ? _mediaFileService.Get(message.Files)
                : new List<BookFile>();

            if (bookFiles.Count == 0)
            {
                _logger.ProgressInfo(FormatRenameResultMessage(new RenameFilesResult(), author.Name));
                return;
            }

            _logger.ProgressInfo("Renaming {0} files for {1}", bookFiles.Count, author.Name);
            var result = RenameFiles(bookFiles, author);
            _logger.ProgressInfo(FormatRenameResultMessage(result, author.Name));
        }

        public void Execute(RenameAuthorCommand message)
        {
            _logger.Debug("Renaming all files for selected author");
            var authorToRename = _authorService.GetAuthors(message.AuthorIds);

            foreach (var author in authorToRename)
            {
                var bookFiles = _mediaFileService.GetFilesByAuthor(author.Id);
                if (bookFiles.Count == 0)
                {
                    _logger.ProgressInfo(FormatRenameResultMessage(new RenameFilesResult(), author.Name));
                    continue;
                }

                _logger.ProgressInfo("Renaming all files in author: {0}", author.Name);
                var result = RenameFiles(bookFiles, author, message.MediaType);
                _logger.ProgressInfo(FormatRenameResultMessage(result, author.Name));
            }
        }
    }
}

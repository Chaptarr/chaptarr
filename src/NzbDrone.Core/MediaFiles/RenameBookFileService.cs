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
        public int BoundarySkippedCount { get; set; }
    }

    public interface IRenameBookFileService
    {
        List<RenameBookFilePreview> GetRenamePreviews(int authorId, string mediaType = null, bool moveToCanonicalAuthorFolder = false);
        List<RenameBookFilePreview> GetRenamePreviews(int authorId, int bookId);
    }

    public class RenameBookFileService : IRenameBookFileService, IExecute<RenameFilesCommand>, IExecute<RenameAuthorCommand>
    {
        private readonly IAuthorService _authorService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMoveBookFiles _bookFileMover;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public RenameBookFileService(IAuthorService authorService,
                                        IMediaFileService mediaFileService,
                                        IMoveBookFiles bookFileMover,
                                        IEventAggregator eventAggregator,
                                        IDiskProvider diskProvider,
                                        Logger logger)
        {
            _authorService = authorService;
            _mediaFileService = mediaFileService;
            _bookFileMover = bookFileMover;
            _eventAggregator = eventAggregator;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public List<RenameBookFilePreview> GetRenamePreviews(int authorId, string mediaType = null, bool moveToCanonicalAuthorFolder = false)
        {
            var author = _authorService.GetAuthor(authorId);
            var files = _mediaFileService.GetFilesByAuthor(authorId);

            if (IsMediaTypeScoped(mediaType))
            {
                files = FilterByMediaType(files, mediaType).ToList();
            }

            _logger.Trace($"got {files.Count} files");

            return GetPreviews(author, files, moveToCanonicalAuthorFolder)
                .OrderByDescending(e => e.BookId)
                .ThenBy(e => e.ExistingPath)
                .ToList();
        }

        public List<RenameBookFilePreview> GetRenamePreviews(int authorId, int bookId)
        {
            var author = _authorService.GetAuthor(authorId);
            var files = _mediaFileService.GetFilesByBook(bookId);

            return GetPreviews(author, files, moveToCanonicalAuthorFolder: false)
                .OrderBy(e => e.ExistingPath).ToList();
        }

        private IEnumerable<RenameBookFilePreview> GetPreviews(Author author, List<BookFile> files, bool moveToCanonicalAuthorFolder)
        {
            var renameFiles = files.Where(x => x.CalibreId == 0).ToList();
            EnsurePartNumbers(renameFiles);
            // Pass 1: compute target directories for audiobook files that are part of this rename batch.
            var batchContext = new RenameBatchContext();

            foreach (var file in renameFiles.Where(file => !string.Equals(GetEffectiveMediaType(file), "ebook", StringComparison.OrdinalIgnoreCase)))
            {
                var plan = _bookFileMover.GetOrganizeDestination(file, author, moveToCanonicalAuthorFolder, batchContext);
                var newFolder = plan.CanOrganize ? Path.GetDirectoryName(plan.DestinationPath) : null;
                var oldFolder = file.Path.IsNotNullOrWhiteSpace() ? Path.GetDirectoryName(file.Path) : null;
                if (!newFolder.IsNullOrWhiteSpace() && !oldFolder.IsNullOrWhiteSpace())
                {
                    batchContext.AddAudiobookFolderRemap(oldFolder, newFolder);
                }
            }

            // Pass 2: compute final preview paths (ebooks may be clamped to audiobook target folders).
            foreach (var file in renameFiles)
            {
                var plan = _bookFileMover.GetOrganizeDestination(file, author, moveToCanonicalAuthorFolder, batchContext);
                var editionId = file.Edition?.Id ?? file.EditionId;
                var bookId = file.Edition?.BookId ?? 0;
                if (bookId <= 0)
                {
                    bookId = file.Edition?.Book?.Id ?? 0;
                }

                if (!plan.CanOrganize || !file.Path.PathEquals(plan.DestinationPath, StringComparison.Ordinal))
                {
                    yield return new RenameBookFilePreview
                    {
                        AuthorId = author.Id,
                        BookId = bookId,
                        EditionId = editionId,
                        BookFileId = file.Id,
                        ExistingPath = file.Path,
                        NewPath = plan.DestinationPath ?? file.Path,
                        CanOrganize = plan.CanOrganize,
                        Reason = plan.SkipReason
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

        private static void EnsurePartNumbers(List<BookFile> files)
        {
            PartAssignmentHelper.NormalizeBookFilesByEdition(files);
        }

        internal static string FormatRenameResultMessage(RenameFilesResult result, string authorName)
        {
            if (result == null || result.SelectedCount == 0)
            {
                return $"No files selected to organize for {authorName}.";
            }

            var message = $"Organized {result.RenamedCount} of {result.SelectedCount} {Pluralize(result.SelectedCount, "file")} for {authorName}";
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

            if (result.BoundarySkippedCount > 0)
            {
                details.Add($"{result.BoundarySkippedCount} skipped because the current author folder could not be determined");
            }

            var notEligibleCount = Math.Max(0, result.SelectedCount - result.AttemptedCount - result.BoundarySkippedCount);
            if (notEligibleCount > 0)
            {
                details.Add($"{notEligibleCount} not eligible for organize");
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

        private RenameFilesResult RenameFiles(List<BookFile> bookFiles, Author author, string mediaType = null, bool moveToCanonicalAuthorFolder = false)
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
            var cleanupCandidates = new List<(string PreviousPath, string SourceAuthorFolderPath)>();
            var canonicalMoves = new List<(string MediaType, string SourceAuthorFolderPath, string DestinationAuthorFolderPath)>();

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

            var batchContext = new RenameBatchContext();

            foreach (var bookFile in ordered)
            {
                var previousPath = bookFile.Path;
                var plan = _bookFileMover.GetOrganizeDestination(bookFile, author, moveToCanonicalAuthorFolder, batchContext);
                if (!plan.CanOrganize)
                {
                    result.BoundarySkippedCount++;
                    _logger.Warn("Skipping organize for {0}: {1}", previousPath, plan.SkipReason);
                    continue;
                }

                result.AttemptedCount++;

                try
                {
                    _logger.Debug("Organizing book file: {0}", bookFile);
                    _bookFileMover.MoveBookFile(bookFile, author, plan, batchContext);

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
                    cleanupCandidates.Add((previousPath, plan.SourceAuthorFolderPath));
                    if (plan.ShouldUpdateStoredAuthorPath)
                    {
                        canonicalMoves.Add((
                            GetEffectiveMediaType(bookFile),
                            plan.SourceAuthorFolderPath,
                            plan.DestinationAuthorFolderPath));
                    }

                    _logger.Debug("Organized book file: {0}", bookFile);

                    _eventAggregator.PublishEvent(new BookFileRenamedEvent(author, bookFile, previousPath));
                }
                catch (FileAlreadyExistsException ex)
                {
                    result.CollisionSkippedCount++;
                    _logger.Warn("File not organized, there is already a file at the destination: {0}", ex.Filename);
                }
                catch (DestinationAlreadyExistsException ex)
                {
                    result.CollisionSkippedCount++;
                    _logger.Warn("File not organized because the destination already exists (naming collision). Source: {0}. {1} Adjust your naming settings (e.g., include subtitle/series/disambiguation) or remove the existing destination file.", previousPath, ex.Message);
                }
                catch (SameFilenameException ex)
                {
                    result.AlreadyInPlaceCount++;
                    _logger.Debug("File not organized, source and destination are the same: {0}", ex.Filename);
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    _logger.Error(ex, "Failed to organize file {0}", previousPath);
                }
            }

            if (renamed.Any())
            {
                UpdateAuthorPathsAfterCanonicalMoves(author, canonicalMoves);
                _eventAggregator.PublishEvent(new AuthorRenamedEvent(author, renamed));
                CleanupEmptySourceFolders(cleanupCandidates);
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

        private void UpdateAuthorPathsAfterCanonicalMoves(
            Author author,
            List<(string MediaType, string SourceAuthorFolderPath, string DestinationAuthorFolderPath)> canonicalMoves)
        {
            if (author == null || canonicalMoves == null || canonicalMoves.Count == 0)
            {
                return;
            }

            var updated = false;
            var groups = canonicalMoves
                .Where(move => move.DestinationAuthorFolderPath.IsNotNullOrWhiteSpace())
                .GroupBy(move => string.Equals(move.MediaType, "ebook", StringComparison.OrdinalIgnoreCase) ? "ebook" : "audiobook")
                .OrderBy(group => group.Key == "ebook" ? 1 : 0);

            foreach (var group in groups)
            {
                var destinations = group
                    .Select(move => move.DestinationAuthorFolderPath)
                    .Distinct(PathEqualityComparer.Instance)
                    .ToList();

                if (destinations.Count != 1)
                {
                    _logger.Warn("Not updating the stored {0} author path because successful files used {1} different author folders.",
                        group.Key,
                        destinations.Count);
                    continue;
                }

                var destination = destinations[0];
                var sources = group
                    .Select(move => move.SourceAuthorFolderPath)
                    .Where(path => path.IsNotNullOrWhiteSpace())
                    .Distinct(PathEqualityComparer.Instance)
                    .ToList();

                if (group.Key == "ebook")
                {
                    if (!destination.PathEquals(author.EbookPath))
                    {
                        author.EbookPath = destination;
                        updated = true;
                    }
                }
                else if (!destination.PathEquals(author.AudiobookPath))
                {
                    author.AudiobookPath = destination;
                    updated = true;
                }

                if (author.Path.IsNotNullOrWhiteSpace() && sources.Any(source => author.Path.PathEquals(source)))
                {
                    author.Path = destination;
                    updated = true;
                }
            }

            if (updated)
            {
                _authorService.UpdateAuthor(author);
            }
        }

        private void CleanupEmptySourceFolders(List<(string PreviousPath, string SourceAuthorFolderPath)> cleanupCandidates)
        {
            foreach (var candidate in cleanupCandidates ?? new List<(string PreviousPath, string SourceAuthorFolderPath)>())
            {
                var folder = candidate.PreviousPath.IsNotNullOrWhiteSpace()
                    ? Path.GetDirectoryName(candidate.PreviousPath)
                    : null;
                var sourceAuthorFolder = candidate.SourceAuthorFolderPath;

                if (folder.IsNullOrWhiteSpace() || sourceAuthorFolder.IsNullOrWhiteSpace())
                {
                    continue;
                }

                try
                {
                    while (folder.PathEquals(sourceAuthorFolder) || sourceAuthorFolder.IsParentPath(folder))
                    {
                        if (_diskProvider.FolderExists(folder))
                        {
                            _diskProvider.RemoveEmptySubfolders(folder);
                            if (_diskProvider.GetFiles(folder, true).Empty())
                            {
                                _logger.Debug("Removing empty source folder after organize: {0}", folder);
                                _diskProvider.DeleteFolder(folder, true);
                            }
                        }

                        if (folder.PathEquals(sourceAuthorFolder))
                        {
                            break;
                        }

                        folder = Path.GetDirectoryName(folder);
                        if (folder.IsNullOrWhiteSpace())
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to clean empty source folders after organizing {0}", candidate.PreviousPath);
                }
            }
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

            _logger.ProgressInfo("Organizing {0} files for {1}", bookFiles.Count, author.Name);
            var result = RenameFiles(bookFiles, author, moveToCanonicalAuthorFolder: message.MoveToCanonicalAuthorFolder);
            _logger.ProgressInfo(FormatRenameResultMessage(result, author.Name));
        }

        public void Execute(RenameAuthorCommand message)
        {
            _logger.Debug("Organizing all files for selected author");
            var authorToRename = _authorService.GetAuthors(message.AuthorIds);

            foreach (var author in authorToRename)
            {
                var bookFiles = _mediaFileService.GetFilesByAuthor(author.Id);
                if (bookFiles.Count == 0)
                {
                    _logger.ProgressInfo(FormatRenameResultMessage(new RenameFilesResult(), author.Name));
                    continue;
                }

                _logger.ProgressInfo("Organizing all files in author: {0}", author.Name);
                var result = RenameFiles(bookFiles, author, message.MediaType);
                _logger.ProgressInfo(FormatRenameResultMessage(result, author.Name));
            }
        }
    }
}

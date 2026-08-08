using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    public enum EbookColocationSkipReason
    {
        None = 0,
        NotEbook = 1,
        MissingContext = 2,
        MissingRootFolder = 3,
        RootNotMixedOrDisabled = 4,
        NoAudiobookFolders = 5
    }

    public sealed class ColocationCandidate
    {
        public int AudiobookBookId { get; set; }
        public int AudiobookFileId { get; set; }
        public string CurrentFolder { get; set; }
        public string PlannedFolder { get; set; }
    }

    public sealed class EbookColocationPlan
    {
        public bool Applies { get; set; }
        public string PrimaryPath { get; set; }
        public List<string> ReplicaPaths { get; set; } = new List<string>();
        public List<ColocationCandidate> Candidates { get; set; } = new List<ColocationCandidate>();
        public EbookColocationSkipReason Reason { get; set; }
        public bool ShouldCleanupReplicas { get; set; }

        public static EbookColocationPlan Skipped(EbookColocationSkipReason reason, bool cleanupReplicas = false)
        {
            return new EbookColocationPlan
            {
                Applies = false,
                Reason = reason,
                ShouldCleanupReplicas = cleanupReplicas
            };
        }
    }

    public sealed class RenameBatchContext
    {
        public Dictionary<string, string> AudiobookFolderRemap { get; } = new Dictionary<string, string>(PathEqualityComparer.Instance);

        public void AddAudiobookFolderRemap(string oldFolder, string newFolder)
        {
            if (oldFolder.IsNullOrWhiteSpace() || newFolder.IsNullOrWhiteSpace() || oldFolder.PathEquals(newFolder))
            {
                return;
            }

            AudiobookFolderRemap[oldFolder] = newFolder;
        }

        public string GetPlannedFolder(string currentFolder)
        {
            if (currentFolder.IsNullOrWhiteSpace())
            {
                return currentFolder;
            }

            return AudiobookFolderRemap.TryGetValue(currentFolder, out var plannedFolder) ? plannedFolder : currentFolder;
        }

        public string GetOriginalFolder(string plannedFolder)
        {
            if (plannedFolder.IsNullOrWhiteSpace())
            {
                return null;
            }

            return AudiobookFolderRemap
                .FirstOrDefault(kvp => kvp.Value.PathEquals(plannedFolder))
                .Key;
        }
    }

    public interface IEbookColocationPlanner
    {
        EbookColocationPlan Plan(BookFile bookFile, Author author, Edition edition, string fileNameOnlyWithExtension, RenameBatchContext batchContext = null);
    }

    public class EbookColocationPlanner : IEbookColocationPlanner
    {
        private readonly IBookService _bookService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IDiskProvider _diskProvider;

        public EbookColocationPlanner(IBookService bookService,
                                      IMediaFileService mediaFileService,
                                      IRootFolderService rootFolderService,
                                      IDiskProvider diskProvider)
        {
            _bookService = bookService;
            _mediaFileService = mediaFileService;
            _rootFolderService = rootFolderService;
            _diskProvider = diskProvider;
        }

        public EbookColocationPlan Plan(BookFile bookFile, Author author, Edition edition, string fileNameOnlyWithExtension, RenameBatchContext batchContext = null)
        {
            var mediaType = GetEffectiveMediaType(bookFile);
            if (!string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase))
            {
                return EbookColocationPlan.Skipped(EbookColocationSkipReason.NotEbook);
            }

            if (bookFile?.Quality?.Quality == null ||
                author == null ||
                edition?.Book == null ||
                fileNameOnlyWithExtension.IsNullOrWhiteSpace())
            {
                return EbookColocationPlan.Skipped(EbookColocationSkipReason.MissingContext);
            }

            var rootFolderPath = author.GetRootFolderForQuality(bookFile.Quality.Quality);
            if (rootFolderPath.IsNullOrWhiteSpace())
            {
                return EbookColocationPlan.Skipped(EbookColocationSkipReason.MissingRootFolder);
            }

            var bestRoot = _rootFolderService.GetBestRootFolder(rootFolderPath);
            if (bestRoot == null || bestRoot.FolderType != FolderType.Mixed || !bestRoot.PlaceEbooksWithAudiobooks)
            {
                return EbookColocationPlan.Skipped(EbookColocationSkipReason.RootNotMixedOrDisabled, cleanupReplicas: true);
            }

            var candidates = GetCandidates(bestRoot, edition.Book, batchContext ?? new RenameBatchContext());
            if (candidates.Count == 0)
            {
                return EbookColocationPlan.Skipped(EbookColocationSkipReason.NoAudiobookFolders, cleanupReplicas: true);
            }

            var ebookCurrentFolder = GetDirectoryNameUsingPathSeparators(bookFile.Path);
            var primary = candidates.FirstOrDefault(c => ebookCurrentFolder.IsNotNullOrWhiteSpace() && c.CurrentFolder.PathEquals(ebookCurrentFolder)) ??
                          candidates.FirstOrDefault(c => ebookCurrentFolder.IsNotNullOrWhiteSpace() && c.PlannedFolder.PathEquals(ebookCurrentFolder)) ??
                          candidates.First();

            var primaryPath = CombineUsingFolderSeparator(primary.PlannedFolder, fileNameOnlyWithExtension);

            var replicaPaths = candidates
                .Where(c => !c.PlannedFolder.PathEquals(primary.PlannedFolder))
                .Select(c => CombineUsingFolderSeparator(c.PlannedFolder, fileNameOnlyWithExtension))
                .Distinct(PathEqualityComparer.Instance)
                .Where(p => p.PathNotEquals(primaryPath))
                .ToList();

            return new EbookColocationPlan
            {
                Applies = true,
                PrimaryPath = primaryPath,
                ReplicaPaths = replicaPaths,
                Candidates = candidates,
                Reason = EbookColocationSkipReason.None
            };
        }

        private List<ColocationCandidate> GetCandidates(RootFolder mixedRootFolder, Book ebookBook, RenameBatchContext batchContext)
        {
            var results = new Dictionary<string, ColocationCandidate>(PathEqualityComparer.Instance);

            if (mixedRootFolder == null || ebookBook?.AuthorId <= 0)
            {
                return results.Values.ToList();
            }

            var audiobookSiblings = _bookService.GetBooksByAuthorId(ebookBook.AuthorId)
                .Where(b => b != null && b.MediaType == BookMediaType.Audiobook)
                .Where(b => WorkIdMatcher.WorkProviderIdMatches(ebookBook, b))
                .OrderBy(b => b.Id)
                .ToList();

            if (audiobookSiblings.Count == 0)
            {
                return results.Values.ToList();
            }

            var allRootFolders = _rootFolderService.All();

            foreach (var audiobookBook in audiobookSiblings)
            {
                var files = _mediaFileService.GetFilesByBook(audiobookBook.Id)
                    .Where(f => f != null && f.Path.IsNotNullOrWhiteSpace())
                    .OrderBy(f => f.Id)
                    .ThenBy(f => f.Path ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var file in files)
                {
                    if (!_diskProvider.FileExists(file.Path))
                    {
                        continue;
                    }

                    var fileFolder = GetDirectoryNameUsingPathSeparators(file.Path);
                    if (fileFolder.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    var plannedFolder = batchContext.GetPlannedFolder(fileFolder);
                    var originalFolder = batchContext.GetOriginalFolder(plannedFolder) ?? fileFolder;

                    var best = _rootFolderService.GetBestRootFolder(plannedFolder, allRootFolders);
                    if (best == null || best.Id != mixedRootFolder.Id)
                    {
                        continue;
                    }

                    if (results.TryGetValue(plannedFolder, out var existing))
                    {
                        if (CompareCandidate(file, audiobookBook, existing) >= 0)
                        {
                            continue;
                        }
                    }

                    results[plannedFolder] = new ColocationCandidate
                    {
                        AudiobookBookId = audiobookBook.Id,
                        AudiobookFileId = file.Id,
                        CurrentFolder = originalFolder,
                        PlannedFolder = plannedFolder
                    };
                }
            }

            return results.Values
                .OrderBy(c => c.AudiobookBookId)
                .ThenBy(c => c.AudiobookFileId)
                .ThenBy(c => c.PlannedFolder ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int CompareCandidate(BookFile file, Book audiobookBook, ColocationCandidate existing)
        {
            var bookCompare = audiobookBook.Id.CompareTo(existing.AudiobookBookId);
            if (bookCompare != 0)
            {
                return bookCompare;
            }

            return file.Id.CompareTo(existing.AudiobookFileId);
        }

        private static string GetDirectoryNameUsingPathSeparators(string path)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return null;
            }

            var lastForwardSlash = path.LastIndexOf('/');
            var lastBackslash = path.LastIndexOf('\\');
            var separatorIndex = Math.Max(lastForwardSlash, lastBackslash);

            if (separatorIndex < 0)
            {
                return null;
            }

            if (separatorIndex == 0)
            {
                return path.Substring(0, 1);
            }

            return path.Substring(0, separatorIndex);
        }

        private static string CombineUsingFolderSeparator(string folder, string fileName)
        {
            if (folder.IsNullOrWhiteSpace())
            {
                return fileName;
            }

            var separator = GetPreferredSeparator(folder);
            var trimmedFolder = folder.TrimEnd('/', '\\');
            var trimmedFileName = fileName?.TrimStart('/', '\\') ?? string.Empty;

            if (trimmedFolder.IsNullOrWhiteSpace())
            {
                return separator + trimmedFileName;
            }

            return trimmedFolder + separator + trimmedFileName;
        }

        private static char GetPreferredSeparator(string path)
        {
            var lastForwardSlash = path.LastIndexOf('/');
            var lastBackslash = path.LastIndexOf('\\');

            if (lastForwardSlash >= 0 && lastForwardSlash > lastBackslash)
            {
                return '/';
            }

            if (lastBackslash >= 0)
            {
                return '\\';
            }

            return Path.DirectorySeparatorChar;
        }

        private static string GetEffectiveMediaType(BookFile bookFile)
        {
            var mediaType = bookFile?.MediaType;
            if (mediaType.IsNullOrWhiteSpace() && bookFile?.Quality != null)
            {
                mediaType = BookFile.DetermineMediaType(bookFile.Quality);
            }

            return mediaType;
        }
    }
}

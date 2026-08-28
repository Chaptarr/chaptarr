using System;
using System.IO;
using System.Linq;
using NzbDrone.Common.Extensions;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.RootFolders
{
    public interface IRootFolderScanService
    {
        AuthorPathUpdate LinkAuthorToFolder(Author author, RootFolder rootFolder, string folderPath);
    }

    public class AuthorPathUpdate
    {
        public int AuthorId { get; set; }
        public string OldPath { get; set; }
        public string NewPath { get; set; }
        public bool HasExistingFiles { get; set; }
        public int FileCount { get; set; }
    }

    public class RootFolderScanService : IRootFolderScanService
    {
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IDiskProvider _diskProvider;
        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;
        private readonly Logger _logger;

        public RootFolderScanService(
            IAuthorService authorService,
            IBookService bookService,
            IDiskProvider diskProvider,
            IRootFolderSettingsResolver rootFolderSettingsResolver,
            Logger logger)
        {
            _authorService = authorService;
            _bookService = bookService;
            _diskProvider = diskProvider;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
            _logger = logger;
        }

        public AuthorPathUpdate LinkAuthorToFolder(Author author, RootFolder rootFolder, string folderPath)
        {
            try
            {
                if (!_diskProvider.FolderExists(folderPath))
                {
                    _logger.Warn($"Cannot link author to non-existent folder: {folderPath}");
                    return null;
                }

                // Path containment: folderPath must be inside the root folder
                var normalizedFolder = Path.GetFullPath(folderPath) + Path.DirectorySeparatorChar;
                var normalizedRoot = Path.GetFullPath(rootFolder.Path) + Path.DirectorySeparatorChar;

                if (!normalizedFolder.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn($"Rejected LinkAuthorToFolder: '{folderPath}' is not inside root folder '{rootFolder.Path}'");
                    return null;
                }

                var files = _diskProvider.GetFiles(folderPath, true).ToList();
                var hasAudiobookFiles = files.Any(IsAudiobookFile);
                var hasEbookFiles = files.Any(IsEbookFile);
                var relevantFiles = files.Where(f => IsRelevantFile(f, rootFolder)).ToList();

                var rootCanLinkAudiobook = rootFolder.FolderType == FolderType.Audiobook ||
                                           (rootFolder.FolderType == FolderType.Mixed && hasAudiobookFiles);
                var rootCanLinkEbook = rootFolder.FolderType == FolderType.Ebook ||
                                       (rootFolder.FolderType == FolderType.Mixed && hasEbookFiles);

                var shouldLinkAudiobook = rootCanLinkAudiobook &&
                                          CanLinkMediaPath(author.AudiobookRootFolderPath, author.AudiobookPath, rootFolder);
                var shouldLinkEbook = rootCanLinkEbook &&
                                      CanLinkMediaPath(author.EbookRootFolderPath, author.EbookPath, rootFolder);

                if (!rootCanLinkAudiobook && !rootCanLinkEbook)
                {
                    _logger.Debug($"No relevant media files found in mixed root folder '{folderPath}' for author '{author.Name}', skipping link");
                    return null;
                }

                // Guard against overwriting existing per-type links
                if (rootCanLinkAudiobook && !shouldLinkAudiobook)
                {
                    _logger.Debug($"Author '{author.Name}' already linked to audiobook folder '{author.AudiobookPath}', skipping relink");
                }

                if (rootCanLinkEbook && !shouldLinkEbook)
                {
                    _logger.Debug($"Author '{author.Name}' already linked to ebook folder '{author.EbookPath}', skipping relink");
                }

                if (!shouldLinkAudiobook && !shouldLinkEbook)
                {
                    return null;
                }

                var update = new AuthorPathUpdate
                {
                    AuthorId = author.Id,
                    OldPath = author.Path
                };

                // Update author path based on the media files that were actually found.
                if (shouldLinkAudiobook)
                {
                    if (string.IsNullOrWhiteSpace(author.AudiobookRootFolderPath))
                    {
                        author.AudiobookRootFolderPath = rootFolder.Path;
                    }

                    author.AudiobookPath = folderPath; // Store the discovered folder path

                    // If this is the primary media type or no path exists, update main path
                    if (string.IsNullOrWhiteSpace(author.Path))
                    {
                        author.Path = folderPath;
                    }

                    // Apply each missing root-folder default independently. This
                    // matters when an author was imported through the other format
                    // or has only a partially configured media side.
                    var settings = _rootFolderSettingsResolver.ResolveSettings(rootFolder, BookMediaType.Audiobook);
                    if (settings.IsConfigured)
                    {
                        author.AudiobookQualityProfileId ??= settings.QualityProfileId;
                        author.AudiobookMetadataProfileId ??= settings.MetadataProfileId;
                        author.AudiobookMonitored ??= settings.Monitored;
                        author.AudiobookMonitorNewItems ??= settings.MonitorNewItems;

                        _logger.Debug($"Applied missing audiobook defaults from root folder to author '{author.Name}' - QualityProfile: {settings.QualityProfileId}, MetadataProfile: {settings.MetadataProfileId}, Monitored: {settings.Monitored}, MonitorNewItems: {settings.MonitorNewItems}, InitialBookMonitoring: {settings.MonitorExistingMode}");
                    }
                    else
                    {
                        _logger.Warn($"No audiobook settings configured for root folder {rootFolder.Path} - skipping author '{author.Name}'");
                    }
                }

                if (shouldLinkEbook)
                {
                    if (string.IsNullOrWhiteSpace(author.EbookRootFolderPath))
                    {
                        author.EbookRootFolderPath = rootFolder.Path;
                    }

                    author.EbookPath = folderPath; // Store the discovered folder path

                    // If this is the primary media type or no path exists, update main path
                    if (string.IsNullOrWhiteSpace(author.Path))
                    {
                        author.Path = folderPath;
                    }

                    // Apply each missing root-folder default independently. This
                    // matters when an author was imported through the other format
                    // or has only a partially configured media side.
                    var settings = _rootFolderSettingsResolver.ResolveSettings(rootFolder, BookMediaType.Ebook);
                    if (settings.IsConfigured)
                    {
                        author.EbookQualityProfileId ??= settings.QualityProfileId;
                        author.EbookMetadataProfileId ??= settings.MetadataProfileId;
                        author.EbookMonitored ??= settings.Monitored;
                        author.EbookMonitorNewItems ??= settings.MonitorNewItems;

                        _logger.Debug($"Applied missing ebook defaults from root folder to author '{author.Name}' - QualityProfile: {settings.QualityProfileId}, MetadataProfile: {settings.MetadataProfileId}, Monitored: {settings.Monitored}, MonitorNewItems: {settings.MonitorNewItems}, InitialBookMonitoring: {settings.MonitorExistingMode}");
                    }
                    else
                    {
                        _logger.Warn($"No ebook settings configured for root folder {rootFolder.Path} - skipping author '{author.Name}'");
                    }
                }

                update.NewPath = author.Path;

                // Seed existing book rows from the root's one-time setting.
                // This is deliberately independent from the author gate and ongoing
                // new-item policy; neither of those settings rewrites book flags.
                if (shouldLinkEbook)
                {
                    var settings = _rootFolderSettingsResolver.ResolveSettings(rootFolder, BookMediaType.Ebook);
                    SeedInitialBooks(author, BookMediaType.Ebook, settings?.MonitorExistingMode);
                }

                if (shouldLinkAudiobook)
                {
                    var settings = _rootFolderSettingsResolver.ResolveSettings(rootFolder, BookMediaType.Audiobook);
                    SeedInitialBooks(author, BookMediaType.Audiobook, settings?.MonitorExistingMode);
                }

                update.HasExistingFiles = relevantFiles.Any();
                update.FileCount = relevantFiles.Count();

                _authorService.UpdateAuthor(author);

                _logger.Debug($"Linked author '{author.Name}' to folder '{folderPath}' ({update.FileCount} existing files found)");

                return update;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error linking author {author.Name} to folder {folderPath}");
                return null;
            }
        }

        private static bool CanLinkMediaPath(string existingRootFolderPath, string existingMediaPath, RootFolder rootFolder)
        {
            if (!string.IsNullOrWhiteSpace(existingMediaPath))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(existingRootFolderPath) ||
                   existingRootFolderPath.PathEquals(rootFolder.Path);
        }

        private void SeedInitialBooks(Author author, BookMediaType mediaType, MonitorTypes? monitorMode)
        {
            if (!monitorMode.HasValue)
            {
                return;
            }

            var books = (_bookService.GetBooksByAuthor(author.Id) ?? new System.Collections.Generic.List<Book>())
                .Where(book => book.MediaType == mediaType)
                .ToList();
            if (books.Count == 0)
            {
                return;
            }

            var booksWithFiles = (_bookService.GetAuthorBooksWithFiles(author) ?? new System.Collections.Generic.List<Book>())
                .Where(book => book.MediaType == mediaType)
                .Select(book => book.Id)
                .ToHashSet();
            var changed = new System.Collections.Generic.List<Book>();

            foreach (var book in books)
            {
                var hasFile = booksWithFiles.Contains(book.Id);
                var shouldMonitor = monitorMode.Value switch
                {
                    MonitorTypes.All => true,
                    MonitorTypes.Missing => !hasFile,
                    MonitorTypes.Existing => hasFile,
                    MonitorTypes.None => false,
                    _ => book.IsMonitoredForMediaType(mediaType)
                };

                if (book.IsMonitoredForMediaType(mediaType) == shouldMonitor)
                {
                    continue;
                }

                book.SetMonitoredForMediaType(mediaType, shouldMonitor);
                changed.Add(book);
            }

            if (changed.Count > 0)
            {
                _bookService.UpdateMany(changed);
            }

            _logger.Debug($"Applied initial {monitorMode} monitoring to {books.Count} {mediaType.ToString().ToLowerInvariant()} books for author '{author.Name}' ({changed.Count} changed)");
        }

        private static bool IsAudiobookFile(string filePath)
        {
            var extension = Path.GetExtension(filePath)?.ToLowerInvariant();
            return MediaFileExtensions.AudioExtensions.Contains(extension);
        }

        private static bool IsEbookFile(string filePath)
        {
            var extension = Path.GetExtension(filePath)?.ToLowerInvariant();
            return MediaFileExtensions.TextExtensions.Contains(extension);
        }

        private bool IsRelevantFile(string filePath, RootFolder rootFolder)
        {
            var extension = Path.GetExtension(filePath)?.ToLowerInvariant();
            var isAudioFile = MediaFileExtensions.AudioExtensions.Contains(extension);
            var isTextFile = MediaFileExtensions.TextExtensions.Contains(extension);

            // If root folder accepts mixed content, any media file is relevant
            if (rootFolder.FolderType == FolderType.Mixed)
            {
                return isAudioFile || isTextFile;
            }

            // If not mixed content, only files matching the folder type are relevant
            if (rootFolder.FolderType == FolderType.Audiobook)
            {
                return isAudioFile;
            }
            else if (rootFolder.FolderType == FolderType.Ebook)
            {
                return isTextFile;
            }

            // For unknown folder types, default to accepting both (backwards compatibility)
            return isAudioFile || isTextFile;
        }
    }
}

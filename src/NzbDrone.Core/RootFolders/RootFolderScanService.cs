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

                    // Apply root folder defaults ONLY if not already set
                    // This handles the case where author was imported via ebooks first
                    if (!author.AudiobookQualityProfileId.HasValue)
                    {
                        var settings = _rootFolderSettingsResolver.ResolveSettings(rootFolder, BookMediaType.Audiobook);
                        if (settings.IsConfigured)
                        {
                            author.AudiobookQualityProfileId = settings.QualityProfileId;
                            author.AudiobookMetadataProfileId = settings.MetadataProfileId;
                            author.AudiobookMonitorExisting = settings.MonitorExisting;
                            author.AudiobookMonitorFuture = settings.MonitorFuture;

                            _logger.Debug($"Applied audiobook defaults from root folder to author '{author.Name}' - QualityProfile: {settings.QualityProfileId}, MetadataProfile: {settings.MetadataProfileId}, MonitorExisting: {settings.MonitorExisting}, MonitorFuture: {settings.MonitorFuture}");
                        }
                        else
                        {
                            _logger.Warn($"No audiobook settings configured for root folder {rootFolder.Path} - skipping author '{author.Name}'");
                        }
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

                    // Apply root folder defaults ONLY if not already set
                    // This handles the case where author was imported via audiobooks first
                    if (!author.EbookQualityProfileId.HasValue)
                    {
                        var settings = _rootFolderSettingsResolver.ResolveSettings(rootFolder, BookMediaType.Ebook);
                        if (settings.IsConfigured)
                        {
                            author.EbookQualityProfileId = settings.QualityProfileId;
                            author.EbookMetadataProfileId = settings.MetadataProfileId;
                            author.EbookMonitorExisting = settings.MonitorExisting;
                            author.EbookMonitorFuture = settings.MonitorFuture;

                            _logger.Debug($"Applied ebook defaults from root folder to author '{author.Name}' - QualityProfile: {settings.QualityProfileId}, MetadataProfile: {settings.MetadataProfileId}, MonitorExisting: {settings.MonitorExisting}, MonitorFuture: {settings.MonitorFuture}");
                        }
                        else
                        {
                            _logger.Warn($"No ebook settings configured for root folder {rootFolder.Path} - skipping author '{author.Name}'");
                        }
                    }
                }

                update.NewPath = author.Path;

                // Update existing book monitoring status to match new author preferences
                // This is crucial for file import to work when root folders are added after books exist
                if (shouldLinkEbook && author.EbookMonitorExisting.HasValue && author.EbookMonitorExisting.Value > 0)
                {
                    var existingEbookBooks = _bookService.GetBooksByAuthor(author.Id)
                        .Where(b => b.MediaType == BookMediaType.Ebook && !b.EbookMonitored).ToList();
                    
                    if (existingEbookBooks.Any())
                    {
                        foreach (var book in existingEbookBooks)
                        {
                            // Apply monitoring based on author's EbookMonitorExisting setting
                            if (author.EbookMonitorExisting == 1) // All Books
                            {
                                book.EbookMonitored = true;
                                _logger.Debug($"Updated ebook book '{book.Title}' to monitored (All Books mode) after root folder scan");
                            }
                            else if (author.EbookMonitorExisting == 2) // Selected Books - enable monitoring but don't force all to monitored
                            {
                                // In Select mode, books can be individually controlled, so we just ensure they CAN be monitored
                                // Don't automatically set to monitored, but the UI can now control them individually
                                _logger.Debug($"Ebook book '{book.Title}' can now be individually monitored (Selected Books mode)");
                            }
                        }
                        
                        if (existingEbookBooks.Any(b => b.EbookMonitored))
                        {
                            _bookService.UpdateMany(existingEbookBooks);
                            _logger.Debug($"Updated monitoring for {existingEbookBooks.Count(b => b.EbookMonitored)} existing ebook books for author '{author.Name}'");
                        }
                    }
                }

                if (shouldLinkAudiobook && author.AudiobookMonitorExisting.HasValue && author.AudiobookMonitorExisting.Value > 0)
                {
                    var existingAudiobookBooks = _bookService.GetBooksByAuthor(author.Id)
                        .Where(b => b.MediaType == BookMediaType.Audiobook && !b.AudiobookMonitored).ToList();
                    
                    if (existingAudiobookBooks.Any())
                    {
                        foreach (var book in existingAudiobookBooks)
                        {
                            // Apply monitoring based on author's AudiobookMonitorExisting setting
                            if (author.AudiobookMonitorExisting == 1) // All Books
                            {
                                book.AudiobookMonitored = true;
                                _logger.Debug($"Updated audiobook book '{book.Title}' to monitored (All Books mode) after root folder scan");
                            }
                            else if (author.AudiobookMonitorExisting == 2) // Selected Books
                            {
                                // In Select mode, books can be individually controlled
                                _logger.Debug($"Audiobook book '{book.Title}' can now be individually monitored (Selected Books mode)");
                            }
                        }
                        
                        if (existingAudiobookBooks.Any(b => b.AudiobookMonitored))
                        {
                            _bookService.UpdateMany(existingAudiobookBooks);
                            _logger.Debug($"Updated monitoring for {existingAudiobookBooks.Count(b => b.AudiobookMonitored)} existing audiobook books for author '{author.Name}'");
                        }
                    }
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

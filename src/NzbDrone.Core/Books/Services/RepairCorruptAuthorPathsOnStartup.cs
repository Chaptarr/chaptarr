using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books.Services
{
    public class RepairCorruptAuthorPathsOnStartup : IHandle<ApplicationStartedEvent>
    {
        private readonly IAuthorService _authorService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IBuildFileNames _fileNameBuilder;
        private readonly Logger _logger;

        public RepairCorruptAuthorPathsOnStartup(IAuthorService authorService,
                                                 IRootFolderService rootFolderService,
                                                 IBuildFileNames fileNameBuilder,
                                                 Logger logger)
        {
            _authorService = authorService;
            _rootFolderService = rootFolderService;
            _fileNameBuilder = fileNameBuilder;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            try
            {
                Repair();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[AUTHOR-PATH-REPAIR] Unexpected error while repairing corrupt author paths");
            }
        }

        private void Repair()
        {
            var rootFolders = _rootFolderService.All();
            if (rootFolders == null || rootFolders.Count == 0)
            {
                return;
            }

            var authors = _authorService.GetAllAuthors(bypassCache: true) ?? new List<Author>();
            if (!authors.Any())
            {
                return;
            }

            var repaired = 0;

            foreach (var author in authors.Where(a => a != null))
            {
                if (!NeedsRepair(author, rootFolders))
                {
                    continue;
                }

                var beforePath = author.Path;
                var beforeAudiobookPath = author.AudiobookPath;
                var beforeEbookPath = author.EbookPath;
                var beforeAudioRoot = author.AudiobookRootFolderPath;
                var beforeEbookRoot = author.EbookRootFolderPath;

                // If a per-media root folder isn't configured, that per-media discovered path must also be empty.
                // Leaving an orphaned per-type path can cause path builders to route imports into a folder for a disabled media type.
                var safeCleanupChanged = false;
                string safeAudiobookPath = beforeAudiobookPath;
                string safeEbookPath = beforeEbookPath;

                if (author.AudiobookRootFolderPath.IsNullOrWhiteSpace() && beforeAudiobookPath.IsNotNullOrWhiteSpace())
                {
                    safeAudiobookPath = null;
                    author.AudiobookPath = null;
                    safeCleanupChanged = true;
                }

                if (author.EbookRootFolderPath.IsNullOrWhiteSpace() && beforeEbookPath.IsNotNullOrWhiteSpace())
                {
                    safeEbookPath = null;
                    author.EbookPath = null;
                    safeCleanupChanged = true;
                }

                if (!TryRepairAuthor(author, rootFolders, out var reason))
                {
                    if (safeCleanupChanged)
                    {
                        // Persist safe cleanup even when full repair cannot complete.
                        author.Path = beforePath;
                        author.AudiobookRootFolderPath = beforeAudioRoot;
                        author.EbookRootFolderPath = beforeEbookRoot;
                        author.AudiobookPath = safeAudiobookPath;
                        author.EbookPath = safeEbookPath;

                        try
                        {
                            _authorService.UpdateAuthor(author);
                            repaired++;

                            _logger.Warn("[AUTHOR-PATH-REPAIR] Partially repaired author '{0}' (ID: {1}): cleared orphaned media paths (AudiobookPath '{2}' -> '{3}', EbookPath '{4}' -> '{5}'). Full repair failed: {6}",
                                author.Name,
                                author.Id,
                                beforeAudiobookPath,
                                author.AudiobookPath,
                                beforeEbookPath,
                                author.EbookPath,
                                reason);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "[AUTHOR-PATH-REPAIR] Failed to persist safe cleanup for author '{0}' (ID: {1})", author.Name, author.Id);
                        }

                        continue;
                    }

                    _logger.Warn("[AUTHOR-PATH-REPAIR] Unable to repair author '{0}' (ID: {1}): {2}", author.Name, author.Id, reason);
                    continue;
                }

                if (beforePath == author.Path &&
                    beforeAudiobookPath == author.AudiobookPath &&
                    beforeEbookPath == author.EbookPath &&
                    beforeAudioRoot == author.AudiobookRootFolderPath &&
                    beforeEbookRoot == author.EbookRootFolderPath)
                {
                    // We thought the author needed repair, but we ended up with no actual changes.
                    // Avoid unnecessary DB writes and avoid log spam on every startup.
                    continue;
                }

                try
                {
                    _authorService.UpdateAuthor(author);
                    repaired++;

                    _logger.Warn("[AUTHOR-PATH-REPAIR] Repaired author '{0}' (ID: {1}): Path '{2}' -> '{3}', AudiobookPath '{4}' -> '{5}', EbookPath '{6}' -> '{7}', AudiobookRoot '{8}' -> '{9}', EbookRoot '{10}' -> '{11}'",
                        author.Name,
                        author.Id,
                        beforePath,
                        author.Path,
                        beforeAudiobookPath,
                        author.AudiobookPath,
                        beforeEbookPath,
                        author.EbookPath,
                        beforeAudioRoot,
                        author.AudiobookRootFolderPath,
                        beforeEbookRoot,
                        author.EbookRootFolderPath);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[AUTHOR-PATH-REPAIR] Failed to persist repaired paths for author '{0}' (ID: {1})", author.Name, author.Id);
                }
            }

            if (repaired > 0)
            {
                _logger.Warn("[AUTHOR-PATH-REPAIR] Repaired {0} author(s) with unsafe or empty paths", repaired);
            }
        }

        private static bool NeedsRepair(Author author, List<RootFolder> rootFolders)
        {
            var hasAudiobookRoot = author.AudiobookRootFolderPath.IsNotNullOrWhiteSpace();
            var hasEbookRoot = author.EbookRootFolderPath.IsNotNullOrWhiteSpace();

            var primaryRoot = author.AudiobookRootFolderPath.IsNotNullOrWhiteSpace()
                ? author.AudiobookRootFolderPath
                : author.EbookRootFolderPath;

            return IsUnsafePath(author.Path, rootFolders) ||
                   (hasAudiobookRoot && IsUnsafePath(author.AudiobookPath, rootFolders)) ||
                   (!hasAudiobookRoot && author.AudiobookPath.IsNotNullOrWhiteSpace()) ||
                   (hasEbookRoot && IsUnsafePath(author.EbookPath, rootFolders)) ||
                   (!hasEbookRoot && author.EbookPath.IsNotNullOrWhiteSpace()) ||
                   (primaryRoot.IsNotNullOrWhiteSpace() && author.Path.IsNotNullOrWhiteSpace() && !primaryRoot.IsParentPath(author.Path)) ||
                   (author.AudiobookRootFolderPath.IsNotNullOrWhiteSpace() && author.AudiobookPath.IsNotNullOrWhiteSpace() && !author.AudiobookRootFolderPath.IsParentPath(author.AudiobookPath)) ||
                   (author.EbookRootFolderPath.IsNotNullOrWhiteSpace() && author.EbookPath.IsNotNullOrWhiteSpace() && !author.EbookRootFolderPath.IsParentPath(author.EbookPath)) ||
                   (author.AudiobookRootFolderPath.IsNotNullOrWhiteSpace() && author.AudiobookPath.PathEquals(author.AudiobookRootFolderPath)) ||
                   (author.EbookRootFolderPath.IsNotNullOrWhiteSpace() && author.EbookPath.PathEquals(author.EbookRootFolderPath));
        }

        private bool TryRepairAuthor(Author author, List<RootFolder> rootFolders, out string reason)
        {
            reason = null;

            var authorFolder = _fileNameBuilder.GetAuthorFolder(author);
            if (authorFolder.IsNullOrWhiteSpace())
            {
                reason = "Author folder format produced an empty folder name";
                return false;
            }

            // If both root folders are missing, infer from the current path if possible.
            if (author.AudiobookRootFolderPath.IsNullOrWhiteSpace() && author.EbookRootFolderPath.IsNullOrWhiteSpace())
            {
                var inferred = _rootFolderService.GetBestRootFolder(author.Path);
                if (inferred != null)
                {
                    if (inferred.FolderType == FolderType.Audiobook)
                    {
                        author.AudiobookRootFolderPath = inferred.Path;
                    }
                    else if (inferred.FolderType == FolderType.Ebook)
                    {
                        author.EbookRootFolderPath = inferred.Path;
                    }
                    else
                    {
                        author.AudiobookRootFolderPath = inferred.Path;
                        author.EbookRootFolderPath = inferred.Path;
                    }
                }
            }

            // Rebuild per-media-type paths when missing or unsafe.
            if (author.AudiobookRootFolderPath.IsNotNullOrWhiteSpace())
            {
                if (author.AudiobookPath.IsNullOrWhiteSpace() ||
                    IsUnsafePath(author.AudiobookPath, rootFolders) ||
                    author.AudiobookPath.PathEquals(author.AudiobookRootFolderPath))
                {
                    author.AudiobookPath = Path.Combine(author.AudiobookRootFolderPath, authorFolder);
                }
            }

            if (author.EbookRootFolderPath.IsNotNullOrWhiteSpace())
            {
                if (author.EbookPath.IsNullOrWhiteSpace() ||
                    IsUnsafePath(author.EbookPath, rootFolders) ||
                    author.EbookPath.PathEquals(author.EbookRootFolderPath))
                {
                    author.EbookPath = Path.Combine(author.EbookRootFolderPath, authorFolder);
                }
            }

            // Primary author.Path should point at a real author folder, not the root.
            var primaryRoot = author.AudiobookRootFolderPath.IsNotNullOrWhiteSpace()
                ? author.AudiobookRootFolderPath
                : author.EbookRootFolderPath;

            if (primaryRoot.IsNullOrWhiteSpace())
            {
                reason = "No root folder configured for author; cannot generate a safe author path";
                return false;
            }

            if (author.Path.IsNullOrWhiteSpace() || IsUnsafePath(author.Path, rootFolders) || author.Path.PathEquals(primaryRoot))
            {
                author.Path = Path.Combine(primaryRoot, authorFolder);
            }

            if (IsUnsafePath(author.Path, rootFolders) || author.Path.PathEquals(primaryRoot))
            {
                reason = $"Repaired path '{author.Path}' is still unsafe";
                return false;
            }

            return true;
        }

        private static bool IsUnsafePath(string path, List<RootFolder> rootFolders)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return true;
            }

            // Unsafe if it is a root folder itself, or if it is a parent of a root folder.
            foreach (var rootFolder in rootFolders)
            {
                if (rootFolder?.Path.IsNullOrWhiteSpace() == true)
                {
                    continue;
                }

                if (rootFolder.Path.PathEquals(path))
                {
                    return true;
                }

                if (path.IsParentPath(rootFolder.Path))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

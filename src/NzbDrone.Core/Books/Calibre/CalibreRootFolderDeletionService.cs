using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books.Calibre
{
    public class CalibreRootFolderDeletionService : IHandle<BookDeletedEvent>, IHandle<AuthorDeletedEvent>
    {
        private readonly IRootFolderService _rootFolderService;
        private readonly ICalibreProxy _calibreProxy;
        private readonly Logger _logger;

        public CalibreRootFolderDeletionService(IRootFolderService rootFolderService,
                                                ICalibreProxy calibreProxy,
                                                Logger logger)
        {
            _rootFolderService = rootFolderService;
            _calibreProxy = calibreProxy;
            _logger = logger;
        }

        public void Handle(BookDeletedEvent message)
        {
            if (!message.DeleteFiles)
            {
                return;
            }

            if (message.Book?.MediaType != BookMediaType.Ebook && !message.ApplyToBothFormats)
            {
                return;
            }

            var author = message.Book?.Author;

            if (author == null || author.Path.IsNullOrWhiteSpace() || !TryGetSettings(author.Path, out var settings))
            {
                return;
            }

            Dictionary<int, string> candidates;

            try
            {
                candidates = _calibreProxy.GetBookTitlesUnderPath(author.Path, settings);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to enumerate calibre records under {0}", author.Path);
                return;
            }

            var targetTitles = new HashSet<string>(
                (message.DeletedBooks ?? new List<Book> { message.Book })
                    .Select(b => Normalize(b?.Title))
                    .Where(t => t.Length > 0),
                StringComparer.Ordinal);

            if (targetTitles.Count == 0)
            {
                return;
            }

            var ids = candidates
                .Where(pair => TitleMatches(pair.Value, targetTitles))
                .Select(pair => pair.Key)
                .ToList();

            if (!ids.Any())
            {
                return;
            }

            try
            {
                _calibreProxy.DeleteBookIds(ids, settings);
                _logger.Info("Removed {0} calibre record(s) from the library for deleted book '{1}'", ids.Count, message.Book.Title);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to remove calibre record(s) for deleted book '{0}'", message.Book?.Title);
            }
        }

        public void Handle(AuthorDeletedEvent message)
        {
            if (!message.DeleteFiles)
            {
                return;
            }

            var author = message.Author;

            if (author == null || author.Path.IsNullOrWhiteSpace() || !TryGetSettings(author.Path, out var settings))
            {
                return;
            }

            try
            {
                var ids = _calibreProxy.GetBookTitlesUnderPath(author.Path, settings).Keys.ToList();

                if (!ids.Any())
                {
                    return;
                }

                _calibreProxy.DeleteBookIds(ids, settings);
                _logger.Info("Removed {0} calibre record(s) from the library for deleted author '{1}'", ids.Count, author.Name);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to remove calibre records for deleted author '{0}'", author?.Name);
            }
        }

        private bool TryGetSettings(string path, out CalibreSettings settings)
        {
            settings = null;

            RootFolder rootFolder;

            try
            {
                rootFolder = _rootFolderService.GetBestRootFolder(path);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to resolve root folder for {0}", path);
                return false;
            }

            if (rootFolder == null || !rootFolder.IsCalibreLibrary || rootFolder.CalibreSettings == null)
            {
                return false;
            }

            settings = rootFolder.CalibreSettings;
            return true;
        }

        private static bool TitleMatches(string candidateTitle, ISet<string> targetTitles)
        {
            var candidate = Normalize(candidateTitle);

            if (candidate.Length == 0)
            {
                return false;
            }

            // CWA retitles mid-flight, so extensions of the deleted title count; the floor keeps short titles from sweeping.
            return targetTitles.Any(t =>
                candidate.Equals(t, StringComparison.Ordinal) ||
                (t.Length >= 6 && candidate.StartsWith(t, StringComparison.Ordinal)));
        }

        private static string Normalize(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            return new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }
    }
}

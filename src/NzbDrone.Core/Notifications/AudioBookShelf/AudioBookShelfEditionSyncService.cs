using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Notifications.AudioBookShelf
{
    public class AudioBookShelfEditionSyncCommand : Command
    {
        // Empty AuthorIds and BookIds: the whole library.
        public List<int> AuthorIds { get; set; } = new List<int>();
        public List<int> BookIds { get; set; } = new List<int>();

        public override bool SendUpdatesToClient => true;
    }

    // Brings books in line with AudioBookShelf's matches: for every AudioBookShelf connection set to
    // keep the metadata, pin each item's folder to the edition with its ASIN. Scoped like Rename:
    // one or more books, one or more authors, or the whole library.
    public class AudioBookShelfEditionSyncService : IExecute<AudioBookShelfEditionSyncCommand>
    {
        private readonly INotificationFactory _notificationFactory;
        private readonly IMediaFileService _mediaFileService;
        private readonly Logger _logger;

        public AudioBookShelfEditionSyncService(INotificationFactory notificationFactory, IMediaFileService mediaFileService, Logger logger)
        {
            _notificationFactory = notificationFactory;
            _mediaFileService = mediaFileService;
            _logger = logger;
        }

        public void Execute(AudioBookShelfEditionSyncCommand message)
        {
            var shelves = _notificationFactory.GetAvailableProviders()
                .OfType<AudioBookShelf>()
                .Where(s => ((AudioBookShelfSettings)s.Definition.Settings).AudioBookShelfKeepsMetadata())
                .ToList();

            if (!shelves.Any())
            {
                _logger.ProgressInfo("No AudioBookShelf connection is set to keep the metadata; nothing to sync");
                return;
            }

            var scoped = (message.AuthorIds?.Any() ?? false) || (message.BookIds?.Any() ?? false);
            var folders = scoped ? GetScopedFolders(message) : null;

            if (scoped && folders.Count == 0)
            {
                _logger.ProgressInfo("The selected books have no files to sync with AudioBookShelf");
                return;
            }

            foreach (var shelf in shelves)
            {
                var outcomes = scoped
                    ? shelf.SyncEditionsForFolders(folders)
                    : shelf.SyncEditionsFromAudioBookShelf();

                var summary = outcomes.Count == 0
                    ? "no matching items"
                    : string.Join(", ", outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key}: {o.Value}"));

                _logger.ProgressInfo("AudioBookShelf edition sync for '{0}': {1}", shelf.Definition.Name, summary);
            }
        }

        private List<string> GetScopedFolders(AudioBookShelfEditionSyncCommand message)
        {
            var files = new List<BookFile>();

            if (message.BookIds?.Any() ?? false)
            {
                files.AddRange(_mediaFileService.GetFilesByBooks(message.BookIds.Distinct().ToList()) ?? new List<BookFile>());
            }

            foreach (var authorId in (message.AuthorIds ?? new List<int>()).Distinct())
            {
                files.AddRange(_mediaFileService.GetFilesByAuthor(authorId) ?? new List<BookFile>());
            }

            return files
                .Select(f => f?.Path)
                .Where(p => p.IsNotNullOrWhiteSpace())
                .Select(Path.GetDirectoryName)
                .Where(d => d.IsNotNullOrWhiteSpace())
                .Distinct()
                .ToList();
        }
    }
}

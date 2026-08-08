using System;
using Dapper;
using NLog;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books.Services
{
    public interface IFtsMaintenanceService
    {
        void UpdateAuthorFts(int authorId);
        void RebuildAllFts();
    }

    public class FtsMaintenanceService : IFtsMaintenanceService,
        IHandle<AuthorAddedEvent>,
        IHandle<AuthorEditedEvent>,
        IHandle<AuthorRefreshCompleteEvent>
    {
        private readonly IMainDatabase _database;
        private readonly IAuthorService _authorService;
        private readonly Logger _logger;

        public FtsMaintenanceService(IMainDatabase database, IAuthorService authorService, Logger logger)
        {
            _database = database;
            _authorService = authorService;
            _logger = logger;
        }

        public void UpdateAuthorFts(int authorId)
        {
            try
            {
                var author = _authorService.GetAuthor(authorId);
                if (author == null)
                {
                    _logger.Warn("[FTS-MAINT] Author {0} not found", authorId);
                    return;
                }

                using (var conn = _database.OpenConnection())
                {
                    // Baseline schema defines author_fts as a content-linked FTS5 table
                    // with triggers managing inserts/updates from Authors.
                    // To refresh the FTS row for this author, perform a no-op UPDATE
                    // on Authors to fire the AFTER UPDATE trigger.

                    var rows = conn.Execute("UPDATE \"Authors\" SET \"LastUpdated\" = COALESCE(\"LastUpdated\", CURRENT_TIMESTAMP) WHERE \"Id\" = @authorId",
                        new { authorId });

                    // If no row was updated (LastUpdated already set), do a harmless Name=no-op to trigger anyway
                    if (rows == 0)
                    {
                        conn.Execute("UPDATE \"Authors\" SET \"Name\" = \"Name\" WHERE \"Id\" = @authorId", new { authorId });
                    }

                    _logger.Debug("[FTS-MAINT] Triggered FTS refresh for author '{0}' (ID: {1})", author.Name, authorId);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[FTS-MAINT] Failed to update FTS for author {0}", authorId);
            }
        }

        public void RebuildAllFts()
        {
            _logger.Debug("[FTS-MAINT] Starting full FTS rebuild");
            var authors = _authorService.GetAllAuthors();

            foreach (var author in authors)
            {
                UpdateAuthorFts(author.Id);
            }

            _logger.Debug("[FTS-MAINT] Completed FTS rebuild for {0} authors", authors.Count);
        }

        private string GetLastnameFirst(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "";

            var parts = name.Trim().Split(' ');
            if (parts.Length >= 2)
            {
                return $"{parts[parts.Length - 1]}, {string.Join(" ", parts, 0, parts.Length - 1)}".ToLowerInvariant();
            }

            return name.ToLowerInvariant();
        }

        public void Handle(AuthorAddedEvent message)
        {
            UpdateAuthorFts(message.Author.Id);
        }

        public void Handle(AuthorEditedEvent message)
        {
            UpdateAuthorFts(message.Author.Id);
        }

        public void Handle(AuthorRefreshCompleteEvent message)
        {
            UpdateAuthorFts(message.Author.Id);
        }
    }
}

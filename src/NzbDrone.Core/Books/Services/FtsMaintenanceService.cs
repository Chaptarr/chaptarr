using System;
using System.Data;
using System.Diagnostics;
using Dapper;
using NLog;
using NzbDrone.Core.Books.Events;
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
                    // Triggers maintain the header author index from the stored folded key.
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
            if (_database.DatabaseType == DatabaseType.SQLite)
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction();
                RebuildSqliteAuthorIndex(connection, transaction, _logger);
                transaction.Commit();
                return;
            }

            _logger.Debug("[FTS-MAINT] Starting full FTS rebuild");
            var authors = _authorService.GetAllAuthors();

            foreach (var author in authors)
            {
                UpdateAuthorFts(author.Id);
            }

            _logger.Debug("[FTS-MAINT] Completed FTS rebuild for {0} authors", authors.Count);
        }

        internal static void RebuildSqliteAuthorIndex(IDbConnection connection, IDbTransaction transaction, Logger logger)
        {
            var stopwatch = Stopwatch.StartNew();
            const string insert = @"INSERT INTO author_fts(rowid, SearchName)
                SELECT Id, COALESCE(SearchName, '') || ' ' || replace(COALESCE(SearchName, ''), '.', '') || ' ' || COALESCE(TitleSlug, '') FROM Authors";
            connection.Execute(@"
                DROP TRIGGER IF EXISTS author_fts_ai;
                DROP TRIGGER IF EXISTS author_fts_ad;
                DROP TRIGGER IF EXISTS author_fts_au;
                DROP TABLE IF EXISTS author_fts;
                CREATE VIRTUAL TABLE author_fts USING fts5(SearchName, content='', contentless_delete=1,
                    tokenize='unicode61 remove_diacritics 1');", transaction: transaction);
            connection.Execute(insert, transaction: transaction);
            connection.Execute($@"
                CREATE TRIGGER author_fts_ai AFTER INSERT ON Authors BEGIN
                    {insert} WHERE Id = new.Id;
                END;
                CREATE TRIGGER author_fts_ad AFTER DELETE ON Authors BEGIN
                    DELETE FROM author_fts WHERE rowid = old.Id;
                END;
                CREATE TRIGGER author_fts_au AFTER UPDATE ON Authors BEGIN
                    DELETE FROM author_fts WHERE rowid = old.Id;
                    {insert} WHERE Id = new.Id;
                END;", transaction: transaction);
            var count = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM author_fts", transaction: transaction);
            logger.Info("Rebuilt header author search index: {0} entries in {1} ms", count, stopwatch.ElapsedMilliseconds);
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

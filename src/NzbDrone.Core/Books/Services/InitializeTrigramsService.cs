using System;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Books.Repositories;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books.Services
{
    public class InitializeTrigramsService : IHandle<ApplicationStartedEvent>
    {
        private readonly IMainDatabase _database;
        private readonly IFuzzyMatchingRepository _fuzzyMatchingRepository;
        private readonly ITrigramMaintenanceService _trigramMaintenanceService;
        private readonly Logger _logger;

        public InitializeTrigramsService(
            IMainDatabase database,
            IFuzzyMatchingRepository fuzzyMatchingRepository,
            ITrigramMaintenanceService trigramMaintenanceService,
            Logger logger)
        {
            _database = database;
            _fuzzyMatchingRepository = fuzzyMatchingRepository;
            _trigramMaintenanceService = trigramMaintenanceService;
            _logger = logger;

            _logger.Debug("[TRIGRAM-INIT] InitializeTrigramsService constructed");
        }

        public void Handle(ApplicationStartedEvent message)
        {
            var startTime = DateTime.UtcNow;
            _logger.Debug("[TRIGRAM-INIT] ========== Application Started - Checking Trigram Tables ==========");

            try
            {
                using (var connection = _database.OpenConnection())
                {
                    // Check various table counts
                    var bookTrigramCount = connection.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM book_trigrams");
                    var authorTrigramCount = connection.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM author_trigrams");
                    var bookNormalizedCount = connection.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM book_normalized");
                    var authorNormalizedCount = connection.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM author_normalized");

                    _logger.Debug("[TRIGRAM-INIT] Current table state:");
                    _logger.Debug("[TRIGRAM-INIT]   book_trigrams: {0} entries", bookTrigramCount);
                    _logger.Debug("[TRIGRAM-INIT]   author_trigrams: {0} entries", authorTrigramCount);
                    _logger.Debug("[TRIGRAM-INIT]   book_normalized: {0} entries", bookNormalizedCount);
                    _logger.Debug("[TRIGRAM-INIT]   author_normalized: {0} entries", authorNormalizedCount);

                    // Also check actual data counts
                    var bookCount = connection.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM \"Books\"");
                    var authorCount = connection.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM \"Authors\"");
                    _logger.Debug("[TRIGRAM-INIT] Database has {0} books and {1} authors", bookCount, authorCount);

                    if (bookTrigramCount == 0 && bookCount > 0)
                    {
                        _logger.Debug("[TRIGRAM-INIT] No trigrams found but database has content - initializing...");

                        // Initialize FTS5 tables if using SQLite
                        if (_database.DatabaseType == DatabaseType.SQLite)
                        {
                            _logger.Debug("[TRIGRAM-INIT] Attempting FTS5 initialization");
                            InitializeFts5Tables();
                        }
                        else
                        {
                            _logger.Debug("[TRIGRAM-INIT] Skipping FTS5 initialization (not SQLite)");
                        }

                        // Populate trigrams for all existing data
                        _logger.Debug("[TRIGRAM-INIT] Starting full trigram population...");
                        _trigramMaintenanceService.UpdateAllTrigrams();

                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        _logger.Debug("[TRIGRAM-INIT] ========== Trigram initialization COMPLETE in {0:F1} seconds ==========", elapsed);
                    }
                    else if (bookTrigramCount > 0)
                    {
                        _logger.Debug("[TRIGRAM-INIT] Trigrams already populated, skipping initialization");

                        // Check for FTS5 tables
                        CheckFts5Status();
                    }
                    else
                    {
                        _logger.Debug("[TRIGRAM-INIT] Database is empty, skipping trigram initialization");
                    }
                }
            }
            catch (Exception ex)
            {
                var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.Error(ex, "[TRIGRAM-INIT] ========== FAILED to initialize trigrams after {0:F2}ms - fuzzy matching may not work properly ==========", elapsed);
            }
        }

        private void InitializeFts5Tables()
        {
            if (_database.DatabaseType != DatabaseType.SQLite)
            {
                return;
            }

            var startTime = DateTime.UtcNow;

            try
            {
                _logger.Debug("[TRIGRAM-INIT-FTS5] Starting FTS5 table initialization");

                _fuzzyMatchingRepository.PopulateFts5Tables();

                var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.Debug("[TRIGRAM-INIT-FTS5] FTS5 tables populated successfully in {0:F2}ms", elapsed);
            }
            catch (Exception ex)
            {
                var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.Warn(ex, "[TRIGRAM-INIT-FTS5] FTS5 initialization failed after {0:F2}ms - FTS5 search will not be available", elapsed);
            }
        }

        private void CheckFts5Status()
        {
            if (_database.DatabaseType != DatabaseType.SQLite)
            {
                return;
            }

            try
            {
                using (var connection = _database.OpenConnection())
                {
                    var bookFtsCount = connection.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM book_fts");
                    var authorFtsCount = connection.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM author_name_fts");
                    _logger.Debug("[TRIGRAM-INIT] FTS5 status: book_fts={0}, author_name_fts={1}", bookFtsCount, authorFtsCount);
                }
            }
            catch
            {
                _logger.Debug("[TRIGRAM-INIT] FTS5 tables not available");
            }
        }
    }
}

using System;
using System.IO;

namespace NzbDrone.Core.Datastore
{
    public sealed record PostgresDatabaseSelection(string MainDb, string LogDb, string CacheDb);

    public static class BookshelfPostgresDatabaseSelector
    {
        public const string ChaptarrMainDb = "chaptarr-main";
        public const string ChaptarrLogDb = "chaptarr-log";
        public const string ChaptarrCacheDb = "chaptarr-cache";
        public const string BookshelfMainDb = "readarr-main";
        public const string BookshelfLogDb = "readarr-log";
        public const string BookshelfCacheDb = "readarr-cache";

        public static PostgresDatabaseSelection Resolve(
            string mainDb,
            string logDb,
            string cacheDb,
            bool mainDbExplicit,
            bool logDbExplicit,
            bool cacheDbExplicit,
            Func<string, PostgresDatabaseState> inspect)
        {
            if (inspect == null)
            {
                throw new ArgumentNullException(nameof(inspect));
            }

            var mainState = inspect(mainDb);

            // BookshelfNG and ChaptarrNG intentionally use different unsaved defaults.
            // Only probe the legacy name when the implicit ChaptarrNG database cannot
            // already be identified as a supported application database.
            if (!mainDbExplicit &&
                string.Equals(mainDb, ChaptarrMainDb, StringComparison.Ordinal) &&
                mainState.Lineage is DatabaseLineage.Empty or DatabaseLineage.Unsupported)
            {
                var legacyState = inspect(BookshelfMainDb);

                if (legacyState.Lineage == DatabaseLineage.Unsupported ||
                    mainState.Lineage == DatabaseLineage.Unsupported && IsSupported(legacyState.Lineage))
                {
                    throw new InvalidDataException(
                        $"The PostgreSQL databases '{ChaptarrMainDb}' and '{BookshelfMainDb}' contain conflicting or unsupported application data. " +
                        "Startup stopped so neither database is silently ignored. Configure the intended main database explicitly after reviewing both databases.");
                }

                if (mainState.Lineage == DatabaseLineage.Empty && IsSupported(legacyState.Lineage))
                {
                    mainDb = BookshelfMainDb;
                    mainState = legacyState;
                }
            }

            var useBookshelfSideDatabases = mainState.Lineage == DatabaseLineage.BookshelfNG ||
                mainState.HasBookshelfMigrationMarker ||
                string.Equals(mainDb, BookshelfMainDb, StringComparison.Ordinal) && mainState.Lineage == DatabaseLineage.Chaptarr;

            if (useBookshelfSideDatabases)
            {
                if (!logDbExplicit && string.Equals(logDb, ChaptarrLogDb, StringComparison.Ordinal))
                {
                    logDb = BookshelfLogDb;
                }

                if (!cacheDbExplicit && string.Equals(cacheDb, ChaptarrCacheDb, StringComparison.Ordinal))
                {
                    cacheDb = BookshelfCacheDb;
                }
            }

            return new PostgresDatabaseSelection(mainDb, logDb, cacheDb);
        }

        private static bool IsSupported(DatabaseLineage lineage)
        {
            return lineage is DatabaseLineage.BookshelfNG or DatabaseLineage.Chaptarr;
        }
    }
}

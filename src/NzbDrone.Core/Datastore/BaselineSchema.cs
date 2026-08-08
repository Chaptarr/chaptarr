using System;

namespace NzbDrone.Core.Datastore
{
    // Holds expected baseline schema checksums per provider.
    // IMPORTANT: Update these checksums when modifying the baseline schema
    // Run: ./scripts/sqlite_dump.sh audioarrdata/chaptarr.db
    // Then copy the SHA256 from baseline_sqlite.sha256
    public static class BaselineSchema
    {
        // SQLite baseline checksum (normalized schema from sqlite_master; header stripped)
        public static readonly string ExpectedSqliteSha256 = ""; // set to the sha256 of normalized SQL (no header)

        // Optional: Postgres baseline checksum (normalized schema); set if you maintain a PG baseline
        public static readonly string ExpectedPostgresSha256 = "";
    }
}

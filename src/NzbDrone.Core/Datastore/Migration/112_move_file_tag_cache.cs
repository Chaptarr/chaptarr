using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(112)]
    public class move_file_tag_cache : NzbDroneMigrationBase
    {
        protected override void CacheDbUpgrade()
        {
            Execute.Sql(@"
                CREATE TABLE file_tag_cache(
                    path TEXT PRIMARY KEY,
                    mtime_ns BIGINT NOT NULL,
                    size_bytes BIGINT NOT NULL,
                    tags_json TEXT NOT NULL,
                    duration_seconds INTEGER,
                    extraction_status TEXT CHECK(extraction_status IN ('evidence', 'noisy_only', 'tagless')),
                    updated_at BIGINT NOT NULL
                );
                CREATE INDEX ix_file_tag_cache_updated ON file_tag_cache(updated_at);
            ");
        }
    }
}

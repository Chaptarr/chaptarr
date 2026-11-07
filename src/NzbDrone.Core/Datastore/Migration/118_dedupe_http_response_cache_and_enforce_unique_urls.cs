using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(118)]
    public class dedupe_http_response_cache_and_enforce_unique_urls : NzbDroneMigrationBase
    {
        protected override void CacheDbUpgrade()
        {
            if (!Schema.Table("HttpResponse").Exists())
            {
                return;
            }

            // Keep the most recently refreshed entry for each URL before adding
            // the unique index. The ID is a deterministic tie-breaker for rows
            // created by simultaneous cache misses.
            Execute.Sql(@"
                DELETE FROM ""HttpResponse""
                WHERE ""Id"" NOT IN (
                    SELECT candidate.""Id""
                    FROM ""HttpResponse"" candidate
                    WHERE candidate.""Url"" = ""HttpResponse"".""Url""
                    ORDER BY candidate.""LastRefresh"" DESC, candidate.""Id"" DESC
                    LIMIT 1
                );
            ");

            if (Schema.Table("HttpResponse").Index("IX_HttpResponse_Url").Exists())
            {
                Delete.Index("IX_HttpResponse_Url").OnTable("HttpResponse");
            }

            if (!Schema.Table("HttpResponse").Index("UX_HttpResponse_Url").Exists())
            {
                Create.Index("UX_HttpResponse_Url")
                    .OnTable("HttpResponse")
                    .OnColumn("Url")
                    .Unique();
            }
        }
    }
}

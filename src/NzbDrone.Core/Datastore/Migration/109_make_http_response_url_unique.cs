using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(109)]
    public class make_http_response_url_unique : NzbDroneMigrationBase
    {
        protected override void CacheDbUpgrade()
        {
            if (!Schema.Table("HttpResponse").Exists())
            {
                return;
            }

            // Concurrent lookups of one URL could each insert a row (the URL index was not unique). Keep the newest
            // row per URL (latest refresh, then highest id) so the unique index can be created.
            Execute.Sql(@"DELETE FROM ""HttpResponse""
                WHERE EXISTS (
                    SELECT 1 FROM ""HttpResponse"" newer
                    WHERE newer.""Url"" = ""HttpResponse"".""Url""
                      AND (newer.""LastRefresh"" > ""HttpResponse"".""LastRefresh""
                           OR (newer.""LastRefresh"" = ""HttpResponse"".""LastRefresh"" AND newer.""Id"" > ""HttpResponse"".""Id"")))");

            if (Schema.Table("HttpResponse").Index("IX_HttpResponse_Url").Exists())
            {
                Delete.Index("IX_HttpResponse_Url").OnTable("HttpResponse");
            }

            Create.Index("IX_HttpResponse_Url")
                .OnTable("HttpResponse")
                .OnColumn("Url").Ascending()
                .WithOptions().Unique();
        }
    }
}

using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(14)]
    public class add_http_response_cache_table : NzbDroneMigrationBase
    {
        protected override void CacheDbUpgrade()
        {
            // HTTP response cache used by CachedHttpResponseService.
            Create.TableForModel("HttpResponse")
                .WithColumn("Url").AsString().NotNullable()
                .WithColumn("LastRefresh").AsDateTime().NotNullable()
                .WithColumn("Expiry").AsDateTime().NotNullable()
                .WithColumn("Value").AsString().NotNullable()
                .WithColumn("StatusCode").AsInt32().NotNullable();

            Create.Index("IX_HttpResponse_Url").OnTable("HttpResponse").OnColumn("Url");
            Create.Index("IX_HttpResponse_Expiry").OnTable("HttpResponse").OnColumn("Expiry");
        }
    }
}


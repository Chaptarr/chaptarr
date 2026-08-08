using FluentMigrator;
using FluentMigrator.Builders.Create.Index;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(78)]
    public class add_history_query_indexes : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (Schema.Table("History").Exists())
            {
                CreateIndexIfMissing("History", "IX_History_EventType", new[] { "EventType" }, x => x.OnColumn("EventType"));
                CreateIndexIfMissing("History", "IX_History_DownloadId_EventType", new[] { "DownloadId", "EventType" }, x => x.OnColumn("DownloadId").Ascending().OnColumn("EventType").Ascending());
                CreateIndexIfMissing("History", "IX_History_BookId_EventType", new[] { "BookId", "EventType" }, x => x.OnColumn("BookId").Ascending().OnColumn("EventType").Ascending());
                CreateIndexIfMissing("History", "IX_History_AuthorId_EventType", new[] { "AuthorId", "EventType" }, x => x.OnColumn("AuthorId").Ascending().OnColumn("EventType").Ascending());
            }

            if (Schema.Table("DownloadHistory").Exists())
            {
                CreateIndexIfMissing("DownloadHistory", "IX_DownloadHistory_EventType", new[] { "EventType" }, x => x.OnColumn("EventType"));
                CreateIndexIfMissing("DownloadHistory", "IX_DownloadHistory_DownloadId", new[] { "DownloadId" }, x => x.OnColumn("DownloadId"));
                CreateIndexIfMissing("DownloadHistory", "IX_DownloadHistory_AuthorId", new[] { "AuthorId" }, x => x.OnColumn("AuthorId"));
                CreateIndexIfMissing("DownloadHistory", "IX_DownloadHistory_BookId", new[] { "BookId" }, x => x.OnColumn("BookId"));
                CreateIndexIfMissing("DownloadHistory", "IX_DownloadHistory_DownloadId_EventType", new[] { "DownloadId", "EventType" }, x => x.OnColumn("DownloadId").Ascending().OnColumn("EventType").Ascending());
            }
        }

        private void CreateIndexIfMissing(string table, string indexName, string[] columns, System.Action<ICreateIndexOnColumnSyntax> build)
        {
            if (Schema.Table(table).Index(indexName).Exists() || !ColumnsExist(table, columns))
            {
                return;
            }

            build(Create.Index(indexName).OnTable(table));
        }

        private bool ColumnsExist(string table, string[] columns)
        {
            foreach (var column in columns)
            {
                if (!Schema.Table(table).Column(column).Exists())
                {
                    return false;
                }
            }

            return true;
        }
    }
}

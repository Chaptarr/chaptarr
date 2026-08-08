using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(40)]
    public class fix_author_monitored_from_media_settings : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection((connection, transaction) =>
            {
                // Keep legacy Authors.Monitored consistent with the per-media monitoring settings.
                // TRI-STATE + FUTURE monitoring model:
                // - MonitorExisting: 0=None, 1=All, 2=Selected. Any value > 0 means the author is tracked for that media type.
                // - MonitorFuture: true means the author is tracked for that media type (even if existing is None).
                //
                // Uses a boolean expression assignment for cross-DB compatibility:
                // - PostgreSQL: boolean
                // - SQLite: 0/1
                connection.Execute(
                    @"UPDATE ""Authors""
                      SET ""Monitored"" = (
                          COALESCE(""AudiobookMonitorExisting"", 0) > 0 OR
                          COALESCE(""AudiobookMonitorFuture"", false) OR
                          COALESCE(""EbookMonitorExisting"", 0) > 0 OR
                          COALESCE(""EbookMonitorFuture"", false)
                      );",
                    transaction: transaction);
            });
        }
    }
}

using System;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(95)]
    public class add_bookfile_match_provenance : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("BookFiles").Column("MatchProvenance").Exists())
            {
                Alter.Table("BookFiles")
                    .AddColumn("MatchProvenance")
                    .AsString(int.MaxValue)
                    .Nullable();
            }

            // The numeric confidence experiment has had no writer since April 2026 and
            // was never populated. MatchProvenance is the non-percentage replacement.
            if (Schema.Table("BookFiles").Column("MatchConfidence").Exists())
            {
                Delete.Column("MatchConfidence").FromTable("BookFiles");
            }
        }
    }
}

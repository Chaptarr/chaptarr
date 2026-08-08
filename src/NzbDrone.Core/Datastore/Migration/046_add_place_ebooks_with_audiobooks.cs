using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(46)]
    public class add_place_ebooks_with_audiobooks : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("RootFolders").Column("PlaceEbooksWithAudiobooks").Exists())
            {
                Alter.Table("RootFolders")
                    .AddColumn("PlaceEbooksWithAudiobooks")
                    .AsBoolean()
                    .NotNullable()
                    .WithDefaultValue(false);
            }

            if (!Schema.Table("BookFiles").Column("ReplicaPaths").Exists())
            {
                Alter.Table("BookFiles")
                    .AddColumn("ReplicaPaths")
                    .AsString()
                    .NotNullable()
                    .WithDefaultValue("[]");
            }

            // Normalize any legacy/bad values (NULL/"") to empty arrays for the embedded List<string> converter.
            Execute.WithConnection((connection, transaction) =>
            {
                connection.Execute(
                    @"UPDATE ""BookFiles""
                      SET ""ReplicaPaths"" = '[]'
                      WHERE ""ReplicaPaths"" IS NULL OR ""ReplicaPaths"" = '' OR ""ReplicaPaths"" = 'null' OR ""ReplicaPaths"" = '{}';",
                    transaction: transaction);
            });
        }
    }
}


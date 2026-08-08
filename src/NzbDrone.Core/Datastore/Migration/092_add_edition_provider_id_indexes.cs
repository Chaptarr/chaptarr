using FluentMigrator;
using FluentMigrator.Builders.Create.Index;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(92)]
    public class add_edition_provider_id_indexes : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            CreateIndexIfMissing("Editions", "IX_Editions_ForeignEditionId", "ForeignEditionId", x => x.OnColumn("ForeignEditionId"));
            CreateIndexIfMissing("Editions", "IX_Editions_HardcoverEditionId", "HardcoverEditionId", x => x.OnColumn("HardcoverEditionId"));
            CreateIndexIfMissing("Editions", "IX_Editions_GoodreadsEditionId", "GoodreadsEditionId", x => x.OnColumn("GoodreadsEditionId"));
            CreateIndexIfMissing("Editions", "IX_Editions_OpenLibraryEditionId", "OpenLibraryEditionId", x => x.OnColumn("OpenLibraryEditionId"));
            CreateIndexIfMissing("Editions", "IX_Editions_GoogleBooksEditionId", "GoogleBooksEditionId", x => x.OnColumn("GoogleBooksEditionId"));
            CreateIndexIfMissing("Authors", "IX_Authors_CleanName", "CleanName", x => x.OnColumn("CleanName"));
        }

        private void CreateIndexIfMissing(string tableName, string indexName, string column, System.Action<ICreateIndexOnColumnSyntax> build)
        {
            if (!Schema.Table(tableName).Exists() ||
                !Schema.Table(tableName).Column(column).Exists() ||
                Schema.Table(tableName).Index(indexName).Exists())
            {
                return;
            }

            build(Create.Index(indexName).OnTable(tableName));
        }
    }
}

using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(79)]
    public class add_provider_alias_index : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("ProviderAliasIndex").Exists())
            {
                Create.Table("ProviderAliasIndex")
                    .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                    .WithColumn("EntityType").AsString(32).NotNullable()
                    .WithColumn("EntityId").AsInt32().NotNullable()
                    .WithColumn("Scope").AsString(32).NotNullable()
                    .WithColumn("Provider").AsString(16).NotNullable()
                    .WithColumn("NormalizedProviderId").AsString(128).NotNullable()
                    .WithColumn("CreatedAt").AsDateTime().NotNullable()
                    .WithColumn("UpdatedAt").AsDateTime().NotNullable();
            }

            if (!Schema.Table("ProviderAliasIndex").Index("IX_ProviderAliasIndex_Unique").Exists())
            {
                Create.Index("IX_ProviderAliasIndex_Unique")
                    .OnTable("ProviderAliasIndex")
                    .OnColumn("EntityType").Ascending()
                    .OnColumn("EntityId").Ascending()
                    .OnColumn("Scope").Ascending()
                    .OnColumn("Provider").Ascending()
                    .OnColumn("NormalizedProviderId").Ascending()
                    .WithOptions().Unique();
            }

            if (!Schema.Table("ProviderAliasIndex").Index("IX_ProviderAliasIndex_Lookup").Exists())
            {
                Create.Index("IX_ProviderAliasIndex_Lookup")
                    .OnTable("ProviderAliasIndex")
                    .OnColumn("Scope").Ascending()
                    .OnColumn("Provider").Ascending()
                    .OnColumn("NormalizedProviderId").Ascending();
            }

            BackfillAliases();
        }

        private void BackfillAliases()
        {
            if (Schema.Table("Books").Exists())
            {
                Execute.Sql(@"
INSERT INTO ""ProviderAliasIndex"" (""EntityType"", ""EntityId"", ""Scope"", ""Provider"", ""NormalizedProviderId"", ""CreatedAt"", ""UpdatedAt"")
SELECT 'Book', ""Id"", 'work', 'hc', REPLACE(REPLACE(TRIM(""HardcoverBookId""), 'hc:', ''), 'HC:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Books"" WHERE ""HardcoverBookId"" IS NOT NULL AND LENGTH(TRIM(""HardcoverBookId"")) > 0
UNION
SELECT 'Book', ""Id"", 'work', 'gr', REPLACE(REPLACE(TRIM(""GoodreadsWorkId""), 'gr:', ''), 'GR:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Books"" WHERE ""GoodreadsWorkId"" IS NOT NULL AND LENGTH(TRIM(""GoodreadsWorkId"")) > 0
UNION
SELECT 'Book', ""Id"", 'work', 'ol', REPLACE(REPLACE(TRIM(""OpenLibraryWorkId""), 'ol:', ''), 'OL:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Books"" WHERE ""OpenLibraryWorkId"" IS NOT NULL AND LENGTH(TRIM(""OpenLibraryWorkId"")) > 0
UNION
SELECT 'Book', ""Id"", 'edition', 'gr', REPLACE(REPLACE(TRIM(""GoodreadsBookId""), 'gr:', ''), 'GR:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Books"" WHERE ""GoodreadsBookId"" IS NOT NULL AND LENGTH(TRIM(""GoodreadsBookId"")) > 0
UNION
SELECT 'Book', ""Id"", 'edition', 'ol', REPLACE(REPLACE(TRIM(""OpenLibraryEditionId""), 'ol:', ''), 'OL:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Books"" WHERE ""OpenLibraryEditionId"" IS NOT NULL AND LENGTH(TRIM(""OpenLibraryEditionId"")) > 0
UNION
SELECT 'Book', ""Id"", 'edition', 'gb', REPLACE(REPLACE(TRIM(""GoogleBooksId""), 'gb:', ''), 'GB:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Books"" WHERE ""GoogleBooksId"" IS NOT NULL AND LENGTH(TRIM(""GoogleBooksId"")) > 0
UNION
SELECT 'Book', ""Id"", 'edition', 'az', UPPER(REPLACE(REPLACE(TRIM(""ASIN""), 'az:', ''), 'AZ:', '')), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Books"" WHERE ""ASIN"" IS NOT NULL AND LENGTH(TRIM(""ASIN"")) > 0
UNION
SELECT 'Book', ""Id"", 'edition', 'az', UPPER(REPLACE(REPLACE(TRIM(""AudibleASIN""), 'az:', ''), 'AZ:', '')), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Books"" WHERE ""AudibleASIN"" IS NOT NULL AND LENGTH(TRIM(""AudibleASIN"")) > 0");
            }

            if (Schema.Table("Authors").Exists())
            {
                Execute.Sql(@"
INSERT INTO ""ProviderAliasIndex"" (""EntityType"", ""EntityId"", ""Scope"", ""Provider"", ""NormalizedProviderId"", ""CreatedAt"", ""UpdatedAt"")
SELECT 'Author', ""Id"", 'author', 'hc', REPLACE(REPLACE(TRIM(""HardcoverAuthorId""), 'hc:', ''), 'HC:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Authors"" WHERE ""HardcoverAuthorId"" IS NOT NULL AND LENGTH(TRIM(""HardcoverAuthorId"")) > 0
UNION
SELECT 'Author', ""Id"", 'author', 'gr', REPLACE(REPLACE(TRIM(""GoodreadsAuthorId""), 'gr:', ''), 'GR:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Authors"" WHERE ""GoodreadsAuthorId"" IS NOT NULL AND LENGTH(TRIM(""GoodreadsAuthorId"")) > 0
UNION
SELECT 'Author', ""Id"", 'author', 'ol', REPLACE(REPLACE(TRIM(""OpenLibraryAuthorId""), 'ol:', ''), 'OL:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Authors"" WHERE ""OpenLibraryAuthorId"" IS NOT NULL AND LENGTH(TRIM(""OpenLibraryAuthorId"")) > 0
UNION
SELECT 'Author', ""Id"", 'author', 'gb', REPLACE(REPLACE(TRIM(""GoogleBooksAuthorId""), 'gb:', ''), 'GB:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Authors"" WHERE ""GoogleBooksAuthorId"" IS NOT NULL AND LENGTH(TRIM(""GoogleBooksAuthorId"")) > 0
UNION
SELECT 'Author', ""Id"", 'author', 'az', UPPER(REPLACE(REPLACE(TRIM(""AudnexusAuthorId""), 'az:', ''), 'AZ:', '')), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Authors"" WHERE ""AudnexusAuthorId"" IS NOT NULL AND LENGTH(TRIM(""AudnexusAuthorId"")) > 0");
            }

            if (Schema.Table("Editions").Exists())
            {
                Execute.Sql(@"
INSERT INTO ""ProviderAliasIndex"" (""EntityType"", ""EntityId"", ""Scope"", ""Provider"", ""NormalizedProviderId"", ""CreatedAt"", ""UpdatedAt"")
SELECT 'Edition', ""Id"", 'edition', 'gr', REPLACE(REPLACE(TRIM(CAST(""GoodreadsEditionId"" AS TEXT)), 'gr:', ''), 'GR:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Editions"" WHERE ""GoodreadsEditionId"" IS NOT NULL AND LENGTH(TRIM(CAST(""GoodreadsEditionId"" AS TEXT))) > 0
UNION
SELECT 'Edition', ""Id"", 'edition', 'hc', REPLACE(REPLACE(TRIM(""HardcoverEditionId""), 'hc:', ''), 'HC:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Editions"" WHERE ""HardcoverEditionId"" IS NOT NULL AND LENGTH(TRIM(""HardcoverEditionId"")) > 0
UNION
SELECT 'Edition', ""Id"", 'edition', 'ol', REPLACE(REPLACE(TRIM(""OpenLibraryEditionId""), 'ol:', ''), 'OL:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Editions"" WHERE ""OpenLibraryEditionId"" IS NOT NULL AND LENGTH(TRIM(""OpenLibraryEditionId"")) > 0
UNION
SELECT 'Edition', ""Id"", 'edition', 'gb', REPLACE(REPLACE(TRIM(""GoogleBooksEditionId""), 'gb:', ''), 'GB:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Editions"" WHERE ""GoogleBooksEditionId"" IS NOT NULL AND LENGTH(TRIM(""GoogleBooksEditionId"")) > 0
UNION
SELECT 'Edition', ""Id"", 'edition', 'az', UPPER(REPLACE(REPLACE(TRIM(""Asin""), 'az:', ''), 'AZ:', '')), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Editions"" WHERE ""Asin"" IS NOT NULL AND LENGTH(TRIM(""Asin"")) > 0
UNION
SELECT 'Edition', ""Id"", 'edition', 'az', UPPER(REPLACE(REPLACE(TRIM(""AudibleASIN""), 'az:', ''), 'AZ:', '')), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Editions"" WHERE ""AudibleASIN"" IS NOT NULL AND LENGTH(TRIM(""AudibleASIN"")) > 0");
            }

            if (Schema.Table("Series").Exists())
            {
                Execute.Sql(@"
INSERT INTO ""ProviderAliasIndex"" (""EntityType"", ""EntityId"", ""Scope"", ""Provider"", ""NormalizedProviderId"", ""CreatedAt"", ""UpdatedAt"")
SELECT 'Series', ""Id"", 'series', 'hc', REPLACE(REPLACE(TRIM(""HardcoverSeriesId""), 'hc:', ''), 'HC:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Series"" WHERE ""HardcoverSeriesId"" IS NOT NULL AND LENGTH(TRIM(""HardcoverSeriesId"")) > 0
UNION
SELECT 'Series', ""Id"", 'series', 'gr', REPLACE(REPLACE(TRIM(""GoodreadsSeriesId""), 'gr:', ''), 'GR:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Series"" WHERE ""GoodreadsSeriesId"" IS NOT NULL AND LENGTH(TRIM(""GoodreadsSeriesId"")) > 0
UNION
SELECT 'Series', ""Id"", 'series', 'ol', REPLACE(REPLACE(TRIM(""OpenLibrarySeriesId""), 'ol:', ''), 'OL:', ''), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Series"" WHERE ""OpenLibrarySeriesId"" IS NOT NULL AND LENGTH(TRIM(""OpenLibrarySeriesId"")) > 0
UNION
SELECT 'Series', ""Id"", 'series', 'az', UPPER(REPLACE(REPLACE(TRIM(""AmazonSeriesAsin""), 'az:', ''), 'AZ:', '')), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP FROM ""Series"" WHERE ""AmazonSeriesAsin"" IS NOT NULL AND LENGTH(TRIM(""AmazonSeriesAsin"")) > 0");
            }
        }
    }
}

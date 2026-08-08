using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(23)]
    public class add_ebook_naming_config : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            if (!Schema.Table("NamingConfig").Column("EbookRenameBooks").Exists())
            {
                Alter.Table("NamingConfig")
                    .AddColumn("EbookRenameBooks").AsBoolean().NotNullable().WithDefaultValue(false);
            }

            if (!Schema.Table("NamingConfig").Column("EbookReplaceIllegalCharacters").Exists())
            {
                Alter.Table("NamingConfig")
                    .AddColumn("EbookReplaceIllegalCharacters").AsBoolean().NotNullable().WithDefaultValue(true);
            }

            if (!Schema.Table("NamingConfig").Column("EbookStandardBookFormat").Exists())
            {
                Alter.Table("NamingConfig")
                    .AddColumn("EbookStandardBookFormat").AsString().Nullable();
            }

            if (!Schema.Table("NamingConfig").Column("EbookAuthorFolderFormat").Exists())
            {
                Alter.Table("NamingConfig")
                    .AddColumn("EbookAuthorFolderFormat").AsString().Nullable();
            }

            if (!Schema.Table("NamingConfig").Column("EbookColonReplacementFormat").Exists())
            {
                Alter.Table("NamingConfig")
                    .AddColumn("EbookColonReplacementFormat").AsInt32().NotNullable().WithDefaultValue(0);
            }

            Execute.WithConnection((connection, transaction) =>
            {
                // Backfill ebook settings from the existing (audiobook) naming settings so behavior is unchanged
                // until the user explicitly configures a different pattern for ebooks.
                connection.Execute(@"
UPDATE ""NamingConfig""
SET
    ""EbookRenameBooks"" = ""RenameBooks"",
    ""EbookReplaceIllegalCharacters"" = ""ReplaceIllegalCharacters"",
    ""EbookStandardBookFormat"" = ""StandardBookFormat"",
    ""EbookAuthorFolderFormat"" = ""AuthorFolderFormat"",
    ""EbookColonReplacementFormat"" = ""ColonReplacementFormat"";
", transaction: transaction);
            });
        }
    }
}


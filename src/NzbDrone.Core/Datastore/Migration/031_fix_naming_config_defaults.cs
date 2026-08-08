using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(31)]
    public class fix_naming_config_defaults : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Update the audiobook default to use smart PartNumber padding instead of forcing 3 digits.
            Execute.Sql(@"
UPDATE ""NamingConfig""
SET ""StandardBookFormat"" = '{Book Title}{ - Narrator}/{Book Title}{ (PartNumber:smart)}'
WHERE ""StandardBookFormat"" = '{Book Title}{ - Narrator}/{Book Title}{ (PartNumber:000)}'
   OR ""StandardBookFormat"" = '{Book Title}{ - Narrator}\{Book Title}{ (PartNumber:000)}';
");

            // Update the ebook default to not include narrator tokens.
            // Only auto-correct patterns that are clearly defaults/backfills.
            Execute.Sql(@"
	    UPDATE ""NamingConfig""
	SET ""EbookStandardBookFormat"" = '{Book Title}/{Book Title}{ (PartNumber:smart)}'
	WHERE
	    ""EbookStandardBookFormat"" IS NULL
	    OR ""EbookStandardBookFormat"" = ''
	    OR (
	        ""EbookStandardBookFormat"" = ""StandardBookFormat""
	        AND ""EbookStandardBookFormat"" LIKE '%Narrator%'
	        AND ""EbookRenameBooks"" = ""RenameBooks""
	        AND ""EbookReplaceIllegalCharacters"" = ""ReplaceIllegalCharacters""
	        AND COALESCE(""EbookAuthorFolderFormat"", '') = COALESCE(""AuthorFolderFormat"", '')
	        AND ""EbookColonReplacementFormat"" = ""ColonReplacementFormat""
	    )
	    OR ""EbookStandardBookFormat"" = '{Book Title}{ - Narrator}/{Book Title}{ (PartNumber:000)}'
	    OR ""EbookStandardBookFormat"" = '{Book Title}{ - Narrator}\{Book Title}{ (PartNumber:000)}'
	    OR ""EbookStandardBookFormat"" = '{Book Title}{ (PartNumber)}/{Author Name} - {Book Title}'
	    OR ""EbookStandardBookFormat"" = '{Book Title}{ (PartNumber)}\{Author Name} - {Book Title}';
");
        }
    }
}

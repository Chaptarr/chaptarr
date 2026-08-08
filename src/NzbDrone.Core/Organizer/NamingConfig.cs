using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Organizer
{
    public class NamingConfig : ModelBase
    {
        public static NamingConfig Default => new NamingConfig
        {
            RenameBooks = false,
            ReplaceIllegalCharacters = true,
            ColonReplacementFormat = ColonReplacementFormat.Smart,
            // Default audiobook organization: a single book folder (optionally including narrator)
            // with PartNumber only in the filename to avoid per-track folders for multi-part imports.
            // Use "smart" PartNumber padding so players that sort by filename keep correct order
            // (1, 2, 10 -> 01, 02, 10) without always forcing 3 digits (1, 2, 3 -> 1, 2, 3).
            StandardBookFormat = "{Book Title}{ - Narrator}/{Book Title}{ (PartNumber:smart)}",
            AuthorFolderFormat = "{Author NameFirstLast}",

            EbookRenameBooks = false,
            EbookReplaceIllegalCharacters = true,
            EbookColonReplacementFormat = ColonReplacementFormat.Smart,
            // Default ebook organization: no narrator tokens, and PartNumber uses the same smart padding behavior.
            EbookStandardBookFormat = "{Book Title}/{Book Title}{ (PartNumber:smart)}",
            EbookAuthorFolderFormat = "{Author NameFirstLast}",
        };

        public bool RenameBooks { get; set; }
        public bool ReplaceIllegalCharacters { get; set; }
        public ColonReplacementFormat ColonReplacementFormat { get; set; }
        public string StandardBookFormat { get; set; }
        public string AuthorFolderFormat { get; set; }

        // Ebook-specific naming configuration (audiobook settings use the legacy fields above)
        public bool EbookRenameBooks { get; set; }
        public bool EbookReplaceIllegalCharacters { get; set; }
        public ColonReplacementFormat EbookColonReplacementFormat { get; set; }
        public string EbookStandardBookFormat { get; set; }
        public string EbookAuthorFolderFormat { get; set; }

        public NamingConfig GetForMediaType(string mediaType)
        {
            if (!string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase))
            {
                return this;
            }

            return new NamingConfig
            {
                Id = Id,

                RenameBooks = EbookRenameBooks,
                ReplaceIllegalCharacters = EbookReplaceIllegalCharacters,
                ColonReplacementFormat = EbookColonReplacementFormat,
                StandardBookFormat = string.IsNullOrWhiteSpace(EbookStandardBookFormat) ? StandardBookFormat : EbookStandardBookFormat,
                AuthorFolderFormat = string.IsNullOrWhiteSpace(EbookAuthorFolderFormat) ? AuthorFolderFormat : EbookAuthorFolderFormat,

                EbookRenameBooks = EbookRenameBooks,
                EbookReplaceIllegalCharacters = EbookReplaceIllegalCharacters,
                EbookColonReplacementFormat = EbookColonReplacementFormat,
                EbookStandardBookFormat = EbookStandardBookFormat,
                EbookAuthorFolderFormat = EbookAuthorFolderFormat
            };
        }
    }
}

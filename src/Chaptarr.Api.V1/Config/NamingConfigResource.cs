using Chaptarr.Http.REST;

namespace Chaptarr.Api.V1.Config
{
    public class NamingConfigResource : RestResource
    {
        public bool RenameBooks { get; set; }
        public bool ReplaceIllegalCharacters { get; set; }
        public int ColonReplacementFormat { get; set; }
        public string StandardBookFormat { get; set; }
        public string AuthorFolderFormat { get; set; }

        public bool? EbookRenameBooks { get; set; }
        public bool? EbookReplaceIllegalCharacters { get; set; }
        public int? EbookColonReplacementFormat { get; set; }
        public string EbookStandardBookFormat { get; set; }
        public string EbookAuthorFolderFormat { get; set; }

        public bool IncludeAuthorName { get; set; }
        public bool IncludeBookTitle { get; set; }
        public bool IncludeQuality { get; set; }
        public bool ReplaceSpaces { get; set; }
        public string Separator { get; set; }
        public string NumberStyle { get; set; }
    }
}

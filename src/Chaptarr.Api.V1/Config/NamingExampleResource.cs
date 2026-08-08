using NzbDrone.Core.Organizer;

namespace Chaptarr.Api.V1.Config
{
    public class NamingExampleResource
    {
        public string SingleBookExample { get; set; }
        public string MultiPartBookExample { get; set; }
        public string AuthorFolderExample { get; set; }
    }

    public static class NamingConfigResourceMapper
    {
        public static NamingConfigResource ToResource(this NamingConfig model)
        {
            return new NamingConfigResource
            {
                Id = model.Id,

                RenameBooks = model.RenameBooks,
                ReplaceIllegalCharacters = model.ReplaceIllegalCharacters,
                ColonReplacementFormat = (int)model.ColonReplacementFormat,
                StandardBookFormat = model.StandardBookFormat,
                AuthorFolderFormat = model.AuthorFolderFormat,

                EbookRenameBooks = model.EbookRenameBooks,
                EbookReplaceIllegalCharacters = model.EbookReplaceIllegalCharacters,
                EbookColonReplacementFormat = (int)model.EbookColonReplacementFormat,
                EbookStandardBookFormat = model.EbookStandardBookFormat,
                EbookAuthorFolderFormat = model.EbookAuthorFolderFormat
            };
        }

        public static void AddToResource(this BasicNamingConfig basicNamingConfig, NamingConfigResource resource)
        {
            resource.IncludeAuthorName = basicNamingConfig.IncludeAuthorName;
            resource.IncludeBookTitle = basicNamingConfig.IncludeBookTitle;
            resource.IncludeQuality = basicNamingConfig.IncludeQuality;
            resource.ReplaceSpaces = basicNamingConfig.ReplaceSpaces;
            resource.Separator = basicNamingConfig.Separator;
            resource.NumberStyle = basicNamingConfig.NumberStyle;
        }

        public static NamingConfig ToModel(this NamingConfigResource resource)
        {
            return new NamingConfig
            {
                Id = resource.Id,

                RenameBooks = resource.RenameBooks,
                ReplaceIllegalCharacters = resource.ReplaceIllegalCharacters,
                ColonReplacementFormat = (ColonReplacementFormat)resource.ColonReplacementFormat,
                StandardBookFormat = resource.StandardBookFormat,
                AuthorFolderFormat = resource.AuthorFolderFormat,

                EbookRenameBooks = resource.EbookRenameBooks ?? resource.RenameBooks,
                EbookReplaceIllegalCharacters = resource.EbookReplaceIllegalCharacters ?? resource.ReplaceIllegalCharacters,
                EbookColonReplacementFormat = (ColonReplacementFormat)(resource.EbookColonReplacementFormat ?? resource.ColonReplacementFormat),
                EbookStandardBookFormat = resource.EbookStandardBookFormat ?? resource.StandardBookFormat,
                EbookAuthorFolderFormat = resource.EbookAuthorFolderFormat ?? resource.AuthorFolderFormat,
            };
        }
    }
}

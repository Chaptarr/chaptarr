using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Download.Clients.Flood.Models
{
    public enum AdditionalTags
    {
        [FieldOption(Label = "NamingTokenCategoryAuthor", Hint = "J.R.R. Tolkien")]
        Author = 0,

        [FieldOption(Label = "Format", Hint = "EPUB")]
        Format = 1,

        [FieldOption(Label = "ReleaseGroup", Hint = "Example-Raws")]
        ReleaseGroup = 2,

        [FieldOption(Label = "Year", Hint = "1954")]
        Year = 3,

        [FieldOption(Label = "Indexer", Hint = "Torznab")]
        Indexer = 4,
    }
}

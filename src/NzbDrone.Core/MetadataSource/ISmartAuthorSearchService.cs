using System.Collections.Generic;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MetadataSource
{
    public interface ISmartAuthorSearchService
    {
        List<Author> SearchAndCreateAuthors(List<string> authorNames);
        Author GetOrCreateAuthor(string authorName);
        List<string> ExtractAuthorNamesFromId3(Dictionary<string, List<string>> id3Tags);
    }
}

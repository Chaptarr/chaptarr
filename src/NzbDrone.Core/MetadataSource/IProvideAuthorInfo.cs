using System.Threading.Tasks;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace NzbDrone.Core.MetadataSource
{
    public interface IProvideAuthorInfo
    {
        Author GetAuthorInfo(string chaptarrId, bool useCache = true);
        RefreshResult RefreshAuthorInfo(string authorId, string etag = null, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false);
    }

    public interface IProvideAuthorInfoAsync : IProvideAuthorInfo
    {
        Task<Author> GetAuthorInfoAsync(string chaptarrId, bool useCache = true);
    }
}

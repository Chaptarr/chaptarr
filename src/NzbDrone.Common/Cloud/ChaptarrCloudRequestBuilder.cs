using NzbDrone.Common.Http;

namespace NzbDrone.Common.Cloud
{
    public interface IChaptarrCloudRequestBuilder
    {
        IHttpRequestBuilderFactory Services { get; }
        IHttpRequestBuilderFactory GitHubApi { get; }
    }

    public class ChaptarrCloudRequestBuilder : IChaptarrCloudRequestBuilder
    {
        public ChaptarrCloudRequestBuilder()
        {
            // Chaptarr update service (arr-style update endpoints).
            // Hash/metadata are served from the Chaptarr services domain; the payload URL may still be GitHub.
            Services = new HttpRequestBuilder("https://services.chaptarr.com/v1/")
                .CreateFactory();

            // GitHub API for update checking
            GitHubApi = new HttpRequestBuilder("https://api.github.com/")
                .SetHeader("Accept", "application/vnd.github.v3+json")
                .SetHeader("User-Agent", "Chaptarr")
                .CreateFactory();
        }

        public IHttpRequestBuilderFactory Services { get; }

        public IHttpRequestBuilderFactory GitHubApi { get; }
    }
}

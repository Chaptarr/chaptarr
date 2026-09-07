using System.Threading.Tasks;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public sealed class NullBrowserDownloadResolver : IBrowserDownloadResolver
    {
        public Task<bool> IsAvailableAsync() => Task.FromResult(false);

        public Task<string> TryResolveSlowDownloadUrlAsync(string infoUrl) => Task.FromResult<string>(null);
    }
}

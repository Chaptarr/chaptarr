using System.Threading;
using System.Threading.Tasks;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public interface IBrowserDownloadResolver
    {
        Task<bool> IsAvailableAsync();

        Task<string> TryResolveSlowDownloadUrlAsync(string infoUrl);

        Task<string> TryResolveSlowDownloadUrlAsync(string infoUrl, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TryResolveSlowDownloadUrlAsync(infoUrl);
        }
    }
}

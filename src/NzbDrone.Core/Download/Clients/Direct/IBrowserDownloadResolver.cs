using System.Threading.Tasks;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public interface IBrowserDownloadResolver
    {
        Task<bool> IsAvailableAsync();

        Task<string> TryResolveSlowDownloadUrlAsync(string infoUrl);
    }
}

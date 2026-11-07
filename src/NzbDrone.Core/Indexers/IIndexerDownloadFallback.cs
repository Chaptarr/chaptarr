namespace NzbDrone.Core.Indexers
{
    public interface IIndexerDownloadFallback
    {
        string GetFallbackDownloadUrl(string downloadUrl);

        bool IsRequiredDownloadFallback(string downloadUrl);
    }
}

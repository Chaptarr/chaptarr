namespace NzbDrone.Core.Download.Clients
{
    // A deliberate library-safety refusal, not a download-client outage.
    public class DownloadDataDeletionRefusedException : DownloadClientException
    {
        public DownloadDataDeletionRefusedException(string message, params object[] args)
            : base(message, args)
        {
        }
    }
}

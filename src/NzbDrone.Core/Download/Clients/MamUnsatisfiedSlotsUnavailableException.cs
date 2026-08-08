namespace NzbDrone.Core.Download.Clients
{
    public class MamUnsatisfiedSlotsUnavailableException : DownloadClientException
    {
        public MamUnsatisfiedSlotsUnavailableException(string message)
            : base(message)
        {
        }
    }
}

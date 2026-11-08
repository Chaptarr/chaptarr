using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Download
{
    // Only the transition into or out of a cleanup verification failure needs a health check.
    public class DownloadCleanupStatusChangedEvent : IEvent
    {
    }
}

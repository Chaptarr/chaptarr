using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class CachesClearedEvent : IEvent
    {
        public string Reason { get; }

        public CachesClearedEvent(string reason)
        {
            Reason = reason;
        }
    }
}

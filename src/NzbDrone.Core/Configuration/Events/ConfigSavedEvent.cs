using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Configuration.Events
{
    public class ConfigSavedEvent : IEvent
    {
        public bool BookCoverCleanupRequired { get; set; }
    }
}

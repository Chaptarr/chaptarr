using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Profiles.Metadata.Events
{
    public class MetadataProfileUpdatedEvent : IEvent
    {
        public MetadataProfile MetadataProfile { get; private set; }
        public MetadataProfile PreviousMetadataProfile { get; private set; }

        public MetadataProfileUpdatedEvent(MetadataProfile metadataProfile, MetadataProfile previousMetadataProfile = null)
        {
            MetadataProfile = metadataProfile;
            PreviousMetadataProfile = previousMetadataProfile;
        }
    }
}

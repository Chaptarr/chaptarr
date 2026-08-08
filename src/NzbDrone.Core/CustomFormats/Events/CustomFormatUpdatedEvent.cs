using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.CustomFormats.Events
{
    public class CustomFormatUpdatedEvent : IEvent
    {
        public CustomFormatUpdatedEvent(CustomFormat format, CustomFormatMediaType previousAppliesTo)
        {
            CustomFormat = format;
            PreviousAppliesTo = previousAppliesTo;
        }

        public CustomFormat CustomFormat { get; }
        public CustomFormatMediaType PreviousAppliesTo { get; }
    }
}

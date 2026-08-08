using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.CustomFormats.Events
{
    public class CustomFormatAddedEvent : IEvent
    {
        public CustomFormatAddedEvent(CustomFormat format, int? audiobookProfileScore = null)
        {
            CustomFormat = format;
            AudiobookProfileScore = audiobookProfileScore;
        }

        public CustomFormat CustomFormat { get; private set; }
        public int? AudiobookProfileScore { get; private set; }
    }
}

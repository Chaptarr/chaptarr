using NLog;
using NLog.Layouts;

namespace NzbDrone.Common.Instrumentation
{
    public sealed class CleanseLayout : Layout
    {
        public Layout Inner { get; set; } = "${message}";

        protected override string GetFormattedMessage(LogEventInfo logEvent)
        {
            var raw = Inner?.Render(logEvent) ?? string.Empty;
            return CleanseLogMessage.Cleanse(raw);
        }
    }
}


using System.Collections.Concurrent;
using NLog;
using NLog.Targets;

namespace Chaptarr.Core.Test.Common
{
    internal sealed class ConcurrentLogTarget : TargetWithLayout
    {
        public ConcurrentQueue<string> Logs { get; } = new();
        protected override void Write(LogEventInfo logEvent) => Logs.Enqueue(Layout.Render(logEvent));
    }
}

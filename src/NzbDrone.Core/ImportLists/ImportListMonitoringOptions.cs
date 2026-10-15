using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.ImportLists
{
    public class ImportListMonitoringOptions : IEmbeddedDocument
    {
        public bool AddMissingAuthors { get; set; } = true;
        public bool ResumePausedAuthors { get; set; }
        public bool? AudiobookMonitored { get; set; }
        public bool? EbookMonitored { get; set; }
        public MonitorTypes? AudiobookMonitor { get; set; }
        public MonitorTypes? EbookMonitor { get; set; }
        public NewItemMonitorTypes? AudiobookMonitorNewItems { get; set; }
        public NewItemMonitorTypes? EbookMonitorNewItems { get; set; }
    }
}

using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public class MonitoringOptions : IEmbeddedDocument
    {
        public MonitoringOptions()
        {
            BooksToMonitor = new List<string>();
        }

        public MonitorTypes Monitor { get; set; }
        // Optional: when set, apply monitoring changes only to this media type (audiobook/ebook).
        // When null, apply to all books (legacy behavior).
        public BookMediaType? MediaType { get; set; }
        public List<string> BooksToMonitor { get; set; }
        public bool Monitored { get; set; }
    }
}

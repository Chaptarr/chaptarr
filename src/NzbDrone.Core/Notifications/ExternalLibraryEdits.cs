using System;
using System.Collections.Generic;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Notifications
{
    public class ExternalLibraryEditPayload
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Description { get; set; }
        public string Publisher { get; set; }
        public DateTime? PublishedDate { get; set; }
        public string SeriesName { get; set; }
        public double? SeriesPosition { get; set; }
        public List<string> Languages { get; set; }
        public List<string> Genres { get; set; }
        public Dictionary<string, string> Identifiers { get; set; }
        public string CoverUrl { get; set; }
        public byte[] CoverBytes { get; set; }
    }

    // Seam between library-edit sources (e.g. the Grimmory forwarder) and connections able to
    // mirror those edits outward. Sources discover targets via the notification factory, so a
    // provider opts in simply by implementing this on top of NotificationBase; nothing here
    // references a concrete provider, keeping each side independently mergeable.
    public interface IExternalLibraryEditTarget
    {
        ProviderDefinition Definition { get; }
        bool AcceptsExternalLibraryEdits { get; }
        void PushExternalLibraryEdit(Book book, List<BookFile> files, ExternalLibraryEditPayload payload);
    }
}

using NzbDrone.Core.Download;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Diagnostics
{
    public class DiagnosticsEventHandler :
        IHandle<ApplicationStartedEvent>,
        IHandle<BookGrabbedEvent>,
        IHandle<BookImportedEvent>,
        IHandle<DownloadFailedEvent>,
        IHandle<ApplicationShutdownRequested>
    {
        private readonly IDiagnosticsReporter _reporter;

        public DiagnosticsEventHandler(IDiagnosticsReporter reporter)
        {
            _reporter = reporter;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _reporter.Start();
            _reporter.Record("application.started");
        }

        public void Handle(BookGrabbedEvent message)
        {
            _reporter.Record("book.grabbed");
        }

        public void Handle(BookImportedEvent message)
        {
            _reporter.Record("book.imported");
        }

        public void Handle(DownloadFailedEvent message)
        {
            _reporter.Record("download.failed");
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            _reporter.StopAsync().GetAwaiter().GetResult();
        }
    }
}

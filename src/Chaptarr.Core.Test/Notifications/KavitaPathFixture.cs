using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Net.Sockets;
using NzbDrone.Core.MediaFiles.Events;
using Chaptarr.Core.Test.MediaFiles;
using System.Text;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Kavita;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class KavitaPathFixture
    {
        public class HttpProxy : DispatchProxy
        {
            public List<HttpRequest> Requests { get; } = new();
            public Exception Failure { get; set; }
            protected override object Invoke(MethodInfo method, object[] args)
            {
                var request = (HttpRequest)args[0];
                Requests.Add(request);
                if (Failure != null) throw Failure;
                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, "{\"Token\":\"fixture\"}");
            }
        }

        [TestCase("import")]
        [TestCase("delete")]
        [TestCase("file-delete")]
        [TestCase("retag")]
        public void sends_only_the_real_parent_folder_in_the_scan_request(string action)
        {
            var http = DispatchProxy.Create<IHttpClient, HttpProxy>();
            var notification = Create(http);
            Assert.That(notification.Test().IsValid, Is.True);
            Assert.That(((HttpProxy)http).Requests, Has.Count.EqualTo(1), "Test authenticates; it does not scan");
            var path = Path.GetFullPath(Path.Combine("library", "Author", "Book", "Part 01.epub"));
            Send(notification, action, new BookFile { Path = path });
            Assert.That(((HttpProxy)http).Requests, Has.Count.EqualTo(1));
            notification.ProcessQueue();
            var requests = ((HttpProxy)http).Requests;
            Assert.That(requests, Has.Count.EqualTo(2));
            var body = JObject.Parse(Encoding.UTF8.GetString(requests[1].ContentData));
            Assert.That((string)body["folderPath"], Is.EqualTo(Path.GetDirectoryName(path)));
            Assert.That(requests[1].Url.FullUri, Does.EndWith("/api/library/scan-folder"));
        }

        [TestCase("import")]
        [TestCase("delete")]
        [TestCase("file-delete")]
        [TestCase("retag")]
        public void missing_file_evidence_does_not_scan_an_invented_path(string action)
        {
            var http = DispatchProxy.Create<IHttpClient, HttpProxy>();
            var notification = Create(http);
            Send(notification, action, null);
            Send(notification, action, new BookFile());
            notification.ProcessQueue();
            Assert.That(((HttpProxy)http).Requests, Is.Empty);
        }

        [Test]
        public void disabled_notifications_do_not_scan()
        {
            var http = DispatchProxy.Create<IHttpClient, HttpProxy>();
            var notification = Create(http);
            ((KavitaSettings)notification.Definition.Settings).Notify = false;
            Send(notification, "import", new BookFile { Path = Path.GetFullPath("book.epub") });
            notification.ProcessQueue();
            Assert.That(((HttpProxy)http).Requests, Is.Empty);
        }

        [Test]
        public void connection_failure_reaches_the_shared_notification_status_handler()
        {
            var http = DispatchProxy.Create<IHttpClient, HttpProxy>();
            ((HttpProxy)http).Failure = new SocketException();
            var notification = Create(http);
            var failures = 0;
            var successes = 0;
            var service = new NotificationService(
                AuthorDeletionSafetyFixture.Proxy<INotificationFactory>((_, _) => new List<INotification> { notification }),
                AuthorDeletionSafetyFixture.Proxy<INotificationStatusService>((method, _) =>
                {
                    if (method.Name == nameof(INotificationStatusService.GetBlockedProviders)) return new List<NotificationStatus>();
                    if (method.Name == nameof(INotificationStatusService.RecordFailure)) failures++;
                    else if (method.Name == nameof(INotificationStatusService.RecordSuccess)) successes++;
                    else throw new NotImplementedException(method.Name);
                    return null;
                }), null, LogManager.GetCurrentClassLogger());

            service.Handle(new BookImportedEvent(new Author { Name = "Author" }, new Book { Title = "Book" },
                new List<BookFile> { new() { Path = Path.GetFullPath("book.epub") } }, new(), true, null));

            Assert.That(failures, Is.EqualTo(1));
            Assert.That(successes, Is.Zero);
        }

        private static Kavita Create(IHttpClient http)
        {
            var logger = LogManager.GetCurrentClassLogger();
            return new Kavita(new KavitaService(new KavitaServiceProxy(http, logger), logger), new CacheManager(), new Chaptarr.Core.Test.Common.MemoryPendingNotifications())
            {
                Definition = new NotificationDefinition { Settings = new KavitaSettings { Host = "fixture.invalid", Port = 5000, ApiKey = "fixture", Notify = true } }
            };
        }

        private static void Send(Kavita notification, string action, BookFile file)
        {
            var files = file == null ? new List<BookFile>() : new List<BookFile> { file };
            switch (action)
            {
                case "import": notification.OnReleaseImport(new BookDownloadMessage { BookFiles = files }); break;
                case "delete": notification.OnBookDelete(new BookDeleteMessage(new Book { BookFiles = files }, true)); break;
                case "file-delete": notification.OnBookFileDelete(new BookFileDeleteMessage { BookFile = file }); break;
                case "retag": notification.OnBookRetag(new BookRetagMessage { BookFile = file }); break;
                default: throw new ArgumentOutOfRangeException(nameof(action));
            }
        }
    }
}

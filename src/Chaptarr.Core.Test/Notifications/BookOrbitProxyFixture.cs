using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Indexers;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Notifications.BookOrbit;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class BookOrbitProxyFixture
    {
        private static BookOrbitSettings Settings => new()
        {
            BaseUrl = "https://bookorbit.example/library",
            Username = "migration-fixture",
            Password = "test-password"
        };

        [Test]
        public void should_authenticate_without_following_redirects()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(url => url.EndsWith("/library/api/v1/auth/login"), request =>
            {
                Assert.That(request.AllowAutoRedirect, Is.False);
                Assert.That(request.LogHttpError, Is.False);
                var body = Encoding.UTF8.GetString(request.ContentData);
                Assert.That(body, Does.Contain("\"username\":\"migration-fixture\""));
                Assert.That(body, Does.Contain("\"password\":\"test-password\""));
                return Task.FromResult(new HttpResponse(request, new HttpHeader(), "{\"accessToken\":\"test-token\"}"));
            });
            transport.AddRoute(url => url.EndsWith("/book-dock/summary"), request =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(request.AllowAutoRedirect, Is.False);
                    Assert.That(request.LogHttpError, Is.False);
                    Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
                    Assert.That(request.Headers.GetSingleValue("Authorization"), Is.EqualTo("Bearer test-token"));
                });
                return Task.FromResult(new HttpResponse(request, new HttpHeader(), "{}", HttpStatusCode.OK));
            });

            Assert.That(Create(transport).Test(Settings), Is.Null);
            Assert.That(transport.RequestedUrls.Count, Is.EqualTo(2));
        }

        [Test]
        public void should_stream_a_duplicate_file_once_with_a_bearer_token_and_preserve_the_source()
        {
            var path = Path.Combine(Path.GetTempPath(), $"bookorbit-{Guid.NewGuid():N}.epub");
            File.WriteAllText(path, "test-book-content");
            try
            {
                var transport = new DirectDownloadTestHttp();
                transport.AddRoute(url => url.EndsWith("/auth/login"), request =>
                    Task.FromResult(new HttpResponse(request, new HttpHeader(), "{\"accessToken\":\"test-token\"}")));
                transport.AddRoute(url => url.EndsWith("/book-dock/summary"), request =>
                    Task.FromResult(new HttpResponse(request, new HttpHeader(), "{}", HttpStatusCode.OK)));
                transport.AddRoute(url => url.EndsWith("/book-dock/upload"), async request =>
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(request.AllowAutoRedirect, Is.False);
                        Assert.That(request.LogHttpError, Is.False);
                        Assert.That(request.Headers.GetSingleValue("Authorization"), Is.EqualTo("Bearer test-token"));
                        Assert.That(request.ContentData, Is.Null);
                        Assert.That(request.Content.Headers.ContentType.MediaType, Is.EqualTo("multipart/form-data"));
                    });
                    var multipart = await request.Content.ReadAsStringAsync();
                    Assert.That(multipart, Does.Contain("test-book-content"));
                    Assert.That(multipart, Does.Contain(Path.GetFileName(path)));
                    return new HttpResponse(request, new HttpHeader(), "{}", HttpStatusCode.Created);
                });

                Create(transport).UploadFiles(Settings, new[] { path, path });

                Assert.That(transport.RequestedUrls.Count, Is.EqualTo(2));
                Assert.That(File.ReadAllText(path), Is.EqualTo("test-book-content"));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void should_skip_empty_import_without_authenticating()
        {
            var transport = new DirectDownloadTestHttp();
            Create(transport).UploadFiles(Settings, Array.Empty<string>());
            Assert.That(transport.RequestedUrls, Is.Empty);
        }

        [Test]
        public void should_stop_a_batch_when_bookorbit_rejects_a_file_and_allow_a_later_retry()
        {
            var firstPath = Path.Combine(Path.GetTempPath(), $"bookorbit-{Guid.NewGuid():N}.epub");
            var secondPath = Path.Combine(Path.GetTempPath(), $"bookorbit-{Guid.NewGuid():N}.m4b");
            File.WriteAllText(firstPath, "first-book-content");
            File.WriteAllText(secondPath, "second-book-content");
            try
            {
                var transport = new DirectDownloadTestHttp();
                var uploadAttempts = 0;
                transport.AddRoute(url => url.EndsWith("/auth/login"), request =>
                    Task.FromResult(new HttpResponse(request, new HttpHeader(), "{\"accessToken\":\"test-token\"}")));
                transport.AddRoute(url => url.EndsWith("/book-dock/upload"), request =>
                {
                    uploadAttempts++;
                    if (uploadAttempts == 2)
                    {
                        throw new HttpException(request, new HttpResponse(request, new HttpHeader(),
                            "remote diagnostics must not be copied into the notification failure", HttpStatusCode.RequestEntityTooLarge));
                    }

                    return Task.FromResult(new HttpResponse(request, new HttpHeader(), "{}", HttpStatusCode.Created));
                });

                var proxy = Create(transport);
                var failure = Assert.Throws<WebException>(() => proxy.UploadFiles(Settings, new[] { firstPath, secondPath }));

                Assert.Multiple(() =>
                {
                    Assert.That(uploadAttempts, Is.EqualTo(2), "a rejected file stops the current batch");
                    Assert.That(failure.Message, Does.Contain(Path.GetFileName(secondPath)));
                    Assert.That(failure.Message, Does.Contain("HTTP 413"));
                    Assert.That(failure.Message, Does.Not.Contain("remote diagnostics"));
                    Assert.That(failure.Message, Does.Not.Contain(Settings.Password));
                    Assert.That(failure.ToString(), Does.Not.Contain("remote diagnostics"));
                    Assert.That(failure.ToString(), Does.Not.Contain(Settings.Password));
                });

                proxy.UploadFiles(Settings, new[] { secondPath });
                Assert.That(uploadAttempts, Is.EqualTo(3), "a later call can upload the file that previously failed");
            }
            finally
            {
                File.Delete(firstPath);
                File.Delete(secondPath);
            }
        }

        [Test]
        public void should_report_rejected_credentials_without_following_redirects()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(_ => true, request =>
                throw new HttpException(request, new HttpResponse(request, new HttpHeader(), "unauthorized", HttpStatusCode.Unauthorized)));

            var failure = Create(transport).Test(Settings);

            Assert.Multiple(() =>
            {
                Assert.That(failure.PropertyName, Is.EqualTo(nameof(BookOrbitSettings.Password)));
                Assert.That(transport.RequestedUrls.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void should_report_when_the_account_lacks_bookdock_access()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(url => url.EndsWith("/auth/login"), request =>
                Task.FromResult(new HttpResponse(request, new HttpHeader(), "{\"accessToken\":\"test-token\"}")));
            transport.AddRoute(url => url.EndsWith("/book-dock/summary"), request =>
                throw new HttpException(request, new HttpResponse(request, new HttpHeader(), "forbidden", HttpStatusCode.Forbidden)));

            var failure = Create(transport).Test(Settings);

            Assert.Multiple(() =>
            {
                Assert.That(failure.PropertyName, Is.Empty);
                Assert.That(failure.ErrorMessage, Does.Contain("Book Dock Access"));
                Assert.That(transport.RequestedUrls.Count, Is.EqualTo(2));
            });
        }

        [Test]
        public void should_refuse_a_login_redirect_without_following_it()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(_ => true, request => Task.FromResult(new HttpResponse(request, new HttpHeader(), string.Empty, HttpStatusCode.Found)));

            var failure = Create(transport).Test(Settings);

            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.Not.Null);
                Assert.That(transport.RequestedUrls.Count, Is.EqualTo(1));
            });
        }

        private static BookOrbitProxy Create(DirectDownloadTestHttp transport)
        {
            return new BookOrbitProxy(transport.CreateClient(), LogManager.GetCurrentClassLogger());
        }
    }
}

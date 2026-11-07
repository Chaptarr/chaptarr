using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Indexers;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Notifications.BookLore;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class BookLoreProxyFixture
    {
        private static BookLoreSettings Settings => new()
        {
            BaseUrl = "https://booklore.example/library",
            Username = "reader",
            Password = "test-password"
        };

        [Test]
        public void should_authenticate_without_following_redirects()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(url => url.EndsWith("/library/api/v1/auth/login"), request =>
            {
                Assert.That(request.AllowAutoRedirect, Is.False);
                return Task.FromResult(new HttpResponse(request, new HttpHeader(), "{\"accessToken\":\"test-token\"}"));
            });

            Assert.That(Create(transport).Test(Settings), Is.Null);
            Assert.That(transport.RequestedUrls.Count, Is.EqualTo(1));
        }

        [Test]
        public void should_stream_file_with_bearer_token_and_preserve_source()
        {
            var path = Path.Combine(Path.GetTempPath(), $"booklore-{Guid.NewGuid():N}.epub");
            File.WriteAllText(path, "test-book-content");
            try
            {
                var transport = new DirectDownloadTestHttp();
                transport.AddRoute(url => url.EndsWith("/auth/login"), request =>
                    Task.FromResult(new HttpResponse(request, new HttpHeader(), "{\"accessToken\":\"test-token\"}")));
                transport.AddRoute(url => url.EndsWith("/files/upload/bookdrop"), async request =>
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(request.AllowAutoRedirect, Is.False);
                        Assert.That(request.Headers.GetSingleValue("Authorization"), Is.EqualTo("Bearer test-token"));
                        Assert.That(request.ContentData, Is.Null);
                        Assert.That(request.Content.Headers.ContentType.MediaType, Is.EqualTo("multipart/form-data"));
                    });
                    var multipart = await request.Content.ReadAsStringAsync();
                    Assert.That(multipart, Does.Contain("test-book-content"));
                    Assert.That(multipart, Does.Contain(Path.GetFileName(path)));
                    return new HttpResponse(request, new HttpHeader(), string.Empty, HttpStatusCode.OK);
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
        public void should_retry_booklore_login_after_token_conflict()
        {
            var path = Path.Combine(Path.GetTempPath(), $"booklore-{Guid.NewGuid():N}.epub");
            File.WriteAllText(path, "test-book-content");
            try
            {
                var transport = new DirectDownloadTestHttp();
                var loginAttempts = 0;
                transport.AddRoute(url => url.EndsWith("/auth/login"), request =>
                {
                    loginAttempts++;
                    if (loginAttempts == 1)
                    {
                        throw new HttpException(request, new HttpResponse(request, new HttpHeader(),
                            "{\"status\":400,\"message\":\"A data conflict occurred. The operation could not be completed.\"}",
                            HttpStatusCode.BadRequest));
                    }

                    return Task.FromResult(new HttpResponse(request, new HttpHeader(), "{\"accessToken\":\"test-token\"}"));
                });
                transport.AddRoute(url => url.EndsWith("/files/upload/bookdrop"), request =>
                    Task.FromResult(new HttpResponse(request, new HttpHeader(), string.Empty, HttpStatusCode.OK)));

                Create(transport).UploadFiles(Settings, new[] { path });

                Assert.That(loginAttempts, Is.EqualTo(2));
                Assert.That(transport.RequestedUrls.Count, Is.EqualTo(3));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void should_report_booklore_bad_credentials_without_retrying()
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(_ => true, request =>
                throw new HttpException(request, new HttpResponse(request, new HttpHeader(),
                    "{\"status\":400,\"message\":\"Invalid credentials\"}", HttpStatusCode.BadRequest)));

            var failure = Create(transport).Test(Settings);

            Assert.Multiple(() =>
            {
                Assert.That(failure.PropertyName, Is.EqualTo(nameof(BookLoreSettings.Password)));
                Assert.That(transport.RequestedUrls.Count, Is.EqualTo(1));
            });
        }

        [TestCase("{}")]
        [TestCase("{\"accessToken\":\"\"}")]
        public void should_reject_login_without_access_token(string payload)
        {
            var transport = new DirectDownloadTestHttp();
            transport.AddRoute(_ => true, request => Task.FromResult(new HttpResponse(request, new HttpHeader(), payload)));
            Assert.That(Create(transport).Test(Settings), Is.Not.Null);
        }

        private static BookLoreProxy Create(DirectDownloadTestHttp transport) =>
            new(transport.CreateClient(), LogManager.GetCurrentClassLogger());
    }
}

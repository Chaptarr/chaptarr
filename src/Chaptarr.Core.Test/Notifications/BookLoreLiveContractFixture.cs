using System;
using System.Collections.Specialized;
using System.IO;
using System.Net.Http;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Notifications.BookLore;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class BookLoreLiveContractFixture
    {
        private class LiveHttpClient : DispatchProxy
        {
            private static readonly System.Net.Http.HttpClient Client = new(new HttpClientHandler
            {
                AllowAutoRedirect = false
            });

            public System.Collections.Generic.List<HttpResponse> Responses { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name != nameof(IHttpClient.Execute))
                {
                    throw new NotImplementedException(targetMethod?.Name);
                }

                var request = (HttpRequest)args[0];
                using var message = new HttpRequestMessage(request.Method, request.Url.ToString());
                message.Content = request.Content ?? (request.ContentData == null ? null : new ByteArrayContent(request.ContentData));

                foreach (var header in request.Headers)
                {
                    if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    {
                        message.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                using var timeout = request.RequestTimeout > TimeSpan.Zero
                    ? new System.Threading.CancellationTokenSource(request.RequestTimeout)
                    : new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var response = Client.Send(message, HttpCompletionOption.ResponseContentRead, timeout.Token);
                var responseHeaders = new NameValueCollection();

                foreach (var header in response.Headers)
                {
                    foreach (var value in header.Value)
                    {
                        responseHeaders.Add(header.Key, value);
                    }
                }

                foreach (var header in response.Content.Headers)
                {
                    foreach (var value in header.Value)
                    {
                        responseHeaders.Add(header.Key, value);
                    }
                }

                var result = new HttpResponse(
                    request,
                    new HttpHeader(responseHeaders),
                    response.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
                    response.StatusCode,
                    response.Version);
                Responses.Add(result);

                if (!request.SuppressHttpError && result.HasHttpError)
                {
                    throw new HttpException(request, result);
                }

                return result;
            }
        }

        [Test]
        [Explicit("Requires BOOKLORE_LIVE_URL, BOOKLORE_LIVE_USERNAME, and BOOKLORE_LIVE_PASSWORD for a real BookLore instance")]
        public void should_authenticate_and_upload_to_live_booklore()
        {
            var settings = new BookLoreSettings
            {
                BaseUrl = Environment.GetEnvironmentVariable("BOOKLORE_LIVE_URL"),
                Username = Environment.GetEnvironmentVariable("BOOKLORE_LIVE_USERNAME"),
                Password = Environment.GetEnvironmentVariable("BOOKLORE_LIVE_PASSWORD")
            };
            Assert.That(settings.BaseUrl, Is.Not.Empty);
            Assert.That(settings.Username, Is.Not.Empty);
            Assert.That(settings.Password, Is.Not.Empty);

            var path = Path.Combine(Path.GetTempPath(), $"booklore-live-{Guid.NewGuid():N}.epub");
            File.WriteAllText(path, "live-booklore-contract-test");
            try
            {
                var httpClient = DispatchProxy.Create<IHttpClient, LiveHttpClient>();
                var transport = (LiveHttpClient)(object)httpClient;
                var proxy = new BookLoreProxy(httpClient, LogManager.GetCurrentClassLogger());

                Assert.That(proxy.Test(settings), Is.Null);
                proxy.UploadFiles(settings, new[] { path });

                Assert.Multiple(() =>
                {
                    Assert.That(transport.Responses.Count, Is.InRange(3, 4));
                    Assert.That(transport.Responses[0].Request.Url.ToString(), Does.EndWith("/api/v1/auth/login"));
                    Assert.That(transport.Responses[1].Request.Url.ToString(), Does.EndWith("/api/v1/auth/login"));
                    if (transport.Responses.Count == 4)
                    {
                        Assert.That(transport.Responses[1].StatusCode, Is.EqualTo(System.Net.HttpStatusCode.BadRequest));
                        Assert.That(transport.Responses[2].Request.Url.ToString(), Does.EndWith("/api/v1/auth/login"));
                    }

                    Assert.That(transport.Responses[^1].Request.Url.ToString(), Does.EndWith("/api/v1/files/upload/bookdrop"));
                    Assert.That(transport.Responses[^1].StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
                    Assert.That(transport.Responses[^1].Content, Is.Empty);
                    Assert.That(File.ReadAllText(path), Is.EqualTo("live-booklore-contract-test"));
                });
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}

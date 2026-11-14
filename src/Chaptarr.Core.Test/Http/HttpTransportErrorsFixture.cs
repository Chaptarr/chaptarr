using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Http;
using NzbDrone.Core.MetadataSource;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    [NonParallelizable]
    public class HttpTransportErrorsFixture
    {
        public static async Task<Exception> CaptureTransportFailure(string failure)
        {
            var cache = new CacheManager();
            var logger = LogManager.GetCurrentClassLogger();
            var factory = new ManagedSocketsHttpHandlerFactory(new ManagedWebProxyFactory(cache),
                Proxy<ICertificateValidationService>((_, _) => false), logger);
            var dispatcher = new ManagedHttpDispatcher(
                Proxy<IHttpProxySettingsProvider>((_, _) => HttpProxySettings.DirectConnection), factory,
                Proxy<IUserAgentBuilder>((_, _) => "ChaptarrTest/1.0"), cache, logger);
            using var reservedPort = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            reservedPort.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                var url = $"http://127.0.0.1:{((IPEndPoint)reservedPort.LocalEndPoint).Port}/";
                if (failure == "dns") url = "http://chaptarr-transport-test.invalid/";
                if (failure is "timeout" or "tls-drop" or "tls-reset")
                {
                    listener.Start();
                    url = $"{(failure is "tls-drop" or "tls-reset" ? "https" : "http")}://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
                }

                var pending = dispatcher.GetResponseAsync(new NzbDrone.Common.Http.HttpRequest(url)
                {
                    RequestTimeout = TimeSpan.FromSeconds(failure == "timeout" ? 1 : 10)
                }, new CookieContainer());
                if (failure is "timeout" or "tls-drop" or "tls-reset")
                {
                    using var acceptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var accepted = await listener.AcceptTcpClientAsync(acceptTimeout.Token);
                    if (failure is "tls-drop" or "tls-reset")
                    {
                        var buffer = new byte[1024];
                        Assert.That(await accepted.GetStream().ReadAsync(buffer, acceptTimeout.Token), Is.GreaterThan(0));
                        if (failure == "tls-reset") accepted.Client.LingerState = new LingerOption(true, 0);
                        accepted.Close();
                    }
                    return await VerifyFailure(pending, failure);
                }

                return await VerifyFailure(pending, failure);
            }
            finally
            {
                listener.Stop();
                foreach (var client in cache.GetCache<System.Net.Http.HttpClient>(typeof(ManagedHttpDispatcher), "httpclient").Values)
                {
                    client.Dispose();
                }
            }
        }

        private static async Task<Exception> VerifyFailure(Task<NzbDrone.Common.Http.HttpResponse> request, string failure)
        {
            Exception error = null;
            try { await request; }
            catch (Exception ex) { error = ex; }
            Assert.That(error, Is.Not.Null, "The actual dispatcher request must fail");
            TestContext.Progress.WriteLine($"{failure}: {error.GetType().FullName}; inner: {error.InnerException?.GetType().FullName}");
            if (failure == "timeout")
            {
                Assert.That(error, Is.TypeOf<WebException>());
                Assert.That(((WebException)error).Status, Is.EqualTo(WebExceptionStatus.Timeout));
            }
            else if (failure is "tls-drop" or "tls-reset")
            {
                Assert.That(error, Is.TypeOf<HttpRequestException>());
                Assert.That(((HttpRequestException)error).HttpRequestError, Is.EqualTo(HttpRequestError.SecureConnectionError));
                Assert.That(error.InnerException, Is.InstanceOf<IOException>());
            }
            else
            {
                if (failure == "dns" && error is WebException { Status: WebExceptionStatus.Timeout })
                {
                    Assert.Inconclusive("The environment DNS resolver timed out; this is not evidence of a DNS rejection. The separate timeout test still runs.");
                }
                var socket = error as SocketException ?? error.InnerException as SocketException;
                Assert.That(socket, Is.Not.Null, "Connection/DNS failures must carry the real socket error, not pass as a timeout");
                Assert.That(socket.SocketErrorCode, failure == "closed-port"
                    ? Is.EqualTo(SocketError.ConnectionRefused)
                    : Is.AnyOf(SocketError.HostNotFound, SocketError.NoData, SocketError.TryAgain));
            }

            return error;
        }

        [TestCase("tls-drop")]
        [TestCase("tls-reset")]
        [TestCase("closed-port")]
        [TestCase("dns")]
        [TestCase("timeout")]
        public async Task real_dispatcher_failures_are_recognized_by_the_shared_rule_and_metadata_gate(string failure)
        {
            var error = await CaptureTransportFailure(failure);
            Assert.That(HttpTransportErrors.IsTransientTransportException(error), Is.True);
            Assert.That(HttpTransportErrors.IsConnectionNotEstablished(error), Is.EqualTo(failure != "timeout"));
            if (error.InnerException is SocketException socket)
            {
                Assert.That(HttpTransportErrors.IsTransientTransportException(socket), Is.False, "Preserve the metadata gate contract; the dispatcher wraps socket failures");
            }
            var health = new HttpServiceHealth(LogManager.GetCurrentClassLogger());
            var gate = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), health, LogManager.GetCurrentClassLogger());
            gate.ReportException(error);
            Assert.That(health.GetStatus(gate.SourceName).IsHealthy, Is.False);
            var reason = health.GetStatus(gate.SourceName).LastErrorMessage;
            Assert.That(reason, Does.Contain(error.Message));
            Assert.That(reason, Does.Not.Contain("Operation canceled").And.Not.Contain("A task was canceled"));
            if (failure == "timeout") Assert.That(reason, Is.EqualTo("Http request timed out"));
            if (failure is "tls-drop" or "tls-reset") Assert.That(reason, Does.Contain(error.InnerException.Message));
            var calibre = new NzbDrone.Core.Books.Calibre.CalibreLibraryGate(health);
            calibre.ReportException("calibre-test", error);
            Assert.That(health.GetStatus("calibre-test").LastErrorMessage, Is.EqualTo(reason));
        }

        [TestCase(HttpRequestError.NameResolutionError, true)]
        [TestCase(HttpRequestError.ConnectionError, true)]
        [TestCase(HttpRequestError.SecureConnectionError, true)]
        [TestCase(HttpRequestError.ProxyTunnelError, true)]
        [TestCase(HttpRequestError.ResponseEnded, false)]
        [TestCase(HttpRequestError.InvalidResponse, false)]
        [TestCase(HttpRequestError.Unknown, false)]
        public void built_in_transport_classification_does_not_guess_from_inner_exception(HttpRequestError error, bool unsent)
        {
            Assert.That(HttpTransportErrors.IsConnectionNotEstablished(new HttpRequestException(error, "fixture", new IOException())), Is.EqualTo(unsent));
        }

        [Test]
        public void bare_task_cancellation_is_not_a_transport_timeout()
        {
            Assert.That(HttpTransportErrors.IsTransientTransportException(new TaskCanceledException()), Is.False);
            Assert.That(HttpTransportErrors.IsTransientTransportException(new WebException("deadline", new TaskCanceledException(), WebExceptionStatus.Timeout, null)), Is.True);
        }

        [TestCase(401)]
        [TestCase(500)]
        public void transport_predicate_is_status_blind_and_does_not_decide_service_policy(int status)
        {
            var request = new NzbDrone.Common.Http.HttpRequest("http://localhost/");
            var response = new NzbDrone.Common.Http.HttpResponse(request, new HttpHeader(), "error", (HttpStatusCode)status);
            Assert.That(HttpTransportErrors.IsTransientTransportException(new NzbDrone.Common.Http.HttpException(response)), Is.False);
            Assert.That(HttpTransportErrors.IsTransientTransportException(new HttpRequestException("response", null, (HttpStatusCode)status)), Is.True, "Callers classify HTTP responses before the original transport fallback");
            Assert.That(HttpTransportErrors.IsTransientTransportException(new WebException("response", WebExceptionStatus.ProtocolError)), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t ")]
        public void join_messages_without_a_previous_message_keeps_the_next(string previous)
        {
            Assert.That(HttpTransportErrors.JoinMessages(previous, "Details"), Is.EqualTo("Details"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t ")]
        public void join_messages_without_a_next_message_keeps_the_previous(string next)
        {
            Assert.That(HttpTransportErrors.JoinMessages("Details", next), Is.EqualTo("Details"));
        }

        [Test]
        public void failure_reasons_preserve_meaning_without_running_messages_together()
        {
            Assert.That(HttpTransportErrors.GetFailureReason(new OperationCanceledException("cancelled")), Is.EqualTo("cancelled"));
            Assert.That(HttpTransportErrors.GetFailureReason(new TaskCanceledException("cancelled", new TimeoutException("timed out"))), Is.EqualTo("cancelled: timed out"));
            Assert.That(HttpTransportErrors.GetFailureReason(new Exception(string.Empty)), Is.EqualTo("Unknown failure"));
            Assert.That(HttpTransportErrors.GetFailureReason(new IOException("Failed to read complete http response", new IOException("Error while copying content"))), Is.EqualTo("Failed to read complete http response: Error while copying content"));
            Assert.That(HttpTransportErrors.GetFailureReason(new Exception("Failed.", new Exception("Details"))), Is.EqualTo("Failed. Details"));
            Assert.That(HttpTransportErrors.GetFailureReason(new Exception("Failed (host)", new Exception("Details"))), Is.EqualTo("Failed (host): Details"));
            var aggregate = HttpTransportErrors.GetFailureReason(new AggregateException("Combined failure", new IOException("Read failed")));
            Assert.That(aggregate, Does.Contain("Combined failure").And.Contain("Read failed"));
        }

        [Test]
        public void local_file_and_metadata_errors_are_not_transport_failures()
        {
            Assert.That(HttpTransportErrors.IsTransientTransportException(new System.IO.IOException("disk")), Is.False);
            Assert.That(HttpTransportErrors.IsTransientTransportException(new FormatException("metadata")), Is.False);
        }
    }
}

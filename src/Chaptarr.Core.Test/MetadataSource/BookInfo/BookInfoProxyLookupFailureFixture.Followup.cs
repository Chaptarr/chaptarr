using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Chaptarr.Http.ErrorManagement;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Exceptions;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    public partial class BookInfoProxyLookupFailureFixture
    {
        [TestCase(LookupKind.Work, " Unsupported {identifier} \n")]
        [TestCase(LookupKind.Edition, " Unsupported {identifier} \n")]
        [TestCase(LookupKind.Legacy, " Unsupported {identifier} \n")]
        [TestCase(LookupKind.Work, " ")]
        [TestCase(LookupKind.Edition, " ")]
        [TestCase(LookupKind.Legacy, " ")]
        public void direct_v5_input_refusal_preserves_identifier_and_literal_reason(LookupKind kind, string body)
        {
            var client = new RecordingHttpClient(request => Response(request, HttpStatusCode.BadRequest, TypedLookupPayload("invalid_provider_id", kind == LookupKind.Edition ? "gr:44767458" : "gr:3634639", body)));
            var exception = Assert.Throws<InvalidProviderIdException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind));
            Assert.That(exception, Is.InstanceOf<NzbDroneClientException>());
            Assert.That((exception as object as NzbDroneClientException)?.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(exception.ProviderId, Is.EqualTo(kind == LookupKind.Edition ? "gr:44767458" : "gr:3634639"));
            Assert.That(exception.Message, Is.EqualTo(string.IsNullOrWhiteSpace(body)
                ? "invalid_provider_id"
                : body.Trim()));
            AssertRequests(client, kind);
        }

        [TestCase(LookupKind.Work)]
        [TestCase(LookupKind.Edition)]
        [TestCase(LookupKind.Legacy)]
        public void untyped_lookup_400_is_unavailable_without_automatic_redirects(LookupKind kind)
        {
            var client = new RecordingHttpClient(request =>
            {
                Assert.That(request.AllowAutoRedirect, Is.False);
                return Response(request, HttpStatusCode.BadRequest, "Generic downstream failure");
            });
            var exception = Assert.Throws<NzbDrone.Core.MetadataSource.BookInfo.BookInfoException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind));
            Assert.That(exception.InnerException, Is.TypeOf<HttpException>());
            Assert.That(exception.Message, Does.Not.Contain("Generic downstream failure"));
            AssertRequests(client, kind);
        }

        [TestCase(LookupKind.Work)]
        [TestCase(LookupKind.Edition)]
        [TestCase(LookupKind.Legacy)]
        public void html_error_on_the_lookup_url_is_not_a_declared_refusal(LookupKind kind)
        {
            var client = new RecordingHttpClient(request => new NzbDrone.Common.Http.HttpResponse(request,
                new HttpHeader { ContentType = "text/html" }, "<h1>proxy body</h1>" + new string('x', 4096), HttpStatusCode.BadRequest));
            var exception = Assert.Throws<NzbDrone.Core.MetadataSource.BookInfo.BookInfoException>(() => Lookup(BookInfoProxyCanonicalLookupFixture.CreateProxy(client), kind));
            Assert.That(exception.InnerException, Is.TypeOf<HttpException>());
            Assert.That(exception.Message, Does.Not.Contain("proxy body"));
            Assert.That(exception.Message.Length, Is.LessThan(512));
            AssertRequests(client, kind);
        }

        [TestCase("hc-ed:123")]
        [TestCase("unsupported:123")]
        [TestCase("unprefixed")]
        [TestCase(" ")]
        [TestCase(null)]
        public void already_invalid_local_work_identifier_uses_typed_refusal_without_http(string identifier)
        {
            var client = new RecordingHttpClient(_ => throw new AssertionException("Invalid local identifiers must not be fetched."));
            var exception = Assert.Throws<InvalidProviderIdException>(() => BookInfoProxyCanonicalLookupFixture.CreateProxy(client).GetWorkInfo(identifier));
            Assert.That(exception.ProviderId, Is.EqualTo(identifier));
            Assert.That(client.Requests, Is.Empty);
        }

        [Test]
        public async Task actual_native_error_pipeline_returns_400_and_literal_direct_refusal_reason()
        {
            var client = new RecordingHttpClient(request => Response(request, HttpStatusCode.BadRequest, TypedLookupPayload("invalid_provider_id", "gb:unsupported", "Unsupported {identifier}\n")));
            var exception = Assert.Throws<InvalidProviderIdException>(() => BookInfoProxyCanonicalLookupFixture.CreateProxy(client).GetWorkInfo("gb:unsupported"));
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/api/v1/book";
            context.Response.Body = new MemoryStream();
            context.Features.Set<IExceptionHandlerPathFeature>(new ExceptionHandlerFeature { Error = exception });
            await new ChaptarrErrorPipeline(LogManager.GetCurrentClassLogger()).HandleException(context);
            context.Response.Body.Position = 0;
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
            Assert.That(await new StreamReader(context.Response.Body).ReadToEndAsync(), Does.Contain("Unsupported {identifier}"));
            Assert.That(client.Requests, Has.Count.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void bulk_refresh_direct_input_refusal_still_skips_existing_invalid_provider_catch(bool postgres)
        {
            AssertBulkRefreshRetainsFailedCopies(postgres, HttpStatusCode.BadRequest, "Unsupported identifier");
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void bulk_refresh_declared_terminal_http_response_retains_failed_copies_and_persists_healthy_media(bool postgres, bool authorTerminal)
        {
            AssertBulkRefreshRetainsFailedCopies(postgres, HttpStatusCode.Conflict, "Declared work rescue stop", authorTerminal);
        }
    }
}

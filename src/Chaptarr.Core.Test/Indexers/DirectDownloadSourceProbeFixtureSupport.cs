using System;
using System.Net;
using System.Threading.Tasks;
using NzbDrone.Common.Http;

namespace Chaptarr.Core.Test.Indexers
{
    internal static class DirectDownloadSourceProbeFixtureSupport
    {
        public static void RegisterCatalogSource(DirectDownloadTestHttp transport, string baseUrl, string isbn, string title, string resultIsbn, string downloadUrl, string fastDownloadJson = null, bool includeFastDownloadRoute = true)
        {
            transport.AddRoute(
                url => url.StartsWith($"{baseUrl}/search", StringComparison.Ordinal),
                request => Task.FromResult(BuildResponse(
                    request,
                    HttpStatusCode.OK,
                    $"<div><a class=\"js-vim-focus\" href=\"/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\">{title}</a><div class=\"font-semibold\">EPUB · 1.2 MB · 1965</div><div class=\"font-mono\">Dune.epub</div></div>",
                    "text/html; charset=utf-8")));
            transport.AddRoute(
                url => url == $"{baseUrl}/md5/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                request => Task.FromResult(BuildResponse(
                    request,
                    HttpStatusCode.OK,
                    $"<a class=\"js-md5-codes-tabs-tab\"><span>ISBN</span><span>{resultIsbn}</span></a>",
                    "text/html; charset=utf-8")));
            if (includeFastDownloadRoute)
            {
                transport.AddRoute(
                    url => url.StartsWith($"{baseUrl}/dyn/api/fast_download.json", StringComparison.Ordinal) && url.Contains("md5=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", StringComparison.Ordinal) && url.Contains("key=real-secret", StringComparison.Ordinal),
                    request => Task.FromResult(BuildResponse(
                        request,
                        HttpStatusCode.OK,
                        fastDownloadJson ?? $"{{\"download_url\":\"{downloadUrl}\"}}",
                        "application/json")));
            }
        }

        public static void RegisterMirrorSource(DirectDownloadTestHttp transport, string baseUrl, string title, string isbn, string downloadUrl, bool emptyIsbnSearch = false, string mirrorResponseHtml = "<a href=\"/get/dune.epub\">GET</a>")
        {
            transport.AddRoute(
                url => url.StartsWith($"{baseUrl}/index.php", StringComparison.Ordinal) && url.Contains("req=9780441172719", StringComparison.Ordinal),
                request => Task.FromResult(BuildResponse(
                    request,
                    HttpStatusCode.OK,
                    emptyIsbnSearch
                        ? "<table></table><table><tr><th>Title</th></tr></table>"
                        : BuildMirrorSearchHtml(title),
                    "text/html; charset=utf-8")));
            transport.AddRoute(
                url => url.StartsWith($"{baseUrl}/index.php", StringComparison.Ordinal) && url.Contains("req=Dune", StringComparison.Ordinal),
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK, BuildMirrorSearchHtml(title), "text/html; charset=utf-8")));
            transport.AddRoute(
                url => url == $"{baseUrl}/mirror1",
                request => Task.FromResult(BuildResponse(request, HttpStatusCode.OK, mirrorResponseHtml, "text/html; charset=utf-8")));
        }

        public static HttpResponse BuildResponse(HttpRequest request, HttpStatusCode statusCode, string content, string contentType, string location = null)
        {
            var headers = new HttpHeader();
            headers.ContentType = contentType;
            if (!string.IsNullOrWhiteSpace(location))
            {
                headers["Location"] = location;
            }

            return new HttpResponse(request, headers, content, statusCode);
        }

        private static string BuildMirrorSearchHtml(string title)
        {
            return $"<table></table><table><tr><th>Title</th></tr><tr><td><a href=\"ignored\">x</a><a href=\"book/index.php?md5=1\">{title}</a></td><td>Frank Herbert</td><td>Chilton</td><td>1965</td><td>English</td><td>412</td><td>1 MB</td><td>EPUB</td><td><a href=\"/mirror1\">Mirror 1</a></td></tr></table>";
        }
    }
}

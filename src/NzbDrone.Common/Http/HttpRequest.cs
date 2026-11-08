using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Common.Http
{
    public class HttpRequest
    {
        private HashSet<string> _privateLogValues;
        public HttpRequest(string url, HttpAccept httpAccept = null)
        {
            Method = HttpMethod.Get;
            Url = new HttpUri(url);
            Headers = new HttpHeader();
            ConnectionKeepAlive = true;
            AllowAutoRedirect = true;
            StoreRequestCookie = true;
            LogHttpError = true;
            Cookies = new Dictionary<string, string>();

            if (!RuntimeInfo.IsProduction)
            {
                AllowAutoRedirect = false;
            }

            if (httpAccept != null)
            {
                Headers.Accept = httpAccept.Value;
            }
        }

        public HttpUri Url { get; set; }
        public HttpMethod Method { get; set; }
        public HttpHeader Headers { get; set; }
        public byte[] ContentData { get; set; }
        public string ContentSummary { get; set; }
        public ICredentials Credentials { get; set; }
        public bool SuppressHttpError { get; set; }
        public IEnumerable<HttpStatusCode> SuppressHttpErrorStatusCodes { get; set; }
        public bool UseSimplifiedUserAgent { get; set; }
        public bool AllowAutoRedirect { get; set; }
        public bool ConnectionKeepAlive { get; set; }
        public bool LogResponseContent { get; set; }
        public bool LogHttpError { get; set; }
        // Opt-in for destinations whose path/query is itself a credential (webhooks/ntfy).
        public bool RedactUrlInLogs { get; set; }
        public Dictionary<string, string> Cookies { get; private set; }
        public bool StoreRequestCookie { get; set; }
        public bool StoreResponseCookie { get; set; }
        public TimeSpan RequestTimeout { get; set; }
        public TimeSpan RateLimit { get; set; }
        public string RateLimitKey { get; set; }
        public Stream ResponseStream { get; set; }

        public override string ToString()
        {
            return ToString(includeMethod: true, includeSummary: true);
        }

        public string ToString(bool includeMethod = true, bool includeSummary = true)
        {
            var builder = new StringBuilder();

            if (includeMethod)
            {
                builder.AppendFormat("Req: [{0}] ", Method);
            }

            builder.Append(GetLogUrl());

            if (includeSummary && ContentSummary.IsNotNullOrWhiteSpace())
            {
                builder.Append(": ");
                builder.Append(ContentSummary);
            }

            return builder.ToString();
        }

        public string GetLogUrl(HttpUri url = null)
        {
            url ??= Url;
            if (!RedactUrlInLogs)
            {
                return url.ToString();
            }

            // Keep only this request's values, including redirects, to scrub server echoes.
            _privateLogValues ??= new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in new[] { (url.Path ?? string.Empty).TrimStart('/'), (url.Query ?? string.Empty).TrimStart('?'), url.Fragment }
                .Where(value => !string.IsNullOrEmpty(value)))
            {
                _privateLogValues.Add(value);
                _privateLogValues.Add(Uri.UnescapeDataString(value));
                _privateLogValues.Add(WebUtility.UrlDecode(value));
            }

            return new HttpUri(url.Scheme, url.Host, url.Port, "(removed)", null, null).ToString();
        }

        public string SanitizeLogMessage(string message)
        {
            if (!RedactUrlInLogs)
            {
                return message;
            }

            GetLogUrl();
            return CleanseLogMessage.Cleanse(message, _privateLogValues);
        }

        public void SetContent(byte[] data)
        {
            ContentData = data;
        }

        public void SetContent(string data)
        {
            var encoding = HttpHeader.GetEncodingFromContentType(Headers.ContentType);
            ContentData = encoding.GetBytes(data);
        }
    }
}

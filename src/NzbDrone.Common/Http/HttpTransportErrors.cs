using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace NzbDrone.Common.Http
{
    public static class HttpTransportErrors
    {
        public static HttpStatusCode? GetStatusCode(HttpResponse response = null, Exception exception = null) =>
            response?.StatusCode ?? (exception as HttpException)?.Response?.StatusCode ?? (exception as HttpRequestException)?.StatusCode;

        public static string GetStatusMessage(HttpResponse response = null, Exception exception = null, string prefix = "HTTP")
        {
            var status = GetStatusCode(response, exception);
            if (status == HttpStatusCode.ProxyAuthenticationRequired ||
                exception is HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError })
            {
                prefix = "Proxy returned HTTP";
            }
            if (!status.HasValue || (exception != null && (int)status.Value is >= 200 and < 300))
            {
                return null;
            }
            return Enum.IsDefined(status.Value) ? $"{prefix} {(int)status.Value} {status.Value}" : $"{prefix} {(int)status.Value}";
        }

        public static string GetFailureReason(Exception exception)
        {
            var messages = new List<string>();
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is OperationCanceledException && messages.Count > 0) break;
                var message = current.Message;
                if (!string.IsNullOrWhiteSpace(message) && !messages.Any(kept => kept.Contains(message, StringComparison.Ordinal)))
                {
                    messages.Add(message);
                }
            }
            if (messages.Count == 0) return "Unknown failure";
            return messages.Aggregate(JoinMessages);
        }

        public static string JoinMessages(string previous, string next)
        {
            if (string.IsNullOrWhiteSpace(previous)) return next;
            if (string.IsNullOrWhiteSpace(next)) return previous;
            var last = previous.TrimEnd()[^1];
            var separator = char.IsPunctuation(last) && char.GetUnicodeCategory(last) is not (UnicodeCategory.ClosePunctuation or UnicodeCategory.FinalQuotePunctuation) ? " " : ": ";
            return previous + separator + next;
        }

        public static bool IsUnavailableStatus(HttpStatusCode statusCode)
        {
            var status = (int)statusCode;
            return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ||
                (status >= 520 && status <= 526);
        }

        // Retry only requests which never connected, not a write with an unknown outcome.
        public static bool IsConnectionNotEstablished(Exception exception) =>
            exception is HttpRequestException
            {
                HttpRequestError: HttpRequestError.NameResolutionError or
                HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError
            };

        public static bool IsAccessDeniedStatus(HttpStatusCode status) =>
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        public static bool IsTransientTransportException(Exception exception)
        {
            if (exception is WebException webException)
            {
                return webException.Status is WebExceptionStatus.ReceiveFailure or WebExceptionStatus.Timeout;
            }

            return exception is HttpRequestException or TimeoutException;
        }
    }
}

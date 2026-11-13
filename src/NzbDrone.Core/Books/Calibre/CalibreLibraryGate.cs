using System;
using System.Net;
using NzbDrone.Common.Http;
using NzbDrone.Core.Http;

namespace NzbDrone.Core.Books.Calibre
{
    public interface ICalibreLibraryGate
    {
        string GetKey(CalibreSettings settings);
        /// <summary>Reads eligibility without reserving a recovery probe.</summary>
        bool CanBegin(string key, out TimeSpan retryAfter);
        /// <summary>Reserves eligibility immediately before real work.</summary>
        bool TryBegin(string key, out TimeSpan retryAfter);
        void ReportSuccess(string key);
        void ReportInconclusive(string key);
        HttpServiceResult ReportException(string key, Exception exception, string file = null, bool libraryRequest = false);
        string GetFailureContext(string key);
    }

    public class CalibreLibraryGate : ICalibreLibraryGate
    {
        private readonly IHttpServiceHealth _health;
        public CalibreLibraryGate(IHttpServiceHealth health) => _health = health;
        public string GetKey(CalibreSettings settings) => CalibreLibraryIdentity.GetKey(settings);
        public bool CanBegin(string key, out TimeSpan retryAfter) => _health.CanBeginRequest(key, out retryAfter, label: "Calibre " + key);
        public bool TryBegin(string key, out TimeSpan retryAfter) => _health.TryBeginRequest(key, out retryAfter, label: "Calibre " + key);
        public void ReportSuccess(string key) => _health.ReportSuccess(key, label: "Calibre " + key);
        public void ReportInconclusive(string key) => _health.ReportInconclusive(key);
        public HttpServiceResult ReportException(string key, Exception exception, string file = null, bool libraryRequest = false)
        {
            var cause = Unwrap(exception);
            var message = GetFailureReason(cause, libraryRequest);
            var status = HttpTransportErrors.GetStatusCode(exception: cause);
            HttpServiceResult? failure = null;
            if (IsAccessDenied(cause))
            {
                failure = HttpServiceResult.AccessDenied;
            }
            else if (status != HttpStatusCode.ProxyAuthenticationRequired && !HttpTransportErrors.IsConnectionNotEstablished(cause) && (HasKnownDiagnosis(cause) || (int?)status is >= 200 and < 400 ||
                (libraryRequest && status.HasValue && !HttpTransportErrors.IsUnavailableStatus(status.Value) &&
                    status is not (HttpStatusCode.InternalServerError or HttpStatusCode.TooManyRequests))))
            {
                if (!HasKnownDiagnosis(cause) && !IsLibraryNotFound(libraryRequest, status))
                {
                    message = "Calibre returned an unusable response: " + message;
                }
                failure = HttpServiceResult.InvalidResponse;
            }
            return _health.ReportResult(key, exception: cause, context: file, label: "Calibre " + key, failureMessage: message, responseFailure: failure);
        }
        public string GetFailureContext(string key) => _health.GetStatus(key).FailureContext;
        public static string GetFailureReason(Exception exception, bool libraryRequest = false)
        {
            var cause = Unwrap(exception);
            var status = HttpTransportErrors.GetStatusCode(exception: cause);
            if (!HttpTransportErrors.IsConnectionNotEstablished(cause))
            {
                if (cause is UnexpectedHtmlContentException)
                {
                    return $"Calibre returned a web page instead of API data (HTTP {(int?)status ?? 200})";
                }
                if (cause is CalibreInvalidResponseException)
                {
                    return cause.Message;
                }
                if (IsLibraryNotFound(libraryRequest, status))
                {
                    return "Calibre library not found (HTTP 404)";
                }
            }
            var message = HttpTransportErrors.GetStatusMessage(exception: cause) ?? HttpTransportErrors.GetFailureReason(cause);
            return IsAccessDenied(cause) ? "Calibre refused access: " + message : message;
        }

        internal static bool IsAccessDenied(Exception exception)
        {
            var cause = Unwrap(exception);
            var status = HttpTransportErrors.GetStatusCode(exception: cause);
            return !HttpTransportErrors.IsConnectionNotEstablished(cause) &&
                status.HasValue && HttpTransportErrors.IsAccessDeniedStatus(status.Value);
        }

        internal static bool HasKnownDiagnosis(Exception exception)
        {
            var cause = Unwrap(exception);
            return cause is CalibreInvalidResponseException or UnexpectedHtmlContentException || IsAccessDenied(cause);
        }

        private static bool IsLibraryNotFound(bool libraryRequest, HttpStatusCode? status) =>
            libraryRequest && status == HttpStatusCode.NotFound;

        public static Exception Unwrap(Exception exception)
        {
            while (exception is CalibreException and not CalibreInvalidResponseException && exception.InnerException != null)
            {
                exception = exception.InnerException;
            }
            return exception;
        }
    }
}

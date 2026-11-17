using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using NLog;
using System.Net;

namespace NzbDrone.Common.Http
{
    public sealed class HttpRequestTargetPolicy
    {
        internal static readonly HttpRequestOptionsKey<DnsResolution> DnsResolutionKey = new("ImageDnsResolution");

        // Per request, never pooled. HttpClient can cancel before the resolver
        // unwinds, so the dispatcher records the phase independently of exception wrapping.
        internal sealed class DnsResolution
        {
            private string _host;
            private long _started;
            public string Host => Volatile.Read(ref _host);

            public void Start(string host)
            {
                _started = Stopwatch.GetTimestamp();
                Volatile.Write(ref _host, host);
            }

            public void Complete() => Volatile.Write(ref _host, null);

            public WebException Failure(string host, Exception error, Logger logger)
            {
                logger.Debug("Couldn't resolve the image host '{0}' after {1} ms ({2})",
                    host, (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds, error.GetType().Name);
                return new WebException($"Couldn't resolve the image host '{host}'.", error, WebExceptionStatus.NameResolutionFailure, null);
            }
        }

        // Pool by the complete policy, so unrestricted or previously trusted
        // connections cannot bypass a changed destination restriction.
        public string Key { get; }
        public Action<Uri> ValidateUrl { get; }
        public Action<Uri, IPAddress> ValidateAddress { get; }

        public HttpRequestTargetPolicy(string key, Action<Uri> validateUrl, Action<Uri, IPAddress> validateAddress)
        {
            Key = key;
            ValidateUrl = validateUrl;
            ValidateAddress = validateAddress;
        }
    }
}

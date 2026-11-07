using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.EnvironmentInfo;

namespace Chaptarr.Diagnostics
{
    public interface IDiagnosticsReporter
    {
        void Start();
        void Record(string eventName);
        Task StopAsync();
    }

    public sealed class DiagnosticsReporter : IDiagnosticsReporter, IDisposable
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
        private static readonly HashSet<string> AllowedEvents = new(StringComparer.Ordinal)
        {
            "application.started",
            "book.grabbed",
            "book.imported",
            "download.failed"
        };
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly object _sync = new();
        private readonly SemaphoreSlim _flushLock = new(1, 1);
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
        private readonly Logger _logger;
        private readonly Uri _endpoint;
        private readonly string _token;
        private readonly HttpClient _httpClient;
        private Timer _timer;
        private DateTimeOffset _windowStart;
        private bool _started;
        private bool _stopped;

        public DiagnosticsReporter(Logger logger)
            : this(logger, null)
        {
        }

        public DiagnosticsReporter(Logger logger, HttpMessageHandler handler)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (!TryReadConfiguration(out _endpoint, out _token))
            {
                _logger.Warn("Optional diagnostics are inactive because the HTTPS OTLP endpoint or bearer token is missing or invalid.");
                return;
            }

            var transport = handler ?? new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            };

            _httpClient = new HttpClient(transport, disposeHandler: true)
            {
                Timeout = RequestTimeout
            };
        }

        public void Start()
        {
            if (_httpClient == null)
            {
                return;
            }

            lock (_sync)
            {
                if (_started || _stopped)
                {
                    return;
                }

                _started = true;
                _windowStart = DateTimeOffset.UtcNow;
                _timer = new Timer(OnTimer, null, ReportInterval, ReportInterval);
            }
        }

        public void Record(string eventName)
        {
            if (_httpClient == null || !AllowedEvents.Contains(eventName))
            {
                return;
            }

            lock (_sync)
            {
                if (!_started || _stopped)
                {
                    return;
                }

                _counts.TryGetValue(eventName, out var count);
                _counts[eventName] = count + 1;
            }
        }

        public async Task StopAsync()
        {
            lock (_sync)
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                _timer?.Dispose();
                _timer = null;
            }

            if (_httpClient != null)
            {
                using var cancellation = new CancellationTokenSource(RequestTimeout);
                await FlushAsync(cancellation.Token).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _httpClient?.Dispose();
            _flushLock.Dispose();
        }

        private static bool TryReadConfiguration(out Uri endpoint, out string token)
        {
            endpoint = null;
            if (!IsExplicitlyEnabled())
            {
                token = null;
                return false;
            }

            token = ReadSetting("CHAPTARR_DIAGNOSTICS_AUTH_TOKEN", "BOOKSHELF_DIAGNOSTICS_AUTH_TOKEN");
            var endpointValue = ReadSetting("CHAPTARR_DIAGNOSTICS_OTLP_ENDPOINT", "BOOKSHELF_DIAGNOSTICS_OTLP_ENDPOINT");

            if (string.IsNullOrWhiteSpace(endpointValue) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var parsed) ||
                parsed.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(parsed.UserInfo) ||
                !string.IsNullOrEmpty(parsed.Query) ||
                !string.IsNullOrEmpty(parsed.Fragment) ||
                !parsed.AbsolutePath.TrimEnd('/').EndsWith("/v1/metrics", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            endpoint = parsed;
            token = token.Trim();
            return token.Length > 0;
        }

        private static bool IsExplicitlyEnabled()
        {
            var currentSetting = Environment.GetEnvironmentVariable("CHAPTARR_DIAGNOSTICS_ENABLED");
            if (!string.IsNullOrWhiteSpace(currentSetting))
            {
                return string.Equals(currentSetting, "true", StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(
                Environment.GetEnvironmentVariable("BOOKSHELF_DIAGNOSTICS_ENABLED"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadSetting(string currentName, string legacyName)
        {
            var currentValue = Environment.GetEnvironmentVariable(currentName);
            return string.IsNullOrWhiteSpace(currentValue)
                ? Environment.GetEnvironmentVariable(legacyName)
                : currentValue;
        }

        private void OnTimer(object state)
        {
            _ = FlushAsync(CancellationToken.None);
        }

        private async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_httpClient == null || !await _flushLock.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            Dictionary<string, long> snapshot;
            var windowStart = DateTimeOffset.UtcNow;
            var windowEnd = DateTimeOffset.UtcNow;

            try
            {
                lock (_sync)
                {
                    if (_counts.Count == 0)
                    {
                        return;
                    }

                    snapshot = new Dictionary<string, long>(_counts, StringComparer.Ordinal);
                    windowStart = _windowStart;
                }

                var payload = CreatePayload(snapshot, windowStart, windowEnd);
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                request.Content = content;

                using var response = await _httpClient.SendAsync(request, cancellation.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn("Optional diagnostics collector returned HTTP {0}; cumulative counters remain available for retry.", (int)response.StatusCode);
                }
            }
            catch (Exception)
            {
                _logger.Warn("Optional diagnostics report failed; cumulative counters remain available for retry.");
            }
            finally
            {
                _flushLock.Release();
            }
        }

        private static object CreatePayload(Dictionary<string, long> counts, DateTimeOffset start, DateTimeOffset end)
        {
            var dataPoints = new List<object>();
            foreach (var entry in counts)
            {
                dataPoints.Add(new
                {
                    attributes = new[]
                    {
                        new { key = "event", value = new { stringValue = entry.Key } }
                    },
                    startTimeUnixNano = ToUnixNanoseconds(start),
                    timeUnixNano = ToUnixNanoseconds(end),
                    asInt = entry.Value.ToString(CultureInfo.InvariantCulture)
                });
            }

            return new
            {
                resourceMetrics = new[]
                {
                    new
                    {
                        resource = new
                        {
                            attributes = new[]
                            {
                                new { key = "service.name", value = new { stringValue = "chaptarrng" } },
                                new { key = "service.version", value = new { stringValue = BuildInfo.Version.ToString() } }
                            }
                        },
                        scopeMetrics = new[]
                        {
                            new
                            {
                                scope = new { name = "chaptarrng.optional-diagnostics" },
                                metrics = new[]
                                {
                                    new
                                    {
                                        name = "chaptarrng.events",
                                        unit = "{event}",
                                        sum = new
                                        {
                                            dataPoints,
                                            aggregationTemporality = 2,
                                            isMonotonic = true
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }

        private static string ToUnixNanoseconds(DateTimeOffset value)
        {
            return (value.ToUnixTimeMilliseconds() * 1000000L).ToString(CultureInfo.InvariantCulture);
        }
    }
}

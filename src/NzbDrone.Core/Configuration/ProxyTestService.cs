using System;
using System.Net.Http;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http.Proxy;

namespace NzbDrone.Core.Configuration
{
    public interface IProxyTestService
    {
        Task<ProxyTestResult> TestProxy(string hostname, int port, ProxyType proxyType, string username = null, string password = null);
    }

    public class ProxyTestResult
    {
        public bool IsValid { get; set; }
        public string Message { get; set; }
        public TimeSpan? ResponseTime { get; set; }
    }

    public class ProxyTestService : IProxyTestService
    {
        private readonly ICreateManagedWebProxy _createManagedWebProxy;
        private readonly Logger _logger;
        
        private const int DefaultMaxRetries = 3;
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);
        private static readonly string[] DefaultTestUrls = { "https://httpbin.org/ip", "https://icanhazip.com", "https://ifconfig.me/ip" };

        public ProxyTestService(ICreateManagedWebProxy createManagedWebProxy, Logger logger)
        {
            _createManagedWebProxy = createManagedWebProxy;
            _logger = logger;
        }

        public async Task<ProxyTestResult> TestProxy(string hostname, int port, ProxyType proxyType, string username = null, string password = null)
        {
            var result = new ProxyTestResult();
            
            _logger.Debug("Starting proxy test - Hostname: {0}, Port: {1}, Type: {2}, Auth: {3}", hostname, port, proxyType, !string.IsNullOrEmpty(username) ? "Yes" : "No");
            
            // Create proxy settings and HttpClient once for reuse across attempts
            var proxySettings = new HttpProxySettings(proxyType, hostname, port, "", false, username, password);
            var webProxy = _createManagedWebProxy.GetWebProxy(proxySettings);
            
            var handler = new HttpClientHandler()
            {
                UseProxy = true,
                Proxy = webProxy,
                UseCookies = false
            };
            
            using var httpClient = new HttpClient(handler)
            {
                Timeout = DefaultTimeout
            };
            
            for (var attempt = 1; attempt <= DefaultMaxRetries; attempt++)
            {
                var attemptStartTime = DateTime.UtcNow;

                try
                {
                    // Get test URL for this attempt (cycle through fallback URLs)
                    var testUrl = DefaultTestUrls[(attempt - 1) % DefaultTestUrls.Length];
                    _logger.Debug("Attempting to connect to {0} through proxy {1}:{2} (attempt {3}/{4})", testUrl, hostname, port, attempt, DefaultMaxRetries);

                    // Make the request through proxy
                    using var response = await httpClient.GetAsync(testUrl, HttpCompletionOption.ResponseHeadersRead);
                    var responseTime = DateTime.UtcNow - attemptStartTime;

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.Debug("Proxy test successful! Status: {0}, Response time: {1}ms", response.StatusCode, responseTime.TotalMilliseconds);
                        result.IsValid = true;
                        result.Message = $"Proxy connection successful via {testUrl}. Response time: {responseTime.TotalMilliseconds:F0}ms";
                        result.ResponseTime = responseTime;
                        return result;
                    }
                    
                    // Check for authentication failure (non-retryable)
                    if (response.StatusCode == System.Net.HttpStatusCode.ProxyAuthenticationRequired)
                    {
                        result.IsValid = false;
                        result.Message = "Proxy authentication failed (HTTP 407). Check username/password.";
                        return result;
                    }
                    
                    _logger.Warn("Proxy test failed - Status: {0} {1}", (int)response.StatusCode, response.StatusCode);

                    // For gateway errors, retry if not the last attempt
                    if (attempt < DefaultMaxRetries &&
                        (response.StatusCode == System.Net.HttpStatusCode.BadGateway ||
                         response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                         response.StatusCode == System.Net.HttpStatusCode.GatewayTimeout))
                    {
                        _logger.Debug("Gateway error detected, will retry after {0} seconds...", DefaultRetryDelay.TotalSeconds);
                        await Task.Delay(DefaultRetryDelay);
                        continue;
                    }

                    result.IsValid = false;
                    result.Message = $"Proxy test failed via {testUrl} with status code: {response.StatusCode}";
                    return result;
                }
                catch (HttpRequestException ex)
                {
                    _logger.Error("HTTP request exception during proxy test (attempt {0}/{1}) - Message: {2}, Inner: {3}", attempt, DefaultMaxRetries, ex.Message, ex.InnerException?.Message ?? "none");
                    _logger.Debug(ex, "Full HTTP request exception details");

                    if (attempt < DefaultMaxRetries)
                    {
                        _logger.Debug("HTTP error detected, will retry after {0} seconds...", DefaultRetryDelay.TotalSeconds);
                        await Task.Delay(DefaultRetryDelay);
                        continue;
                    }

                    result.IsValid = false;
                    result.Message = $"Connection failed: {ex.Message}";
                    return result;
                }
                catch (TaskCanceledException ex)
                {
                    _logger.Warn("Proxy test timed out after {0} seconds - Proxy: {1}:{2}, Attempt: {3}/{4}", DefaultTimeout.TotalSeconds, hostname, port, attempt, DefaultMaxRetries);
                    _logger.Debug(ex, "Timeout exception details");

                    if (attempt < DefaultMaxRetries)
                    {
                        _logger.Debug("Timeout detected, will retry after {0} seconds...", DefaultRetryDelay.TotalSeconds);
                        await Task.Delay(DefaultRetryDelay);
                        continue;
                    }

                    result.IsValid = false;
                    result.Message = "Connection timed out";
                    return result;
                }
                catch (Exception ex)
                {
                    _logger.Error("Unexpected error during proxy test (attempt {0}/{1}) - Type: {2}, Message: {3}", attempt, DefaultMaxRetries, ex.GetType().Name, ex.Message);
                    _logger.Debug(ex, "Full exception details");

                    result.IsValid = false;
                    result.Message = $"Test failed: {ex.Message}";
                    return result;
                }
            }

            result.IsValid = false;
            result.Message = "Proxy test failed after all retries";
            return result;
        }
    }
}

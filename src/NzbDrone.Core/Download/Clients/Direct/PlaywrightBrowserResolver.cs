using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public sealed class PlaywrightBrowserResolver : IBrowserDownloadResolver
    {
        private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan LinkWaitTimeout = TimeSpan.FromSeconds(15);
        private static readonly Regex SlowDownloadRegex = new(
            @"/slow_download/[^""\s]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FastDownloadRegex = new(
            @"/fast_download/[^""\s]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DirectFileRegex = new(
            @"https?://[^""\s]+\.(?:pdf|epub|mobi|azw3|djvu|cbz|cbr|fb2|txt)(?:\?[^""\s]*)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Logger _logger;

        public PlaywrightBrowserResolver(Logger logger)
        {
            _logger = logger;
        }

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(new()
                {
                    Headless = true,
                    Args = new[]
                    {
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-dev-shm-usage"
                    }
                });

                await browser.CloseAsync();
                playwright.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Playwright browser is not available: {0}", ex.Message);
                return false;
            }
        }

        public async Task<string> TryResolveSlowDownloadUrlAsync(string infoUrl)
        {
            if (infoUrl.IsNullOrWhiteSpace())
            {
                return null;
            }

            Microsoft.Playwright.IPlaywright playwright = null;
            Microsoft.Playwright.IBrowser browser = null;

            try
            {
                playwright = await Microsoft.Playwright.Playwright.CreateAsync();
                browser = await playwright.Chromium.LaunchAsync(new()
                {
                    Headless = true,
                    Args = new[]
                    {
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-dev-shm-usage"
                    }
                });

                var context = await browser.NewContextAsync(new()
                {
                    UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36"
                });

                var page = await context.NewPageAsync();
                await page.GotoAsync(infoUrl, new() { Timeout = (float)NavigationTimeout.TotalMilliseconds, WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle });

                // Wait for download links to appear (DDoS challenge may need JS execution)
                try
                {
                    await page.WaitForSelectorAsync(
                        "a[href*='/slow_download/'], a[href*='/fast_download/'], a[href$='.epub'], a[href$='.pdf']",
                        new() { Timeout = (float)LinkWaitTimeout.TotalMilliseconds });
                }
                catch (Microsoft.Playwright.TimeoutException)
                {
                    _logger.Debug("Browser timed out waiting for download links on {0}", infoUrl);
                }

                var content = await page.ContentAsync();

                // Extract download URLs in priority order
                var slowMatch = SlowDownloadRegex.Match(content);
                if (slowMatch.Success)
                {
                    var href = slowMatch.Value;
                    var resolved = new Uri(new Uri(infoUrl), href).AbsoluteUri;
                    _logger.Debug("Browser resolved slow download URL: {0}", Redact(resolved));
                    await CloseAsync(browser, playwright);
                    return resolved;
                }

                var fastMatch = FastDownloadRegex.Match(content);
                if (fastMatch.Success)
                {
                    var href = fastMatch.Value;
                    var resolved = new Uri(new Uri(infoUrl), href).AbsoluteUri;
                    _logger.Debug("Browser resolved fast download URL: {0}", Redact(resolved));
                    await CloseAsync(browser, playwright);
                    return resolved;
                }

                var fileMatch = DirectFileRegex.Match(content);
                if (fileMatch.Success)
                {
                    _logger.Debug("Browser resolved direct file URL: {0}", Redact(fileMatch.Value));
                    await CloseAsync(browser, playwright);
                    return fileMatch.Value;
                }

                _logger.Debug("Browser could not find any download link on {0}", infoUrl);
                await CloseAsync(browser, playwright);
                return null;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Browser download resolution failed for {0}: {1}", infoUrl, ex.Message);
                await CloseSafeAsync(browser, playwright);
                return null;
            }
        }

        private static async Task CloseAsync(Microsoft.Playwright.IBrowser browser, Microsoft.Playwright.IPlaywright playwright)
        {
            if (browser != null)
            {
                await browser.CloseAsync();
            }

            playwright?.Dispose();
        }

        private static async Task CloseSafeAsync(Microsoft.Playwright.IBrowser browser, Microsoft.Playwright.IPlaywright playwright)
        {
            try
            {
                await CloseAsync(browser, playwright);
            }
            catch
            {
                // Swallow cleanup errors
            }
        }

        private static string Redact(string url)
        {
            // Redact query parameters that might contain keys
            if (url == null)
            {
                return null;
            }

            var queryIndex = url.IndexOf('?');
            return queryIndex >= 0 ? url[..queryIndex] + "?[redacted]" : url;
        }
    }
}

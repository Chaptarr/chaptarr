using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public sealed class PlaywrightBrowserResolver : IBrowserDownloadResolver
    {
        private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan LinkWaitTimeout = TimeSpan.FromSeconds(15);
        private static readonly HashSet<string> DirectBookFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".epub", ".mobi", ".azw3", ".djvu", ".cbz", ".cbr", ".fb2", ".txt"
        };

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
                    ExecutablePath = ResolveChromiumExecutablePath(Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH")),
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
                _logger.Debug("Playwright browser is not available: {0}", CleanseLogMessage.Cleanse(ex.ToString()));
                return false;
            }
        }

        public Task<string> TryResolveSlowDownloadUrlAsync(string infoUrl)
        {
            return TryResolveSlowDownloadUrlAsync(infoUrl, CancellationToken.None);
        }

        public async Task<string> TryResolveSlowDownloadUrlAsync(string infoUrl, CancellationToken cancellationToken)
        {
            if (infoUrl.IsNullOrWhiteSpace())
            {
                return null;
            }

            Microsoft.Playwright.IPlaywright playwright = null;
            Microsoft.Playwright.IBrowser browser = null;
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                if (browser != null)
                {
                    _ = CloseSafeAsync(browser, playwright);
                }
            });

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                playwright = await Microsoft.Playwright.Playwright.CreateAsync();
                browser = await playwright.Chromium.LaunchAsync(new()
                {
                    ExecutablePath = ResolveChromiumExecutablePath(Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH")),
                    Headless = true,
                    Args = new[]
                    {
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-dev-shm-usage"
                    }
                });
                cancellationToken.ThrowIfCancellationRequested();

                var context = await browser.NewContextAsync(new()
                {
                    UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36"
                });
                cancellationToken.ThrowIfCancellationRequested();

                var page = await context.NewPageAsync();
                await page.GotoAsync(infoUrl, new() { Timeout = (float)NavigationTimeout.TotalMilliseconds, WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle });
                cancellationToken.ThrowIfCancellationRequested();

                // Wait for download links to appear (DDoS challenge may need JS execution)
                try
                {
                    await page.WaitForSelectorAsync(
                        "a[href*='/slow_download/'], a[href*='/fast_download/'], a[href*='.pdf' i], a[href*='.epub' i], a[href*='.mobi' i], a[href*='.azw3' i], a[href*='.djvu' i], a[href*='.cbz' i], a[href*='.cbr' i], a[href*='.fb2' i], a[href*='.txt' i]",
                        new() { Timeout = (float)LinkWaitTimeout.TotalMilliseconds });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _logger.Debug("Browser timed out or failed waiting for download links on {0}: {1}", Redact(infoUrl), CleanseLogMessage.Cleanse(ex.Message));
                }
                cancellationToken.ThrowIfCancellationRequested();

                // Extract download URLs in priority order
                var slowUrl = await GetFirstHttpLinkAsync(page, "a[href*='/slow_download/']", infoUrl);
                if (slowUrl != null)
                {
                    _logger.Debug("Browser resolved slow download URL: {0}", Redact(slowUrl));
                    await CloseAsync(browser, playwright);
                    return slowUrl;
                }

                var fastUrl = await GetFirstHttpLinkAsync(page, "a[href*='/fast_download/']", infoUrl);
                if (fastUrl != null)
                {
                    _logger.Debug("Browser resolved fast download URL: {0}", Redact(fastUrl));
                    await CloseAsync(browser, playwright);
                    return fastUrl;
                }

                var directFileUrl = await GetFirstDirectBookFileUrlAsync(page, infoUrl);
                if (directFileUrl != null)
                {
                    _logger.Debug("Browser resolved direct file URL: {0}", Redact(directFileUrl));
                    await CloseAsync(browser, playwright);
                    return directFileUrl;
                }

                _logger.Debug("Browser could not find any download link on {0}", Redact(infoUrl));
                await CloseAsync(browser, playwright);
                return null;
            }
            catch (Exception ex)
            {
                await CloseSafeAsync(browser, playwright);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                _logger.Warn("Browser download resolution failed for {0}: {1}", Redact(infoUrl), CleanseLogMessage.Cleanse(ex.ToString()));
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

        private static async Task<string> GetFirstHttpLinkAsync(Microsoft.Playwright.IPage page, string selector, string infoUrl)
        {
            var links = page.Locator(selector);
            var count = await links.CountAsync();

            for (var index = 0; index < count; index++)
            {
                var href = await links.Nth(index).GetAttributeAsync("href");
                if (TryResolveHttpUri(infoUrl, href, out var uri))
                {
                    return uri.AbsoluteUri;
                }
            }

            return null;
        }

        private static async Task<string> GetFirstDirectBookFileUrlAsync(Microsoft.Playwright.IPage page, string infoUrl)
        {
            var links = page.Locator("a[href]");
            var count = await links.CountAsync();

            for (var index = 0; index < count; index++)
            {
                var href = await links.Nth(index).GetAttributeAsync("href");
                if (TryResolveHttpUri(infoUrl, href, out var uri) && DirectBookFileExtensions.Contains(Path.GetExtension(uri.AbsolutePath)))
                {
                    return uri.AbsoluteUri;
                }
            }

            return null;
        }

        private static bool TryResolveHttpUri(string infoUrl, string href, out Uri uri)
        {
            uri = null;
            if (href.IsNullOrWhiteSpace() || !Uri.TryCreate(new Uri(infoUrl), href, out var resolved) ||
                (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            uri = resolved;
            return true;
        }

        internal static string ResolveChromiumExecutablePath(string legacyEnvironmentValue)
        {
            return string.IsNullOrWhiteSpace(legacyEnvironmentValue) ? null : legacyEnvironmentValue;
        }

        private static string Redact(string url)
        {
            // Redact query parameters that might contain keys
            if (url == null)
            {
                return null;
            }

            var queryIndex = url.IndexOf('?');
            var redacted = queryIndex >= 0 ? url[..queryIndex] + "?[redacted]" : url;
            return CleanseLogMessage.Cleanse(redacted);
        }
    }
}

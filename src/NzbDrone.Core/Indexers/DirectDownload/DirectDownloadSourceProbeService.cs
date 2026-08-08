using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Core.Indexers.DirectDownload
{
    public sealed class DirectDownloadSourceProbeService
    {
        private readonly Logger _logger;
        private readonly DirectDownloadSourceRuntimeCache _runtimeCache;
        private readonly Dictionary<DirectDownloadSourceFamily, Func<Uri, DirectDownloadProbeRequest, Task<DirectDownloadAdapterResult>>> _adapters;

        public DirectDownloadSourceProbeService(IHttpClient httpClient, Logger logger)
            : this(httpClient, logger, new DirectDownloadSourceRuntimeCache())
        {
        }

        public DirectDownloadSourceProbeService(IHttpClient httpClient, Logger logger, DirectDownloadSourceRuntimeCache runtimeCache)
        {
            _logger = logger;
            _runtimeCache = runtimeCache ?? new DirectDownloadSourceRuntimeCache();

            var reader = new DirectDownloadResponseReader(httpClient);
            var catalogAdapter = new DirectDownloadCatalogAdapter(reader);
            var mirrorAdapter = new DirectDownloadMirrorAdapter(reader);

            _adapters = new Dictionary<DirectDownloadSourceFamily, Func<Uri, DirectDownloadProbeRequest, Task<DirectDownloadAdapterResult>>>
            {
                [DirectDownloadSourceFamily.CatalogPage] = catalogAdapter.SearchAsync,
                [DirectDownloadSourceFamily.MirrorIndex] = mirrorAdapter.SearchAsync
            };
        }

        public async Task<DirectDownloadProbeResult> ProbeAsync(DirectDownloadProbeRequest request)
        {
            if (request?.SourceUrls == null || request.SourceUrls.Count == 0)
            {
                throw new DirectDownloadProbeException("At least one source URL is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Title) && string.IsNullOrWhiteSpace(request.Isbn))
            {
                throw new DirectDownloadProbeException("Either an ISBN or a title is required for probing.");
            }

            var failures = new StringBuilder();
            foreach (var rawUrl in request.SourceUrls.Where(url => !string.IsNullOrWhiteSpace(url)))
            {
                Uri baseUri;
                try
                {
                    baseUri = DirectDownloadUrlSafety.NormalizeAndValidate(rawUrl);
                }
                catch (DirectDownloadProbeException ex)
                {
                    AppendFailure(failures, RedactMessage(ex.Message, request.ApiKey));
                    continue;
                }

                var attemptedFamily = false;
                foreach (var family in GetOrderedFamilies(baseUri.AbsoluteUri))
                {
                    attemptedFamily = true;

                    try
                    {
                        var result = await _adapters[family](baseUri, request);
                        if (!result.Supported)
                        {
                            continue;
                        }

                        _runtimeCache.Set(baseUri.AbsoluteUri, family);
                        if (result.Releases.Count == 0)
                        {
                            continue;
                        }

                        return new DirectDownloadProbeResult
                        {
                            SelectedSourceUrl = baseUri.AbsoluteUri,
                            SelectedFamily = family,
                            Releases = result.Releases
                        };
                    }
                    catch (DirectDownloadProbeException ex)
                    {
                        AppendFailure(failures, RedactMessage(ex.Message, request.ApiKey));
                        continue;
                    }
                    catch (Exception ex)
                    {
                        AppendFailure(failures, RedactMessage(ex.Message, request.ApiKey));
                        continue;
                    }
                }

                if (!attemptedFamily)
                {
                    AppendFailure(failures, RedactMessage($"No direct source adapter is available for '{baseUri.AbsoluteUri}'.", request.ApiKey));
                }
            }

            throw new DirectDownloadProbeException(failures.Length == 0 ? "No safe direct-download sources succeeded." : failures.ToString().Trim());
        }

        private IEnumerable<DirectDownloadSourceFamily> GetOrderedFamilies(string sourceUrl)
        {
            if (_runtimeCache.TryGet(sourceUrl, out var cachedFamily))
            {
                yield return cachedFamily;

                foreach (var family in _adapters.Keys.Where(family => family != cachedFamily))
                {
                    yield return family;
                }

                yield break;
            }

            yield return DirectDownloadSourceFamily.CatalogPage;
            yield return DirectDownloadSourceFamily.MirrorIndex;
        }

        private void AppendFailure(StringBuilder failures, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (failures.Length > 0)
            {
                failures.AppendLine();
            }

            failures.Append("- ");
            failures.Append(message);
            _logger.Debug("Direct source probe failure: {0}", message);
        }

        private static string RedactMessage(string message, string apiKey)
        {
            var cleansed = CleanseLogMessage.Cleanse(message ?? string.Empty);
            return string.IsNullOrWhiteSpace(apiKey)
                ? cleansed
                : cleansed.Replace(apiKey, "(removed)", StringComparison.Ordinal);
        }
    }
}

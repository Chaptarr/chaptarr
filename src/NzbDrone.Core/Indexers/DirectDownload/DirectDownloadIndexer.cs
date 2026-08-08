using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Direct;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Indexers.DirectDownload
{
    public class DirectDownloadIndexer : IndexerBase<DirectDownloadSettings>
    {
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
        private readonly DirectDownloadSourceProbeService _probeService;
        private readonly DirectDownloadGrabUrlResolver _grabUrlResolver;

        public DirectDownloadIndexer(
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger,
            DirectDownloadSourceProbeService probeService = null,
            DirectDownloadGrabUrlResolver grabUrlResolver = null)
            : base(indexerStatusService, configService, parsingService, logger)
        {
            _probeService = probeService;
            _grabUrlResolver = grabUrlResolver;
        }

        public override string Name => "Direct Download";

        public override DownloadProtocol Protocol => DownloadProtocol.Direct;

        public override bool SupportsRss => false;

        public override bool SupportsSearch => true;

        public override ProviderMessage Message => new(
            "This indexer supports ebook searches only. Configured URLs remain in the order you enter them.",
            ProviderMessageType.Info);

        public override Task<IList<ReleaseInfo>> FetchRecent()
        {
            return Task.FromResult<IList<ReleaseInfo>>(Array.Empty<ReleaseInfo>());
        }

        public override async Task<IList<ReleaseInfo>> Fetch(BookSearchCriteria searchCriteria)
        {
            if (SearchMediaTypeHelper.GetRequestedMediaType(searchCriteria) != BookMediaType.Ebook)
            {
                return Array.Empty<ReleaseInfo>();
            }

            var request = BuildProbeRequest(searchCriteria?.Author?.Name, searchCriteria?.BookTitle, searchCriteria?.BookIsbn);
            return await ProbeAsync(request);
        }

        public override async Task<IList<ReleaseInfo>> Fetch(AuthorSearchCriteria searchCriteria)
        {
            var books = (searchCriteria?.Books ?? new List<Book>())
                .Where(book => book != null && book.MediaType == BookMediaType.Ebook)
                .ToList();

            if (books.Count == 0)
            {
                return Array.Empty<ReleaseInfo>();
            }

            var releases = new List<ReleaseInfo>();
            foreach (var book in books)
            {
                var selectedEdition = book.Editions?
                    .Where(edition => edition != null && edition.Monitored)
                    .OrderBy(edition => edition.Id)
                    .FirstOrDefault();

                var request = BuildProbeRequest(
                    searchCriteria.Author?.Name,
                    NzbDrone.Core.IndexerSearch.ReleaseSearchService.GetSearchBookTitle(book, selectedEdition),
                    NzbDrone.Core.IndexerSearch.ReleaseSearchService.GetSearchBookIsbn(book, selectedEdition));

                var bookReleases = await ProbeAsync(request);
                releases.AddRange(bookReleases);
            }

            return CleanupReleases(releases).ToList();
        }

        public override HttpRequest GetDownloadRequest(string link)
        {
            return new HttpRequest(link);
        }

        protected override async Task Test(List<ValidationFailure> failures)
        {
            var validation = Settings.Validate();
            if (!validation.IsValid)
            {
                failures.AddRange(validation.Errors);
                Definition.Message = null;
                return;
            }

            var normalizedUrls = DirectDownloadSettings.NormalizeUrls(Settings.Urls);
            var outcomes = normalizedUrls
                .Select((_, index) => $"URL {index + 1}: configuration valid");

            var messages = new List<string>(outcomes);

            if (_grabUrlResolver != null && !string.IsNullOrWhiteSpace(Settings.ApiKey) && normalizedUrls.Count > 0)
            {
                var keyResult = await _grabUrlResolver.ValidateApiKeyAsync(normalizedUrls[0], Settings.ApiKey);
                var maskedPrefix = MaskKeyForDisplay(Settings.ApiKey);
                var keyPrefix = $"API key ({maskedPrefix}):";

                switch (keyResult.Outcome)
                {
                    case ApiKeyValidationOutcome.Valid:
                        messages.Insert(0, $"{keyPrefix} {keyResult.Message}");
                        break;
                    case ApiKeyValidationOutcome.NoDownloadsRemaining:
                        messages.Insert(0, $"{keyPrefix} {keyResult.Message}");
                        break;
                    case ApiKeyValidationOutcome.InvalidOrExpired:
                        failures.Add(new ValidationFailure(nameof(DirectDownloadSettings.ApiKey), keyResult.Message));
                        break;
                    case ApiKeyValidationOutcome.TransientFailure:
                        messages.Insert(0, $"{keyPrefix} {keyResult.Message}");
                        break;
                }
            }
            else if (string.IsNullOrWhiteSpace(Settings.ApiKey))
            {
                messages.Insert(0, "No API key configured. Using public download links only.");
            }

            Definition.Message = new ProviderMessage(
                string.Join(Environment.NewLine, messages),
                ProviderMessageType.Info);
        }

        private static string MaskKeyForDisplay(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length <= 4)
            {
                return "***";
            }

            return apiKey.Substring(0, 4) + "***";
        }

        private async Task<IList<ReleaseInfo>> ProbeAsync(DirectDownloadProbeRequest request)
        {
            if (_probeService == null || request == null)
            {
                return Array.Empty<ReleaseInfo>();
            }

            try
            {
                var result = await _probeService.ProbeAsync(request);
                return CleanupReleases(result.Releases).ToList();
            }
            catch (DirectDownloadProbeException ex)
            {
                _logger?.Debug(ex, "Direct download probe returned no releases for {0}", Definition?.Name ?? Name);
                return Array.Empty<ReleaseInfo>();
            }
        }

        private DirectDownloadProbeRequest BuildProbeRequest(string author, string title, string isbn)
        {
            if (Definition?.Settings is not DirectDownloadSettings settings)
            {
                return null;
            }

            var sourceUrls = DirectDownloadSettings.NormalizeUrls(settings.Urls);
            if (sourceUrls.Count == 0)
            {
                return null;
            }

            return new DirectDownloadProbeRequest
            {
                SourceUrls = sourceUrls,
                ApiKey = settings.ApiKey,
                Author = author,
                Title = title,
                Isbn = isbn,
                RequestTimeout = ProbeTimeout,
                MaxResponseBytes = 1024 * 1024
            };
        }
    }
}

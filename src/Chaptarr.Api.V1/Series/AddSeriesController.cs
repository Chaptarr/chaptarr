using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Api.V1.ProviderIds;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource;
// using NzbDrone.Core.MetadataSource.Hardcover; // Removed - using V5 API via BookInfoProxy

namespace Chaptarr.Api.V1.Series
{
    [V1ApiController("series/add")]
    public class AddSeriesController : Controller
    {
        private enum SeriesMonitorExistingMode
        {
            None,
            All,
            Select
        }

        // private readonly IHardcoverSearchProxy _hardcoverSearchProxy; // Removed - using V5 API via BookInfoProxy
        // DEPRECATED-IDENTIFICATION: IAddAuthorService removed - use IAuthorLibraryService instead
        // private readonly IAddAuthorService _addAuthorService;
        private readonly IAuthorLibraryService _authorLibraryService;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IProviderAliasService _providerAliasService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public AddSeriesController(
            // IHardcoverSearchProxy hardcoverSearchProxy, // Removed - using V5 API via BookInfoProxy
            IAuthorLibraryService authorLibraryService,
            IAuthorService authorService,
            IBookService bookService,
            IManageCommandQueue commandQueueManager,
            Logger logger,
            IProviderAliasService providerAliasService = null)
        {
            // _hardcoverSearchProxy = hardcoverSearchProxy; // Removed - using V5 API via BookInfoProxy
            _authorLibraryService = authorLibraryService;
            _authorService = authorService;
            _bookService = bookService;
            _providerAliasService = providerAliasService;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        [HttpPost]
        [ProducesResponseType(typeof(ProviderAmbiguityResource), ProviderAmbiguityHelper.StatusCode)]
        public async Task<ActionResult<AddSeriesResult>> AddSeries([FromBody] AddSeriesRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ForeignSeriesId))
            {
                _logger.Warn("AddSeries: missing foreignSeriesId");
                return BadRequest(new AddSeriesResult
                {
                    Success = false,
                    ErrorMessage = "foreignSeriesId is required"
                });
            }

            try
            {
                if (string.IsNullOrWhiteSpace(request.SelectedMediaType))
                {
                    _logger.Warn("AddSeries: missing selectedMediaType for series {0}", request.ForeignSeriesId);
                    return BadRequest(new AddSeriesResult
                    {
                        Success = false,
                        ErrorMessage = "selectedMediaType is required"
                    });
                }

                if (request.SelectedBooks == null || request.SelectedBooks.Count == 0)
                {
                    _logger.Warn("AddSeries: missing selectedBooks for series {0}", request.ForeignSeriesId);
                    return BadRequest(new AddSeriesResult
                    {
                        Success = false,
                        ErrorMessage = "selectedBooks is required"
                    });
                }

                BookMediaType selectedMediaType;
                try
                {
                    selectedMediaType = MediaTypeParameterParser.ParseRequired(request.SelectedMediaType);
                }
                catch (BadRequestException)
                {
                    _logger.Warn("AddSeries: invalid selectedMediaType '{0}' for series {1}", request.SelectedMediaType, request.ForeignSeriesId);
                    return BadRequest(new AddSeriesResult
                    {
                        Success = false,
                        ErrorMessage = "Invalid selectedMediaType (expected 'audiobook' or 'ebook')"
                    });
                }

                var selectedBookIds = request.SelectedBooks
                    .Select(b => b?.ForeignBookId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var authorProviderIds = request.SelectedBooks
                    .Select(b => b?.ForeignAuthorId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (authorProviderIds.Count == 0)
                {
                    _logger.Warn("AddSeries: no foreignAuthorId values for series {0}", request.ForeignSeriesId);
                    return BadRequest(new AddSeriesResult
                    {
                        Success = false,
                        ErrorMessage = "selectedBooks must include foreignAuthorId values"
                    });
                }

                var monitorExistingMode = ParseMonitorExistingMode(request);

                foreach (var authorProviderId in authorProviderIds)
                {
                    var authorAmbiguity = GetAuthorProviderAmbiguityResult(authorProviderId);
                    if (authorAmbiguity != null)
                    {
                        return authorAmbiguity;
                    }
                }

                if (monitorExistingMode == SeriesMonitorExistingMode.Select)
                {
                    foreach (var selectedBookId in selectedBookIds)
                    {
                        var bookAmbiguity = GetBookProviderAmbiguityResult(selectedBookId, selectedMediaType);
                        if (bookAmbiguity != null)
                        {
                            return bookAmbiguity;
                        }
                    }
                }

                var monitorFuture = request.MonitorFuture == true;

                _logger.Info("Adding series {0}: {1} selected books, {2} authors, mediaType={3}, monitorExisting={4}, monitorFuture={5}",
                    request.ForeignSeriesId,
                    selectedBookIds.Count,
                    authorProviderIds.Count,
                    selectedMediaType,
                    monitorExistingMode,
                    monitorFuture);

                var monitoringConfig = BuildMonitoringConfig(request, selectedMediaType, monitorExistingMode, monitorFuture, selectedBookIds);

                var addedAuthorResources = new List<AuthorResource>();
                var monitoredBookResources = new List<BookResource>();
                var pendingAuthorImportIds = new HashSet<int>();

                foreach (var authorProviderId in authorProviderIds)
                {
                    var dbAuthor = await EnsureAuthorExistsAsync(authorProviderId, monitoringConfig, selectedMediaType);
                    if (dbAuthor == null)
                    {
                        _logger.Warn("Failed to add/resolve author {0} while adding series {1}", authorProviderId, request.ForeignSeriesId);
                        continue;
                    }

                    // Pending import: author not available on metadata server yet; queued for retry.
                    // AddAuthorAsync returns a negative ID marker: -pendingId
                    if (dbAuthor.Id < 0)
                    {
                        var pendingId = -dbAuthor.Id;
                        pendingAuthorImportIds.Add(pendingId);
                        _logger.Info("AddSeries: queued pending author import {0} (pendingId={1}) for series {2}", authorProviderId, pendingId, request.ForeignSeriesId);
                        continue;
                    }

                    if (monitorExistingMode == SeriesMonitorExistingMode.All)
                    {
                        var monitoredBooks = MonitorAllBooks(dbAuthor, selectedMediaType);
                        monitoredBookResources.AddRange(monitoredBooks.Select(b => b.ToResource()));
                    }
                    else if (monitorExistingMode == SeriesMonitorExistingMode.Select)
                    {
                        var monitoredBooks = MonitorSelectedBooks(dbAuthor, selectedMediaType, selectedBookIds);
                        monitoredBookResources.AddRange(monitoredBooks.Select(b => b.ToResource()));
                    }

                    addedAuthorResources.Add(dbAuthor.ToResource());
                }

                // De-dupe monitored books
                monitoredBookResources = monitoredBookResources
                    .GroupBy(b => b.Id)
                    .Select(g => g.First())
                    .ToList();

                return Ok(new AddSeriesResult
                {
                    Success = true,
                    AddedAuthors = addedAuthorResources,
                    MonitoredBooks = monitoredBookResources,
                    PendingAuthorImportIds = pendingAuthorImportIds.OrderBy(x => x).ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error adding series {request.ForeignSeriesId}");
                return StatusCode(500, new AddSeriesResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        private static SeriesMonitorExistingMode ParseMonitorExistingMode(AddSeriesRequest request)
        {
            var normalized = (request?.MonitorExisting ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized switch
                {
                    "all" => SeriesMonitorExistingMode.All,
                    "select" => SeriesMonitorExistingMode.Select,
                    "specificbook" => SeriesMonitorExistingMode.Select,
                    "none" => SeriesMonitorExistingMode.None,
                    // Legacy monitor options map closest to "All" for series adds.
                    "existing" => SeriesMonitorExistingMode.All,
                    "missing" => SeriesMonitorExistingMode.All,
                    _ => SeriesMonitorExistingMode.Select
                };
            }

            return request?.MonitorAllBooksByAllAuthors == true
                ? SeriesMonitorExistingMode.All
                : SeriesMonitorExistingMode.Select;
        }

        private MonitoringConfig BuildMonitoringConfig(
            AddSeriesRequest request,
            BookMediaType selectedMediaType,
            SeriesMonitorExistingMode monitorExistingMode,
            bool monitorFuture,
            List<string> selectedBookIds)
        {
            var tags = request.Tags != null ? new HashSet<int>(request.Tags) : null;

            var config = new MonitoringConfig
            {
                IsManualAddition = true,
                CreateAudiobook = selectedMediaType == BookMediaType.Audiobook,
                CreateEbook = selectedMediaType == BookMediaType.Ebook,
                MonitorNewItems = true,
                QueueIfUnavailable = true,
                AudiobookRootFolderPath = request.AudiobookRootFolderPath,
                EbookRootFolderPath = request.EbookRootFolderPath,
                AudiobookQualityProfileId = request.AudiobookQualityProfileId,
                EbookQualityProfileId = request.EbookQualityProfileId,
                AudiobookMetadataProfileId = request.AudiobookMetadataProfileId,
                EbookMetadataProfileId = request.EbookMetadataProfileId,
                Tags = tags,
                RequestedBy = "ApiV1SeriesAdd"
            };

            if (selectedMediaType == BookMediaType.Audiobook)
            {
                if (tags != null && tags.Count > 0)
                {
                    config.AudiobookTags = tags;
                }

                config.AudiobookMonitorFuture = monitorFuture;
                config.EbookMonitorExisting = 0;
                config.EbookMonitorFuture = false;

                if (monitorExistingMode == SeriesMonitorExistingMode.All)
                {
                    config.AudiobookMonitorExisting = 1;
                }
                else if (monitorExistingMode == SeriesMonitorExistingMode.Select)
                {
                    config.AudiobookMonitorExisting = 2;
                    config.MonitorMode = MonitorTypes.SpecificBook;
                    config.SpecificBookProviderIds = new HashSet<string>(selectedBookIds, StringComparer.OrdinalIgnoreCase);
                    config.SpecificBookMediaType = BookMediaType.Audiobook;
                    // Pending imports don't persist SpecificBookProviderIds; store an explicit list for later.
                    config.AudiobookBooksToMonitor = selectedBookIds;
                }
                else
                {
                    config.AudiobookMonitorExisting = 0;
                }
            }
            else
            {
                if (tags != null && tags.Count > 0)
                {
                    config.EbookTags = tags;
                }

                config.EbookMonitorFuture = monitorFuture;
                config.AudiobookMonitorExisting = 0;
                config.AudiobookMonitorFuture = false;

                if (monitorExistingMode == SeriesMonitorExistingMode.All)
                {
                    config.EbookMonitorExisting = 1;
                }
                else if (monitorExistingMode == SeriesMonitorExistingMode.Select)
                {
                    config.EbookMonitorExisting = 2;
                    config.MonitorMode = MonitorTypes.SpecificBook;
                    config.SpecificBookProviderIds = new HashSet<string>(selectedBookIds, StringComparer.OrdinalIgnoreCase);
                    config.SpecificBookMediaType = BookMediaType.Ebook;
                    config.EbookBooksToMonitor = selectedBookIds;
                }
                else
                {
                    config.EbookMonitorExisting = 0;
                }
            }

            return config;
        }

        private static (string provider, string id) SplitProviderId(string prefixedId)
        {
            if (string.IsNullOrWhiteSpace(prefixedId))
            {
                return (null, null);
            }

            var trimmed = prefixedId.Trim().Trim('{', '}');
            var idx = trimmed.IndexOf(':');
            if (idx <= 0 || idx == trimmed.Length - 1)
            {
                return (string.Empty, trimmed);
            }

            return (trimmed.Substring(0, idx), trimmed.Substring(idx + 1));
        }


        private ActionResult GetProviderAmbiguityResult(ProviderAmbiguityResource ambiguity)
        {
            return ambiguity == null ? null : StatusCode(ProviderAmbiguityHelper.StatusCode, ambiguity);
        }

        private ActionResult GetAuthorProviderAmbiguityResult(string prefixedProviderId)
        {
            return GetProviderAmbiguityResult(ProviderAmbiguityHelper.GetAuthorAmbiguity(
                _authorService,
                _providerAliasService,
                prefixedProviderId,
                "foreignAuthorId",
                _logger,
                "adding series"));
        }

        private ActionResult GetBookProviderAmbiguityResult(string prefixedProviderId, BookMediaType mediaType)
        {
            return GetProviderAmbiguityResult(ProviderAmbiguityHelper.GetBookAmbiguity(
                _bookService,
                prefixedProviderId,
                mediaType,
                "foreignBookId",
                _logger,
                "adding series"));
        }

        private async Task<NzbDrone.Core.Books.Author> EnsureAuthorExistsAsync(string authorProviderId, MonitoringConfig config, BookMediaType requestedMediaType)
        {
            var (provider, rawId) = SplitProviderId(authorProviderId);
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(rawId))
            {
                _logger.Warn("Invalid author provider ID: {0}", authorProviderId);
                return null;
            }

            // Prefer an existing author if present, including provider aliases from prior server-side merges.
            var existingMatches = ProviderAmbiguityHelper.FindAuthorProviderMatches(_authorService, _providerAliasService, provider, rawId, _logger);
            var existing = existingMatches.Count == 1 ? existingMatches[0] : null;
            if (existing != null)
            {
                // Ensure author is monitored so selected books can be monitored.
                if (!existing.Monitored)
                {
                    existing.Monitored = true;
                    _authorService.UpdateAuthor(existing);
                }

                // Ensure the requested media type exists locally for this author.
                // The Series add flow may target a media type the author has never been hydrated for yet.
                try
                {
                    var existingBooks = _bookService.GetBooksByAuthor(existing.Id) ?? new List<NzbDrone.Core.Books.Book>();
                    var hasRequestedMediaType = existingBooks.Any(b => b.MediaType == requestedMediaType);

                    if (!hasRequestedMediaType)
                    {
                        _logger.Info("AddSeries: existing author {0} missing {1} catalog; hydrating from provider", authorProviderId, requestedMediaType);

                        var hydrated = await _authorLibraryService.AddAuthorAsync(authorProviderId, config);
                        if (hydrated != null)
                        {
                            return hydrated;
                        }
                    }
                    else
                    {
                        // Apply progressive settings for this media type context without forcing a full re-hydration.
                        existing = _authorService.UpdateAuthorProgressiveSettings(
                            existing,
                            config.CreateAudiobook ? config.AudiobookQualityProfileId : null,
                            config.CreateAudiobook ? config.AudiobookMetadataProfileId : null,
                            config.CreateAudiobook ? config.AudiobookMonitorExisting : null,
                            config.CreateAudiobook ? config.AudiobookMonitorFuture : null,
                            config.CreateEbook ? config.EbookQualityProfileId : null,
                            config.CreateEbook ? config.EbookMetadataProfileId : null,
                            config.CreateEbook ? config.EbookMonitorExisting : null,
                            config.CreateEbook ? config.EbookMonitorFuture : null,
                            config.AudiobookRootFolderPath ?? config.EbookRootFolderPath
                        );
                    }
                }
                catch (AuthorNotFoundException ex)
                {
                    _logger.Warn(ex, "AddSeries: unable to hydrate missing {0} catalog for existing author {1}", requestedMediaType, authorProviderId);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "AddSeries: unexpected error hydrating missing {0} catalog for existing author {1}", requestedMediaType, authorProviderId);
                }

                return existing;
            }

            return await _authorLibraryService.AddAuthorAsync(authorProviderId, config);
        }

        private List<NzbDrone.Core.Books.Book> MonitorAllBooks(NzbDrone.Core.Books.Author author, BookMediaType mediaType)
        {
            var books = _bookService.GetBooksByAuthor(author.Id) ?? new List<NzbDrone.Core.Books.Book>();

            var toUpdate = new List<NzbDrone.Core.Books.Book>();
            foreach (var book in books)
            {
                if (book == null || book.MediaType != mediaType)
                {
                    continue;
                }

                if (mediaType == BookMediaType.Audiobook && !book.AudiobookMonitored)
                {
                    book.AudiobookMonitored = true;
                    toUpdate.Add(book);
                }
                else if (mediaType == BookMediaType.Ebook && !book.EbookMonitored)
                {
                    book.EbookMonitored = true;
                    toUpdate.Add(book);
                }
            }

            if (toUpdate.Any())
            {
                _bookService.UpdateMany(toUpdate);
            }

            return toUpdate;
        }

        private List<NzbDrone.Core.Books.Book> MonitorSelectedBooks(NzbDrone.Core.Books.Author author, BookMediaType mediaType, List<string> selectedProviderBookIds)
        {
            var books = _bookService.GetBooksByAuthor(author.Id) ?? new List<NzbDrone.Core.Books.Book>();

            var toUpdate = new List<NzbDrone.Core.Books.Book>();
            foreach (var book in books)
            {
                if (book == null || book.MediaType != mediaType)
                {
                    continue;
                }

                if (!selectedProviderBookIds.Any(id => BookMatchesProviderId(book, id)))
                {
                    continue;
                }

                if (mediaType == BookMediaType.Audiobook && !book.AudiobookMonitored)
                {
                    book.AudiobookMonitored = true;
                    toUpdate.Add(book);
                }
                else if (mediaType == BookMediaType.Ebook && !book.EbookMonitored)
                {
                    book.EbookMonitored = true;
                    toUpdate.Add(book);
                }
            }

            if (toUpdate.Any())
            {
                _bookService.UpdateMany(toUpdate);
            }

            return toUpdate;
        }

        private static bool BookMatchesProviderId(NzbDrone.Core.Books.Book book, string providerId)
        {
            if (book == null || string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            var parts = providerId.Trim().Trim('{', '}').Split(':');
            if (parts.Length != 2)
            {
                return false;
            }

            var prefix = parts[0].Trim().ToLowerInvariant();
            var id = parts[1].Trim();
            var normalizedProviderId = $"{prefix}:{id}";

            if (BookIdentity.GetProviderIdentityTokens(book).Contains(normalizedProviderId))
            {
                return true;
            }

            static string ExtractRaw(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return string.Empty;
                }

                var idx = value.IndexOf(':');
                return idx > 0 ? value.Substring(idx + 1) : value;
            }

            return prefix switch
            {
                "hc" => ExtractRaw(book.HardcoverBookId) == id,
                "gr" => ExtractRaw(book.GoodreadsBookId) == id,
                "ol" => ExtractRaw(book.OpenLibraryWorkId) == id,
                "gb" => ExtractRaw(book.GoogleBooksId) == id,
                "az" => book.ASIN?.Equals(id, StringComparison.OrdinalIgnoreCase) == true,
                _ => false
            };
        }
    }

    public class AddSeriesRequest
    {
        public string ForeignSeriesId { get; set; }
        public string SelectedMediaType { get; set; }
        public List<SelectedSeriesBook> SelectedBooks { get; set; }
        public bool MonitorAllBooksByAllAuthors { get; set; } = false;
        // New: parity with AddAuthorOptionsForm (all/select/none) + monitor new items
        public string MonitorExisting { get; set; }
        public bool? MonitorFuture { get; set; }
        public string RootFolderPath { get; set; }

        // Per-type settings (same shape as author import)
        public string AudiobookRootFolderPath { get; set; }
        public string EbookRootFolderPath { get; set; }
        public int? AudiobookQualityProfileId { get; set; }
        public int? EbookQualityProfileId { get; set; }
        public int? AudiobookMetadataProfileId { get; set; }
        public int? EbookMetadataProfileId { get; set; }

        public List<int> Tags { get; set; }
    }

    public class SelectedSeriesBook
    {
        public string ForeignBookId { get; set; }
        public string ForeignAuthorId { get; set; }
    }

    public class AddSeriesResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string SeriesTitle { get; set; }
        public List<AuthorResource> AddedAuthors { get; set; }
        public List<BookResource> MonitoredBooks { get; set; }
        public List<int> PendingAuthorImportIds { get; set; }
    }
}

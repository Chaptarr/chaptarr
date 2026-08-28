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
        // private readonly IHardcoverSearchProxy _hardcoverSearchProxy; // Removed - using V5 API via BookInfoProxy
        // DEPRECATED-IDENTIFICATION: IAddAuthorService removed - use IAuthorLibraryService instead
        // private readonly IAddAuthorService _addAuthorService;
        private readonly IAuthorLibraryService _authorLibraryService;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IBookMonitoredService _bookMonitoredService;
        private readonly IProviderAliasService _providerAliasService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public AddSeriesController(
            // IHardcoverSearchProxy hardcoverSearchProxy, // Removed - using V5 API via BookInfoProxy
            IAuthorLibraryService authorLibraryService,
            IAuthorService authorService,
            IBookService bookService,
            IBookMonitoredService bookMonitoredService,
            IManageCommandQueue commandQueueManager,
            Logger logger,
            IProviderAliasService providerAliasService = null)
        {
            // _hardcoverSearchProxy = hardcoverSearchProxy; // Removed - using V5 API via BookInfoProxy
            _authorLibraryService = authorLibraryService;
            _authorService = authorService;
            _bookService = bookService;
            _bookMonitoredService = bookMonitoredService;
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

                MonitorTypes monitorMode;
                try
                {
                    monitorMode = ParseMonitorMode(request, selectedMediaType);
                }
                catch (ArgumentException ex)
                {
                    return BadRequest(new AddSeriesResult
                    {
                        Success = false,
                        ErrorMessage = ex.Message
                    });
                }

                foreach (var authorProviderId in authorProviderIds)
                {
                    var authorAmbiguity = GetAuthorProviderAmbiguityResult(authorProviderId);
                    if (authorAmbiguity != null)
                    {
                        return authorAmbiguity;
                    }
                }

                if (monitorMode == MonitorTypes.SpecificBook)
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

                _logger.Info("Adding series {0}: {1} selected books, {2} authors, mediaType={3}, monitor={4}",
                    request.ForeignSeriesId,
                    selectedBookIds.Count,
                    authorProviderIds.Count,
                    selectedMediaType,
                    monitorMode);

                var monitoringConfig = BuildMonitoringConfig(request, selectedMediaType, monitorMode, selectedBookIds);

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

                    ApplyCurrentBookMonitoring(dbAuthor, selectedMediaType, monitorMode, selectedBookIds);
                    var monitoredBooks = (_bookService.GetBooksByAuthor(dbAuthor.Id) ?? new List<NzbDrone.Core.Books.Book>())
                        .Where(book => book.MediaType == selectedMediaType &&
                            (selectedMediaType == BookMediaType.Audiobook ? book.AudiobookMonitored : book.EbookMonitored));
                    monitoredBookResources.AddRange(monitoredBooks.Select(book => book.ToResource()));

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

        private static MonitorTypes ParseMonitorMode(AddSeriesRequest request, BookMediaType selectedMediaType)
        {
            var perMediaMode = selectedMediaType == BookMediaType.Audiobook
                ? request?.AudiobookMonitorExistingMode
                : request?.EbookMonitorExistingMode;
            if (perMediaMode.HasValue)
            {
                return perMediaMode.Value;
            }

            var normalized = (request?.Monitor ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                // The selected books are the natural one-time target when no
                // explicit current-catalog action is supplied.
                return MonitorTypes.SpecificBook;
            }

            return normalized switch
            {
                "all" => MonitorTypes.All,
                "future" => MonitorTypes.Future,
                "missing" => MonitorTypes.Missing,
                "existing" => MonitorTypes.Existing,
                "first" => MonitorTypes.First,
                "latest" => MonitorTypes.Latest,
                "none" => MonitorTypes.None,
                "specificbook" => MonitorTypes.SpecificBook,
                _ => throw new ArgumentException("Invalid monitor value. Expected all, future, missing, existing, first, latest, none, or specificBook.")
            };
        }

        private MonitoringConfig BuildMonitoringConfig(
            AddSeriesRequest request,
            BookMediaType selectedMediaType,
            MonitorTypes monitorMode,
            List<string> selectedBookIds)
        {
            var tags = request.Tags != null ? new HashSet<int>(request.Tags) : null;

            var config = new MonitoringConfig
            {
                IsManualAddition = true,
                CreateAudiobook = selectedMediaType == BookMediaType.Audiobook,
                CreateEbook = selectedMediaType == BookMediaType.Ebook,
                QueueIfUnavailable = true,
                AudiobookRootFolderPath = request.AudiobookRootFolderPath,
                EbookRootFolderPath = request.EbookRootFolderPath,
                AudiobookQualityProfileId = request.AudiobookQualityProfileId,
                EbookQualityProfileId = request.EbookQualityProfileId,
                AudiobookMetadataProfileId = request.AudiobookMetadataProfileId,
                EbookMetadataProfileId = request.EbookMetadataProfileId,
                AudiobookMonitored = selectedMediaType == BookMediaType.Audiobook
                    ? monitorMode == MonitorTypes.SpecificBook ? true : request.AudiobookMonitored
                    : null,
                AudiobookMonitorNewItems = selectedMediaType == BookMediaType.Audiobook ? request.AudiobookMonitorNewItems : null,
                AudiobookMonitorExistingMode = selectedMediaType == BookMediaType.Audiobook ? monitorMode : null,
                EbookMonitored = selectedMediaType == BookMediaType.Ebook
                    ? monitorMode == MonitorTypes.SpecificBook ? true : request.EbookMonitored
                    : null,
                EbookMonitorNewItems = selectedMediaType == BookMediaType.Ebook ? request.EbookMonitorNewItems : null,
                EbookMonitorExistingMode = selectedMediaType == BookMediaType.Ebook ? monitorMode : null,
                Tags = tags,
                RequestedBy = "ApiV1SeriesAdd",
                MonitorMode = monitorMode
            };

            if (selectedMediaType == BookMediaType.Audiobook)
            {
                if (tags != null && tags.Count > 0)
                {
                    config.AudiobookTags = tags;
                }

                if (monitorMode == MonitorTypes.SpecificBook)
                {
                    config.SpecificBookProviderIds = new HashSet<string>(selectedBookIds, StringComparer.OrdinalIgnoreCase);
                    config.SpecificBookMediaType = BookMediaType.Audiobook;
                    // Pending imports don't persist SpecificBookProviderIds; store an explicit list for later.
                    config.AudiobookBooksToMonitor = selectedBookIds;
                }
            }
            else
            {
                if (tags != null && tags.Count > 0)
                {
                    config.EbookTags = tags;
                }

                if (monitorMode == MonitorTypes.SpecificBook)
                {
                    config.SpecificBookProviderIds = new HashSet<string>(selectedBookIds, StringComparer.OrdinalIgnoreCase);
                    config.SpecificBookMediaType = BookMediaType.Ebook;
                    config.EbookBooksToMonitor = selectedBookIds;
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
                            existing = hydrated;
                        }
                    }
                    else
                    {
                        // Apply progressive settings for this media type context without forcing a full re-hydration.
                        existing = _authorService.UpdateAuthorProgressiveSettings(
                            existing,
                            config.CreateAudiobook ? config.AudiobookQualityProfileId : null,
                            config.CreateAudiobook ? config.AudiobookMetadataProfileId : null,
                            config.CreateAudiobook ? config.AudiobookMonitored : null,
                            config.CreateAudiobook ? config.AudiobookMonitorNewItems : null,
                            config.CreateEbook ? config.EbookQualityProfileId : null,
                            config.CreateEbook ? config.EbookMetadataProfileId : null,
                            config.CreateEbook ? config.EbookMonitored : null,
                            config.CreateEbook ? config.EbookMonitorNewItems : null,
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

                return ApplyRequestedAuthorMonitoring(existing, config, requestedMediaType);
            }

            return await _authorLibraryService.AddAuthorAsync(authorProviderId, config);
        }

        private NzbDrone.Core.Books.Author ApplyRequestedAuthorMonitoring(
            NzbDrone.Core.Books.Author author,
            MonitoringConfig config,
            BookMediaType mediaType)
        {
            if (author == null || config == null)
            {
                return author;
            }

            var monitored = mediaType == BookMediaType.Audiobook ? config.AudiobookMonitored : config.EbookMonitored;
            var monitorNewItems = mediaType == BookMediaType.Audiobook ? config.AudiobookMonitorNewItems : config.EbookMonitorNewItems;
            if (!author.ApplyMediaTypeMonitoringSettings(mediaType, monitored, monitorNewItems))
            {
                return author;
            }

            return _authorService.UpdateAuthor(author);
        }

        private void ApplyCurrentBookMonitoring(
            NzbDrone.Core.Books.Author author,
            BookMediaType mediaType,
            MonitorTypes monitorMode,
            List<string> selectedProviderBookIds)
        {
            var books = _bookService.GetBooksByAuthor(author.Id) ?? new List<NzbDrone.Core.Books.Book>();
            var options = new MonitoringOptions
            {
                Monitor = monitorMode,
                MediaType = mediaType
            };

            if (monitorMode == MonitorTypes.SpecificBook)
            {
                options.BooksToMonitor = books
                    .Where(book => book.MediaType == mediaType && selectedProviderBookIds.Any(id => BookMatchesProviderId(book, id)))
                    .Select(book => book.Id.ToString())
                    .ToList();

                if (!options.BooksToMonitor.Any())
                {
                    _logger.Warn("AddSeries: none of the requested {0} books were present for author {1}; leaving current monitoring unchanged", mediaType, author.Id);
                    return;
                }
            }

            _bookMonitoredService.SetBookMonitoredStatus(author, options);
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
        // One-time action for current catalog rows. It is not persisted as an
        // author monitoring policy.
        public string Monitor { get; set; }
        public MonitorTypes? AudiobookMonitorExistingMode { get; set; }
        public MonitorTypes? EbookMonitorExistingMode { get; set; }
        public bool? AudiobookMonitored { get; set; }
        public NewItemMonitorTypes? AudiobookMonitorNewItems { get; set; }
        public bool? EbookMonitored { get; set; }
        public NewItemMonitorTypes? EbookMonitorNewItems { get; set; }
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

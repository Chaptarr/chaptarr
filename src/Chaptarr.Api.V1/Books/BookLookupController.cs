using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Chaptarr.Api.V1;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using Swashbuckle.AspNetCore.Annotations;

namespace Chaptarr.Api.V1.Books
{
    [V1ApiController("book/lookup")]
    public class BookLookupController : Controller
    {
        private readonly ISearchForNewBook _searchProxy;
        private readonly IMapCoversToLocal _coverMapper;
        private readonly IMediaCoverProxy _mediaCoverProxy;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IProviderAliasService _providerAliasService;
        private readonly Logger _logger;

        public BookLookupController(
            ISearchForNewBook searchProxy,
            IMapCoversToLocal coverMapper,
            IBookService bookService,
            IEditionService editionService,
            IMediaFileService mediaFileService,
            IMediaCoverProxy mediaCoverProxy,
            IProviderAliasService providerAliasService = null,
            Logger logger = null)
        {
            _searchProxy = searchProxy;
            _coverMapper = coverMapper;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _mediaCoverProxy = mediaCoverProxy;
            _providerAliasService = providerAliasService;
            _logger = logger ?? LogManager.GetCurrentClassLogger();
        }

        [HttpGet]
        [ProducesResponseType(typeof(List<BookResource>), 200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        [ProducesResponseType(typeof(ApiErrorResource), 503)]
        public ActionResult<List<BookResource>> Search(
            [FromQuery, SwaggerParameter("Title search or provider ID. hc: and gr: select works; hc-ed:, hc:edition: and gr-ed: select editions. Native edition:N retains legacy Goodreads edition lookup. Facade work:N and edition:N use the route's provider dialect.", Required = true)] string term,
            [FromQuery, SwaggerParameter("Optional audiobook or ebook scope. A Readarr facade's media scope takes precedence. Omitted native scope returns all eligible media rows and copies.")] string mediaType = null)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                return BadRequest(new ApiErrorResource { Error = "term is required" });
            }

            var facadeContext = HttpContext.GetReadarrFacadeContext();
            if (facadeContext == null && ReadarrFacadeProviderIdTranslator.IsBareWorkTerm(term))
            {
                return BadRequest(new ApiErrorResource
                {
                    Error = ReadarrFacadeProviderIdTranslator.ProviderPrefixRequiredMessage("term")
                });
            }

            term = ReadarrFacadeProviderIdTranslator.NormalizeLookupTerm(term, facadeContext);

            var requestedMediaType = MediaTypeParameterParser.ParseOptional(facadeContext?.MediaType ?? mediaType);

            // 1) Try provider-prefixed ID local lookup first (e.g., hc:123, gr:456, ol:OL123, gb:xyz)
            var loadedLocalBooks = new List<Book>();
            var localMatches = LookupLocalByProviderOrEdition(term, requestedMediaType, loadedLocalBooks);

            // If a caller is explicitly requesting a mediaType, ignore local matches of the wrong type
            // and fall back to remote lookup instead.
            if (requestedMediaType.HasValue && localMatches != null)
            {
                localMatches = localMatches.Where(b => b.MediaType == requestedMediaType.Value).ToList();
                if (!localMatches.Any())
                {
                    localMatches = null;
                }
            }

            if (facadeContext != null && localMatches?.Count > 1)
            {
                // Generic clients consume the first result. Reuse a selected copy instead
                // of monitoring another one merely because its local ID sorts first.
                var preferred = BookIdentity.SelectRequestedBook(localMatches);
                localMatches = localMatches.OrderByDescending(book => book.Id == preferred.Id).ThenBy(book => book.Id).ToList();
            }

            // 2) Fall back to existing remote search path if nothing local
            var searchResults = localMatches ?? _searchProxy.SearchForNewBook(term, null, mediaType: requestedMediaType);

            // Apply mediaType filter to remote results
            if (requestedMediaType.HasValue)
            {
                searchResults = searchResults.Where(b => b.MediaType == requestedMediaType.Value).ToList();
            }

            var resources = MapToResource(searchResults, facadeContext, loadedLocalBooks).ToList();
            BookResourceMapper.WarnFacadeIdentityGaps(resources, facadeContext, "book lookup response");
            return resources;
        }

        private List<NzbDrone.Core.Books.Book> LookupLocalByProviderOrEdition(string term, BookMediaType? requestedMediaType, List<Book> loadedLocalBooks)
        {
            var requestedTerm = term;

            // Supports canonical prefixes: hc, gr, ol, gb, az, isbn.
            try
            {
                var explicitEdition = BookEditionIdentity.IsExplicitEditionProviderId(term);
                term = BookEditionIdentity.NormalizeRequestedEditionProviderId(term);
                var idx = term.IndexOf(':');
                if (idx <= 0)
                {
                    return null;
                }

                var prefix = term.Substring(0, idx).Trim().ToLowerInvariant();
                var id = term.Substring(idx + 1).Trim();
                if (string.IsNullOrEmpty(id))
                {
                    return null;
                }

                string provider = prefix switch
                {
                    "hc" => "hc",
                    "gr" => "gr",
                    "ol" => "ol",
                    "gb" => "gb",
                    "az" => "az",
                    "isbn" => "isbn",
                    _ => null
                };

                if ((prefix is "hc" or "gr") && !explicitEdition)
                {
                    return LookupLocalByProvider(provider, $"{prefix}:{id}", requestedMediaType, loadedLocalBooks);
                }

                var editionBookIds = new List<int>();
                if (prefix is "hc" or "gr" or "ol" or "gb" or "az" or "isbn")
                {
                    editionBookIds = (_editionService.GetEditionsByProviderAndId(prefix, id) ?? new List<Edition>())
                        .Where(edition => edition != null && edition.BookId > 0).Select(edition => edition.BookId).Distinct().ToList();
                    if (editionBookIds.Any())
                    {
                        var books = GetExistingLookupMatches(editionBookIds, requestedMediaType, requestedTerm, loadedLocalBooks);
                        if (books?.Any() == true)
                        {
                            return books;
                        }
                    }
                }

                if (explicitEdition)
                {
                    var aliasIds = _providerAliasService?.FindBookIds("edition",
                        BookEditionIdentity.GetEditionProviderIds(new Edition { ForeignEditionId = term }))?.Except(editionBookIds).ToList();
                    return aliasIds?.Count > 0
                        ? GetExistingLookupMatches(aliasIds, requestedMediaType, requestedTerm, loadedLocalBooks)
                        : null;
                }

                if (provider != null)
                {
                    var found = LookupLocalByProvider(provider, $"{prefix}:{id}", requestedMediaType, loadedLocalBooks);
                    if (found?.Any() == true)
                    {
                        return HydrateLocalLookupBooks(found, loadedLocalBooks);
                    }
                }
            }
            catch (NzbDroneClientException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Local book lookup failed for '{0}'", requestedTerm);
                throw new NzbDroneClientException(HttpStatusCode.ServiceUnavailable,
                    "The local library lookup could not be completed. Please retry.");
            }

            return null;
        }

        private List<Book> LookupLocalByProvider(string provider, string providerId, BookMediaType? requestedMediaType, List<Book> loadedLocalBooks)
        {
            if (provider is "hc" or "gr")
            {
                var key = (Scope: "work", Provider: provider, Id: providerId);
                var incomplete = false;
                var ids = _bookService.FindBookIdsByProviderIds(new[] { key }, _ => incomplete = true)[key];
                if (incomplete)
                {
                    throw new NzbDroneClientException(HttpStatusCode.ServiceUnavailable,
                        "The local library lookup could not be completed. Please retry.");
                }
                return GetExistingLookupMatches(ids, requestedMediaType, providerId, loadedLocalBooks)?.OrderBy(book => book.Id).ToList();
            }

            var formats = requestedMediaType.HasValue ? new[] { requestedMediaType.Value } : new[] { BookMediaType.Audiobook, BookMediaType.Ebook };
            return formats.SelectMany(mediaType => _bookService.FindAllByProviderId(provider, providerId, mediaType) ?? new List<Book>())
                .Where(book => book != null).DistinctBy(book => book.Id).OrderBy(book => book.Id).ToList();
        }

        private List<Book> GetExistingLookupMatches(IEnumerable<int> ids, BookMediaType? requestedMediaType, string requestedTerm, List<Book> loadedLocalBooks)
        {
            var requestedIds = ids.Distinct().ToList();
            var books = _bookService.GetExistingBooks(requestedIds);
            var missingCount = requestedIds.Count - books.Count;
            if (missingCount > 0)
            {
                _logger.Warn("Local book lookup for '{0}' skipped {1} missing local book rows; retaining valid matches or falling back to metadata search",
                    requestedTerm, missingCount);
            }

            HydrateLocalLookupBooks(books, loadedLocalBooks);
            var matches = books.Where(book => !requestedMediaType.HasValue || book.MediaType == requestedMediaType.Value).ToList();
            return matches.Count > 0 ? matches : null;
        }

        private List<Book> HydrateLocalLookupBooks(List<Book> books, List<Book> loadedLocalBooks)
        {
            if (books?.Count is not > 0)
            {
                return null;
            }

            var ids = books.Select(book => book.Id).ToList();
            var missingEditions = books.Where(book => book.Editions == null).Select(book => book.Id).ToList();
            var editions = missingEditions.Count > 0
                ? _editionService.GetEditionsByBook(missingEditions).ToLookup(edition => edition.BookId)
                : null;
            var files = _mediaFileService.GetFilesByBooks(ids).ToLookup(file => file.Edition.BookId);
            foreach (var book in books)
            {
                book.Editions ??= editions[book.Id].ToList();
                book.BookFiles = files[book.Id].ToList();
            }

            loadedLocalBooks.AddRange(books);
            return books.OrderBy(book => book.Id).ToList();
        }

        private IEnumerable<BookResource> MapToResource(IEnumerable<NzbDrone.Core.Books.Book> books, ReadarrFacadeContext facadeContext, IEnumerable<Book> loadedLocalBooks = null)
        {
            var resources = new List<BookResource>();
            var bookList = books.ToList();
            var localBooks = BookLocalInstanceLookup.FindExistingBooks(_bookService, _providerAliasService, bookList, loadedLocalBooks);

            foreach (var currentBook in bookList)
            {
                var resource = currentBook.ToResource(new BookResourceMappingOptions { FacadeContext = facadeContext });

                resource.LocalAudiobookBooks = localBooks[currentBook].Where(book => book.MediaType == BookMediaType.Audiobook)
                    .Select(x => x.ToLocalInstanceResource())
                    .Where(x => x != null)
                    .ToList();
                resource.LocalEbookBooks = localBooks[currentBook].Where(book => book.MediaType == BookMediaType.Ebook)
                    .Select(x => x.ToLocalInstanceResource())
                    .Where(x => x != null)
                    .ToList();

                // Readarr/Seerr: lookup responses must include editions so clients can post them back on add.
                resource.Editions = currentBook.Editions?.ToResource(facadeContext) ?? new List<EditionResource>();
                foreach (var edition in resource.Editions)
                {
                    // Lookup/edition-choice rows need their own edition art. Mapping
                    // every edition through the book's one local cover collapses all
                    // choices to the monitored edition's image.
                    _mediaCoverProxy.ProxyRemoteUrls(edition.Images);
                }

                var monitoredEdition = currentBook.Editions?.FirstOrDefault(x => x.Monitored) ??
                                       currentBook.Editions?.FirstOrDefault();
                var cover = monitoredEdition?.Images?.FirstOrDefault(c => c.CoverType == MediaCoverTypes.Cover);

                if (cover != null)
                {
                    resource.RemoteCover = cover.Url;
                }

                resources.Add(resource);
            }

            if (resources.Count > 0)
            {
                _coverMapper.ConvertBookCoversToLocalUrls(resources.Select(resource =>
                    new KeyValuePair<int, List<MediaCover>>(resource.Id, resource.Images)));
            }

            return resources;
        }
    }
}

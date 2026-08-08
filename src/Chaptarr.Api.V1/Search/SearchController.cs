using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.Series;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Organizer;

namespace Chaptarr.Api.V1.Search
{
    [V1ApiController]
    public class SearchController : Controller
    {
        private readonly ISearchForNewEntity _searchProxy;
        private readonly IBuildFileNames _fileNameBuilder;
        private readonly IMapCoversToLocal _coverMapper;
        private readonly IMediaCoverProxy _mediaCoverProxy;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IProviderAliasService _providerAliasService;

        public SearchController(ISearchForNewEntity searchProxy, IBuildFileNames fileNameBuilder, IMapCoversToLocal coverMapper, IMediaCoverProxy mediaCoverProxy, IAuthorService authorService, IBookService bookService, IProviderAliasService providerAliasService = null)
        {
            _searchProxy = searchProxy;
            _fileNameBuilder = fileNameBuilder;
            _coverMapper = coverMapper;
            _mediaCoverProxy = mediaCoverProxy;
            _authorService = authorService;
            _bookService = bookService;
            _providerAliasService = providerAliasService;
        }

        [HttpGet]
        [ProducesResponseType(typeof(List<SearchResource>), 200)]
        public ActionResult<List<SearchResource>> Search([FromQuery] string term, [FromQuery] string provider = null)
        {
            // Add logging to trace the search request
            var logger = NLog.LogManager.GetCurrentClassLogger();
            logger.Info($"[SEARCH API] Called with term: '{term}', provider: '{provider ?? "null"}'");

            try
            {
                var searchResults = string.IsNullOrEmpty(provider)
                    ? _searchProxy.SearchForNewEntity(term)
                    : _searchProxy.SearchForNewEntity(term, provider);

                logger.Info($"[SEARCH API] Returned {searchResults?.Count ?? 0} results");

                if (searchResults?.Count > 0)
                {
                    var firstResult = searchResults.First();
                    if (firstResult is NzbDrone.Core.Books.Author author)
                    {
                        logger.Info($"[SEARCH API] First result is Author: {author.Name} (ID: {author.Id})");
                    }
                    else if (firstResult is NzbDrone.Core.Books.Book book)
                    {
                        logger.Info($"[SEARCH API] First result is Book: {book.Title} (ID: {book.Id.ToString()})");
                    }
                    else if (firstResult is NzbDrone.Core.Books.Series series)
                    {
                        logger.Info($"[SEARCH API] First result is Series: {series.Title} (ID: {series.Id.ToString()})");
                    }
                }

                var facadeContext = HttpContext.GetReadarrFacadeContext();
                var resources = MapToResource(searchResults, provider, facadeContext).ToList();
                AuthorResourceMapper.WarnFacadeIdentityGaps(resources.Select(resource => resource.Author), facadeContext, "search response");
                BookResourceMapper.WarnFacadeIdentityGaps(resources.Select(resource => resource.Book), facadeContext, "search response");
                return resources;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"[SEARCH API] Error during search for term: '{term}', provider: '{provider}'");
                throw;
            }
        }

        private IEnumerable<SearchResource> MapToResource(IEnumerable<object> results, string provider = null, ReadarrFacadeContext facadeContext = null)
        {
            var isAudiobookProvider = string.Equals(provider, "audible", StringComparison.OrdinalIgnoreCase);
            var id = 1;
            foreach (var result in results)
            {
                var resource = new SearchResource();
                resource.Id = id++;

                if (result is NzbDrone.Core.Books.Author author)
                {
                    resource.Author = author.ToResource(facadeContext);

                    // Cross-reference against local library to detect existing authors
                    var existingAuthor = FindExistingAuthor(author);
                    if (existingAuthor != null)
                    {
                        resource.Author.Id = existingAuthor.Id;
                        resource.Author.TitleSlug = existingAuthor.TitleSlug;
                        resource.Author.SelectedPosterHash = existingAuthor.SelectedPosterHash;
                    }

                    resource.ForeignId = resource.Author.ForeignAuthorId;
                    resource.ProviderId = resource.Author.ForeignAuthorId;
                    resource.ExistingLocalId = existingAuthor?.Id;

                    // For search results, use MediaCoverProxy to proxy remote images
                    if (resource.Author.Id == 0 && resource.Author.Images != null)
                    {
                        foreach (var image in resource.Author.Images)
                        {
                            if (!string.IsNullOrWhiteSpace(image.Url))
                            {
                                image.Url = _mediaCoverProxy.RegisterUrl(image.Url);
                            }
                        }
                    }
                    else
                    {
                        _coverMapper.ConvertToLocalUrls(resource.Author.Id, MediaCoverEntity.Author, resource.Author.Images, resource.Author.SelectedPosterHash);
                    }

                    var poster = author.Images.FirstOrDefault(c => c.CoverType == MediaCoverTypes.Poster);

                    if (poster != null)
                    {
                        resource.Author.RemotePoster = poster.Url;
                    }

                    // Only set folder for authors that have been saved to the database (ID > 0)
                    // Search results don't have IDs yet, so skip folder generation
                    if (existingAuthor != null)
                    {
                        resource.Author.Folder = _fileNameBuilder.GetAuthorFolder(existingAuthor);
                    }
                    else if (author.Id > 0)
                    {
                        resource.Author.Folder = _fileNameBuilder.GetAuthorFolder(author);
                    }
                }
                else if (result is NzbDrone.Core.Books.Book book)
                {
                    resource.Book = book.ToResource(new BookResourceMappingOptions { FacadeContext = facadeContext });

                    resource.Book.LocalAudiobookBooks = FindExistingBooks(book, BookMediaType.Audiobook)
                        .Select(x => x.ToLocalInstanceResource())
                        .Where(x => x != null)
                        .ToList();
                    resource.Book.LocalEbookBooks = FindExistingBooks(book, BookMediaType.Ebook)
                        .Select(x => x.ToLocalInstanceResource())
                        .Where(x => x != null)
                        .ToList();

                    var monitoredEdition = book.Editions.FirstOrDefault(x => x.Monitored);
                    if (monitoredEdition != null)
                    {
                        resource.Book.Overview = monitoredEdition.Overview;
                    }
                    resource.Book.Author = book.Author.ToResource(facadeContext);

                    // Cross-reference book's author against local library
                    var existingBookAuthor = book.Author != null ? FindExistingAuthor(book.Author) : null;
                    if (existingBookAuthor != null)
                    {
                        resource.Book.Author.Id = existingBookAuthor.Id;
                        resource.Book.Author.TitleSlug = existingBookAuthor.TitleSlug;
                    }

                    resource.Book.Editions = book.Editions.ToResource(facadeContext);

                    if (isAudiobookProvider &&
                        string.IsNullOrWhiteSpace(resource.Book.ForeignBookId) &&
                        BookEditionIdentity.GetAsin(book) is string asin &&
                        !string.IsNullOrWhiteSpace(asin))
                    {
                        resource.Book.ForeignBookId = $"az:{asin.Trim().ToUpperInvariant()}";
                    }

                    // Use the book resource's ForeignBookId which contains the provider ID (e.g., "hc:495645")
                    resource.ForeignId = resource.Book.ForeignBookId;
                    resource.ProviderId = resource.Book.ForeignBookId;

                    var localBookIds = resource.Book.LocalAudiobookBooks
                        .Concat(resource.Book.LocalEbookBooks)
                        .Select(x => x.Id)
                        .Distinct()
                        .ToList();
                    resource.ExistingLocalId = localBookIds.Count == 1 ? localBookIds[0] : (int?)null;

                    // For search results, use MediaCoverProxy to proxy remote images
                    if (resource.Book.Id == 0 && resource.Book.Images != null)
                    {
                        foreach (var image in resource.Book.Images)
                        {
                            if (!string.IsNullOrWhiteSpace(image.Url))
                            {
                                image.Url = _mediaCoverProxy.RegisterUrl(image.Url);
                            }
                        }
                    }
                    else
                    {
                        _coverMapper.ConvertToLocalUrls(resource.Book.Id, MediaCoverEntity.Book, resource.Book.Images);
                    }

                    var cover = book.Editions.FirstOrDefault(x => x.Monitored)?.Images?.FirstOrDefault(c => c.CoverType == MediaCoverTypes.Cover);

                    if (cover != null)
                    {
                        resource.Book.RemoteCover = cover.Url;
                    }

                    // Only set folder for authors that have been saved to the database (ID > 0)
                    // Search results don't have IDs yet, so skip folder generation
                    if (existingBookAuthor != null)
                    {
                        resource.Book.Author.Folder = _fileNameBuilder.GetAuthorFolder(existingBookAuthor);
                    }
                    else if (book.Author?.Id > 0)
                    {
                        resource.Book.Author.Folder = _fileNameBuilder.GetAuthorFolder(book.Author);
                    }
                }
                else if (result is NzbDrone.Core.Books.Series series)
                {
                    resource.Series = series.ToResource();
                    string providerForeignId = null;
                    if (!string.IsNullOrWhiteSpace(series.HardcoverSeriesId))
                    {
                        providerForeignId = series.HardcoverSeriesId; // already includes "hc:" prefix
                    }
                    else if (!string.IsNullOrWhiteSpace(series.GoodreadsSeriesId))
                    {
                        providerForeignId = series.GoodreadsSeriesId; // already includes "gr:" prefix
                    }
                    else if (!string.IsNullOrWhiteSpace(series.OpenLibrarySeriesId))
                    {
                        providerForeignId = series.OpenLibrarySeriesId; // already includes "ol:" prefix
                    }

                    resource.ForeignId = providerForeignId;
                    resource.ProviderId = providerForeignId;
                    resource.ExistingLocalId = series.Id > 0 ? series.Id : (int?)null;

                    // Get first 3 book covers from the series for stacked display
                    if (series.Books?.Count > 0)
                    {
                        var bookCovers = series.Books
                            .Where(book => book.Editions?.Any(e => e.Images?.Any(img => img.CoverType == MediaCoverTypes.Cover) == true) == true)
                            .SelectMany(book => book.Editions
                                .Where(e => e.Images?.Any(img => img.CoverType == MediaCoverTypes.Cover) == true)
                                .Select(e => e.Images.First(img => img.CoverType == MediaCoverTypes.Cover)))
                            .Take(3)
                            .ToList();

                        if (bookCovers.Any())
                        {
                            resource.Series.Images = bookCovers;

                            // For search results, proxy the remote images
                            foreach (var image in resource.Series.Images)
                            {
                                if (!string.IsNullOrWhiteSpace(image.Url))
                                {
                                    image.Url = _mediaCoverProxy.RegisterUrl(image.Url);
                                }
                            }
                        }
                    }

                    // If we already have images (e.g. from SeriesBooks cover URLs), proxy them for search results
                    if (series.Id == 0 && resource.Series?.Images != null)
                    {
                        foreach (var image in resource.Series.Images)
                        {
                            if (!string.IsNullOrWhiteSpace(image.Url) &&
                                image.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                                !image.Url.Contains("/MediaCoverProxy/", StringComparison.OrdinalIgnoreCase))
                            {
                                image.Url = _mediaCoverProxy.RegisterUrl(image.Url);
                            }
                        }
                    }
                }
                else
                {
                    throw new NotImplementedException($"Bad response from search all proxy - unknown type: {result?.GetType()?.Name ?? "null"}");
                }

                yield return resource;
            }
        }

        private NzbDrone.Core.Books.Author FindExistingAuthor(NzbDrone.Core.Books.Author searchAuthor)
        {
            if (_providerAliasService != null)
            {
                try
                {
                    var aliasAuthorId = _providerAliasService.FindAuthorIds(GetAuthorProviderAliasLookups(searchAuthor))
                        .OrderBy(id => id)
                        .FirstOrDefault();

                    if (aliasAuthorId > 0)
                    {
                        return _authorService.GetAuthor(aliasAuthorId);
                    }
                }
                catch
                {
                    // Ignore alias-index lookup failures; legacy single-field lookup below remains as fallback.
                }
            }

	            var lookups = new (string Provider, string Id)[]
	            {
	                ("hc", searchAuthor.HardcoverAuthorId),
	                ("gr", searchAuthor.GoodreadsAuthorId),
	                ("ol", searchAuthor.OpenLibraryAuthorId),
	                ("gb", searchAuthor.GoogleBooksAuthorId),
	                ("az", searchAuthor.AudnexusAuthorId)
	            };

            foreach (var (provider, id) in lookups)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                try
                {
                    var existing = _authorService.FindByProviderId(provider, id);
                    if (existing != null)
                    {
                        return existing;
                    }
                }
                catch
                {
                    // Ignore lookup failures
                }
            }

            return null;
        }

        private static List<string> GetAuthorProviderAliasLookups(NzbDrone.Core.Books.Author searchAuthor)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string id)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id.Trim());
                }
            }

            Add(searchAuthor?.HardcoverAuthorId);
            Add(searchAuthor?.GoodreadsAuthorId);
            Add(searchAuthor?.OpenLibraryAuthorId);
            Add(searchAuthor?.GoogleBooksAuthorId);
            Add(searchAuthor?.AudnexusAuthorId);

            if (searchAuthor?.RemoteProviderIds != null)
            {
                foreach (var id in searchAuthor.RemoteProviderIds)
                {
                    Add(id);
                }
            }

            return ids.ToList();
        }

        private List<NzbDrone.Core.Books.Book> FindExistingBooks(NzbDrone.Core.Books.Book searchBook, BookMediaType mediaType)
        {
            return BookLocalInstanceLookup.FindExistingBooks(_bookService, _providerAliasService, searchBook, mediaType);
        }
    }
}

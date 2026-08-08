using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;

namespace NzbDrone.Api.V1.Editions
{
    [V1ApiController]
    public class EditionController : Controller
    {
        private readonly IEditionService _editionService;
        private readonly IBookService _bookService;
        private readonly IMediaCoverProxy _mediaCoverProxy;

        public EditionController(IEditionService editionService,
                                 IBookService bookService,
                                 IMediaCoverProxy mediaCoverProxy)
        {
            _editionService = editionService;
            _bookService = bookService;
            _mediaCoverProxy = mediaCoverProxy;
        }

        [HttpGet]
        public List<EditionResource> GetEditions([FromQuery] List<int> bookId)
        {
            var requestedBookIds = bookId?
                .Where(id => id > 0)
                .Distinct()
                .ToList() ?? new List<int>();

            var editions = _editionService.GetEditionsByBook(requestedBookIds);
            var resources = editions.ToResource(HttpContext.GetReadarrFacadeContext());

            foreach (var resource in resources)
            {
                _mediaCoverProxy.ProxyRemoteUrls(resource.Images);
            }

            AnnotateAudiobookMonitoringStatus(resources, editions, requestedBookIds);

            return resources;
        }

        private void AnnotateAudiobookMonitoringStatus(List<EditionResource> resources, List<Edition> editions, List<int> requestedBookIds)
        {
            if (resources == null || resources.Count == 0 || editions == null || editions.Count == 0 || requestedBookIds == null || requestedBookIds.Count == 0)
            {
                return;
            }

            var requestedBooks = _bookService.GetBooks(requestedBookIds)
                .Where(book => book != null)
                .ToList();

            if (!requestedBooks.Any())
            {
                return;
            }

            var requestedAuthorIds = requestedBooks
                .Select(book => book.AuthorId)
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            var allAudiobookBooks = requestedAuthorIds
                .SelectMany(authorId => _bookService.GetBooksByAuthor(authorId) ?? new List<Book>())
                .Where(book => book != null && book.MediaType == BookMediaType.Audiobook)
                .GroupBy(book => book.Id)
                .Select(group => group.First())
                .ToList();

            var monitoredAudiobookBooks = allAudiobookBooks
                .Where(book => book.AudiobookMonitored)
                .ToList();

            var monitoredEditions = _editionService.GetEditionsByBook(monitoredAudiobookBooks.Select(book => book.Id))
                .Where(edition => edition != null && edition.Monitored)
                .ToList();

            var monitoredEditionsByBookId = monitoredEditions
                .GroupBy(edition => edition.BookId)
                .ToDictionary(group => group.Key, group => group.ToList());

            var relatedAudiobookBookIdsByRequestedBookId = requestedBooks
                .ToDictionary(
                    requestedBook => requestedBook.Id,
                    requestedBook => allAudiobookBooks
                        .Where(book => book.Id != requestedBook.Id && WorkIdMatcher.WorkIdMatches(requestedBook, book))
                        .Select(book => book.Id)
                        .ToHashSet());

            var resourceByEditionId = resources
                .Where(resource => resource != null)
                .ToDictionary(resource => resource.Id);

            var requestedBookById = requestedBooks.ToDictionary(book => book.Id);

            foreach (var edition in editions.Where(edition => edition != null))
            {
                if (!resourceByEditionId.TryGetValue(edition.Id, out var resource) ||
                    !requestedBookById.TryGetValue(edition.BookId, out var requestedBook))
                {
                    continue;
                }

                var requestedEditionProviderIds = BookEditionIdentity.GetEditionProviderIds(edition);
                if (!requestedEditionProviderIds.Any())
                {
                    continue;
                }

                if (!relatedAudiobookBookIdsByRequestedBookId.TryGetValue(requestedBook.Id, out var relatedAudiobookBookIds))
                {
                    continue;
                }

                foreach (var relatedBookId in relatedAudiobookBookIds)
                {
                    if (!monitoredEditionsByBookId.TryGetValue(relatedBookId, out var relatedMonitoredEditions))
                    {
                        continue;
                    }

                    if (relatedMonitoredEditions.Any(monitoredEdition =>
                        BookEditionIdentity.GetEditionProviderIds(monitoredEdition)
                            .Intersect(requestedEditionProviderIds, StringComparer.OrdinalIgnoreCase)
                            .Any()))
                    {
                        resource.MonitoredByAnotherAudiobookBook = true;
                        break;
                    }
                }
            }
        }
    }
}

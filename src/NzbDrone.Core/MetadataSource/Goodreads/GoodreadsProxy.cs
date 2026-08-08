using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Http;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.MetadataSource.Goodreads
{
    public interface IGoodreadsProxy
    {
        Book GetBookInfo(string foreignEditionId, bool useCache = true);
    }

    public class GoodreadsProxy : IGoodreadsProxy, IProvideSeriesInfo, IProvideListInfo
    {
        private readonly ICachedHttpResponseService _cachedHttpClient;
        private readonly Logger _logger;
        private readonly IHttpRequestBuilderFactory _requestBuilder;

        public GoodreadsProxy(ICachedHttpResponseService cachedHttpClient,
                              Logger logger)
        {
            _cachedHttpClient = cachedHttpClient;
            _logger = logger;

            _requestBuilder = new HttpRequestBuilder("https://www.goodreads.com/{route}")
                .AddQueryParam("key", new string("gSuM2Onzl6sjMU25HY1Xcd".Reverse().ToArray()))
                .AddQueryParam("_nc", "1")
                .SetHeader("User-Agent", GoodreadsAndroidUserAgent.GetRandom())
                .KeepAlive()
                .CreateFactory();
        }

        public SeriesResource GetSeriesInfo(int foreignSeriesId, bool useCache = true)
        {
            _logger.Debug("Getting Series with GoodreadsId of {0}", foreignSeriesId);

            var httpRequest = _requestBuilder.Create()
                .SetSegment("route", $"series/{foreignSeriesId}")
                .AddQueryParam("format", "xml")
                .Build();

            httpRequest.AllowAutoRedirect = true;
            httpRequest.SuppressHttpError = true;

            var httpResponse = _cachedHttpClient.Get(httpRequest, useCache, TimeSpan.FromDays(7));

            if (httpResponse.HasHttpError)
            {
                if (httpResponse.StatusCode == HttpStatusCode.BadRequest)
                {
                    throw new BadRequestException(foreignSeriesId.ToString());
                }
                else
                {
                    throw new HttpException(httpRequest, httpResponse);
                }
            }

            var resource = httpResponse.Deserialize<ShowSeriesResource>();

            return resource.Series;
        }

        public ListResource GetListInfo(int foreignListId, int page, bool useCache = true)
        {
            _logger.Debug("Getting List with GoodreadsId of {0}", foreignListId);

            var httpRequest = new HttpRequestBuilder("https://www.goodreads.com/book/list/listopia.xml")
                .AddQueryParam("key", new string("whFzJP3Ud0gZsAdyXxSr7T".Reverse().ToArray()))
                .AddQueryParam("_nc", "1")
                .AddQueryParam("format", "xml")
                .AddQueryParam("id", foreignListId)
                .AddQueryParam("items_per_page", 30)
                .AddQueryParam("page", page)
                .SetHeader("User-Agent", GoodreadsAndroidUserAgent.GetRandom())
                .KeepAlive()
                .Build();

            httpRequest.AllowAutoRedirect = true;
            httpRequest.SuppressHttpError = true;

            var httpResponse = _cachedHttpClient.Get(httpRequest, useCache, TimeSpan.FromDays(7));

            if (httpResponse.HasHttpError)
            {
                if (httpResponse.StatusCode == HttpStatusCode.BadRequest)
                {
                    throw new BadRequestException(foreignListId.ToString());
                }
                else
                {
                    throw new HttpException(httpRequest, httpResponse);
                }
            }

            return httpResponse.Deserialize<ListResource>();
        }

        public Book GetBookInfo(string foreignEditionId, bool useCache = true)
        {
            _logger.Debug("Getting Book with GoodreadsId of {0}", foreignEditionId);

            var httpRequest = _requestBuilder.Create()
                .SetSegment("route", $"api/book/basic_book_data/{foreignEditionId}")
                .AddQueryParam("format", "xml")
                .Build();

            httpRequest.AllowAutoRedirect = true;
            httpRequest.SuppressHttpError = true;

            var httpResponse = _cachedHttpClient.Get(httpRequest, useCache, TimeSpan.FromDays(90));

            if (httpResponse.HasHttpError)
            {
                if (httpResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new BookNotFoundException(foreignEditionId);
                }
                else if (httpResponse.StatusCode == HttpStatusCode.BadRequest)
                {
                    throw new BadRequestException(foreignEditionId);
                }
                else
                {
                    throw new HttpException(httpRequest, httpResponse);
                }
            }

            var resource = httpResponse.Deserialize<BookResource>();

            var book = MapBook(resource);
            book.CleanTitle = Parser.Parser.CleanAuthorName(book.Title);

            var authorMetadata = resource.Authors.SelectList(MapAuthor);
            // Convert AuthorMetadata to Author
            var firstAuthorMetadata = authorMetadata.First();
            book.Author = new Author
            {
                Name = firstAuthorMetadata.Name,
                GoodreadsAuthorId = firstAuthorMetadata.GoodreadsAuthorId,
                TitleSlug = firstAuthorMetadata.TitleSlug,
                SortName = firstAuthorMetadata.SortName,
                NameLastFirst = firstAuthorMetadata.NameLastFirst,
                SortNameLastFirst = firstAuthorMetadata.SortNameLastFirst,
                Status = firstAuthorMetadata.Status,
                Overview = firstAuthorMetadata.Overview,
                Images = firstAuthorMetadata.Images,
                Links = firstAuthorMetadata.Links,
                Ratings = firstAuthorMetadata.Ratings
            };

            return book;
        }

        private static Author MapAuthor(AuthorSummaryResource resource)
        {
            var author = new Author
            {
                GoodreadsAuthorId = resource.Id.ToString(),
                Name = resource.Name.CleanSpaces(),
                TitleSlug = resource.Id.ToString(),
                Images = new List<MediaCover.MediaCover>(),
                Links = new List<Links>()
            };

            author.SortName = author.Name.ToLower();
            author.NameLastFirst = author.Name.ToLastFirst();
            author.SortNameLastFirst = author.NameLastFirst.ToLower();

            if (resource.ImageUrl.IsValidHttpUrl() &&
                !MediaCover.MediaCoverRendition.IsKnownPlaceholderImageUrl(resource.ImageUrl))
            {
                author.Images.Add(new MediaCover.MediaCover
                {
                    CoverType = MediaCover.MediaCoverTypes.Poster,
                    Url = resource.ImageUrl
                });
            }

            if (!string.IsNullOrWhiteSpace(resource.Link))
            {
                author.Links.Add(new Links { Name = "goodreads", Url = resource.Link });
            }

            if (resource.RatingsCount.HasValue)
            {
                author.Ratings = new Ratings
                {
                    Votes = resource.RatingsCount ?? 0,
                    Value = resource.AverageRating ?? 0
                };
            }

            return author;
        }

        private static Book MapBook(BookResource resource)
        {
            var book = new Book
            {
                GoodreadsWorkId = ProviderIdHelper.Canonicalize(resource.Work.Id.ToString(), "gr"),
                GoodreadsBookId = ProviderIdHelper.Canonicalize(resource.Id.ToString(), "gr"),
                Title = (resource.Work.OriginalTitle ?? resource.TitleWithoutSeries).CleanSpaces(),
                TitleSlug = resource.Work.Id.ToString(),
                ReleaseDate = resource.Work.OriginalPublicationDate ?? resource.PublicationDate,
                Ratings = new Ratings { Votes = resource.Work.RatingsCount, Value = resource.Work.AverageRating },
                AnyEditionOk = true
            };

            if (resource.EditionsUrl != null)
            {
                book.Links.Add(new Links { Url = resource.EditionsUrl, Name = "Goodreads Editions" });
            }

            var edition = new Edition
            {
                ForeignEditionId = resource.Id.ToString(),
                TitleSlug = resource.Id.ToString(),
                Isbn13 = resource.Isbn13,
                Asin = resource.Asin ?? resource.KindleAsin,
                Title = resource.TitleWithoutSeries,
                Language = resource.LanguageCode,
                Overview = resource.Description,
                Format = resource.Format,
                IsEbook = resource.IsEbook,
                Disambiguation = resource.EditionInformation,
                Publisher = resource.Publisher,
                PageCount = resource.Pages,
                ReleaseDate = resource.PublicationDate,
                Ratings = new Ratings { Votes = resource.RatingsCount, Value = resource.AverageRating },
                Monitored = true
            };

            edition.Links.Add(new Links { Url = resource.Url, Name = "Goodreads Book" });

            book.Editions = new List<Edition> { edition };

            Debug.Assert(!book.Editions.Any() || book.Editions.Count(x => x.Monitored) == 1, "one edition monitored");

            return book;
        }
    }
}

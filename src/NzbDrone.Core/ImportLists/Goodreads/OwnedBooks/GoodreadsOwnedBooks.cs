using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource.Goodreads;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.ImportLists.Goodreads
{
    public class GoodreadsOwnedBooksImportListSettings : GoodreadsSettingsBase<GoodreadsOwnedBooksImportListSettings>
    {
    }

    public class GoodreadsOwnedBooks : GoodreadsImportListBase<GoodreadsOwnedBooksImportListSettings>
    {
        public GoodreadsOwnedBooks(IImportListStatusService importListStatusService,
                                   IConfigService configService,
                                   IParsingService parsingService,
                                   IHttpClient httpClient,
                                   Logger logger)
        : base(importListStatusService, configService, parsingService, httpClient, logger)
        {
        }

        public override string Name => "Goodreads Owned Books";
        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(12);

        public override IList<ImportListItemInfo> Fetch()
        {
            var result = new List<ImportListItemInfo>();
            var userId = Settings.UserId;

            try
            {
                if (userId.IsNullOrWhiteSpace())
                {
                    userId = GetUser(null).Item1;
                    Settings.UserId = userId;
                }

                for (var page = 1; page <= 100; page++)
                {
                    var current = GetOwned(userId, page);
                    if (current.Count == 0)
                    {
                        break;
                    }

                    foreach (var ownedBook in current)
                    {
                        var book = ownedBook?.Book;
                        var author = book?.Authors?.FirstOrDefault();
                        if (author?.Name.IsNullOrWhiteSpace() != false || book.TitleWithoutSeries.IsNullOrWhiteSpace())
                        {
                            continue;
                        }

                        result.Add(new ImportListItemInfo
                        {
                            Author = author.Name.CleanSpaces(),
                            AuthorGoodreadsId = $"gr:{author.Id}",
                            Book = book.TitleWithoutSeries.CleanSpaces(),
                            BookGoodreadsId = book.WorkId.HasValue ? $"gr:{book.WorkId.Value}" : null,
                            EditionGoodreadsId = $"gr:{book.Id}"
                        });
                    }

                    if (current.Count < 100)
                    {
                        break;
                    }
                }

                _importListStatusService.RecordSuccess(Definition.Id);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Error fetching Goodreads owned books");
                _importListStatusService.RecordFailure(Definition.Id);
                return CleanupListItems(new List<ImportListItemInfo>());
            }

            return CleanupListItems(result);
        }

        private IReadOnlyList<OwnedBookResource> GetOwned(string userId, int page)
        {
            var builder = RequestBuilder()
                .SetSegment("route", "owned_books/user")
                .AddQueryParam("format", "xml")
                .AddQueryParam("id", userId)
                .AddQueryParam("page", page);

            var httpResponse = OAuthGet(builder);

            return httpResponse.Deserialize<PaginatedList<OwnedBookResource>>("owned_books")?.List ?? Array.Empty<OwnedBookResource>();
        }
    }
}

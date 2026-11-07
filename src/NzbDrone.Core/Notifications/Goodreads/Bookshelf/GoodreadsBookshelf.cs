using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.Goodreads;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Goodreads
{
    public class GoodreadsBookshelf : GoodreadsNotificationBase<GoodreadsBookshelfNotificationSettings>
    {
        public GoodreadsBookshelf(IHttpClient httpClient,
                              Logger logger)
        : base(httpClient, logger)
        {
        }

        public override string Name => "Goodreads Bookshelves";
        public override string Link => "https://goodreads.com/";

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            var importedBook = message.Book;
            var workId = NormalizeGoodreadsId(importedBook?.GoodreadsWorkId);
            var authorName = importedBook?.Author?.Name;
            if (workId.IsNullOrWhiteSpace() || authorName.IsNullOrWhiteSpace())
            {
                return;
            }

            foreach (var shelf in Settings.RemoveIds ?? Enumerable.Empty<string>())
            {
                // try to find the edition that we need to remove
                var listBooks = SearchShelf(shelf, authorName);
                var toRemove = listBooks.Where(x => x.Book.WorkId?.ToString() == workId);

                foreach (var listBook in toRemove)
                {
                    RemoveBookFromShelves(listBook.Book.Id, shelf);
                }
            }

            var selectedEdition = importedBook.Editions?.SingleOrDefault(x => x.Monitored);
            var bookId = selectedEdition?.GoodreadsEditionId?.ToString() ?? NormalizeGoodreadsId(selectedEdition?.ForeignEditionId);
            if (bookId.IsNotNullOrWhiteSpace())
            {
                AddToShelves(bookId, Settings.AddIds ?? Enumerable.Empty<string>());
            }
        }

        public override void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            if (deleteMessage.DeletedFiles)
            {
                foreach (var shelf in Settings.RemoveIds ?? Enumerable.Empty<string>())
                {
                    var listBooks = SearchShelf(shelf, deleteMessage.Author.Name);
                    var workIds = (deleteMessage.Author.Books ?? new List<Book>())
                        .Select(book => NormalizeGoodreadsId(book.GoodreadsWorkId))
                        .Where(id => id.IsNotNullOrWhiteSpace())
                        .ToHashSet(StringComparer.Ordinal);
                    var toRemove = listBooks.Where(x => x.Book.WorkId?.ToString() is { } id && workIds.Contains(id));

                    foreach (var listBook in toRemove)
                    {
                        RemoveBookFromShelves(listBook.Book.Id, shelf);
                    }
                }
            }
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            if (deleteMessage.DeletedFiles)
            {
                foreach (var shelf in Settings.RemoveIds ?? Enumerable.Empty<string>())
                {
                    var authorName = deleteMessage.Book.Author?.Name;
                    if (authorName.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    var listBooks = SearchShelf(shelf, authorName);
                    var workId = NormalizeGoodreadsId(deleteMessage.Book.GoodreadsWorkId);
                    var toRemove = listBooks.Where(x => x.Book.WorkId?.ToString() == workId);

                    foreach (var listBook in toRemove)
                    {
                        RemoveBookFromShelves(listBook.Book.Id, shelf);
                    }
                }
            }
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            foreach (var shelf in Settings.RemoveIds ?? Enumerable.Empty<string>())
            {
                var authorName = deleteMessage.Book.Author?.Name;
                if (authorName.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var listBooks = SearchShelf(shelf, authorName);
                var workId = NormalizeGoodreadsId(deleteMessage.Book.GoodreadsWorkId);
                var toRemove = listBooks.Where(x => x.Book.WorkId?.ToString() == workId);

                foreach (var listBook in toRemove)
                {
                    RemoveBookFromShelves(listBook.Book.Id, shelf);
                }
            }
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "getBookshelves")
            {
                if (Settings.AccessToken.IsNullOrWhiteSpace() || Settings.AccessTokenSecret.IsNullOrWhiteSpace())
                {
                    return new
                    {
                        shelves = new List<object>()
                    };
                }

                Settings.Validate().Filter("AccessToken").ThrowOnError();

                var shelves = new List<UserShelfResource>();
                for (var page = 1; page <= 100; page++)
                {
                    var curr = GetShelfList(page);
                    if (curr == null || curr.Count == 0)
                    {
                        break;
                    }

                    shelves.AddRange(curr);
                    if (curr.Count < 100)
                    {
                        break;
                    }
                }

                var helptext = new
                {
                    addIds = $"Add imported book to {Settings.UserName}'s shelves:",
                    removeIds = $"Remove imported book from {Settings.UserName}'s shelves:"
                };

                return new
                {
                    options = new
                    {
                        helptext,
                        user = Settings.UserName,
                        shelves = shelves.OrderBy(p => p.Name)
                        .Select(p => new
                        {
                            id = p.Name,
                            name = p.Name
                        })
                    }
                };
            }
            else
            {
                return base.RequestAction(action, query);
            }
        }

        private IReadOnlyList<UserShelfResource> GetShelfList(int page)
        {
            try
            {
                var builder = RequestBuilder()
                    .SetSegment("route", $"shelf/list.xml")
                    .AddQueryParam("user_id", Settings.UserId)
                    .AddQueryParam("per_page", 100)
                    .AddQueryParam("page", page);

                var httpResponse = OAuthExecute(builder);

                return httpResponse.Deserialize<PaginatedList<UserShelfResource>>("shelves")?.List ?? Array.Empty<UserShelfResource>();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Error fetching bookshelves from Goodreads");
                return new List<UserShelfResource>();
            }
        }

        private IReadOnlyList<ReviewResource> SearchShelf(string shelf, string query)
        {
            var results = new List<ReviewResource>();
            for (var page = 1; page <= 100; page++)
            {
                try
                {
                    var builder = RequestBuilder()
                        .SetSegment("route", $"review/list.xml")
                        .AddQueryParam("v", 2)
                        .AddQueryParam("id", Settings.UserId)
                        .AddQueryParam("shelf", shelf)
                        .AddQueryParam("per_page", 200)
                        .AddQueryParam("page", page)
                        .AddQueryParam("search[query]", query);

                    var httpResponse = OAuthExecute(builder);

                    var resource = httpResponse.Deserialize<PaginatedList<ReviewResource>>("reviews");

                    var current = resource?.List ?? Array.Empty<ReviewResource>();
                    results.AddRange(current);

                    if (current.Count < 200 || resource?.Pagination == null || resource.Pagination.End >= resource.Pagination.TotalItems)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Error fetching bookshelves from Goodreads");
                    return results;
                }
            }

            return results;
        }

        private static string NormalizeGoodreadsId(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            var normalized = value.Trim();
            if (normalized.StartsWith("gr:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[3..];
            }

            return long.TryParse(normalized, out var id) && id > 0 ? id.ToString() : string.Empty;
        }

        private void RemoveBookFromShelves(long bookId, string shelf)
        {
            var req = RequestBuilder()
                .Post()
                .SetSegment("route", "shelf/add_to_shelf.xml")
                .AddFormParameter("name", shelf)
                .AddFormParameter("book_id", bookId)
                .AddFormParameter("a", "remove");

            // in case not found in shelf
            req.SuppressHttpError = true;

            OAuthExecute(req);
        }

        private void AddToShelves(string bookId, IEnumerable<string> shelves)
        {
            var req = RequestBuilder()
                .Post()
                .SetSegment("route", "shelf/add_books_to_shelves.xml")
                .AddFormParameter("bookids", bookId)
                .AddFormParameter("shelves", shelves.ConcatToString());

            OAuthExecute(req);
        }
    }
}

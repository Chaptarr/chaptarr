using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Tags;

namespace Chaptarr.Api.V1.Calendar
{
    [V1FeedController("calendar")]
    public class CalendarFeedController : Controller
    {
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IEditionService _editionService;
        private readonly ITagService _tagService;

        public CalendarFeedController(IBookService bookService, IAuthorService authorService, IEditionService editionService, ITagService tagService)
        {
            _bookService = bookService;
            _authorService = authorService;
            _editionService = editionService;
            _tagService = tagService;
        }

        [HttpGet("Readarr.ics")]
        [HttpGet("Chaptarr.ics")]
        public IActionResult GetCalendarFeed(int pastDays = 7, int futureDays = 28, string tags = "", string tagList = "", bool unmonitored = false)
        {
            var isLegacyReadarrFeed = HttpContext?.Request?.Path.Value?.EndsWith("Readarr.ics", StringComparison.OrdinalIgnoreCase) == true;

            var start = DateTime.Today.AddDays(-pastDays);
            var end = DateTime.Today.AddDays(futureDays);
            var tagQuery = tags.IsNotNullOrWhiteSpace() ? tags : tagList;
            var parsedTags = new List<int>();

            if (tagQuery.IsNotNullOrWhiteSpace())
            {
                parsedTags.AddRange(tagQuery.Split(',').Select(_tagService.GetTag).Select(t => t.Id));
            }

            var books = _bookService.BooksBetweenDates(start, end, unmonitored);

            var authorIds = books.Where(book => book.AuthorId > 0).Select(book => book.AuthorId).Distinct().ToList();
            var authorsById = authorIds.Any()
                ? _authorService.GetAuthors(authorIds).ToDictionary(author => author.Id)
                : new Dictionary<int, NzbDrone.Core.Books.Author>();

            var bookIds = books.Where(book => book.Id > 0).Select(book => book.Id).Distinct().ToList();
            var editionsByBook = new Dictionary<int, List<Edition>>();

            if (bookIds.Any())
            {
                var editions = _editionService.GetEditionsByBook(bookIds) ?? new List<Edition>();
                editionsByBook = editions
                    .GroupBy(edition => edition.BookId)
                    .ToDictionary(group => group.Key, group => group.ToList());
            }

            foreach (var book in books.Where(book => book.Id > 0 && book.Editions == null))
            {
                book.Editions = editionsByBook.TryGetValue(book.Id, out var bookEditions)
                    ? bookEditions
                    : new List<Edition>();
            }

            var calendar = new Ical.Net.Calendar
            {
                ProductId = isLegacyReadarrFeed
                    ? "-//readarr.com//Readarr//EN"
                    : "-//chaptarr.com//Chaptarr//EN"
            };

            var calendarName = isLegacyReadarrFeed ? "Readarr Book Schedule" : "Chaptarr Book Schedule";
            calendar.AddProperty(new CalendarProperty("NAME", calendarName));
            calendar.AddProperty(new CalendarProperty("X-WR-CALNAME", calendarName));

            foreach (var book in books.Where(book => book.ReleaseDate.HasValue).OrderBy(v => v.ReleaseDate.Value))
            {
                if (!authorsById.TryGetValue(book.AuthorId, out var author))
                {
                    try
                    {
                        author = _authorService.GetAuthor(book.AuthorId);
                        authorsById[author.Id] = author;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }

                if (parsedTags.Any() && (author.Tags == null || parsedTags.None(author.Tags.Contains)))
                {
                    continue;
                }

                var occurrence = calendar.Create<CalendarEvent>();
                occurrence.Uid = (isLegacyReadarrFeed ? "Readarr_book_" : "Chaptarr_book_") + book.Id;

                //occurrence.Status = book.HasFile ? EventStatus.Confirmed : EventStatus.Tentative;
                occurrence.Description = book.Editions?.FirstOrDefault(edition => edition?.Monitored == true)?.Overview
                                         ?? book.Editions?.FirstOrDefault(edition => edition != null)?.Overview
                                         ?? book.Overview
                                         ?? string.Empty;
                occurrence.Categories = book.Genres;

                var releaseDate = DateTime.SpecifyKind(book.ReleaseDate.Value.Date, DateTimeKind.Unspecified);
                occurrence.Start = new CalDateTime(releaseDate, false);
                occurrence.End = new CalDateTime(releaseDate.AddDays(1), false);

                occurrence.Summary = $"{author.Name} - {book.Title}";
            }

            var serializer = (IStringSerializer)new SerializerFactory().Build(calendar.GetType(), new SerializationContext());
            var icalendar = serializer.SerializeToString(calendar);

            return Content(icalendar, "text/calendar");
        }
    }
}

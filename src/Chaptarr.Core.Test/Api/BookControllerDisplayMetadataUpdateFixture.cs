using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Books;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.SignalR;

using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookControllerDisplayMetadataUpdateFixture
    {
        private sealed class DisconnectedBroadcaster : IBroadcastSignalRMessage
        {
            public bool IsConnected => false;
            public Task BroadcastMessage(SignalRMessage message) => Task.CompletedTask;
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        public void round_tripped_book_put_preserves_metadata_and_automatic_edition_selection(bool postgres, bool facade, bool hiddenNarrator)
        {
            WithEndpoint(postgres, facade, hiddenNarrator, (controller, books, editions, book, context) =>
            {
                var request = controller.GetBookById(book.Id).Value;
                Assert.That(request.Ratings.Value, Is.EqualTo(4.8m));
                Assert.That(request.Narrator, Is.EqualTo(hiddenNarrator ? null : "Edition Narrator"));
                Assert.That(request.DurationMinutes, Is.EqualTo(hiddenNarrator ? 12 : 90.5));
                request.Monitored = true;
                request.AudiobookMonitored = true;
                if (facade)
                {
                    request.Monitored = false;
                    request.AudiobookMonitored = false;
                }

                var json = JsonSerializer.Serialize(request);
                Assert.That(json.IndexOf("NarratorSpecified", StringComparison.OrdinalIgnoreCase), Is.EqualTo(-1));
                request = JsonSerializer.Deserialize<BookResource>(json);
                var result = facade ? controller.UpdateBookByBody(request) : controller.UpdateBook(request);
                Assert.That(result.Result, Is.InstanceOf<AcceptedAtActionResult>());
                var persisted = books.Get(book.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(persisted.Ratings.Value, Is.EqualTo(3.2m));
                    Assert.That(persisted.Ratings.Votes, Is.EqualTo(100));
                    Assert.That(persisted.Narrator, Is.EqualTo("Book Narrator"));
                    Assert.That(persisted.DurationMinutes, Is.EqualTo(12));
                    Assert.That(persisted.AudiobookMonitored, Is.EqualTo(!facade));
                    Assert.That(persisted.AnyEditionOk, Is.True);
                    Assert.That(editions.Get(book.Editions.Single(edition => edition.Monitored).Id).ManualAdd, Is.False);
                });
                TestContext.WriteLine($"Book PUT: {book.Title}; book rating={persisted.Ratings.Value}; narrator={persisted.Narrator}; duration={persisted.DurationMinutes}; generic={persisted.AnyEditionOk}");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ebook_put_preserves_work_rating_and_duration_from_get_projection(bool postgres)
        {
            WithEndpoint(postgres, false, true, (controller, books, _, book, _) =>
            {
                var request = controller.GetBookById(book.Id).Value;
                Assert.That(request.Ratings.Value, Is.EqualTo(4.8m));
                Assert.That(request.DurationMinutes, Is.EqualTo(12));
                controller.UpdateBook(JsonSerializer.Deserialize<BookResource>(JsonSerializer.Serialize(request)));
                var persisted = books.Get(book.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(persisted.MediaType, Is.EqualTo(BookMediaType.Ebook));
                    Assert.That(persisted.Ratings.Value, Is.EqualTo(3.2m));
                    Assert.That(persisted.DurationMinutes, Is.EqualTo(12));
                    Assert.That(persisted.Narrator, Is.Null);
                });
            }, BookMediaType.Ebook);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void pinning_generic_book_does_not_turn_hidden_null_into_an_explicit_narrator_clear(bool postgres)
        {
            WithEndpoint(postgres, false, true, (controller, books, _, book, _) =>
            {
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                controller.UpdateBook(request);
                Assert.That(books.Get(book.Id).Narrator, Is.EqualTo("Book Narrator"));
                Assert.That(books.Get(book.Id).AnyEditionOk, Is.False);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_narrator_clear_is_preserved_but_omitted_narrator_is_not_a_clear(bool postgres)
        {
            WithEndpoint(postgres, false, false, (controller, books, _, book, _) =>
            {
                var partial = JsonSerializer.Deserialize<BookResource>("{\"Id\":" + book.Id + ",\"AnyEditionOk\":true}");
                controller.UpdateBook(partial);
                Assert.That(books.Get(book.Id).Narrator, Is.EqualTo("Book Narrator"));

                var clear = JsonSerializer.Deserialize<BookResource>("{\"Id\":" + book.Id + ",\"AnyEditionOk\":true,\"Narrator\":null}");
                controller.UpdateBook(clear);
                Assert.That(books.Get(book.Id).Narrator, Is.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void round_trip_preserves_an_existing_manual_edition_pin(bool postgres)
        {
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                book.AnyEditionOk = false;
                books.SetFields(book, item => item.AnyEditionOk);
                var selected = book.Editions.Single(edition => edition.Monitored);
                selected.ManualAdd = true;
                editions.SetFields(selected, edition => edition.ManualAdd);
                var request = controller.GetBookById(book.Id).Value;
                Assert.That(request.Narrator, Is.EqualTo("Edition Narrator"));

                controller.UpdateBook(request);

                Assert.Multiple(() =>
                {
                    Assert.That(books.Get(book.Id).Narrator, Is.EqualTo("Book Narrator"));
                    Assert.That(books.Get(book.Id).AnyEditionOk, Is.False);
                    Assert.That(editions.Get(selected.Id).ManualAdd, Is.True);
                    Assert.That(editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(selected.Id));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void native_put_still_accepts_explicit_narrator_edit_and_edition_selection(bool postgres)
        {
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                var request = controller.GetBookById(book.Id).Value;
                request.Narrator = "Chosen Narrator";
                request.Editions = book.Editions.Select(edition => edition.ToResource()).ToList();
                var selectedId = book.Editions.Single(edition => !edition.Monitored).Id;
                foreach (var edition in request.Editions)
                {
                    edition.Monitored = edition.Id == selectedId;
                }

                request = JsonSerializer.Deserialize<BookResource>(JsonSerializer.Serialize(request));
                controller.UpdateBook(request);
                var persisted = books.Get(book.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(persisted.Narrator, Is.EqualTo("Chosen Narrator"));
                    Assert.That(persisted.AnyEditionOk, Is.False);
                    Assert.That(editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(selectedId));
                });
            });
        }

        private static void WithEndpoint(bool postgres, bool facade, bool hiddenNarrator,
            Action<BookController, BookRepository, EditionRepository, Book, ReadarrFacadeContext> test,
            BookMediaType mediaType = BookMediaType.Audiobook)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var author = authors.Insert(new Author
                {
                    Name = "Fixture Author", CleanName = "fixtureauthor", TitleSlug = "fixture-author",
                    Path = @"C:\Audio\Fixture Author".AsOsAgnostic(), Added = DateTime.UtcNow,
                    AudiobookRootFolderPath = mediaType == BookMediaType.Audiobook ? @"C:\Audio".AsOsAgnostic() : null,
                    EbookRootFolderPath = mediaType == BookMediaType.Ebook ? @"C:\Audio".AsOsAgnostic() : null
                });
                var authorService = Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAuthor) => authors.Get((int)args[0]),
                    nameof(IAuthorService.GetAuthors) => authors.Get((IEnumerable<int>)args[0]).ToList(),
                    _ => throw new NotImplementedException(method.Name)
                });
                var roots = Proxy<IRootFolderService>((method, _) => method.Name == nameof(IRootFolderService.All)
                    ? new List<RootFolder> { new() { Path = @"C:\Audio".AsOsAgnostic(), FolderType = mediaType == BookMediaType.Audiobook ? FolderType.Audiobook : FolderType.Ebook } }
                    : throw new NotImplementedException(method.Name));
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var book = books.Insert(new Book
                {
                    AuthorId = author.Id, Title = "Projected Metadata", CleanTitle = "projectedmetadata",
                    TitleSlug = "projected-metadata", HardcoverBookId = "hc:123", MediaType = mediaType,
                    AnyEditionOk = true, Added = DateTime.UtcNow, Narrator = mediaType == BookMediaType.Audiobook ? "Book Narrator" : null, DurationMinutes = 12,
                    Ratings = new Ratings { Votes = 100, Value = 3.2m }
                });
                var selected = editions.Insert(new Edition
                {
                    BookId = book.Id, Title = book.Title, ForeignEditionId = "hc:edition:456", HardcoverEditionId = "456",
                    ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : 3,
                    Format = mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook", Monitored = true,
                    Narrator = mediaType == BookMediaType.Audiobook ? "Edition Narrator" : null,
                    Ratings = new Ratings { Votes = 20, Value = 4.8m }
                });
                editions.Insert(new Edition
                {
                    BookId = book.Id, Title = book.Title, ForeignEditionId = "hc:edition:789", HardcoverEditionId = "789",
                    ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : 3,
                    Format = mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook", Monitored = false,
                    Narrator = mediaType == BookMediaType.Audiobook ? "Other Narrator" : null
                });
                var files = hiddenNarrator ? new List<BookFile>() : new List<BookFile>
                {
                    new() { Id = 1, EditionId = selected.Id, MediaType = "audiobook", DurationSeconds = 5430,
                        Path = @"C:\Audio\Fixture Author\Projected Metadata.m4b".AsOsAgnostic() }
                };
                var media = Proxy<IMediaFileService>((method, _) => method.Name.StartsWith("GetFiles", StringComparison.Ordinal)
                    ? files
                    : throw new NotImplementedException(method.Name));
                var editionService = new EditionService(editions, events, logger);
                var writer = new BookService(books, editionService, events, authorService, media, roots,
                    new SeriesBookLinkRepository(database, events), Proxy<IMultiCopySeriesService>((method, _) => throw new NotImplementedException(method.Name)),
                    logger, providerAliasService: new ProviderAliasService(new ProviderAliasRepository(database, events)));
                var stats = Proxy<IAuthorStatisticsService>((method, _) => method.Name == nameof(IAuthorStatisticsService.AuthorStatistics)
                    ? new AuthorStatistics { BookStatistics = new() }
                    : throw new NotImplementedException(method.Name));
                var covers = Proxy<IMapCoversToLocal>((method, _) => method.Name == nameof(IMapCoversToLocal.ConvertBookCoversToLocalUrls)
                    ? null
                    : throw new NotImplementedException(method.Name));
                var controller = new BookController(null, writer, null, editionService, new EditionSelector(logger), null,
                    stats, media, covers, null, new DisconnectedBroadcaster(), null, null, null, null, null,
                    new QualityProfileExistsValidator(new TestQualityProfileService()),
                    new MetadataProfileExistsValidator(new TestMetadataProfileService()), logger);
                var context = facade ? new ReadarrFacadeContext("hc", "audiobook", "/readarr/hc/audiobook") : null;
                var http = new DefaultHttpContext();
                if (context != null)
                {
                    http.Items[ReadarrFacadeContext.ItemKey] = context;
                }

                controller.ControllerContext = new ControllerContext { HttpContext = http };
                test(controller, books, editions, writer.GetBook(book.Id), context);
            });
        }
    }
}

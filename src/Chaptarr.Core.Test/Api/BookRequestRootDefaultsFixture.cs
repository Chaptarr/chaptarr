using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http.Middleware;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookRequestRootDefaultsFixture
    {
        public class ServiceProxy : DispatchProxy
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }
            protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
        }

        [Test]
        public void initial_scan_should_keep_explicit_requests_but_later_edits_should_not(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(MonitorTypes.None, MonitorTypes.All, MonitorTypes.Missing, MonitorTypes.Existing)] MonitorTypes initial,
            [Values(false, true)] bool initialScan,
            [Values(false, true)] bool requestedMonitored,
            [Values(false, true)] bool changedBeforeScan)
        {
            var author = new Author { Id = 42, AddOptions = new AddAuthorOptions() };
            var requested = new Book { Id = 1, MediaType = mediaType, AddOptions = new AddBookOptions { AddType = BookAddType.Manual, MonitorBook = changedBeforeScan ? !requestedMonitored : requestedMonitored } };
            requested.SetMonitored(requestedMonitored);
            var withFiles = new Book { Id = 2, MediaType = mediaType };
            var withoutFiles = new Book { Id = 3, MediaType = mediaType };
            var opposite = new Book { Id = 4, MediaType = mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook };
            var books = new List<Book> { requested, withFiles, withoutFiles, opposite };
            var usedUnscopedWrite = false;
            var fullCatalogLookups = 0;
            var leanCatalogLookups = 0;
            var fileLookups = 0;
            var bookService = DispatchProxy.Create<IBookService, ServiceProxy>();
            ((ServiceProxy)bookService).Handler = (method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IBookService.GetBooksByAuthor):
                        fullCatalogLookups++;
                        return books;
                    case nameof(IBookService.GetBooksByAuthorId):
                        leanCatalogLookups++;
                        return books;
                    case nameof(IBookService.GetAuthorBooksWithFiles):
                        fileLookups++;
                        return new List<Book> { withFiles };
                    case nameof(IBookService.SetMonitoredForMediaType) when initialScan:
                        return null;
                    case nameof(IBookService.UpdateManyWithLifecycle):
                        usedUnscopedWrite = true;
                        return null;
                    default:
                        throw new NotImplementedException(method.Name);
                }
            };
            var authorService = DispatchProxy.Create<IAuthorService, ServiceProxy>();
            ((ServiceProxy)authorService).Handler = (method, args) => method.Name == nameof(IAuthorService.UpdateAuthor)
                ? args[0] : throw new NotImplementedException(method.Name);
            var service = new BookMonitoredService(authorService, bookService, LogManager.GetCurrentClassLogger());

            service.SetBookMonitoredStatus(author, new MonitoringOptions { Monitor = initial, MediaType = mediaType }, initialSetup: initialScan);

            Assert.Multiple(() =>
            {
                Assert.That(requested.IsMonitored(), Is.EqualTo(initialScan ? requestedMonitored : initial is MonitorTypes.All or MonitorTypes.Missing));
                Assert.That(withFiles.IsMonitored(), Is.EqualTo(initial is MonitorTypes.All or MonitorTypes.Existing));
                Assert.That(withoutFiles.IsMonitored(), Is.EqualTo(initial is MonitorTypes.All or MonitorTypes.Missing));
                Assert.That(opposite.IsMonitored(), Is.False);
                Assert.That(usedUnscopedWrite, Is.EqualTo(!initialScan));
                Assert.That(fullCatalogLookups, Is.EqualTo(initialScan ? 0 : 1));
                Assert.That(leanCatalogLookups, Is.EqualTo(initialScan ? 1 : 0));
                Assert.That(fileLookups, Is.EqualTo(initial is MonitorTypes.Missing or MonitorTypes.Existing ? 1 : 0));
            });
        }

        [Test]
        public void add_book_mapping_keeps_explicit_selection_separate_from_author_monitor_none(
            [Values("audiobook", "ebook")] string mediaType,
            [Values("", ",\"monitored\":false", ",\"monitored\":true")] string selection)
        {
            var resource = System.Text.Json.JsonSerializer.Deserialize<BookResource>(
                "{\"title\":\"Dune\",\"mediaType\":\"" + mediaType + "\",\"author\":{\"addOptions\":{\"monitor\":\"none\"}}" + selection + "}",
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
            var book = resource.ToModel();
            Assert.That(book.IsMonitored(), Is.EqualTo(selection.EndsWith("true")));
            Assert.That(book.Author.AddOptions.Monitor, Is.EqualTo(MonitorTypes.None));
            Assert.That(book.AddOptions.MonitorBook, Is.EqualTo(selection.EndsWith("true")));
        }

        [Test]
        public void facade_should_resolve_first_setup_per_format_and_preserve_configured_settings(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(0, 1, 2)] int existingSide)
        {
            var audiobook = mediaType == BookMediaType.Audiobook;
            var existing = existingSide == 0 ? null : new Author();
            if (existing != null)
            {
                var configureAudio = existingSide == 2 ? audiobook : !audiobook;
                if (configureAudio)
                {
                    existing.AudiobookQualityProfileId = 2;
                    existing.AudiobookRootFolderPath = @"C:\Audio";
                    existing.AudiobookMonitored = false;
                    existing.AudiobookMonitorNewItems = NewItemMonitorTypes.New;
                }
                else
                {
                    existing.EbookQualityProfileId = 4;
                    existing.EbookRootFolderPath = @"C:\Books";
                    existing.EbookMonitored = false;
                    existing.EbookMonitorNewItems = NewItemMonitorTypes.New;
                }
            }

            var requested = new AuthorResource
            {
                Monitored = true,
                MonitorNewItems = "none",
                AddOptions = new AddAuthorOptions { BooksToMonitor = new List<string> { "hc:1001" } }
            };
            BookController.ApplyFacadeBookRequestDefaults(requested, existing, mediaType);

            var model = requested.ToModel(new ReadarrFacadeContext("hc", audiobook ? "audiobook" : "ebook", "/readarr"));
            Assert.Multiple(() =>
            {
                Assert.That(audiobook ? model.AudiobookMonitored : model.EbookMonitored,
                    Is.EqualTo(existingSide != 2));
                Assert.That(audiobook ? model.AudiobookMonitorNewItems : model.EbookMonitorNewItems,
                    Is.EqualTo(existingSide == 2 ? NewItemMonitorTypes.New : NewItemMonitorTypes.None));
                Assert.That(audiobook ? model.AddOptions.AudiobookMonitor : model.AddOptions.EbookMonitor,
                    Is.EqualTo(MonitorTypes.None));
                Assert.That(audiobook ? model.EbookMonitored : model.AudiobookMonitored, Is.Null);
                Assert.That(audiobook ? model.EbookMonitorNewItems : model.AudiobookMonitorNewItems, Is.Null);
                Assert.That(model.AddOptions.BooksToMonitor, Is.EqualTo(new[] { "hc:1001" }));
                Assert.That(model.AddOptions.SearchForMissingBooks, Is.False);
            });
        }
    }
}

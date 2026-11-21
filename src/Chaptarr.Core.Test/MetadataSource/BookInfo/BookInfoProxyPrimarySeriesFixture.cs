using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class BookInfoProxyPrimarySeriesFixture
    {
        private static BookInfoProxy CreateProxy(JObject payload, Logger logger = null)
        {
            var http = InterfaceProxy.Proxy<IHttpClient>((method, args) => method.Name == nameof(IHttpClient.Get)
                ? new HttpResponse((HttpRequest)args[0], new HttpHeader { ContentType = "application/json" }, payload.ToString(), HttpStatusCode.OK)
                : throw new AssertionException(method.Name));
            var config = InterfaceProxy.Proxy<IConfigService>((method, _) => method.Name == "get_MetadataServerUrl"
                ? "http://metadata" : throw new AssertionException(method.Name));
            logger ??= LogManager.GetCurrentClassLogger();
            return new BookInfoProxy(http, null, null, null, null, null, null, null, config, null,
                logger, new CacheManager(), new MetadataServerHealthGate(config, new MetadataServerHealthService(logger), logger));
        }

        // Constructed V5 payload: two upstream pockets share a work ID; each has both formats.
        private static JObject Payload(long? seriesId = 49075)
        {
            var book = JObject.Parse(@"{
                'id':'gr:8134945', 'title':'The Way of Kings', 'goodreadsWorkId':'gr:8134945',
                'seriesName':'The Stormlight Archive', 'seriesPosition':'1',
                'editions':[
                    {'id':'az:B003ZWFO7E','title':'The Way of Kings','readingFormatId':2,'languageCode':'en'},
                    {'id':'gr:7235533','title':'The Way of Kings','readingFormatId':3,'languageCode':'en'}]
            }");
            book["seriesId"] = seriesId.HasValue ? new JValue(seriesId.Value) : JValue.CreateNull();
            var duplicate = (JObject)book.DeepClone();
            duplicate["id"] = "hc:999";
            return new JObject
            {
                ["author"] = JObject.Parse(@"{'id':'gr:38550','name':'Brandon Sanderson'}"),
                ["books"] = new JArray(book, duplicate),
                ["series"] = JArray.Parse(@"[
                    {'id':'gr:49075','goodreadsSeriesId':'gr:49075','name':'The Stormlight Archive',
                     'books':[{'bookId':'gr:8134945','position':'1','isPrimary':true}]},
                    {'id':'gr:135117','goodreadsSeriesId':'gr:135117','name':'The Cosmere',
                     'books':[{'bookId':'gr:8134945','position':'5','isPrimary':false}]}]")
            };
        }

        [TestCase(49075L)]
        [TestCase(135117L)]
        [TestCase(999L)]
        [TestCase(null)]
        public void numeric_primary_series_and_all_memberships_reach_every_same_format_pocket(long? seriesId)
        {
            var author = CreateProxy(Payload(seriesId)).GetAuthorInfo("gr:38550", useCache: false);
            Assert.That(author.Books, Has.Count.EqualTo(4));
            foreach (var book in author.Books)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(book.SeriesId, Is.EqualTo(seriesId));
                    Assert.That(BookSeries.Select(book).Series.Value.GoodreadsSeriesId, Is.EqualTo(seriesId == 135117 ? "gr:135117" : "gr:49075"));
                    Assert.That(book.SeriesLinks, Has.Count.EqualTo(2));
                    Assert.That(book.SeriesLinks.All(link => link.Series.Value.MediaType == book.MediaType), Is.True);
                    Assert.That(book.SeriesLinks.Single(link => link.Series.Value.GoodreadsSeriesId == "gr:49075").Position, Is.EqualTo("1"));
                    Assert.That(book.SeriesLinks.Single(link => link.Series.Value.GoodreadsSeriesId == "gr:135117").IsPrimary, Is.False);
                });
            }
        }

        [TestCase(null, 4, 0)]
        [TestCase(49075L, 0, 0)]
        [TestCase(999L, 0, 4)]
        [TestCase(0L, 0, 4)]
        public void primary_series_diagnostic_should_count_only_books_with_usable_memberships(long? headerId, int missing, int unmatched)
        {
            var payload = Payload(headerId);
            var standalone = (JObject)payload["books"][0].DeepClone();
            standalone["id"] = standalone["goodreadsWorkId"] = "gr:88888";
            standalone["title"] = "Standalone";
            standalone["seriesId"] = 999999;
            standalone.Remove("seriesName");
            standalone.Remove("seriesPosition");
            foreach (var edition in standalone["editions"].Cast<JObject>()) edition["id"] = "gr:" + (88880 + edition["readingFormatId"].Value<int>());
            ((JArray)payload["books"]).Add(standalone);
            foreach (var debug in new[] { false, true })
            {
                using var factory = new LogFactory();
                var messages = new MemoryTarget { Layout = "${level}|${message}" };
                var logging = new LoggingConfiguration();
                logging.AddRule(debug ? LogLevel.Debug : LogLevel.Info, LogLevel.Warn, messages);
                factory.Configuration = logging;

                var author = CreateProxy(payload, factory.GetLogger(nameof(BookInfoProxyPrimarySeriesFixture))).GetAuthorInfo("gr:38550", useCache: false);

                Assert.That(author.Books, Has.Count.EqualTo(6));
                var warnings = messages.Logs.Where(message => message.StartsWith("Warn|[SERIES-V5]")).ToList();
                Assert.That(warnings, Has.Count.EqualTo(unmatched > 0 ? 1 : 0));
                if (unmatched > 0) Assert.That(warnings.Single(), Does.Contain($"{unmatched} book instances have a header series ID"));
                if (debug)
                    Assert.That(messages.Logs, Has.Some.Contain($"4 book instances with Goodreads memberships, {missing} without primary series ID, {unmatched} with an unmatched primary ID, 2 without a Goodreads membership"));
                else
                    Assert.That(messages.Logs.Any(message => message.StartsWith("Debug|")), Is.False);
            }
        }

        [Test]
        public void served_header_series_should_survive_storage_and_control_naming(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(null, 135117L, 999L)] long? seriesId)
        {
            var remote = CreateProxy(Payload(seriesId)).GetAuthorInfo("gr:38550", useCache: false);
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var author = new AuthorRepository(database, events, LogManager.GetCurrentClassLogger()).Insert(new Author
                {
                    Name = remote.Name, CleanName = "brandonsanderson", Path = "/series-naming", Added = DateTime.UtcNow
                });
                var book = remote.Books.First(book => book.MediaType == mediaType);
                book.Author = author;
                book.Added = DateTime.UtcNow;
                var books = new BasicRepository<Book>(database, events);
                books.Insert(book);
                var series = new BasicRepository<Series>(database, events);
                var links = new SeriesBookLinkRepository(database, events);
                foreach (var link in book.SeriesLinks)
                {
                    series.Insert(link.Series.Value);
                    link.SeriesId = link.Series.Value.Id;
                    link.BookId = book.Id;
                    links.Insert(link);
                }
                book = books.Get(book.Id);
                book.SeriesLinks = links.GetLinksByBook(new List<int> { book.Id });
                Assert.That(book.SeriesId, Is.EqualTo(seriesId));
                var config = NamingConfig.Default;
                config.RenameBooks = config.EbookRenameBooks = true;
                config.StandardBookFormat = config.EbookStandardBookFormat =
                    "{Book Series}/{Book SeriesTitle}/{Book SeriesPosition:00 - }{Book Title}";
                var ebook = mediaType == BookMediaType.Ebook;
                var file = new BookFile
                {
                    Path = "/series-naming/original." + (ebook ? "epub" : "m4b"),
                    MediaType = ebook ? "ebook" : "audiobook",
                    Quality = new QualityModel(ebook ? Quality.EPUB : Quality.M4B)
                };
                var actual = Chaptarr.Core.Test.Organizer.FileNameBuilderSeriesTokensFixture.CreateBuilder()
                    .BuildBookFileName(author, new Edition { Title = book.Title, Book = book }, file, config, new List<CustomFormat>());
                var expectedSeries = seriesId == 135117 ? "The Cosmere" : "The Stormlight Archive";
                var position = seriesId == 135117 ? "5" : "1";
                Assert.That(actual, Is.EqualTo(Path.Combine(expectedSeries, expectedSeries + " #" + position,
                    "0" + position + " - The Way of Kings")));
            });
        }

        [Test]
        public void omitted_header_series_should_keep_the_existing_selection()
        {
            var payload = Payload();
            foreach (var book in payload["books"].Cast<JObject>()) book.Remove("seriesId");
            var author = CreateProxy(payload).GetAuthorInfo("gr:38550", useCache: false);
            foreach (var book in author.Books)
            {
                Assert.That(book.SeriesId, Is.Null);
                Assert.That(BookSeries.Select(book).Series.Value.GoodreadsSeriesId, Is.EqualTo("gr:49075"));
            }
        }

        [Test]
        public void scalar_series_without_a_provider_membership_must_not_create_a_link()
        {
            var payload = Payload();
            payload["series"] = new JArray();
            var author = CreateProxy(payload).GetAuthorInfo("gr:38550", useCache: false);
            Assert.That(author.Books.All(book => book.SeriesLinks.Count == 0), Is.True);
            Assert.That(author.Series, Is.Empty);
        }

        [Test]
        public void series_id_outside_the_local_integer_range_must_not_wrap_to_another_series()
        {
            var error = Assert.Throws<BookInfoException>(() => CreateProxy(Payload((long)int.MaxValue + 1))
                .GetAuthorInfo("gr:38550", useCache: false));
            Assert.That(error.InnerException, Is.TypeOf<OverflowException>());
        }
    }
}

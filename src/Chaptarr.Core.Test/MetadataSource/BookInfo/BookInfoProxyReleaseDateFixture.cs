using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NLog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.BookInfo.V5;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class BookInfoProxyReleaseDateFixture
    {
        private static BookInfoProxy CreateProxy(string payload, List<HttpRequest> requests, string workPayload = null)
        {
            HttpResponse Response(HttpRequest request)
            {
                requests.Add(request);
                if (workPayload != null && request.Url.ToString().Contains("/api/v5/work/"))
                {
                    return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, workPayload, HttpStatusCode.OK);
                }
                return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, payload, HttpStatusCode.OK);
            }
            var http = InterfaceProxy.Proxy<IHttpClient>((method, args) => method.Name switch
            {
                nameof(IHttpClient.Get) => Response((HttpRequest)args[0]),
                nameof(IHttpClient.GetAsync) => Task.FromResult(Response((HttpRequest)args[0])),
                _ => throw new AssertionException(method.Name)
            });
            var cached = InterfaceProxy.Proxy<ICachedHttpResponseService>((method, args) => method.Name == nameof(ICachedHttpResponseService.Get)
                ? Response((HttpRequest)args[0]) : throw new AssertionException(method.Name));
            var config = InterfaceProxy.Proxy<IConfigService>((method, _) => method.Name == "get_MetadataServerUrl"
                ? "http://metadata" : throw new AssertionException(method.Name));
            var logger = LogManager.GetCurrentClassLogger();
            return new BookInfoProxy(http, cached, null, null, null, null, null, null, config,
                new MetadataRequestBuilder(config), logger, new CacheManager(),
                new MetadataServerHealthGate(config, new MetadataServerHealthService(logger), logger));
        }

        [TestCase(300, true, false)]
        [TestCase(301, true, false)]
        [TestCase(300, false, false)]
        [TestCase(301, false, false)]
        [TestCase(300, true, true)]
        [TestCase(301, true, true)]
        [TestCase(300, false, true)]
        [TestCase(301, false, true)]
        [TestCase(301, false, false, true)]
        [TestCase(301, false, true, true)]
        public void legacy_catalog_should_not_be_pruned_by_author_or_work_lookup(int workCount, bool hasIsbn, bool workLookup, bool dated = false)
        {
            var resource = new NzbDrone.Core.MetadataSource.BookInfo.AuthorResource
            {
                ForeignId = 9001, Name = "Legacy Author", Series = new List<SeriesResource>(),
                Works = Enumerable.Range(1, workCount).Select(id => new WorkResource
                {
                    ForeignId = id, Title = "Catalog Work " + id, ReleaseDate = dated ? new DateTime(2020, 5, 6) : null,
                    Books = new List<NzbDrone.Core.MetadataSource.BookInfo.BookResource>
                    {
                        new() { ForeignId = id + 10000, Title = "Undated Edition " + id,
                            Isbn13 = hasIsbn ? "9781234567890" : null, ReleaseDate = null,
                            Contributors = new List<ContributorResource> { new() { ForeignId = 9001, Role = "Author" } } }
                    }
                }).ToList()
            };
            resource.Works.Add(new WorkResource
            {
                ForeignId = 20000, Title = "Other Author Work", Books = new List<NzbDrone.Core.MetadataSource.BookInfo.BookResource>
                {
                    new() { ForeignId = 20001, Title = "Other Edition", Contributors = new List<ContributorResource>
                        { new() { ForeignId = 9002, Role = "Author" } } }
                }
            });
            var work = new JObject
            {
                ["id"] = "gr:1", ["title"] = "Catalog Work 1", ["goodreadsWorkId"] = "gr:1",
                ["publicationDate"] = dated ? new JValue(new DateTime(2020, 5, 6)) : JValue.CreateNull(),
                ["editions"] = new JArray(new JObject
                {
                    ["id"] = "gr:10001", ["title"] = "Undated Edition 1", ["readingFormatId"] = 2,
                    ["isbn13"] = hasIsbn ? new JValue("9781234567890") : JValue.CreateNull(),
                    ["publicationDate"] = JValue.CreateNull()
                })
            };
            var workPayload = new JObject
            {
                ["work"] = work, ["editions"] = work["editions"].DeepClone(),
                ["authors"] = new JArray(new JObject { ["name"] = resource.Name,
                    ["providerIds"] = new JObject { ["gr"] = "gr:9001" } })
            };
            var requests = new List<HttpRequest>();
            var proxy = CreateProxy(JsonConvert.SerializeObject(resource), requests, workPayload.ToString());
            var author = proxy.GetAuthorInfo("legacy-author", useCache: workLookup);
            if (workLookup)
            {
                var book = proxy.GetWorkInfo("gr:1").Item2;
                Assert.That(book.Title, Is.EqualTo("Catalog Work 1"));
                Assert.That(book.ReleaseDate, Is.EqualTo(dated ? new DateTime(2020, 5, 6) : (DateTime?)null));
                Assert.That(book.Editions.All(edition => edition.ReleaseDate == null), Is.True);
                Assert.That(proxy.GetAuthorInfo("legacy-author"), Is.SameAs(author));
                Assert.That(requests[1].Url.ToString(), Does.EndWith("/api/v5/work/gr:1"));
            }
            Assert.That(requests, Has.Count.EqualTo(workLookup ? 2 : 1));
            Assert.That(requests[0].Url.ToString(), Does.EndWith("/author/legacy-author"));
            Assert.That(author.Books, Has.Count.EqualTo(workCount));
            Assert.That(author.Books.All(book => book.ReleaseDate == (dated ? new DateTime(2020, 5, 6) : null) && book.Editions.All(edition => edition.ReleaseDate == null)), Is.True);
            Assert.That(author.Books.Any(book => book.Title == "Other Author Work"), Is.False);
            TestContext.WriteLine($"Legacy Author: {workCount} works retained with ISBN={hasIsbn}, dated={dated}, workLookup={workLookup}; {requests.Count} metadata requests.");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("not-a-date")]
        [TestCase("0000-00-00")]
        public void v5_author_and_work_lookup_should_preserve_unknown_dates(string date)
        {
            var work = new JObject
            {
                ["id"] = "hc:1001", ["title"] = "Undated Work", ["publicationDate"] = date,
                ["editions"] = new JArray(new JObject
                {
                    ["id"] = "hc:edition:2001", ["title"] = "Undated Audio", ["readingFormatId"] = 2,
                    ["publicationDate"] = date
                }, new JObject
                {
                    ["id"] = "hc:edition:2002", ["title"] = "Undated Ebook", ["readingFormatId"] = 3,
                    ["publicationDate"] = date
                })
            };
            var authorPayload = new JObject
            {
                ["author"] = new JObject { ["id"] = "hc:9001", ["name"] = "Undated Author" },
                ["books"] = new JArray(work), ["series"] = new JArray()
            };
            var requests = new List<HttpRequest>();
            var author = CreateProxy(authorPayload.ToString(), requests).GetAuthorInfo("hc:9001", useCache: false);
            Assert.That(author.Books, Has.Count.EqualTo(2));
            Assert.That(author.Books.All(book => book.ReleaseDate == null && book.Editions.All(edition => edition.ReleaseDate == null)), Is.True);
            Assert.That(requests, Has.Count.EqualTo(1));

            var workPayload = new JObject
            {
                ["work"] = work, ["editions"] = work["editions"].DeepClone(),
                ["authors"] = new JArray(new JObject { ["name"] = "Undated Author", ["role"] = "primary",
                    ["providerIds"] = new JObject { ["hardcover"] = "hc:9001" },
                    ["providerIdsAll"] = new JObject { ["hardcover"] = new JArray("hc:9001") } })
            };
            requests.Clear();
            var proxy = CreateProxy(workPayload.ToString(), requests);
            foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            {
                var book = proxy.GetWorkInfo("hc:1001", side).Item2;
                Assert.That(book.MediaType, Is.EqualTo(side));
                Assert.That(book.ReleaseDate, Is.Null);
                Assert.That(book.Editions.All(edition => edition.ReleaseDate == null), Is.True);
            }
            Assert.That(requests, Has.Count.EqualTo(2));
        }

        [TestCase(0, null, null)]
        [TestCase(-1, null, null)]
        [TestCase(10000, null, null)]
        [TestCase(0, "2020-05-06", "2020-05-06")]
        [TestCase(2020, null, "2020-01-01")]
        [TestCase(2099, null, "2099-01-01")]
        public void publication_year_should_not_make_an_undated_author_catalog_fail(int year, string editionDate, string expectedDate)
        {
            var requests = new List<HttpRequest>();
            var payload = new JObject
            {
                ["author"] = new JObject { ["id"] = "hc:9001", ["name"] = "Undated Author" },
                ["books"] = new JArray(new JObject
                {
                    ["id"] = "hc:1001", ["title"] = "Undated Work", ["publicationYear"] = year,
                    ["editions"] = new JArray(new JObject
                    {
                        ["id"] = "hc:edition:2001", ["title"] = "Undated Audio", ["readingFormatId"] = 2,
                        ["publicationDate"] = editionDate
                    })
                })
            };
            var author = CreateProxy(payload.ToString(), requests).GetAuthorInfo("hc:9001", useCache: false);
            var book = author.Books.Single(book => book.MediaType == BookMediaType.Audiobook);
            Assert.That(book.ReleaseDate, Is.EqualTo(expectedDate == null ? (DateTime?)null : DateTime.Parse(expectedDate)));
            Assert.That(book.PublicationYear, Is.EqualTo(year));
            Assert.That(book.Editions.Single().ReleaseDate, Is.EqualTo(editionDate == null ? (DateTime?)null : DateTime.Parse(editionDate)));
            Assert.That(requests, Has.Count.EqualTo(1));
        }

        [Test]
        public void known_future_date_and_valid_year_precision_should_remain_metadata()
        {
            var value = JsonConvert.DeserializeObject<V5Book>(@"{""publicationDate"":""2099-04-05"",""publicationYear"":2099}");
            Assert.That(value.PublicationDate.ToUtcDateTime(), Is.EqualTo(new DateTime(2099, 4, 5, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(value.PublicationYear, Is.EqualTo(2099));
        }
    }
}

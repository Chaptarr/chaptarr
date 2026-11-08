using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Core.Test.Common;
using NLog;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Books
{
    public partial class BookServiceWantedNarratorSeriesLinksFixture
    {
        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void adding_undated_work_should_round_trip_null_dates_through_actual_writer_and_api(bool postgres, BookMediaType side)
        {
            RunWithDatabase(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = new StubEventAggregator();
                var authorRows = new AuthorRepository(database, events, logger);
                var author = authorRows.Insert(new Author
                {
                    Name = "Undated Author", CleanName = "undatedauthor", TitleSlug = "undated-author",
                    Overview = string.Empty, HardcoverAuthorId = "hc:9001", Path = @"C:\Books\Undated Author".AsOsAgnostic(),
                    Added = DateTime.UtcNow
                });
                var bookRows = new BookRepository(database, events);
                var editionRows = new EditionRepository(database, events);
                var editions = new EditionService(editionRows, events, logger);
                var books = new BookService(bookRows, editions, events, new StubAuthorService(author),
                    new StubMediaFileService(), null, new SeriesBookLinkRepository(database, events), new StubMultiCopySeriesService(), logger);
                var incoming = new Book
                {
                    AuthorId = author.Id, Author = author, Title = "Undated Work", CleanTitle = "undatedwork",
                    TitleSlug = "undated-work", Overview = string.Empty, HardcoverBookId = "hc:1001",
                    Added = DateTime.UtcNow, MediaType = side, ReleaseDate = null, AnyEditionOk = true,
                    Editions = new List<Edition>
                    {
                        new() { ForeignEditionId = "hc:edition:2001", Title = "Undated Edition", ReleaseDate = null,
                            ReadingFormatId = side == BookMediaType.Audiobook ? 2 : 3, Language = "eng" }
                    }
                };
                var added = books.AddBook(incoming, doRefresh: false);
                var stored = books.GetBook(added.Id);
                Assert.That(stored.ReleaseDate, Is.Null);
                Assert.That(stored.Editions.Single().ReleaseDate, Is.Null);
                var resource = stored.ToResource();
                resource.Editions = stored.Editions.Select(edition => edition.ToResource()).ToList();
                Assert.That(resource.ReleaseDate, Is.Null);
                Assert.That(resource.Editions.Single().ReleaseDate, Is.Null);
                var json = JObject.Parse(STJson.ToJson(resource));
                Assert.That(json["releaseDate"], Is.Null);
                Assert.That(json["editions"].Single()["releaseDate"], Is.Null);
                var roundTrip = STJson.Deserialize<BookResource>(json.ToString()).ToModel();
                Assert.That(roundTrip.ReleaseDate, Is.Null);
                Assert.That(roundTrip.Editions.Single().ReleaseDate, Is.Null);
                json["releaseDate"] = JValue.CreateNull();
                json["editions"].Single()["releaseDate"] = JValue.CreateNull();
                var explicitNull = STJson.Deserialize<BookResource>(json.ToString()).ToModel();
                Assert.That(explicitNull.ReleaseDate, Is.Null);
                Assert.That(explicitNull.Editions.Single().ReleaseDate, Is.Null);
                Assert.That(bookRows.All(), Has.Count.EqualTo(1));
                Assert.That(editionRows.All(), Has.Count.EqualTo(1));
                TestContext.WriteLine($"{database.DatabaseType}/{side}: Undated Author hc:9001 / Undated Work hc:1001 persisted once; omitted and explicit-null API dates remain null.");
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void explicit_metadata_profile_preferences_should_remain_authoritative_for_unrequested_undated_and_isbn_free_works(bool postgres, BookMediaType side)
        {
            RunWithDatabase(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = new StubEventAggregator();
                var profiles = new MetadataProfileRepository(database, events);
                var profile = profiles.Insert(new MetadataProfile
                {
                    Name = "Require supplied date and ISBN", SkipMissingDate = true, SkipMissingIsbn = true,
                    AllowedLanguages = "eng", MinPopularity = 0, Ignored = new List<string>()
                });
                var service = new MetadataProfileService(profiles,
                    InterfaceProxy.Proxy<IAuthorService>((method, _) => throw new AssertionException(method.Name)),
                    InterfaceProxy.Proxy<IBookService>((method, _) => throw new AssertionException(method.Name)),
                    InterfaceProxy.Proxy<IEditionService>((method, _) => throw new AssertionException(method.Name)),
                    new StubMediaFileService(), null, null, new TestTermMatcherService(), events, logger);
                Book Work(string id, bool requested, bool dated, bool isbn) => new()
                {
                    Title = id, HardcoverBookId = id, MediaType = side,
                    ReleaseDate = dated ? new DateTime(2020, 5, 6) : null,
                    AddOptions = new AddBookOptions { AddType = requested ? BookAddType.Manual : BookAddType.Automatic },
                    Editions = new List<Edition>
                    {
                        new() { Title = id + " Edition", ForeignEditionId = id + "-edition", Language = "eng",
                            ReadingFormatId = side == BookMediaType.Audiobook ? 2 : 3,
                            Isbn13 = isbn ? "9781234567890" : null }
                    }
                };
                Author Catalog() => new()
                {
                    Name = "Profile Author", Books = new List<Book>
                    {
                        Work("hc:1001", true, false, false), Work("hc:1002", false, false, true),
                        Work("hc:1003", false, true, false), Work("hc:1004", false, true, true)
                    }
                };
                var strict = service.FilterBooks(Catalog(), profile.Id);
                Assert.That(strict.Select(book => book.HardcoverBookId), Is.EquivalentTo(new[] { "hc:1001", "hc:1004" }));
                Assert.That(strict.Single(book => book.HardcoverBookId == "hc:1001").ReleaseDate, Is.Null);
                profile.SkipMissingDate = false;
                profiles.Update(profile);
                Assert.That(service.FilterBooks(Catalog(), profile.Id).Select(book => book.HardcoverBookId),
                    Is.EquivalentTo(new[] { "hc:1001", "hc:1002", "hc:1004" }));
                profile.SkipMissingIsbn = false;
                profiles.Update(profile);
                Assert.That(service.FilterBooks(Catalog(), profile.Id), Has.Count.EqualTo(4));
                TestContext.WriteLine($"{database.DatabaseType}/{side}: explicit date and ISBN filters retain requested work; each preference continues filtering unrequested catalog rows until disabled.");
            });
        }
    }
}

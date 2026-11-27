using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class SeriesTagMatchingFixture
    {
        [Test]
        public void series_tags_preserve_the_selected_qualified_edition_on_both_databases(
            [Values(false, true)] bool postgres,
            [Values("Seven and the Swift (International Edition)", "Seven and the Swift: An Epic Romantic Fantasy", "Seven and the Swift: An Epic Romantic Fantasy (Shield of Sparrows Book 3)")] string title)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var author = authors.Insert(new Author { Name = "Devney Perry", CleanName = "devneyperry", SortName = "Devney Perry", TitleSlug = "devney-perry", Path = "/library", Added = DateTime.UtcNow });
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var links = new SeriesBookLinkRepository(database, events);
                var series = new NzbDrone.Core.Datastore.BasicRepository<Series>(database, events).Insert(new Series { Title = "Shield of Sparrows", GoodreadsSeriesId = "gr:1", MediaType = BookMediaType.Audiobook });
                Edition Seed(int id, string bookTitle, string editionTitle, string position)
                {
                    var book = books.Insert(new Book { AuthorId = author.Id, Title = bookTitle, CleanTitle = bookTitle.ToLowerInvariant(), TitleSlug = "book-" + id,
                        MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow, HardcoverBookId = "hc:" + id, BaseBookId = "hc:" + id, AnyEditionOk = true, SeriesName = series.Title, SeriesPosition = position });
                    links.Insert(new SeriesBookLink { BookId = book.Id, SeriesId = series.Id, Position = position });
                    return editions.Insert(new Edition { BookId = book.Id, Title = editionTitle, Subtitle = id == 1 ? "An Epic Romantic Fantasy" : null,
                        MatchingTitle = NzbDrone.Core.Books.Extensions.StringSuperNormalizer.ComputeMatchingTitle(editionTitle), ForeignEditionId = "gr:" + id,
                        Format = "audiobook", ReadingFormatId = 2, Language = "eng", Monitored = true });
                }
                var target = Seed(1, "Seven and the Swift", title, "3");
                Seed(2, "Shield of Sparrows", "Shield of Sparrows", "1");
                Seed(3, "Shield of Sparrows (Part 1 of 2) [Dramatized Adaptation]", "Shield of Sparrows (Part 1 of 2) [Dramatized Adaptation]", "1, Part 1");
                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, events));
                var authorService = new AuthorService(authors, events, null, null, null, new CacheManager(), books, null, logger, null, null, null, database, aliases);
                var editionService = new EditionService(editions, events, logger);
                var bookService = new BookService(books, editionService, events, authorService, null, null, links, null, logger, providerAliasService: aliases);
                var fts = new EditionFtsRepository(database, logger);
                fts.RebuildIndex();
                var matcher = new FileMatchingService(Proxy<IMatchingUploadLogger>((_, _) => null), null, new ContainmentValidator(new TagNormalizer(), logger), null, null, null, null,
                    ConfigServiceTestProxy.Create(usePathAsTagsFallback: false), authorService, events, null, fts, bookService, editionService, editions, null, logger);
                foreach (var prefix in new[] { "", "ID3v2:TXXX:", "MP4:----:com.apple.iTunes:", "MP4:----:com.pilabor.tone:", "XIPH:", "ASF:", "APE:" })
                {
                    var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["TITLE"] = new() { title }, ["ALBUM"] = new() { "Seven and the Swift" }, ["ARTIST"] = new() { author.Name } };
                    if (prefix.Length != 0)
                    {
                        tags[prefix + "SERIES"] = new() { series.Title };
                        tags[prefix + (prefix.Contains("com.pilabor.tone") ? "PART" : "SERIES-PART")] = new() { "3 - Shield of Sparrows" };
                        TagCanonicalizer.AddCanonicalKeys(tags);
                    }
                    var result = matcher.HolyGrailMatchFile(new DiscoveredFileWithMetadata { Path = "/downloads/book.m4b", AllTags = tags }, BookMediaType.Audiobook, restrictToAuthorId: author.Id);
                    Assert.That(result, Is.Not.Null, prefix);
                    Assert.That(result.EditionId, Is.EqualTo(target.Id), prefix);
                    Assert.That(result.BookId, Is.EqualTo(target.BookId), prefix);
                }
            });
        }
    }
}

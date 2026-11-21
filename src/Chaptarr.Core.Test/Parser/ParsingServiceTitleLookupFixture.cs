using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Data.Common;
using Chaptarr.Core.Test.Common;
using Dapper;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ParsingServiceTitleLookupFixture
    {
        public class CallProxy<T> : DispatchProxy where T : class
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }
            protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
        }

        internal static T Proxy<T>(Func<MethodInfo, object[], object> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, CallProxy<T>>();
            ((CallProxy<T>)(object)proxy).Handler = handler;
            return proxy;
        }

        internal sealed class Harness
        {
            public Author Author { get; } = new() { Id = 38, Name = "Test Writer" };
            public List<Book> Catalog { get; }
            public ParsingService Parser { get; }
            public BookService Books { get; }
            public int BookIdReads { get; private set; }
            public int ExistingBookReads { get; private set; }
            public List<int> ExistingBookIds { get; private set; } = new();
            public int BookReads { get; private set; }
            public int AuthorReads { get; private set; }
            public int EditionReads { get; private set; }
            public int SeriesReads { get; private set; }

            public Harness(List<Book> catalog)
            {
                Catalog = catalog;
                var editions = catalog.SelectMany(book => book.Editions ?? new List<Edition>()).ToList();
                var authors = Proxy<IAuthorService>((method, args) =>
                {
                    if (method.Name == nameof(IAuthorService.GetAuthor))
                    {
                        AuthorReads++;
                        return Author;
                    }

                    if (method.Name == nameof(IAuthorService.GetExistingAuthors))
                    {
                        AuthorReads++;
                        return ((IEnumerable<int>)args[0]).Select(id => id == Author.Id ? Author : new Author { Id = id, Name = "Other Writer" }).ToList();
                    }

                    if (method.Name == nameof(IAuthorService.FindByName))
                    {
                        return (string)args[0] == Author.Name ? Author : null;
                    }

                    if (method.Name == nameof(IAuthorService.GetCandidates))
                    {
                        return (string)args[0] == Author.Name ? new List<Author> { Author } : new List<Author>();
                    }

                    throw new NotImplementedException(method.Name);
                });
                var repository = Proxy<IBookRepository>((method, args) =>
                {
                    if (method.Name == nameof(IBookRepository.GetBooks) || method.Name == nameof(IBookRepository.GetBooksByAuthorId))
                    {
                        BookReads++;
                        return catalog.Where(book => book.AuthorId == (int)args[0]).ToList();
                    }

                    if (method.Name == nameof(IBookRepository.Get))
                    {
                        BookIdReads++;
                        return catalog.Single(book => book.Id == (int)args[0]);
                    }

                    if (method.Name == nameof(IBookRepository.FindExisting))
                    {
                        ExistingBookReads++;
                        ExistingBookIds = ((IEnumerable<int>)args[0]).ToList();
                        var ids = ExistingBookIds.ToHashSet();
                        return catalog.Where(book => ids.Contains(book.Id)).ToList();
                    }

                    if (method.Name == "FindByTitle")
                    {
                        BookReads++;
                        return catalog.FirstOrDefault(book => book.AuthorId == (int)args[0] &&
                            book.Title.CleanAuthorName() == ((string)args[1]).CleanAuthorName());
                    }

                    throw new NotImplementedException(method.Name);
                });
                var editionService = Proxy<IEditionService>((method, args) =>
                {
                    if (method.Name == nameof(IEditionService.GetEditionsByAuthor) || method.Name == nameof(IEditionService.GetEditionsByBook))
                    {
                        EditionReads++;
                        return args[0] is int bookId && method.Name == nameof(IEditionService.GetEditionsByBook)
                            ? editions.Where(edition => edition.BookId == bookId).ToList() : editions;
                    }

                    if (method.Name is "FindByTitle" or "FindByTitleInexact")
                    {
                        EditionReads++;
                        return editions.FirstOrDefault(edition => edition.Title.CleanAuthorName() == ((string)args[1]).CleanAuthorName());
                    }

                    if (method.Name == "GetCandidates")
                    {
                        EditionReads++;
                        return new List<Edition>();
                    }

                    throw new NotImplementedException(method.Name);
                });
                var links = Proxy<ISeriesBookLinkRepository>((method, args) =>
                {
                    if (method.Name == nameof(ISeriesBookLinkRepository.GetLinksByBook))
                    {
                        SeriesReads++;
                        return new List<SeriesBookLink>();
                    }

                    throw new NotImplementedException(method.Name);
                });
                var files = Proxy<IMediaFileService>((method, args) => new List<BookFile>());
                Books = new BookService(repository, editionService, null, authors, files, null, links, null,
                    LogManager.GetCurrentClassLogger());
                Parser = new ParsingService(authors, Books, editionService, null, LogManager.GetCurrentClassLogger());
            }
        }

        internal static Book Make(int id, string title, BookMediaType media = BookMediaType.Audiobook, string language = "English", int authorId = 38)
        {
            return new Book
            {
                Id = id, AuthorId = authorId, Title = title, CleanTitle = title.CleanAuthorName(), MediaType = media,
                HardcoverBookId = $"hc:{id}",
                Editions = new List<Edition>
                {
                    new() { Id = id, BookId = id, Title = title, Monitored = true, Language = language }
                }
            };
        }

        internal static ParsedBookInfo Parsed(string title, BookMediaType media = BookMediaType.Audiobook) => new()
        {
            AuthorName = "Test Writer", BookTitle = title,
            Quality = new QualityModel(media == BookMediaType.Ebook ? Quality.EPUB : Quality.M4B)
        };

        private static List<List<Book>> Batch(Harness harness, IReadOnlyList<ParsedBookInfo> parsed)
        {
            var method = typeof(ParsingService).GetMethod(nameof(ParsingService.GetBooks), new[] { typeof(IReadOnlyList<ParsedBookInfo>), typeof(Author) });
            Assert.That(method, Is.Not.Null, "The concrete parser must expose the canonical batch operation.");
            return (List<List<Book>>)method.Invoke(harness.Parser, new object[] { parsed, harness.Author });
        }

        [TestCase("The Paper Lanterns", "Paper Lanterns", "English", true)]
        [TestCase("Paper Lanterns", "The Paper Lanterns", "English", true)]
        [TestCase("A Paper Lanterns", "Paper Lanterns", "en", true)]
        [TestCase("An Paper Lanterns", "Paper Lanterns", "eng", true)]
        [TestCase("Terrible Worlds: The Destinations", "Terrible Worlds: Destinations", "English", true)]
        [TestCase("Terrible Worlds: Destinations", "Terrible Worlds: The Destinations", "English", true)]
        [TestCase("The Paper Lanterns", "Paper Lanterns", null, false)]
        [TestCase("I Married a Naga", "I Married Naga", "English", false)]
        [TestCase("The Art of War", "The Art War", "English", false)]
        [TestCase("Volume 2", "Volume", "English", false)]
        [TestCase("Blå がとう", "Blaa がとう", "English", true)]
        [TestCase("Blå がとう", "Blaa かとう", "English", false)]
        [TestCase("Blå हिंदी", "Blaa हिदी", "English", false)]
        public void fallback_should_apply_the_shared_title_rules(string stored, string supplied, string language, bool expected)
        {
            var book = Make(501, stored, language: language);
            var harness = new Harness(new List<Book> { book });

            var matches = harness.Parser.GetBooks(Parsed(supplied), harness.Author);

            Assert.That(matches.Select(match => match.Id), Is.EqualTo(expected ? new[] { book.Id } : Array.Empty<int>()));
            Assert.That(harness.BookReads, Is.EqualTo(1));
            Assert.That(harness.EditionReads, Is.EqualTo(1));
            Assert.That(harness.SeriesReads, Is.EqualTo(1));
        }

        [Test]
        public void selected_edition_should_attest_a_legitimate_subtitle_fallback()
        {
            var book = Make(501, "Mistborn: The Final Empire");
            var harness = new Harness(new List<Book> { book });
            Assert.That(harness.Parser.GetBooks(Parsed("Mistborn"), harness.Author), Is.EqualTo(new[] { book }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void same_enum_more_specific_title_should_displace_a_shorter_target(bool scopedTarget)
        {
            var shortTitle = Make(501, "Paper Lanterns");
            var longer = Make(502, "Paper Lanterns Encyclopedia");
            var harness = new Harness(new List<Book> { shortTitle, longer });
            var criteria = scopedTarget ? new BookSearchCriteria
            {
                Author = harness.Author, Books = new List<Book> { shortTitle }, AuthorCatalog = harness.Catalog
            } : null;

            var matches = harness.Parser.GetBooks(Parsed(longer.Title), harness.Author, criteria);

            Assert.That(matches, Is.EqualTo(scopedTarget ? Array.Empty<Book>() : new[] { longer }));
        }

        [Test]
        public void another_enum_or_author_should_not_supply_contradiction_evidence()
        {
            var target = Make(501, "The Paper Lanterns");
            var otherMedia = Make(502, "Paper Lanterns", BookMediaType.Ebook);
            var otherAuthor = Make(503, "Paper Lanterns", authorId: 39);
            var harness = new Harness(new List<Book> { target, otherMedia, otherAuthor });

            var criteria = new BookSearchCriteria
            {
                Author = harness.Author, Books = harness.Catalog, AuthorCatalog = harness.Catalog
            };
            Assert.That(harness.Parser.GetBooks(Parsed("Paper Lanterns"), harness.Author, criteria), Is.EqualTo(new[] { target }));
            Assert.That(harness.BookReads, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void equally_supported_rows_should_remain_ambiguous(bool sameWork)
        {
            var first = Make(501, "Paper Lanterns");
            var second = Make(502, "Paper Lanterns");
            if (sameWork)
            {
                second.HardcoverBookId = first.HardcoverBookId;
            }

            var harness = new Harness(new List<Book> { first, second });
            Assert.That(harness.Parser.GetBooks(Parsed(first.Title), harness.Author), Is.Empty);
        }

        [TestCase(false, "Test Writer - Paper Lanterns [EPUB]", "Paper Lanterns", 0)]
        [TestCase(true, "Test Writer - Paper Lanterns [EPUB]", "Paper Lanterns", 501)]
        [TestCase(false, "Test Writer - Paper Lanterns", "Paper Lanterns", 0)]
        [TestCase(false, null, "EPUB Guide", 0)]
        [TestCase(false, "Test Writer - The Ebook Handbook", "The Ebook Handbook", 0)]
        [TestCase(false, "Test Writer - The Audible Life", "The Audible Life", 0)]
        [TestCase(false, "Test Writer - MP3 Mysteries", "MP3 Mysteries", 0)]
        public void title_lookup_should_use_known_quality_and_leave_unknown_quality_ambiguous_despite_release_words(
            bool knownAudio, string releaseTitle, string bookTitle, int expectedId)
        {
            Assert.Multiple(() =>
            {
                foreach (var batch in new[] { false, true })
                {
                    var harness = new Harness(new List<Book> { Make(501, bookTitle), Make(502, bookTitle, BookMediaType.Ebook) });
                    var parsed = Parsed(bookTitle);
                    parsed.Quality = new QualityModel(knownAudio ? Quality.M4B : Quality.Unknown);
                    parsed.ReleaseTitle = releaseTitle;
                    var matches = batch ? harness.Parser.GetBooks(new[] { parsed }, harness.Author).Single()
                        : harness.Parser.GetBooks(parsed, harness.Author);

                    Assert.That(matches.Select(book => book.Id), Is.EqualTo(expectedId == 0 ? Array.Empty<int>() : new[] { expectedId }), $"batch={batch}");
                    Assert.That(harness.BookReads, Is.EqualTo(1));
                }
            });
        }

        [TestCase("EPUB", BookMediaType.Ebook, 502)]
        [TestCase("M4B", BookMediaType.Audiobook, 501)]
        public void canonical_quality_parsing_should_scope_explicit_release_formats(string format, BookMediaType expectedMedia, int expectedId)
        {
            Assert.Multiple(() =>
            {
                foreach (var batch in new[] { false, true })
                {
                    const string bookTitle = "Paper Lanterns";
                    var harness = new Harness(new List<Book> { Make(501, bookTitle), Make(502, bookTitle, BookMediaType.Ebook) });
                    var parsed = Parsed(bookTitle);
                    parsed.ReleaseTitle = $"Test Writer - {bookTitle} [{format}]";
                    parsed.Quality = QualityParser.ParseQuality(parsed.ReleaseTitle);

                    var matches = batch ? harness.Parser.GetBooks(new[] { parsed }, harness.Author).Single()
                        : harness.Parser.GetBooks(parsed, harness.Author);

                    Assert.That(QualityMediaTypeHelper.GetKnownMediaType(parsed.Quality.Quality), Is.EqualTo(expectedMedia));
                    Assert.That(matches.Select(book => book.Id), Is.EqualTo(new[] { expectedId }), $"batch={batch}");
                    Assert.That(harness.BookReads, Is.EqualTo(1));
                }
            });
        }

        [Test]
        public void explicit_catalog_should_require_no_database_reads()
        {
            var book = Make(501, "The Paper Lanterns");
            var harness = new Harness(new List<Book> { book });
            var criteria = new BookSearchCriteria { Author = harness.Author, Books = harness.Catalog, AuthorCatalog = harness.Catalog };

            Assert.That(harness.Parser.GetBooks(Parsed("Paper Lanterns"), harness.Author, criteria), Is.EqualTo(new[] { book }));
            Assert.That(harness.BookReads + harness.AuthorReads + harness.EditionReads + harness.SeriesReads, Is.Zero);
        }

        [TestCase(1, 1000)]
        [TestCase(25, 1000)]
        [TestCase(1, 3000)]
        [TestCase(25, 3000)]
        public void batch_should_load_the_large_catalog_once_and_preserve_result_order(int requestCount, int catalogCount)
        {
            var catalog = Enumerable.Range(1, catalogCount).Select(id => Make(id, $"Distinct title number {id}")).ToList();
            var harness = new Harness(catalog);
            var parsed = catalog.Take(requestCount).Select(book => Parsed(book.Title)).ToList();
            var timer = Stopwatch.StartNew();

            var matches = Batch(harness, parsed);

            timer.Stop();
            Assert.That(matches.Select(rows => rows.Single().Id), Is.EqualTo(catalog.Take(requestCount).Select(book => book.Id)));
            Assert.That(harness.BookReads, Is.EqualTo(1));
            Assert.That(harness.AuthorReads, Is.EqualTo(1));
            Assert.That(harness.EditionReads, Is.EqualTo(1));
            Assert.That(harness.SeriesReads, Is.EqualTo(1));
            TestContext.Progress.WriteLine($"MATCHING_BATCH requests={requestCount} books={catalogCount} bookReads={harness.BookReads} editionReads={harness.EditionReads} seriesReads={harness.SeriesReads} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void batch_should_propagate_a_real_repository_failure_instead_of_reporting_no_books(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using (var connection = database.OpenConnection())
                {
                    connection.Execute("ALTER TABLE \"Books\" RENAME COLUMN \"ReleaseDate\" TO \"UnavailableReleaseDate\"");
                }

                var logger = LogManager.GetCurrentClassLogger();
                var repository = new BookRepository(database, null);
                var books = new BookService(repository, null, null, null, null, null, null, null, logger);
                var parser = new ParsingService(null, books, null, null, logger);
                var parsed = Parsed("Collected works");
                parsed.Discography = true;
                parsed.DiscographyStart = 2000;
                parsed.DiscographyEnd = 2020;

                Assert.That(() => parser.GetBooks(new[] { parsed }, new Author { Id = 38 }),
                    Throws.InstanceOf<DbException>());
            });
        }

        [Test]
        public void batch_should_keep_a_healthy_title_after_one_item_fails()
        {
            var book = Make(501, "Paper Lanterns");
            var harness = new Harness(new List<Book> { book });
            var invalid = Parsed("Bad date range");
            invalid.Discography = true;
            invalid.DiscographyStart = 10000;
            invalid.DiscographyEnd = 10000;

            var matches = Batch(harness, new[] { invalid, Parsed(book.Title) });

            Assert.That(matches[0], Is.Empty);
            Assert.That(matches[1], Is.EqualTo(new[] { book }));
            Assert.That(harness.BookReads, Is.EqualTo(1));
        }
    }
}

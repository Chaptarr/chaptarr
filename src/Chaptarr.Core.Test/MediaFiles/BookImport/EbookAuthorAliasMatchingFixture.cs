using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class EbookAuthorAliasMatchingFixture
    {
        [TestCase(false, "captured-azw3")]
        [TestCase(true, "captured-azw3")]
        [TestCase(false, "epub-aut")]
        [TestCase(true, "epub-aut")]
        [TestCase(false, "epub-unclassified")]
        [TestCase(true, "epub-unclassified")]
        public void generated_author_views_must_not_score_as_another_editions_title(bool postgres, string source)
        {
            const string title = "The Richest Man in Babylon";
            const string authorName = "George S. Clason";
            var tags = source == "captured-azw3"
                ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["title"] = new() { title },
                    ["author"] = new() { authorName },
                    ["authors"] = new() { authorName },
                    ["isbn"] = new() { "9781773350394" },
                    ["publisher"] = new() { "Magdalene Press" },
                    ["publish_date"] = new() { "2017-10-23T07:00:00+00:00" }
                }
                : ReadEpubTags(source == "epub-aut");

            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method, args) => null);
                var author = new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = authorName, CleanName = "georgesclason", SortName = "georgesclason",
                    TitleSlug = "george-s-clason", Path = "/test-clason", Added = DateTime.UtcNow
                });
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                Book Seed(string editionTitle, string providerId, int readingFormat)
                {
                    var book = books.Insert(new Book
                    {
                        AuthorId = author.Id, Title = editionTitle, TitleSlug = providerId,
                        CleanTitle = NzbDrone.Core.Parser.Parser.CleanBookTitle(editionTitle),
                        MediaType = BookMediaType.Ebook, BaseBookId = providerId,
                        Added = DateTime.UtcNow, AnyEditionOk = true
                    });
                    book.Editions = new List<Edition> { editions.Insert(new Edition
                    {
                        BookId = book.Id, Title = editionTitle, ForeignEditionId = providerId,
                        MatchingTitle = NzbDrone.Core.Books.Extensions.StringSuperNormalizer.ComputeMatchingTitle(editionTitle),
                        ReadingFormatId = readingFormat, Monitored = true,
                        Publisher = "Magdalene Press", ReleaseDate = new DateTime(2017, 10, 23)
                    }) };
                    return book;
                }

                var correct = Seed(title, "gr:36478210-ebook", 3);
                var wrong = Seed("Gold Ahead by George S. Clason", "gr:234618194-ebook", 1);
                var catalog = new[] { correct, wrong };
                var bookService = Proxy<IBookService>((method, args) => method.Name switch
                {
                    "GetBook" => catalog.SingleOrDefault(book => book.Id == (int)args[0]),
                    "GetBooks" or "GetExistingBooks" => catalog.Where(book => ((IEnumerable<int>)args[0]).Contains(book.Id)).ToList(),
                    "GetBooksByAuthor" => catalog.ToList(),
                    _ => throw new AssertionException("Unexpected book read " + method.Name)
                });
                var authorService = Proxy<IAuthorService>((method, args) => author);
                var nativeFts = new EditionFtsRepository(database, logger);
                nativeFts.RebuildIndex();
                var recallCalls = 0;
                var ranked = new List<EditionFtsMatch>();
                var fts = Proxy<IEditionFtsRepository>((method, args) =>
                {
                    if (method.Name == nameof(IEditionFtsRepository.RecallBooks))
                    {
                        Assert.That(args[4], Is.EqualTo(20), "File matching retains the release candidate limit on both databases.");
                        recallCalls++;
                    }
                    var result = method.Invoke(nativeFts, args);
                    if (method.Name == nameof(IEditionFtsRepository.RankEditions)) ranked.AddRange((List<EditionFtsMatch>)result);
                    return result;
                });
                var matcher = new FileMatchingService(null, null, new ContainmentValidator(new TagNormalizer(), logger),
                    null, null, null, null, ConfigServiceTestProxy.Create(usePathAsTagsFallback: false), authorService,
                    events, null, fts, bookService, null, editions, null, logger);
                var match = matcher.HolyGrailMatchFile(new DiscoveredFileWithMetadata
                {
                    Path = "/captured-ebook-regression/book." + (source == "captured-azw3" ? "azw3" : "epub"),
                    AllTags = tags
                }, BookMediaType.Ebook, restrictToAuthorId: null);

                Assert.Multiple(() =>
                {
                    Assert.That(recallCalls, Is.EqualTo(1));
                    Assert.That(ranked.Single(row => row.EditionId == wrong.Editions[0].Id).Stage2TitleScore, Is.Zero);
                    Assert.That(ranked.Single(row => row.EditionId == correct.Editions[0].Id).Stage2TitleScore, Is.GreaterThan(0));
                    Assert.That(match?.EditionId, Is.EqualTo(correct.Editions[0].Id));
                });
            });
        }

        private static Dictionary<string, List<string>> ReadEpubTags(bool explicitAuthorRole)
        {
            var path = Path.Combine(Path.GetTempPath(), "chaptarr-ebook-alias-" + Guid.NewGuid().ToString("N") + ".epub");
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    void Write(string name, string value)
                    {
                        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                        writer.Write(value);
                    }

                    Write("mimetype", "application/epub+zip");
                    Write("META-INF/container.xml", "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
                    Write("book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\"><dc:identifier id=\"id\">fixture</dc:identifier><dc:title>The Richest Man in Babylon</dc:title><dc:creator" +
                        (explicitAuthorRole ? " opf:role=\"aut\"" : "") + ">George S. Clason</dc:creator><dc:language>en</dc:language></metadata><manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"chapter\"/></spine></package>");
                    Write("chapter.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Fixture</title></head><body><p>Fixture.</p></body></html>");
                }

                return new EBookTagService(null, null, null, null, null, null, LogManager.GetCurrentClassLogger())
                    .ReadAllTags(new FileInfoWrapper(new FileSystem(), new FileInfo(path)));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}

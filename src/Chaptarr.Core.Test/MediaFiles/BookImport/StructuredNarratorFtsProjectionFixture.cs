using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class StructuredNarratorFtsProjectionFixture
    {
        private static IEnumerable<TestCaseData> NativeCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var scenario in new[] { "cast-semicolon", "cast-reversed-comma", "cast-multi-value", "single-inverted", "raw-legacy" })
                yield return new TestCaseData(postgres, scenario);
        }

        [TestCaseSource(nameof(NativeCases))]
        public void structured_cast_projection_should_preserve_members_and_narrator_evidence(bool postgres, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method, args) => null);
                var author = new AuthorRepository(database, events, logger).Insert(new Author { Name = "Test Writer", CleanName = "testwriter",
                    SortName = "testwriter", TitleSlug = "test-writer", Path = "/test-writer", Added = DateTime.UtcNow });
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var aliases = new ProviderAliasSeed(database, events);
                Book Seed(string slug, string asin, List<string> cast, string scalar)
                {
                    var book = books.Insert(new Book { AuthorId = author.Id, Title = "Paper Lanterns", CleanTitle = "paperlanterns", TitleSlug = slug,
                        MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow, HardcoverBookId = "hc:7000", BaseBookId = "hc:7000", AnyEditionOk = false });
                    aliases.SeedAliases("Book", book.Id, "work", new[] { "hc:7000" });
                    var edition = editions.Insert(new Edition { BookId = book.Id, Title = book.Title, ForeignEditionId = "am:" + asin, Asin = asin,
                        Narrator = scalar, NarratorNames = cast, Format = "audiobook", ReadingFormatId = 2, Monitored = true });
                    book.Editions = new List<Edition> { edition };
                    return book;
                }
                var a = Seed("copy-a", "B000000001", scenario == "single-inverted" ? new() { "Reader, First" } :
                    scenario == "raw-legacy" ? new() : new() { "First Reader", "Third Reader" }, scenario == "raw-legacy" ? "Reader, First" : "First Reader");
                var b = Seed("copy-b", "B000000002", new() { "First Reader" }, "First Reader");
                var sql = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) =>
                {
                    if (method.Name != nameof(IMainDatabase.OpenConnection)) return method.Invoke(database, args);
                    var connection = database.OpenConnection();
                    return Proxy<IDbConnection>((m, values) =>
                    {
                        var value = m.Invoke(connection, values);
                        return value is IDbCommand command ? Proxy<IDbCommand>((cm, ca) =>
                        {
                            if (cm.Name is nameof(IDbCommand.ExecuteReader) or nameof(IDbCommand.ExecuteScalar) or nameof(IDbCommand.ExecuteNonQuery)) sql.Add(cm.Name);
                            return cm.Invoke(command, ca);
                        }) : value;
                    });
                });
                var readBooks = new BookRepository(observed, events);
                var readEditions = new EditionRepository(observed, events);
                var readAliases = new ProviderAliasService(new ProviderAliasRepository(observed, events));
                var authorService = new AuthorService(new AuthorRepository(observed, events, logger), events, null, null, null, new CacheManager(), readBooks,
                    null, logger, null, null, null, observed, readAliases);
                var editionService = new EditionService(readEditions, events, logger);
                var actualBooks = new BookService(readBooks, editionService, events, authorService, null, null,
                    new SeriesBookLinkRepository(observed, events), null, logger, providerAliasService: readAliases);
                var batches = 0;
                var bookService = Proxy<IBookService>((method, args) => { if (method.Name == nameof(IBookService.GetExistingBooks)) batches++; return method.Invoke(actualBooks, args); });
                var nativeFts = new EditionFtsRepository(observed, logger);
                nativeFts.RebuildIndex();
                sql.Clear();
                var recallCalls = 0;
                var rankCalls = 0;
                var projected = new List<EditionFtsMatch>();
                var publicFts = Proxy<IEditionFtsRepository>((method, args) =>
                {
                    if (method.Name == nameof(IEditionFtsRepository.RecallBooks)) recallCalls++;
                    var value = method.Invoke(nativeFts, args);
                    if (method.Name == nameof(IEditionFtsRepository.RankEditions)) { rankCalls++; projected.AddRange((List<EditionFtsMatch>)value); }
                    return value;
                });
                var matcher = new FileMatchingService(Proxy<IMatchingUploadLogger>((method, args) => null), null,
                    new ContainmentValidator(new TagNormalizer(), logger), null, null, null, null, ConfigServiceTestProxy.Create(), authorService,
                    events, null, publicFts, bookService, editionService, readEditions, null, logger);
                var tags = new Dictionary<string, List<string>> { ["TITLE"] = new() { a.Title }, ["AUTHOR"] = new() { author.Name } };
                tags["NARRATOR"] = scenario switch
                {
                    "cast-semicolon" => new() { "First Reader; Third Reader" },
                    "cast-reversed-comma" => new() { "Third Reader, First Reader" },
                    "cast-multi-value" => new() { "First Reader", "Third Reader" },
                    _ => new() { "First Reader" }
                };
                var files = new List<DiscoveredFileWithMetadata> { new() { Path = "/downloads/payload/book.m4b", AllTags = tags } };
                var context = MatchingContextPresets.ForDownloaded(false);
                var result = matcher.MatchFilesToLibraryAsync(files.ToArray(), author.Id, context).GetAwaiter().GetResult();
                var candidate = projected.First(row => row.EditionId == a.Editions[0].Id);
                var castCount = ReleaseNarratorEvidenceExtractor.SplitCredits(candidate.NarratorNames).Count();
                var evidenceMethod = typeof(FileMatchingService).GetMethod("GetNarratorEvidenceInResidualTags", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(evidenceMethod, Is.Not.Null, "The narrator evidence method was renamed; update this test.");
                var evidence = ((int Matched, int Independent, int Missing))evidenceMethod.Invoke(matcher, new object[] { candidate.NarratorNames, candidate.AuthorName, tags });
                var narratorCount = evidence.Matched;
                Assert.That(recallCalls, Is.EqualTo(1));
                Assert.That(rankCalls, Is.EqualTo(1));
                Assert.That(batches, Is.Zero);
                if (scenario.StartsWith("cast-", StringComparison.Ordinal))
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(castCount, Is.EqualTo(2));
                        Assert.That(narratorCount, Is.EqualTo(2));
                        Assert.That(result.MatchedFiles, Has.Length.EqualTo(1));
                        Assert.That(result.MatchedFiles.Single().BookId, Is.EqualTo(a.Id));
                    });
                }
                else
                {
                    Assert.That(candidate.NarratorNames, Is.EqualTo("Reader, First"));
                    Assert.That(castCount, Is.EqualTo(1));
                    Assert.That(narratorCount, Is.EqualTo(1));
                }
            });
        }

    }
}

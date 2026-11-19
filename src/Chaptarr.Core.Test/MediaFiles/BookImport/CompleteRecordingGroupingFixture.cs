using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class CompleteRecordingGroupingFixture
    {
        [Test]
        public void split_only_complete_recordings_after_checking_the_entire_group(
            [Values(false, true)] bool postgres,
            [Values(false, true)] bool manualPreview,
            [Values("rebel-copies", "main-and-credits", "short-halves", "multipart", "ambiguous-double", "partial-copy", "unknown-duration", "different-fields", "different-narrators", "single-recording", "single-chapter", "monitored-tie")] string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var author = authors.Insert(new Author { Name = "Callie Hart", CleanName = "calliehart", SortName = "Callie Hart",
                    TitleSlug = "callie-hart", Path = "/library/Callie Hart", Added = DateTime.UtcNow });
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var book = books.Insert(new Book { AuthorId = author.Id, Title = "The Rebel of Raleigh High",
                    CleanTitle = "the rebel of raleigh high", TitleSlug = "rebel", MediaType = BookMediaType.Audiobook,
                    Added = DateTime.UtcNow, HardcoverBookId = "hc:rebel", BaseBookId = "hc:rebel", AnyEditionOk = true });
                var duration = scenario == "short-halves" ? 480 : 36780;
                var edition = editions.Insert(new Edition { BookId = book.Id, Title = book.Title,
                    MatchingTitle = book.CleanTitle, ForeignEditionId = "gr:48841310-audiobook", DurationSeconds = duration,
                    Narrator = "Charlotte North", NarratorNames = new() { "Charlotte North" },
                    Format = "audiobook", ReadingFormatId = 2, Language = "eng", Monitored = true });
                Edition otherRecording = null;
                if (scenario is "different-narrators" or "monitored-tie")
                {
                    otherRecording = editions.Insert(new Edition { BookId = book.Id, Title = book.Title,
                        MatchingTitle = book.CleanTitle, ForeignEditionId = "gr:constructed-other-recording", DurationSeconds = duration,
                        Narrator = scenario == "monitored-tie" ? "Charlotte North" : "Sean Barrett",
                        NarratorNames = new() { scenario == "monitored-tie" ? "Charlotte North" : "Sean Barrett" },
                        Format = "audiobook", ReadingFormatId = 2, Language = "eng", Monitored = false });
                }
                if (scenario == "monitored-tie")
                {
                    edition.Monitored = false;
                    editions.Update(edition);
                    otherRecording.Monitored = true;
                    editions.Update(otherRecording);
                }
                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, events));
                var authorService = new AuthorService(authors, events, null, null, null, new CacheManager(), books,
                    null, logger, null, null, null, database, aliases);
                var editionService = new EditionService(editions, events, logger);
                var bookService = new BookService(books, editionService, events, authorService, null, null,
                    new SeriesBookLinkRepository(database, events), null, logger, providerAliasService: aliases);
                var fts = new EditionFtsRepository(database, logger);
                fts.RebuildIndex();
                var recalls = 0;
                var search = Proxy<IEditionFtsRepository>((method, args) =>
                {
                    if (method.Name == nameof(IEditionFtsRepository.RecallBooks)) recalls++;
                    return method.Invoke(fts, args);
                });
                var matcher = new FileMatchingService(Proxy<IMatchingUploadLogger>((_, _) => null), null,
                    new ContainmentValidator(new TagNormalizer(), logger), null, null, null, null,
                    ConfigServiceTestProxy.Create(usePathAsTagsFallback: false), authorService, events, null,
                    search, bookService, editionService, editions, null, logger);
                int?[] durations = scenario switch
                {
                    "rebel-copies" => new int?[] { 36846, 36846 },
                    "main-and-credits" => new int?[] { 36720, 60 },
                    "short-halves" => new int?[] { 240, 240, 240, 240 },
                    "multipart" => Enumerable.Repeat<int?>(1226, 30).ToArray(),
                    "ambiguous-double" => Enumerable.Repeat<int?>(1226, 60).ToArray(),
                    "partial-copy" => new int?[] { 36780, 18390 },
                    "unknown-duration" => new int?[] { 36780, null },
                    "single-recording" or "monitored-tie" => new int?[] { 36780 },
                    "single-chapter" => new int?[] { 1226 },
                    _ => new int?[] { 36780, 36780 }
                };
                var files = durations.Select((seconds, i) => new DiscoveredFileWithMetadata
                {
                    Path = $"/library/Callie Hart/The Rebel of Raleigh High/Recording {i + 1}.m4b",
                    Size = scenario == "rebel-copies" ? 584847243 : 100000 + i,
                    DurationSeconds = seconds,
                    AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        [scenario == "different-fields" && i == 1 ? "TITLE" : "ALBUM"] = new() { "The Rebel of Raleigh High (Raleigh Rebels #1) (Unabridged)" },
                        ["ARTIST"] = new() { "Callie Hart" }
                    }
                }).ToArray();
                if (scenario == "different-narrators")
                {
                    for (var i = 0; i < files.Length; i++) files[i].AllTags["NARRATOR"] = new() { i == 0 ? "Charlotte North" : "Sean Barrett" };
                }
                var context = manualPreview ? MatchingContextPresets.ForManualPreview(false) : MatchingContextPresets.ForAuthorReady();
                var result = matcher.MatchFilesToLibraryAsync(files, manualPreview ? null : author.Id, context).GetAwaiter().GetResult();
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));
                if (scenario == "different-narrators")
                {
                    Assert.That(result.MatchedFiles.Single(match => match.File.Path == files[0].Path).EditionId, Is.EqualTo(edition.Id));
                    Assert.That(result.MatchedFiles.Single(match => match.File.Path == files[1].Path).EditionId, Is.EqualTo(otherRecording.Id));
                    Assert.That(result.MatchedFiles.Select(match => match.IsStandaloneRecording), Is.All.True, "Distinct complete recordings must retain distinct import destinations.");
                    return;
                }
                Assert.That(result.MatchedFiles.Select(match => match.EditionId), Is.All.EqualTo(scenario == "monitored-tie" ? otherRecording.Id : edition.Id));
                Assert.That(result.MatchedFiles.Select(match => match.IsStandaloneRecording),
                    Is.All.EqualTo(scenario is "rebel-copies" or "different-fields" or "single-recording" or "monitored-tie"));
                if (scenario is "multipart" or "ambiguous-double")
                {
                    Assert.That(recalls, Is.LessThanOrEqualTo(2), "Complete-copy detection must not rematch every chapter.");
                }
                TestContext.Progress.WriteLine($"COMPLETE_RECORDING db={database.DatabaseType} manual={manualPreview} scenario={scenario} files={files.Length} recalls={recalls}");
            });
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class SharedGrabCommonEvidenceFixture
    {
        [Test]
        public void common_book_evidence_must_survive_chapter_details_without_stealing_a_sibling(
            [Values(false, true)] bool postgres,
            [Values(BookMatchingStrictness.Balanced, BookMatchingStrictness.Strict)] BookMatchingStrictness strictness,
            [Values("flat", "discs")] string layout,
            [Values(false, true)] bool manualPreview,
            [Values("chapter-title", "chapter-custom", "competing-title", "shared-title", "shared-album", "shared-custom", "shared-raw", "shared-rtl", "shared-unicode", "shared-japanese", "shared-repeated", "shared-dune", "shared-messiah", "partial-title")] string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var shared = scenario.StartsWith("shared-", StringComparison.Ordinal);
                var numbered = scenario.StartsWith("numbered-", StringComparison.Ordinal);
                var mixed = scenario == "mixed-dune" || numbered;
                var dunePair = mixed || scenario is "shared-dune" or "shared-messiah";
                var authorName = numbered ? "Casual Farmer" : dunePair ? "Frank Herbert" : scenario == "shared-rtl" ? "اسم المؤلف" : scenario == "shared-japanese" ? "宮沢賢治" :
                    scenario == "shared-unicode" ? "Renée Reader" : shared || scenario == "partial-title" ? "J.K. Rowling" : "Frank Herbert";
                var primaryTitle = numbered ? "Beware of Chicken 1" : dunePair ? "Dune" : scenario == "shared-rtl" ? "عنوان الكتاب" : scenario == "shared-japanese" ? "銀河鉄道の夜" :
                    scenario == "shared-repeated" ? "Never Never Land" : shared || scenario == "partial-title" ? "Harry Potter and the Philosopher's Stone" : "Dune";
                var author = authors.Insert(new Author { Name = authorName, CleanName = authorName, SortName = authorName,
                    TitleSlug = "frank-herbert", Path = "/library/Frank Herbert", Added = DateTime.UtcNow });
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                Book Seed(string title, string id)
                {
                    var book = books.Insert(new Book { AuthorId = author.Id, Title = title, CleanTitle = title.ToLowerInvariant(),
                        TitleSlug = id, MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow,
                        HardcoverBookId = "hc:" + id, BaseBookId = "hc:" + id, AnyEditionOk = true, SeriesName = numbered ? "Beware of Chicken" : "Dune" });
                    book.Editions = new List<Edition> { editions.Insert(new Edition { BookId = book.Id, Title = title,
                        MatchingTitle = title.ToLowerInvariant(), ForeignEditionId = "gr:" + id,
                        Narrator = "Simon Vance", NarratorNames = new() { "Simon Vance" }, DurationSeconds = 3600,
                        Format = "audiobook", ReadingFormatId = 2, Language = "eng", Monitored = true }) };
                    return book;
                }
                var dune = Seed(primaryTitle, "1001");
                var messiah = Seed(numbered ? "Beware of Chicken 2" : (shared && !dunePair) || scenario == "partial-title" ? "Harry Potter and the Goblet of Fire" : "Dune Messiah", "1002");
                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, events));
                var authorService = new AuthorService(authors, events, null, null, null, new CacheManager(), books,
                    null, logger, null, null, null, database, aliases);
                var editionService = new EditionService(editions, events, logger);
                var bookService = new BookService(books, editionService, events, authorService, null, null,
                    new SeriesBookLinkRepository(database, events), null, logger, providerAliasService: aliases);
                var fts = new EditionFtsRepository(database, logger);
                fts.RebuildIndex();
                var queries = 0;
                var search = Proxy<IEditionFtsRepository>((method, args) =>
                {
                    if (method.Name == nameof(IEditionFtsRepository.RecallBooks))
                    {
                        Assert.That(args[4], Is.EqualTo(20), "File matching retains the release candidate limit on both databases.");
                        queries++;
                    }
                    return method.Invoke(fts, args);
                });
                var matcher = new FileMatchingService(Proxy<IMatchingUploadLogger>((_, _) => null), null,
                    new ContainmentValidator(new TagNormalizer(), logger), null, null, null, null,
                    ConfigServiceTestProxy.Create(strictness, usePathAsTagsFallback: false), authorService, events, null,
                    search, bookService, editionService, editions, null, logger);
                var chapters = new[] { "The Departure", "Arrival", "The Desert", "La rencontre", "الصحراء", "Die Reise" };
                var fileCount = layout == "large" ? 360 : 12;
                var files = Enumerable.Range(0, fileCount).Select(index =>
                {
                    var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ALBUM"] = new() { "Dune" }, ["ARTIST"] = new() { author.Name },
                        ["NARRATOR"] = new() { "Simon Vance" },
                        ["TITLE"] = new() { scenario == "chapter-custom" ? "Dune" : chapters[index % chapters.Length] }
                    };
                    if (scenario == "chapter-custom") tags["CHAPTERTITLE"] = new() { chapters[index % chapters.Length] };
                    if (mixed) tags["TITLE"] = new() { $"{(index < 6 ? dune.Title : messiah.Title)} - Chapter {index % 6 + 1}" };
                    if (numbered) tags["ALBUM"] = new() { "Beware of Chicken" };
                    if (scenario == "numbered-no-series") tags.Remove("ALBUM");
                    if (scenario == "competing-title") tags["TITLE"] = new() { "Dune Messiah" };
                    if (shared || scenario == "partial-title")
                    {
                        var field = scenario == "shared-album" ? "ALBUM" : scenario == "shared-custom" ? "CUSTOM" : "TITLE";
                        var name = scenario == "shared-unicode" && index % 2 != 0 ? "Rene\u0301e Reader" : authorName;
                        var title = scenario == "partial-title" ? "Harry Potter" : scenario == "shared-messiah" ? "Dune Messiah" : primaryTitle;
                        var identity = name + " " + title;
                        var value = index % 2 == 0 ? $"{chapters[index % chapters.Length]} — {identity}" : $"{identity} — {chapters[index % chapters.Length]}";
                        if (scenario == "shared-japanese") value = index % 2 == 0 ? $"序章「{name}・{title}」" : $"「{name}・{title}」終章";
                        tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { [field] = new() { value } };
                        if (scenario == "shared-raw") tags["ID3v2:TIT2"] = new() { value };
                    }
                    return new DiscoveredFileWithMetadata
                    {
                        Path = $"/downloads/grab/{(layout == "discs" ? $"CD {index / 6 + 1}/" : "")}{index + 1:00}.mp3",
                        Size = mixed ? 1000000 + index : 1000000, DurationSeconds = mixed ? 600 : 3600 / fileCount, AllTags = tags
                    };
                }).ToArray();
                var allocatedBefore = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                var target = scenario == "shared-messiah" ? messiah : dune;
                var context = mixed
                    ? manualPreview ? MatchingContextPresets.ForManualPreview(false) : MatchingContextPresets.ForAuthorReady()
                    : manualPreview ? MatchingContextPresets.ForManualPreview(false, new[] { target.Id }) : MatchingContextPresets.ForDownloaded(false, new[] { target.Id });
                var result = matcher.MatchFilesToLibraryAsync(files, mixed && manualPreview ? null : author.Id, context).GetAwaiter().GetResult();
                timer.Stop();
                var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
                TestContext.Progress.WriteLine($"SHARED_GRAB constructed db={database.DatabaseType} mode={strictness} manual={manualPreview} layout={layout} scenario={scenario} matched={result.MatchedFiles.Length} unmatched={result.UnmatchedFiles.Length} recalls={queries} elapsedMs={timer.Elapsed.TotalMilliseconds:F2} allocatedBytes={allocated}");
                if (files.Length <= 12)
                {
                    foreach (var match in result.MatchedFiles) TestContext.Progress.WriteLine($"PICK {match.File.Path} -> {match.BookId}/{match.EditionId} via={match.MatchedVia}");
                }
                foreach (var miss in result.UnmatchedFiles) TestContext.Progress.WriteLine($"MISS {miss.File.Path} -> {miss.Reason}");
                if (scenario == "partial-title")
                {
                    Assert.That(result.MatchedFiles, Is.Empty);
                    Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(files.Length));
                    return;
                }
                if (mixed)
                {
                    Assert.That(result.UnmatchedFiles, Is.Empty);
                    Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));
                    foreach (var match in result.MatchedFiles)
                    {
                        var book = files.Take(6).Any(file => file.Path == match.File.Path) ? dune : messiah;
                        Assert.That((match.BookId, match.EditionId), Is.EqualTo((book.Id, book.Editions[0].Id)), match.File.Path);
                    }
                    Assert.That(queries, Is.LessThanOrEqualTo(6), "different catalog titles need separate decisions, not a query for every chapter");
                    return;
                }
                var expected = scenario is "competing-title" or "shared-messiah" ? messiah : dune;
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(result.MatchedFiles.All(match => match.BookId == expected.Id && match.EditionId == expected.Editions[0].Id), Is.True);
                if (shared)
                {
                    Assert.That(queries, Is.EqualTo(layout == "discs" ? 2 : 1), "shared identity must reuse the unit match");
                    foreach (var match in result.MatchedFiles)
                    {
                        Assert.That(match.IdentityProof.Values, Is.Not.Empty);
                        Assert.That(match.IdentityProof.Values.All(proof => match.File.AllTags.TryGetValue(proof.Field, out var values) && values.Contains(proof.Observed)), Is.True,
                            "membership proof must refer to the original physical field values");
                    }
                }
            });
        }

        [Test]
        public void shared_series_album_must_not_hide_a_more_specific_catalog_title(
            [Values(false, true)] bool postgres, [Values(false, true)] bool manualPreview,
            [Values("mixed-dune", "numbered-series", "numbered-no-series")] string scenario)
        {
            common_book_evidence_must_survive_chapter_details_without_stealing_a_sibling(
                postgres, BookMatchingStrictness.Balanced, "flat", manualPreview, scenario);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void large_chapter_unit_should_reuse_one_native_recall(bool postgres)
        {
            common_book_evidence_must_survive_chapter_details_without_stealing_a_sibling(
                postgres, BookMatchingStrictness.Balanced, "large", false, "shared-title");
        }

        [Test]
        public void repeated_title_in_another_field_must_not_split_the_recording(
            [Values(false, true)] bool postgres, [Values(false, true)] bool manualPreview,
            [Values(false, true)] bool swapFields, [Values(false, true)] bool firstFileLacksCommonField)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var author = authors.Insert(new Author { Name = "Jim Collins", CleanName = "jim collins", SortName = "Jim Collins",
                    TitleSlug = "jim-collins", Path = "/library/Jim Collins", Added = DateTime.UtcNow });
                var book = books.Insert(new Book { AuthorId = author.Id, Title = "Great by Choice", CleanTitle = "great by choice",
                    TitleSlug = "great-by-choice", MediaType = BookMediaType.Audiobook, AnyEditionOk = true,
                    Added = DateTime.UtcNow, BaseBookId = "gr:14041209" });
                Edition Edition(string providerId, int duration, bool monitored) => editions.Insert(new Edition
                {
                    BookId = book.Id, Title = "Great by Choice", MatchingTitle = "great by choice", ForeignEditionId = providerId,
                    Narrator = "Jim Collins", NarratorNames = new() { "Jim Collins" }, DurationSeconds = duration,
                    Format = "audiobook", ReadingFormatId = 2, Language = "eng", Monitored = monitored
                });
                var expected = Edition("gr:230041436-audiobook", 31500, true);
                Edition("gr:229962638-audiobook", 30780, false);
                var aliases = new ProviderAliasService(new ProviderAliasRepository(database, events));
                var authorService = new AuthorService(authors, events, null, null, null, new CacheManager(), books,
                    null, logger, null, null, null, database, aliases);
                var editionService = new EditionService(editions, events, logger);
                var bookService = new BookService(books, editionService, events, authorService, null, null,
                    new SeriesBookLinkRepository(database, events), null, logger, providerAliasService: aliases);
                var fts = new EditionFtsRepository(database, logger);
                fts.RebuildIndex();
                var matcher = new FileMatchingService(Proxy<IMatchingUploadLogger>((_, _) => null), null,
                    new ContainmentValidator(new TagNormalizer(), logger), null, null, null, null,
                    ConfigServiceTestProxy.Create(BookMatchingStrictness.Balanced, usePathAsTagsFallback: false), authorService,
                    events, null, fts, bookService, editionService, editions, null, logger);

                // Captured track titles/durations; swapping the physical keys is a constructed control.
                var titles = new[] { "Great By Choice", "Now in...", "Surprised by...", "Chapter 2", "Scott, in contrast...",
                    "In the late...", "So now we", "In early 1986", "Level 5", "How do people", "Chapter 3", "20 Mile",
                    "What makes", "While the 20", "Accomplishing", "What would you", "Chapter 4", "Gillette didn't",
                    "Bullets then", "Unfortunately an", "Empirical validation", "Apple itself", "Chapter 5", "We Conducted",
                    "So in summary", "There we go", "In contrast", "Chapter 6", "Think about", "Now here's", "Amending the",
                    "Consistency and", "Chapter 7", "What role", "The 10X Cases", "High ROL", "So we just", "Progessive and",
                    "Luck is not", "Epilogue", "Question: What", "Question: How", "Question: Do", "Acknowledgments" };
                var durations = new[] { 722, 731, 669, 512, 624, 670, 675, 645, 625, 642, 923, 448, 785, 562, 829, 876,
                    841, 651, 782, 792, 803, 710, 857, 894, 705, 856, 819, 638, 764, 821, 885, 686, 657, 626, 591, 735,
                    677, 833, 664, 570, 664, 568, 722, 706 };
                var commonField = swapFields ? "ID3v2:TIT2" : "ID3v2:TALB";
                var files = titles.Select((title, index) => new DiscoveredFileWithMetadata
                {
                    Path = $"/downloads/Great by Choice/Great by Choice ({index + 1}).mp3",
                    Size = 1000000 + index, DurationSeconds = durations[index],
                    AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        [commonField] = new() { index < 10 ? "Great by Choice" : "Great By Choice" },
                        [swapFields ? "ID3v2:TALB" : "ID3v2:TIT2"] = new() { title },
                        ["ID3v2:TPE1"] = new() { "Jim Collins" }, ["ID3v2:TPE2"] = new() { "Jim Collins" }
                    }
                }).ToArray();
                if (firstFileLacksCommonField)
                {
                    files[0].AllTags.Remove(commonField);
                }

                var context = manualPreview ? MatchingContextPresets.ForManualPreview(false) : MatchingContextPresets.ForAuthorReady();
                var result = matcher.MatchFilesToLibraryAsync(files, manualPreview ? null : author.Id, context).GetAwaiter().GetResult();
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(44));
                if (!firstFileLacksCommonField)
                {
                    Assert.That(result.MatchedFiles.Select(match => match.EditionId), Is.All.EqualTo(expected.Id));
                }

                Assert.That(result.MatchedFiles.Select(match => match.Provenance.DecisionId).Distinct().Count(),
                    Is.EqualTo(firstFileLacksCommonField ? 2 : 1),
                    "only a common physical title field permits using all 31,455 seconds as one recording");
                foreach (var match in result.MatchedFiles)
                {
                    var proofField = firstFileLacksCommonField && match.File.Path == files[0].Path
                        ? swapFields ? "ID3v2:TALB" : "ID3v2:TIT2"
                        : commonField;
                    Assert.That(match.IdentityProof.Values.Any(value => value.Role == MatchIdentityRole.Title && value.Field == proofField), Is.True);
                }
            });
        }
    }
}

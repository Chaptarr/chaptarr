using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class CorroboratedEditionTitleFixture
    {
        [TestCase(false, BookMatchingStrictness.Balanced, "captured", true)]
        [TestCase(true, BookMatchingStrictness.Balanced, "captured", true)]
        [TestCase(false, BookMatchingStrictness.Strict, "captured", false)]
        [TestCase(true, BookMatchingStrictness.Strict, "captured", false)]
        [TestCase(false, BookMatchingStrictness.Balanced, "missing-narrator", false)]
        [TestCase(true, BookMatchingStrictness.Balanced, "missing-narrator", false)]
        [TestCase(false, BookMatchingStrictness.Balanced, "wrong-narrator", false)]
        [TestCase(true, BookMatchingStrictness.Balanced, "wrong-narrator", false)]
        [TestCase(false, BookMatchingStrictness.Balanced, "wrong-duration", false)]
        [TestCase(true, BookMatchingStrictness.Balanced, "wrong-duration", false)]
        [TestCase(false, BookMatchingStrictness.Balanced, "missing-title", false)]
        [TestCase(true, BookMatchingStrictness.Balanced, "missing-title", false)]
        public void captured_recording_should_keep_its_qualified_edition(bool postgres, BookMatchingStrictness strictness, string input, bool expectedQualified)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var catalog = new Catalog(database, "Caro Claire Burke");
                catalog.Add("Yesteryear", null, "hc:1941363", "gr:228799994", "gr:239599885-audiobook", "Rebecca Lowman", 43200);
                var expected = catalog.Add("Yesteryear: A GMA Book Club Pick", "A Novel", null, "gr:306628943",
                    "az:B0FKV9JSWV-audiobook", "Rebecca Lowman", 49620);
                var tags = new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { "Yesteryear" },
                    ["ALBUM"] = new() { "Yesteryear: A Novel" },
                    ["ARTIST"] = new() { "Caro Claire Burke" },
                    ["ALBUMARTIST"] = new() { "Caro Claire Burke" },
                    ["COMPOSER"] = new() { "Rebecca Lowman" }
                };
                if (input == "missing-narrator") tags.Remove("COMPOSER");
                if (input == "wrong-narrator") tags["COMPOSER"] = new() { "Another Reader" };
                if (input == "missing-title") { tags.Remove("TITLE"); tags.Remove("ALBUM"); }
                var match = catalog.Match(tags, input == "wrong-duration" ? 43200 : 49635, strictness);
                TestContext.Progress.WriteLine($"{database.DatabaseType}: {strictness}, selected edition {match?.EditionId}, qualified edition {expected.Id}");
                Assert.That(match?.EditionId == expected.Id, Is.EqualTo(expectedQualified));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void extra_narrator_evidence_does_not_disqualify_the_matching_solo_edition(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var catalog = new Catalog(database, "Test Writer");
                catalog.Add("Paper Lanterns", null, "hc:100", "gr:200", "az:B000000001-audiobook", "Third Reader, Fourth Reader", 36000);
                var expected = catalog.Add("Paper Lanterns", null, "hc:100", "gr:200", "az:B000000002-audiobook", "First Reader", 36000);
                var match = catalog.Match(new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { "Paper Lanterns" }, ["ARTIST"] = new() { "Test Writer" },
                    ["NARRATOR"] = new() { "First Reader", "Second Reader" }
                }, 36000, BookMatchingStrictness.Balanced);
                Assert.That(match?.EditionId, Is.EqualTo(expected.Id));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void corroboration_should_not_choose_between_distinct_unproven_story_titles(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var catalog = new Catalog(database, "Arthur Conan Doyle");
                catalog.Add("Sherlock Holmes: A Study in Scarlet", null, "hc:100", "gr:200", "az:B000000001-audiobook", "First Reader", 18000);
                catalog.Add("Sherlock Holmes: The Sign of Four", null, "hc:300", "gr:400", "az:B000000002-audiobook", "First Reader", 18000);
                var match = catalog.Match(new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { "Sherlock Holmes" }, ["ARTIST"] = new() { "Arthur Conan Doyle" }, ["NARRATOR"] = new() { "First Reader" }
                }, 18000, BookMatchingStrictness.Balanced);
                Assert.That(match, Is.Null);
            });
        }

        [TestCase(false, "shared-work", true)]
        [TestCase(true, "shared-work", true)]
        [TestCase(false, "shared-edition", true)]
        [TestCase(true, "shared-edition", true)]
        [TestCase(false, "distinct", false)]
        [TestCase(true, "distinct", false)]
        [TestCase(false, "different-provider", false)]
        [TestCase(true, "different-provider", false)]
        public void overlapping_identity_should_not_block_the_best_edition(bool postgres, string identity, bool expectedQualified)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var catalog = new Catalog(database, "Test Writer");
                catalog.Add("Paper Lanterns", null, "hc:100", "gr:200", "az:B000000001-audiobook", "First Reader", 20000);
                var expected = catalog.Add("Paper Lanterns: Collected Stories", "Collected Stories",
                    identity == "different-provider" ? "hc:200" : "hc:300",
                    identity == "shared-work" ? "gr:200" : "gr:400",
                    identity == "shared-edition" ? "az:B000000001-audiobook" : "az:B000000002-audiobook", "First Reader", 36000);
                var match = catalog.Match(new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { "Paper Lanterns" }, ["ARTIST"] = new() { "Test Writer" }, ["NARRATOR"] = new() { "First Reader" }
                }, 36001, BookMatchingStrictness.Strict);
                Assert.That(match, Is.Not.Null);
                Assert.That(match.EditionId == expected.Id, Is.EqualTo(expectedQualified));
            });
        }

        [Test]
        public void partial_cast_corroboration_respects_solo_duration_and_narrator_evidence(
            [Values(false, true)] bool postgres,
            [Values("partial-only", "solo-same-duration", "solo-outside-duration", "more-names", "unrelated-solo", "provider-clone", "no-narrator", "wrong-duration", "strict")] string scenario,
            [Values(false, true)] bool selfNarrated)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var author = selfNarrated ? "First Reader" : "Test Writer";
                var catalog = new Catalog(database, author);
                Edition solo = null;
                if (scenario is "solo-same-duration" or "solo-outside-duration" or "more-names" or "unrelated-solo" or "provider-clone")
                {
                    solo = catalog.Add(scenario == "unrelated-solo" ? "Other Stories" : "Paper Lanterns", null, "hc:100", "gr:200", "az:B000000001-audiobook",
                        "First Reader", scenario == "solo-outside-duration" ? 20000 : 36000);
                }
                var cast = catalog.Add("Paper Lanterns: A Special Recording", null,
                    scenario == "unrelated-solo" ? "hc:300" : "hc:100",
                    scenario == "unrelated-solo" ? "gr:400" : "gr:200",
                    scenario == "provider-clone" ? "az:B000000001-audiobook" : "az:B000000002-audiobook",
                    "First Reader, Second Reader, Third Reader, Fourth Reader", 36000);
                var tags = new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { "Paper Lanterns" },
                    ["ARTIST"] = new() { author },
                    ["NARRATOR"] = new() { scenario == "more-names" ? "First Reader, Second Reader, Third Reader" : "First Reader" }
                };
                if (scenario == "no-narrator") tags.Remove("NARRATOR");
                var match = catalog.Match(tags, scenario == "wrong-duration" ? 48000 : 36001,
                    scenario == "strict" ? BookMatchingStrictness.Strict : BookMatchingStrictness.Balanced);
                if (scenario is "no-narrator" or "wrong-duration" or "strict")
                {
                    Assert.That(match, Is.Null);
                }
                else
                {
                    Assert.That(match, Is.Not.Null);
                    if (scenario == "provider-clone")
                    {
                        Assert.That(match.EditionId, Is.AnyOf(solo.Id, cast.Id));
                    }
                    else
                    {
                        Assert.That(match.EditionId, Is.EqualTo(scenario == "solo-same-duration" ? solo.Id : cast.Id));
                        if (scenario == "solo-same-duration")
                        {
                            Assert.That(catalog.TraceEvents.Any(item => item.EditionId == cast.Id && item.Reason == "CONTAINMENT_FAILED"), Is.True,
                                "A shared voice and matching duration must not prove the cast qualification when the solo recording also fits.");
                        }
                    }
                }
            });
        }

        [Test]
        public void self_narrated_solo_and_cast_recordings_require_distinct_narrator_evidence(
            [Values(false, true)] bool postgres,
            [Values("solo", "author-fields-only", "two-cast-voices")] string credits,
            [Values(false, true)] bool identicalTitles,
            [Values(false, true)] bool castFirst)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var catalog = new Catalog(database, "First Reader");
                Edition AddSolo() => catalog.Add("Paper Lanterns", null, "hc:100", "gr:200", "az:B000000002-audiobook", "First Reader", 36000);
                Edition AddCast() => catalog.Add(identicalTitles ? "Paper Lanterns" : "Paper Lanterns: A Special Recording", null, "hc:100", "gr:200", "az:B000000001-audiobook",
                    "First Reader, Second Reader, Third Reader, Fourth Reader", 36000);
                var first = castFirst ? AddCast() : AddSolo();
                var second = castFirst ? AddSolo() : AddCast();
                var solo = castFirst ? second : first;
                var cast = castFirst ? first : second;
                var tags = new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { "Paper Lanterns" },
                    ["ARTIST"] = new() { "First Reader" },
                    ["ALBUMARTIST"] = new() { "First Reader" }
                };
                if (credits != "author-fields-only")
                    tags["NARRATOR"] = new() { credits == "solo" ? "First Reader" : "First Reader, Second Reader" };

                var match = catalog.Match(tags, 36001, BookMatchingStrictness.Balanced);
                Assert.That(match, Is.Not.Null);
                Assert.That(match.EditionId, Is.EqualTo(credits == "two-cast-voices" ? cast.Id : solo.Id));
                if (credits == "author-fields-only" && !identicalTitles)
                    Assert.That(catalog.TraceEvents.Any(item => item.EditionId == cast.Id && item.Reason == "CONTAINMENT_FAILED"), Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void independent_author_credits_can_corroborate_a_self_narrated_cast_recording(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var catalog = new Catalog(database, "First Reader");
                var edition = catalog.Add("Paper Lanterns: A Special Recording", null, "hc:100", "gr:200", "az:B000000002-audiobook",
                    "First Reader, Second Reader, Third Reader", 36000);
                var match = catalog.Match(new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { "Paper Lanterns" },
                    ["ARTIST"] = new() { "First Reader" },
                    ["ALBUMARTIST"] = new() { "First Reader" }
                }, 36001, BookMatchingStrictness.Balanced);
                Assert.That(match, Is.Not.Null);
                Assert.That(match.EditionId, Is.EqualTo(edition.Id));
            });
        }

        [Test]
        public void repeated_author_credits_preserve_explicit_self_or_other_narrator(
            [Values(false, true)] bool postgres,
            [Values("First Reader", "Second Reader")] string narrator,
            [Values(false, true)] bool otherFirst,
            [Values(false, true)] bool swapFields)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var catalog = new Catalog(database, "First Reader");
                Edition AddSelf() => catalog.Add("Paper Lanterns", null, "hc:100", "gr:200", "az:B000000002-audiobook", "First Reader", 36000);
                Edition AddOther() => catalog.Add("Paper Lanterns", null, "hc:100", "gr:200", "az:B000000001-audiobook", "Second Reader", 36000);
                var first = otherFirst ? AddOther() : AddSelf();
                var second = otherFirst ? AddSelf() : AddOther();
                var selfNarrated = otherFirst ? second : first;
                var otherNarrated = otherFirst ? first : second;
                var match = catalog.Match(new Dictionary<string, List<string>>
                {
                    ["TITLE"] = new() { "Paper Lanterns" },
                    [swapFields ? "COMPOSER" : "ARTIST"] = new() { "First Reader" },
                    [swapFields ? "NARRATOR" : "ALBUMARTIST"] = new() { "First Reader" },
                    [swapFields ? "ARTIST" : "NARRATOR"] = new() { narrator }
                }, 36001, BookMatchingStrictness.Balanced);
                Assert.That(match, Is.Not.Null);
                Assert.That(match.EditionId, Is.EqualTo(narrator == "First Reader" ? selfNarrated.Id : otherNarrated.Id));
            });
        }

        [Test]
        public void captured_narrator_catalogs_keep_the_selected_recording(
            [Values(false, true)] bool postgres,
            [Values("Five People", "Listen In")] string name,
            [Values(false, true)] bool reverseMonitoring,
            [Values(false, true)] bool swapAsins)
        {
            AssertCapturedRecordingSelection(postgres, name, reverseMonitoring, swapAsins);
        }

        [Test]
        public void captured_duration_evidence_keeps_the_selected_recording(
            [Values(false, true)] bool postgres,
            [Values(false, true)] bool reverseMonitoring,
            [Values(false, true)] bool swapAsins)
        {
            AssertCapturedRecordingSelection(postgres, "Good to Great", reverseMonitoring, swapAsins);
        }

        private static void AssertCapturedRecordingSelection(bool postgres, string name, bool reverseMonitoring, bool swapAsins)
        {
            var samples = JArray.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
                "Fixtures", "Matching", "SelfNarratedRecordings.json")));
            var sample = samples.Single(item => (string)item["Name"] == name);
            DualDatabaseFixture.Run(postgres, database =>
            {
                var catalog = new Catalog(database, (string)sample["Author"]);
                var editions = new List<Edition>();
                var capturedIds = new List<int>();
                var capturedEditions = sample["Editions"].ToList();
                Assert.That(capturedEditions, Has.Count.EqualTo(2));
                foreach (var item in capturedEditions)
                {
                    capturedIds.Add((int)item["Id"]);
                    var asin = (string)capturedEditions[swapAsins ? 1 - editions.Count : editions.Count]["Asin"];
                    var edition = catalog.Add((string)item["Title"], (string)item["Subtitle"], "hc:100", "gr:200",
                        (string)item["ForeignEditionId"], (string)item["Narrator"], (int)item["DurationSeconds"],
                        reverseMonitoring ? !(bool)item["Monitored"] : (bool)item["Monitored"], item["NarratorNames"].ToObject<List<string>>(), asin);
                    Assert.That(edition.ForeignEditionId, Is.EqualTo((string)item["ForeignEditionId"]));
                    Assert.That(edition.Asin, Is.EqualTo(asin));
                    editions.Add(edition);
                }
                var match = catalog.Match(sample["Tags"].ToObject<Dictionary<string, List<string>>>(), (int)sample["Duration"], BookMatchingStrictness.Balanced);
                Assert.That(match, Is.Not.Null);
                var selected = capturedIds[editions.FindIndex(edition => edition.Id == match.EditionId)];
                TestContext.Progress.WriteLine($"{database.DatabaseType}: {name}, constructed tags {(bool)sample["ConstructedTags"]}, reversed monitoring {reverseMonitoring}, swapped captured ASINs {swapAsins}, selected captured edition {selected}");
                Assert.That(selected, Is.EqualTo(name == "Five People" ? 54404 : name == "Listen In" ? 106198 : 53573));
            });
        }

        private sealed class Catalog
        {
            private readonly IMainDatabase _database;
            private readonly Logger _logger = LogManager.GetCurrentClassLogger();
            private readonly IEventAggregator _events = Proxy<IEventAggregator>((method, args) => null);
            private readonly List<Book> _books = new();
            private readonly Author _author;
            public List<MatchingTraceEvent> TraceEvents { get; } = new();

            public Catalog(IMainDatabase database, string author)
            {
                _database = database;
                _author = new AuthorRepository(database, _events, _logger).Insert(new Author
                {
                    Name = author, CleanName = author, SortName = author, TitleSlug = "writer", Path = "/writer", Added = DateTime.UtcNow
                });
            }

            public Edition Add(string title, string subtitle, string hardcover, string goodreads, string editionId, string narrator, int duration, bool monitored = true, List<string> narratorNames = null, string asin = null)
            {
                var book = new BookRepository(_database, _events).Insert(new Book
                {
                    AuthorId = _author.Id, Title = title, CleanTitle = title, TitleSlug = "book-" + _books.Count, MediaType = BookMediaType.Audiobook,
                    BaseBookId = hardcover ?? goodreads, HardcoverBookId = hardcover, GoodreadsWorkId = goodreads,
                    AnyEditionOk = true, Added = DateTime.UtcNow, SeriesLinks = new List<SeriesBookLink>()
                });
                var edition = new EditionRepository(_database, _events).Insert(new Edition
                {
                    BookId = book.Id, Title = title, Subtitle = subtitle, ForeignEditionId = editionId, ReadingFormatId = 2,
                    Narrator = narrator, NarratorNames = narratorNames ?? new() { narrator }, DurationSeconds = duration, Language = "eng", Monitored = monitored,
                    Asin = asin ?? (editionId.StartsWith("az:") ? editionId.Substring(3).Replace("-audiobook", "") : null)
                });
                book.Editions = new List<Edition> { edition };
                _books.Add(book);
                return edition;
            }

            public FileMatch Match(Dictionary<string, List<string>> tags, int duration, BookMatchingStrictness strictness)
            {
                var fts = new EditionFtsRepository(_database, _logger);
                fts.RebuildIndex();
                var books = Proxy<IBookService>((method, args) => method.Name switch
                {
                    nameof(IBookService.GetBook) => _books.SingleOrDefault(book => book.Id == (int)args[0]),
                    nameof(IBookService.GetBooks) => _books.Where(book => ((IEnumerable<int>)args[0]).Contains(book.Id)).ToList(),
                    nameof(IBookService.GetBooksByAuthor) => _books.Where(book => book.AuthorId == (int)args[0]).ToList(),
                    _ => throw new AssertionException("Unexpected book read " + method.Name)
                });
                var authors = Proxy<IAuthorService>((method, args) => _author);
                var matcher = new FileMatchingService(null, null, new ContainmentValidator(new TagNormalizer(), _logger),
                    null, null, null, null, ConfigServiceTestProxy.Create(strictness: strictness, usePathAsTagsFallback: false),
                    authors, null, null, fts, books, null, null, null, _logger);
                ((System.Threading.AsyncLocal<IMatchingTraceSink>)typeof(FileMatchingService)
                    .GetField("_matchingTraceSink", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(matcher)).Value =
                    Proxy<IMatchingTraceSink>((method, args) =>
                    {
                        TraceEvents.Add((MatchingTraceEvent)args[0]);
                        return null;
                    });
                return matcher.HolyGrailMatchFile(new DiscoveredFileWithMetadata
                {
                    Path = "/downloads/recording.m4b", DurationSeconds = duration, AllTags = tags
                }, BookMediaType.Audiobook, restrictToAuthorId: _author.Id);
            }
        }
    }
}

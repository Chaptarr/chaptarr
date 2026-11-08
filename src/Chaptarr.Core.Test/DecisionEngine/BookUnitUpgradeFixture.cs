using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class BookUnitUpgradeFixture
    {
        private sealed class UnexpectedRelationLoad<T> : LazyLoaded<T>
        {
            public override void LazyLoad() => throw new AssertionException("Grab-time unit scoring must reuse already-known book/author and representative edition");
        }

        [Test]
        public void repository_loaded_audiobook_is_scored_as_whole_book_at_grab_time(
            [Values(false, true)] bool postgres,
            [Values("single", "part-zero", "numbered", "scattered", "other-row", "mixed-quality", "smaller-release")] string scenario,
            [Values(100, -100)] int score)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var format = new CustomFormat("Whole recording size", new SizeSpecification { Min = 30d / (1024 * 1024 * 1024), Max = 1 }) { Id = 17 };
                var profile = new QualityProfile { Id = 7, Name = "Fixture audio", ProfileType = ProfileType.Audiobook,
                    UpgradeAllowed = scenario == "smaller-release", PreferCustomFormatsOverQuality = true, Cutoff = Quality.MP3.Id,
                    CutoffFormatScore = score > 0 ? score : 0,
                    Items = new() { new QualityProfileQualityItem { Quality = Quality.MP3, Allowed = true }, new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true } },
                    FormatItems = new() { new ProfileFormatItem { Format = format, Score = score } } };
                var author = new Author { Name = "Fixture Writer", CleanName = "fixturewriter", SortName = "fixturewriter", TitleSlug = "fixture-writer",
                    Path = "/fixture-library", AudiobookRootFolderPath = "/fixture-library", Added = DateTime.UtcNow,
                    AudiobookQualityProfileId = profile.Id, AudiobookQualityProfile = profile };
                new AuthorRepository(database, events, logger).Insert(author);
                var book = new Book { AuthorId = author.Id, Author = author, Title = "Fixture Book", CleanTitle = "fixturebook", TitleSlug = "fixture-book",
                    MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow };
                new BookRepository(database, events).Insert(book);
                var edition = new Edition { BookId = book.Id, Book = book, Title = book.Title, ForeignEditionId = "local:book-unit", Monitored = true, ReadingFormatId = 2 };
                new EditionRepository(database, events).Insert(edition);
                book.Editions = new() { edition };
                var repository = new MediaFileRepository(database, events);
                var count = scenario is "single" or "other-row" ? 1 : 2;
                var totalSize = scenario == "other-row" ? 20 : 40;
                for (var part = 1; part <= count; part++)
                {
                    repository.Insert(new BookFile { EditionId = edition.Id, Path = scenario == "scattered"
                            ? $"/fixture-library/folder{part}/part.mp3" : $"/fixture-library/part{part}.mp3",
                        Size = totalSize / count, Part = scenario == "numbered" ? part : 0, PartCount = count,
                        Quality = new QualityModel(scenario == "mixed-quality" && part == 2 ? Quality.M4B : Quality.MP3), MediaInfo = new MediaInfoModel(),
                        DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                        AllTags = new Dictionary<string, List<string>> { ["COMMENT"] = new() { "Part " + part } } });
                }
                if (scenario == "other-row")
                {
                    var other = new Book { AuthorId = author.Id, Author = author, Title = "Other recording", CleanTitle = "otherrecording",
                        TitleSlug = "other-recording", MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow };
                    new BookRepository(database, events).Insert(other);
                    var otherEdition = new Edition { BookId = other.Id, Title = other.Title, ForeignEditionId = "local:other-recording", ReadingFormatId = 2 };
                    new EditionRepository(database, events).Insert(otherEdition);
                    repository.Insert(new BookFile { EditionId = otherEdition.Id, Path = "/fixture-library/other-recording.mp3", Size = 40,
                        Quality = new QualityModel(Quality.MP3), MediaInfo = new MediaInfoModel(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow });
                }
                book.BookFiles = repository.GetFilesByBook(book.Id);
                Assert.That(book.BookFiles.Select(file => file.PartCount), Is.All.Zero, "Use actual repository-loaded transient counts");
                foreach (var file in book.BookFiles) file.LazyAuthor = new UnexpectedRelationLoad<Author>();
                foreach (var file in book.BookFiles.Skip(1)) file.LazyEdition = new UnexpectedRelationLoad<Edition>();
                var calculator = new CustomFormatCalculationService(InterfaceProxy.Proxy<ICustomFormatService>((_, _) => new List<CustomFormat> { format }), logger);
                var release = new RemoteBook { Author = author, Books = new() { book }, Release = new ReleaseInfo { Title = "Fixture Writer - Fixture Book", Size = scenario == "smaller-release" ? 20 : totalSize },
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(scenario == "mixed-quality" ? Quality.M4B : Quality.MP3) } };
                release.CustomFormats = calculator.ParseCustomFormat(release, release.Release.Size);
                Assert.That(release.CustomFormats.Select(item => item.Id), Is.EqualTo(scenario is "other-row" or "smaller-release" ? Array.Empty<int>() : new[] { format.Id }));
                var parsed = new List<BookFile>();
                var countedFormats = InterfaceProxy.Proxy<ICustomFormatCalculationService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(ICustomFormatCalculationService.ParseCustomFormat)));
                    var input = (BookFile)args[0]; parsed.Add(input);
                    return args.Length == 2 ? calculator.ParseCustomFormat(input, (Author)args[1]) : calculator.ParseCustomFormat(input);
                });
                var upgradable = new UpgradableSpecification(ConfigServiceTestProxy.Create(), logger);
                var disk = new UpgradeDiskSpecification(upgradable, new CacheManager(), countedFormats, logger).IsSatisfiedBy(release, null);
                Assert.That(disk.Accepted, Is.EqualTo(scenario == "smaller-release" && score < 0),
                    "A smaller unpenalized release improves a whole penalized recording; identical or less-preferred releases do not");
                AssertSingleUnit(parsed, count, totalSize); parsed.Clear();
                var allowed = new UpgradeAllowedSpecification(upgradable, logger, countedFormats).IsSatisfiedBy(release, null);
                Assert.That(allowed.Accepted, Is.EqualTo(scenario != "mixed-quality"), "Equal preferences cannot fake an upgrade, while mixed-quality improvements retain the upgrade-allowed check");
                AssertSingleUnit(parsed, count, totalSize); parsed.Clear();
                var cutoff = new CutoffSpecification(upgradable, countedFormats, logger).IsSatisfiedBy(release, null);
                Assert.That(cutoff.Accepted, Is.EqualTo(scenario == "other-row" ? score > 0 : score < 0), "Cutoff must use the complete recording score, including penalties");
                AssertSingleUnit(parsed, count, totalSize);
                Assert.That(book.BookFiles.Select(file => file.Size), Is.All.EqualTo(totalSize / count), "Scoring projection must not rewrite loaded files");
            });
        }

        private static void AssertSingleUnit(List<BookFile> parsed, int count, long size)
        {
            Assert.That(parsed, Has.Count.EqualTo(1), "Custom formats are evaluated once per stored book unit");
            Assert.That(parsed.Single().Size, Is.EqualTo(size));
            Assert.That(parsed.Single().AllTags.Single(pair => string.Equals(pair.Key, "COMMENT", StringComparison.OrdinalIgnoreCase)).Value, Has.Count.EqualTo(count));
        }
    }
}

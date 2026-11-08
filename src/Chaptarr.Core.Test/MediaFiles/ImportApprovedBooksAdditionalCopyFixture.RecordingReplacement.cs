using Chaptarr.Core.Test.Common;
using NzbDrone.Core.Profiles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        [Test]
        public void repository_loaded_recording_parts_keep_size_and_union_evidence_for_replacement(
            [Values(false, true)] bool postgres, [Values("identical", "part-zero", "production-union", "ebook", "original-path", "single")] string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var (profile, author, book, edition) = CreateAudiobookConversionGraph(489);
                var logger = LogManager.GetCurrentClassLogger();
                var events = new NoOpEventAggregator();
                profile.UpgradeAllowed = true;
                profile.PreferCustomFormatsOverQuality = true;
                profile.ConvertToQualityId = null;
                author.Id = 0; author.Path = "/library"; author.CleanName = "writer"; author.SortName = "writer";
                author.TitleSlug = "writer"; author.Added = DateTime.UtcNow;
                new NzbDrone.Core.Books.AuthorRepository(database, events, logger).Insert(author);
                book.Id = 0; book.AuthorId = author.Id; book.CleanTitle = "blacksheep"; book.Added = DateTime.UtcNow;
                if (scenario == "ebook") book.MediaType = NzbDrone.Core.Books.BookMediaType.Ebook;
                new NzbDrone.Core.Books.BookRepository(database, events).Insert(book);
                edition.Id = 0; edition.BookId = book.Id;
                edition.NarratorNames = new List<string> { "Tim Gerard Reynolds", "John Curless" };
                new NzbDrone.Core.Books.EditionRepository(database, events).Insert(edition);
                var format = scenario == "original-path"
                    ? new CustomFormat("Original retail", new ReleaseTitleSpecification { Value = "Retail" }) { Id = 97 }
                    : new CustomFormat("Size", new SizeSpecification { Min = 15d / (1024 * 1024 * 1024), Max = 1 }) { Id = 97 };
                profile.FormatItems = new List<ProfileFormatItem> { new() { Format = format, Score = 100 } };
                var files = new MediaFileRepository(database, events);
                foreach (var part in scenario == "single" ? new[] { 1 } : new[] { 1, 2 })
                {
                    files.Insert(new BookFile
                    {
                        Path = $"/library/renamed{part}." + (scenario == "ebook" ? (part == 1 ? "epub" : "mobi") : "mp3"),
                        OriginalFilePath = scenario == "original-path" ? $"/source/Retail{part}.mp3" : null,
                        EditionId = edition.Id, Size = 10, Part = scenario == "part-zero" ? 0 : part, PartCount = 2,
                        MediaInfo = new MediaInfoModel(),
                        Quality = new QualityModel(scenario == "ebook" ? Quality.EPUB : Quality.MP3),
                        DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                        AllTags = new Dictionary<string, List<string>> { ["NARRATOR"] = new() { edition.NarratorNames[part - 1] } },
                        IsGraphicAudio = scenario == "production-union" && part == 2
                    });
                }
                var loaded = files.GetFilesByBook(book.Id);
                Assert.That(loaded.Select(file => file.PartCount), Is.All.Zero, "PartCount is transient; whole-row scoring must not depend on it");
                var incoming = new LocalBook
                {
                    Author = author, Book = book, Edition = edition, Path = "/download/Retail.mp3", Size = scenario == "single" ? 10 : 20,
                    Quality = new QualityModel(scenario == "ebook" ? Quality.EPUB : scenario == "production-union" ? Quality.M4B : Quality.MP3),
                    IsGraphicAudio = scenario == "production-union",
                    RawTags = new RawFileTags { AllTags = new Dictionary<string, List<string>> { ["NARRATOR"] = edition.NarratorNames.ToList() } }
                };
                var calculator = RecordingFormats(format);
                var storedSizes = new List<long>();
                var formats = InterfaceProxy.Proxy<ICustomFormatCalculationService>((_, args) =>
                {
                    var stored = (BookFile)args[0]; storedSizes.Add(stored.Size);
                    return calculator.ParseCustomFormat(stored);
                });
                var service = new ImportApprovedBooks(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                    logger, configService: CreateConfigService(), diskProvider: InterfaceProxy.Proxy<IDiskProvider>((_, _) => true),
                    customFormatCalculationService: formats);
                var method = typeof(ImportApprovedBooks).GetMethod("CheckReplacement", BindingFlags.NonPublic | BindingFlags.Instance);
                var result = ((ImportResultType Result, string Reason))method.Invoke(service, new object[]
                {
                    new[] { incoming }, loaded, profile, false,
                    new Dictionary<LocalBook, List<CustomFormat>> { [incoming] = calculator.ParseCustomFormat(incoming) }, loaded
                });
                Assert.That(result.Result, Is.EqualTo(scenario is "production-union" or "ebook" ? ImportResultType.Imported : ImportResultType.Skipped), result.Reason);
                Assert.That(storedSizes, Is.EqualTo(scenario == "ebook" ? new long[] { 10, 10 } : scenario == "single" ? new long[] { 10 } : new long[] { 20 }));
                Assert.That(loaded.Select(file => file.PartCount), Is.All.Zero, "Scoring must not mutate loaded rows");
            });
        }

        [Test]
        public void stored_book_row_is_one_unit_across_editions_folders_and_part_numbers_but_copies_use_other_rows()
        {
            var first = RecordingPart(100, "/library/disc1/one.mp3", 10);
            var second = RecordingPart(101, "/library/disc2/two.mp3", 20);
            var other = RecordingPart(200, "/library/copy/one.mp3", 30);
            other.Book.Id = 11; other.Edition.BookId = 11;
            var files = new[] { first, second, other }.Select((part, index) => new BookFile
            {
                Id = index + 1, Path = part.Path, Size = part.Size, Edition = part.Edition,
                EditionId = part.Edition.Id, Author = part.Author, Quality = part.Quality
            }).ToList();
            var groups = RecordingCustomFormatHelper.GroupStoredForScoring(files);
            Assert.That(groups.Select(group => RecordingCustomFormatHelper.CreateStoredInput(group).Size), Is.EquivalentTo(new long[] { 30, 30 }));
            Assert.That(groups.Single(group => group.Contains(files[0])), Does.Contain(files[1]));
            Assert.That(files.Select(file => file.Part), Is.All.Zero);
            Assert.That(RecordingCustomFormatHelper.GroupForScoring(new[] { first, second, other }), Has.Count.EqualTo(3),
                "Pending incoming recordings retain edition identity until correctly assigned to a stored row");
        }

        [TestCase("size-reward", 10, 20, 100, ImportResultType.Skipped)]
        [TestCase("size-penalty", 10, 10, -100, ImportResultType.Imported)]
        [TestCase("real-upgrade", 5, 20, 100, ImportResultType.Imported)]
        [TestCase("real-rejection", 10, 10, 100, ImportResultType.Skipped)]
        [TestCase("tag-union", 10, 20, 100, ImportResultType.Skipped)]
        [TestCase("production-union", 10, 20, 100, ImportResultType.Imported)]
        public void replacement_scores_existing_and_incoming_recordings_with_the_same_size_and_tag_boundary(
            string scenario, int storedPartSize, int incomingSize, int formatScore, ImportResultType expected)
        {
            var (profile, author, book, edition) = CreateAudiobookConversionGraph(488);
            profile.UpgradeAllowed = true;
            profile.ConvertToQualityId = null;
            profile.PreferCustomFormatsOverQuality = true;
            edition.NarratorNames = new List<string> { "Tim Gerard Reynolds", "John Curless" };
            var format = scenario == "tag-union"
                ? new CustomFormat("Complete cast", new PreferredNarratorCompleteSpecification()) { Id = 91 }
                : new CustomFormat("Recording size", new SizeSpecification { Min = 15d / (1024 * 1024 * 1024), Max = 1 }) { Id = 91 };
            profile.FormatItems = new List<ProfileFormatItem> { new() { Format = format, Score = formatScore } };
            var calculator = RecordingFormats(format);
            var parsedStored = new List<BookFile>();
            var formats = InterfaceProxy.Proxy<ICustomFormatCalculationService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(ICustomFormatCalculationService.ParseCustomFormat)));
                if (args[0] is BookFile stored)
                {
                    parsedStored.Add(stored);
                    return calculator.ParseCustomFormat(stored);
                }

                return calculator.ParseCustomFormat((LocalBook)args[0]);
            });
            var storedParts = Enumerable.Range(1, 2).Select(part => new BookFile
            {
                Id = part, Path = $"/library/recording/part{part}.mp3", Size = storedPartSize,
                EditionId = edition.Id, Edition = edition, Author = author,
                Part = part, PartCount = 2, Quality = new QualityModel(Quality.MP3),
                AllTags = new Dictionary<string, List<string>> { ["NARRATOR"] = new() { edition.NarratorNames[part - 1] } }
            }).ToList();
            var unrelated = storedParts[0].CloneForImport();
            unrelated.Id = 3;
            unrelated.EditionId = edition.Id + 1;
            unrelated.Edition = new NzbDrone.Core.Books.Edition { Id = unrelated.EditionId, BookId = book.Id + 1,
                Book = new NzbDrone.Core.Books.Book { Id = book.Id + 1, MediaType = book.MediaType } };
            unrelated.Size = 1000;
            unrelated.Path = "/library/another-recording/part1.mp3";
            var incoming = new LocalBook
            {
                Path = "/download/recording.mp3", Author = author, Book = book, Edition = edition,
                Quality = new QualityModel(Quality.MP3), Size = incomingSize,
                RawTags = new RawFileTags { AllTags = new Dictionary<string, List<string>> { ["NARRATOR"] = edition.NarratorNames.ToList() } }
            };
            if (scenario == "production-union")
            {
                storedParts[1].AllTags["PRODUCTION"] = new() { "GraphicAudio" };
                incoming.IsGraphicAudio = true;
                incoming.Quality = new QualityModel(Quality.M4B);
            }
            var cache = new Dictionary<LocalBook, List<CustomFormat>> { [incoming] = calculator.ParseCustomFormat(incoming) };
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IDiskProvider.FileExists)));
                return true;
            });
            var service = new ImportApprovedBooks(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                LogManager.GetCurrentClassLogger(), configService: CreateConfigService(), diskProvider: disk, customFormatCalculationService: formats);
            var method = typeof(ImportApprovedBooks).GetMethod("CheckReplacement", BindingFlags.Instance | BindingFlags.NonPublic);
            var result = ((ImportResultType Result, string Reason))method.Invoke(service,
                new object[] { new[] { incoming }, storedParts.Append(unrelated).ToArray(), profile, false, cache, null });

            Assert.That(result.Result, Is.EqualTo(expected), result.Reason);
            Assert.That(parsedStored, Has.Count.EqualTo(1), "Parse the existing recording once, not each part");
            Assert.That(parsedStored.Single().Size, Is.EqualTo(storedPartSize * 2));
            Assert.That(parsedStored.Single().AllTags["NARRATOR"], Is.EquivalentTo(edition.NarratorNames));
            Assert.That(storedParts.Select(file => file.Size), Is.All.EqualTo(storedPartSize));
            Assert.That(storedParts.Select(file => file.AllTags["NARRATOR"].Count), Is.All.EqualTo(1));
            if (scenario is "size-reward" or "tag-union")
                Assert.That(result.Reason, Does.Contain("score 100"), "The complete stored recording must not look like a weaker per-part score");
        }
    }
}

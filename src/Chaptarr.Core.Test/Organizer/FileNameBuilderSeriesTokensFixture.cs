using System;
using System.Collections.Generic;
using System.IO;
using Chaptarr.Api.V1.Config;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Organizer
{
    [TestFixture]
    public class FileNameBuilderSeriesTokensFixture
    {
        private class StubNamingConfigService : INamingConfigService
        {
            public NamingConfig GetConfig() => NamingConfig.Default;
            public void Save(NamingConfig namingConfig) { }
        }

        private class StubQualityDefinitionService : IQualityDefinitionService
        {
            public void Update(QualityDefinition qualityDefinition) => throw new NotImplementedException();
            public void UpdateMany(List<QualityDefinition> qualityDefinitions) => throw new NotImplementedException();
            public List<QualityDefinition> All() => throw new NotImplementedException();
            public QualityDefinition GetById(int id) => throw new NotImplementedException();
            public QualityDefinition Get(Quality quality) => new QualityDefinition(quality);
        }

        private class StubCustomFormatCalculationService : ICustomFormatCalculationService
        {
            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => new();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => new();
        }

        [TestCase("1", "00", "01")]
        [TestCase("3", "000", "003")]
        [TestCase("10", "00", "10")]
        [TestCase("123", "00", "123")]
        [TestCase("0", "00", "00")]
        [TestCase("2.5", "00", "02.5")]
        [TestCase("0.50", "000", "000.50")]
        [TestCase("-2.5", "00", "-02.5")]
        [TestCase("+2", "00", "+02")]
        [TestCase("003", "00", "003")]
        [TestCase("1234567890123456789012345678901234567890", "00", "1234567890123456789012345678901234567890")]
        [TestCase("2.5", null, "2.5")]
        [TestCase("3", "0", "3")]
        [TestCase("3", "bogus", "3")]
        [TestCase("3", "smart", "3")]
        [TestCase("1-3", "00", "1-3")]
        [TestCase("1,2,3", "00", "1,2,3")]
        [TestCase("3.5,4.5,6.7", "00", "3.5,4.5,6.7")]
        [TestCase("2A", "00", "2A")]
        [TestCase("Part 1", "00", "Part 1")]
        [TestCase("序章", "00", "序章")]
        [TestCase("2,5", "00", "2,5")]
        [TestCase("2.5.1", "00", "2.5.1")]
        [TestCase(null, "00", null)]
        [TestCase("", "00", "")]
        [SetCulture("fr-FR")]
        public void should_zero_pad_numeric_series_positions_without_changing_their_meaning(string position, string format, string expected)
        {
            var builder = CreateBuilder();
            var author = new Author { Name = "Jim Butcher" };
            var book = new Book { Author = author, SeriesName = "Dresden Files", SeriesPosition = position };
            var edition = new Edition { Title = "Grave Peril", Book = book };
            var suffix = format == null ? "" : ":" + format;
            var pattern = "{Book Series}/Book {Book SeriesPosition" + suffix + " - }{Book Title}";

            foreach (var mediaType in new[] { "audiobook", "ebook" })
            {
                var config = NamingConfig.Default;
                config.RenameBooks = true;
                config.EbookRenameBooks = true;
                config.StandardBookFormat = pattern;
                config.EbookStandardBookFormat = pattern;
                var file = new BookFile
                {
                    Path = @"C:\books\Grave Peril.m4b".AsOsAgnostic(),
                    MediaType = mediaType,
                    Quality = new QualityModel(mediaType == "ebook" ? Quality.EPUB : Quality.M4B)
                };

                var result = builder.BuildBookFileName(author, edition, file, config, new List<CustomFormat>());

                var positionPart = string.IsNullOrEmpty(expected) ? "" : expected + " - ";
                Assert.That(result, Is.EqualTo(Path.Combine("Dresden Files", "Book " + positionPart + "Grave Peril")), mediaType);
            }
        }

        [TestCase("audiobook")]
        [TestCase("ebook")]
        public void should_render_padded_series_position_in_naming_examples_from_series_links(string mediaType)
        {
            var builder = CreateBuilder();
            var controller = new NamingConfigController(new StubNamingConfigService(),
                new FileNameSampleService(builder), new FileNameValidationService(), builder);
            var pattern = "{Book Series}/{Book SeriesPosition:00 - }{Book Title}";
            var config = new NamingConfigResource
            {
                Id = 1,
                RenameBooks = true,
                EbookRenameBooks = true,
                StandardBookFormat = mediaType == "audiobook" ? pattern : "{Book Title}",
                EbookStandardBookFormat = mediaType == "ebook" ? pattern : "{Book Title}"
            };

            var examples = (NamingExampleResource)controller.GetExamples(config, mediaType);

            Assert.That(examples.SingleBookExample, Is.EqualTo(Path.Combine("Series Title", "01 - The Edition Title")));
        }

        private static FileNameBuilder CreateBuilder()
        {
            return new FileNameBuilder(new StubNamingConfigService(), new StubQualityDefinitionService(),
                new CacheManager(), new StubCustomFormatCalculationService(), LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_render_series_tokens_from_book_fields_when_links_not_loaded()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "Jim Butcher" };
            var book = new Book
            {
                Author = author,
                SeriesName = "Dresden Files",
                SeriesPosition = "3"
            };
            var edition = new Edition { Title = "Grave Peril", Book = book };
            var bookFile = new BookFile
            {
                Path = "/books/Grave Peril.m4b",
                Quality = new QualityModel(Quality.Unknown)
            };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = "{Book SeriesTitle}";

            var result = builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            Assert.That(result, Is.EqualTo("Dresden Files #3"));
        }

        [Test]
        public void should_drop_connector_only_series_path_segments_when_series_is_missing()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "Jim Butcher" };
            var book = new Book { Author = author };
            var edition = new Edition
            {
                Title = "Standalone Book",
                Book = book,
                Narrator = "James Marsters"
            };

            var bookFile = new BookFile
            {
                Path = "/books/Standalone Book.m4b",
                Quality = new QualityModel(Quality.M4B)
            };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = "{Author Name}/{Book Series}/{Book Series}{, Book SeriesPosition}/{Book Title}{ - Narrator}/{Book Title}{ - Narrator}";

            var result = builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            Assert.That(result, Is.EqualTo(Path.Combine("Jim Butcher", "Standalone Book - James Marsters", "Standalone Book - James Marsters")));
        }

        [Test]
        public void should_keep_comma_connector_when_series_position_exists()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "Jim Butcher" };
            var book = new Book
            {
                Author = author,
                SeriesName = "Dresden Files",
                SeriesPosition = "3"
            };
            var edition = new Edition
            {
                Title = "Grave Peril",
                Book = book,
                Narrator = "James Marsters"
            };

            var bookFile = new BookFile
            {
                Path = "/books/Grave Peril.m4b",
                Quality = new QualityModel(Quality.M4B)
            };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = "{Author Name}/{Book Series}/{Book Series}{, Book SeriesPosition}/{Book Title}{ - Narrator}/{Book Title}{ - Narrator}";

            var result = builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            Assert.That(result, Is.EqualTo(Path.Combine("Jim Butcher", "Dresden Files", "Dresden Files, 3", "Grave Peril - James Marsters", "Grave Peril - James Marsters")));
        }

        // Known gap: TrimSeparatorsRegex only collapses connectors at the anchored start/end of a
        // path segment. When a token in the MIDDLE of a segment is empty and dashes/commas live
        // BETWEEN braces (not as wrapping prefix/suffix), they dangle. The bidirectional comma-aware
        // trim added 2026-05-12 deliberately does not address this — it's a separate fix that would
        // need to extend FileNameCleanupRegex or add an interior-connector collapser. Pin the
        // current output so a future "this looks ugly" fix updates this test intentionally.
        [Test]
        public void mid_string_connectors_dangle_when_inner_token_is_empty()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "Jim Butcher" };
            var book = new Book { Author = author };
            var edition = new Edition
            {
                Title = "Grave Peril",
                Book = book,
                Narrator = string.Empty
            };

            var bookFile = new BookFile
            {
                Path = "/books/Grave Peril.m4b",
                Quality = new QualityModel(Quality.M4B)
            };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            // Literal " - " BETWEEN braces (not as wrapping prefix/suffix). When {Narrator} is empty,
            // the surrounding " - " literals remain in the middle of the segment.
            namingConfig.StandardBookFormat = "{Author Name}/{Book Title} - {Narrator} - {Quality Title}";

            var result = builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            var segments = result.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.None);
            Assert.That(segments[0], Is.EqualTo("Jim Butcher"));
            // Today's behavior: "Grave Peril -  - M4B" → cleanup collapses double-space → "Grave Peril - - M4B".
            // When mid-string dangling is fixed, update this assertion to "Grave Peril - M4B".
            Assert.That(segments[1], Is.EqualTo("Grave Peril - - M4B"));
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
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
    public class FileNameBuilderMultiPartFixture
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

        [Test]
        public void should_preserve_original_filename_for_multipart_when_pattern_has_no_disambiguator()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "J.K. Rowling" };
            var book = new Book { Author = author };
            var edition = new Edition { Title = "Harry Potter and the Prisoner of Azkaban", Book = book };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = "{Book Title}/{Book Title}";

            var bookFile1 = new BookFile
            {
                Path = "/downloads/01-owl_post.mp3",
                Quality = new QualityModel(Quality.MP3),
                Part = 1,
                PartCount = 22
            };

            var bookFile2 = new BookFile
            {
                Path = "/downloads/02-aunt_marges_big_mistake.mp3",
                Quality = new QualityModel(Quality.MP3),
                Part = 2,
                PartCount = 22
            };

            var result1 = builder.BuildBookFileName(author, edition, bookFile1, namingConfig, customFormats: new List<CustomFormat>());
            var result2 = builder.BuildBookFileName(author, edition, bookFile2, namingConfig, customFormats: new List<CustomFormat>());

            Assert.That(result1, Is.EqualTo(Path.Combine(edition.Title, "01-owl_post")));
            Assert.That(result2, Is.EqualTo(Path.Combine(edition.Title, "02-aunt_marges_big_mistake")));
            Assert.That(result1, Is.Not.EqualTo(result2));
        }

        [Test]
        public void should_not_override_filename_when_pattern_includes_part_number()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "J.K. Rowling" };
            var book = new Book { Author = author };
            var edition = new Edition { Title = "Harry Potter and the Prisoner of Azkaban", Book = book };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = "{Book Title}/{Book Title} {PartNumber:00}";

            var bookFile = new BookFile
            {
                Path = "/downloads/02-aunt_marges_big_mistake.mp3",
                Quality = new QualityModel(Quality.MP3),
                Part = 2,
                PartCount = 22
            };

            var result = builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            Assert.That(result, Is.EqualTo(Path.Combine(edition.Title, $"{edition.Title} 02")));
        }

        [Test]
        public void should_not_pad_smart_part_numbers_for_single_digit_part_counts()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "J.K. Rowling" };
            var book = new Book { Author = author };
            var edition = new Edition { Title = "Harry Potter and the Goblet of Fire", Book = book };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = "{Book Title}/{Book Title} {PartNumber:smart}";

            var bookFile = new BookFile
            {
                Path = "/downloads/part-7.mp3",
                Quality = new QualityModel(Quality.MP3),
                Part = 7,
                PartCount = 9
            };

            var result = builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            Assert.That(result, Is.EqualTo(Path.Combine(edition.Title, $"{edition.Title} 7")));
        }

        [Test]
        public void should_pad_smart_part_numbers_to_part_count_width()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "J.K. Rowling" };
            var book = new Book { Author = author };
            var edition = new Edition { Title = "Harry Potter and the Order of the Phoenix", Book = book };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = "{Book Title}/{Book Title} {PartNumber:smart}";

            var bookFile = new BookFile
            {
                Path = "/downloads/part-7.mp3",
                Quality = new QualityModel(Quality.MP3),
                Part = 7,
                PartCount = 12
            };

            var result = builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            Assert.That(result, Is.EqualTo(Path.Combine(edition.Title, $"{edition.Title} 07")));
        }

        [Test]
        public void should_allow_curly_brace_wrapped_narrator_without_leaving_trailing_spaces()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "Some Author" };
            var book = new Book { Author = author };
            var editionWithNarrator = new Edition { Title = "The Edition Title", Narrator = "Alice Reader", Book = book };
            var editionWithoutNarrator = new Edition { Title = "The Edition Title", Narrator = "", Book = book };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = "{Book Title}{ {Narrator}}/{Book Title}";

            var bookFile = new BookFile
            {
                Path = "/downloads/file.m4b",
                Quality = new QualityModel(Quality.M4B),
                Part = 1,
                PartCount = 1
            };

            var resultWithNarrator = builder.BuildBookFileName(author, editionWithNarrator, bookFile, namingConfig, customFormats: new List<CustomFormat>());
            var resultWithoutNarrator = builder.BuildBookFileName(author, editionWithoutNarrator, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            Assert.That(resultWithNarrator, Is.EqualTo(Path.Combine("The Edition Title {Alice Reader}", "The Edition Title")));
            Assert.That(resultWithoutNarrator, Is.EqualTo(Path.Combine("The Edition Title", "The Edition Title")));
        }

        [Test]
        public void should_use_folder_structure_when_rename_disabled()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "Some Author" };
            var book = new Book { Author = author };
            var edition = new Edition { Title = "The Edition Title", Book = book };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = false;
            namingConfig.StandardBookFormat = "{Book Title}/{Book Title}";

            var bookFile = new BookFile
            {
                Path = "/downloads/original-file-name.m4b",
                Quality = new QualityModel(Quality.M4B),
                Part = 1,
                PartCount = 1
            };

            var result = builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            Assert.That(result, Is.EqualTo(Path.Combine("The Edition Title", "original-file-name")));
        }

        [Test]
        public void should_render_full_cast_for_narrator_token_when_multiple_narrators()
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author { Name = "Some Author" };
            var book = new Book { Author = author };
            var edition = new Edition
            {
                Title = "The Edition Title",
                Book = book,
                NarratorNames = new List<string> { "Alice Reader", "Bob Reader", "Charlie Reader" }
            };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = "{Book Title}{ - Narrator}/{Book Title}";

            var bookFile = new BookFile
            {
                Path = "/downloads/file.m4b",
                Quality = new QualityModel(Quality.M4B),
                Part = 1,
                PartCount = 1
            };

            var result = builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());

            Assert.That(result, Is.EqualTo(Path.Combine("The Edition Title - Full Cast", "The Edition Title")));
        }
    }
}

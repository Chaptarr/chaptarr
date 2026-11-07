using System;
using System.Collections.Generic;
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
    public class FileNameBuilderEditionTokensFixture
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

        private static string Build(string format, Edition edition)
        {
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                new CacheManager(),
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger(),
                null);

            var author = new Author { Name = "Jim Butcher" };
            edition.Book = new Book { Author = author };

            var bookFile = new BookFile
            {
                Path = "/books/x.m4b",
                Quality = new QualityModel(Quality.M4B)
            };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = format;

            return builder.BuildBookFileName(author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());
        }

        [Test]
        public void should_render_audible_asin_in_brackets()
        {
            var edition = new Edition { Title = "Grave Peril", AudibleASIN = "B0ABCDEFGH" };

            Assert.That(Build("{Book Title}{ [Edition AudibleAsin]}", edition), Is.EqualTo("Grave Peril [B0ABCDEFGH]"));
        }

        [Test]
        public void should_collapse_audible_asin_bracket_when_missing()
        {
            var edition = new Edition { Title = "Grave Peril", Asin = "0451457811", Isbn13 = "9780451457813" };

            Assert.That(Build("{Book Title}{ [Edition AudibleAsin]}", edition), Is.EqualTo("Grave Peril"));
        }

        [Test]
        public void should_keep_audible_asin_uppercase_when_token_is_spelled_lowercase()
        {
            var edition = new Edition { Title = "Grave Peril", AudibleASIN = "b0abcdefgh" };

            Assert.That(Build("{Book Title}{ [edition audibleasin]}", edition), Is.EqualTo("Grave Peril [B0ABCDEFGH]"));
            Assert.That(Build("{Book Title}{ [edition.audibleasin]}", edition), Is.EqualTo("Grave Peril [B0ABCDEFGH]"));
        }

        [Test]
        public void should_not_change_formats_without_audible_asin_token()
        {
            var edition = new Edition { Title = "Grave Peril", AudibleASIN = "B0ABCDEFGH" };

            Assert.That(Build("{Book Title}", edition), Is.EqualTo("Grave Peril"));
            Assert.That(Build("{book title}", edition), Is.EqualTo("grave peril"));
        }
    }
}

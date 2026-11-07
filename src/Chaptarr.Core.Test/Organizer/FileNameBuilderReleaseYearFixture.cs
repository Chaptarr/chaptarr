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
    public class FileNameBuilderReleaseYearFixture
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
            edition.Book ??= new Book();
            edition.Book.Author = author;

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
        public void should_render_release_year_from_edition()
        {
            var edition = new Edition { Title = "Title", ReleaseDate = new DateTime(2016, 5, 1) };

            Assert.That(Build("{Release Year - }{Book Title}", edition), Is.EqualTo("2016 - Title"));
        }

        [Test]
        public void should_render_release_year_from_book_when_edition_has_none()
        {
            var edition = new Edition { Title = "Title", Book = new Book { ReleaseDate = new DateTime(2015, 1, 1) } };

            Assert.That(Build("{Release Year - }{Book Title}", edition), Is.EqualTo("2015 - Title"));
        }

        [Test]
        public void should_collapse_release_year_prefix_when_year_is_missing()
        {
            var edition = new Edition { Title = "Title" };

            Assert.That(Build("{Release Year - }{Book Title}", edition), Is.EqualTo("Title"));
        }

        [Test]
        public void should_collapse_release_year_suffix_when_year_is_missing()
        {
            var edition = new Edition { Title = "Title" };

            Assert.That(Build("{Book Title}{ (Release Year)}", edition), Is.EqualTo("Title"));
        }
    }
}

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
    public class FileNameBuilderReleasePartFixture
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

        private static string Build(string pattern, BookFile file)
        {
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                new CacheManager(),
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger(),
                null);

            var author = new Author { Name = "Jane Author" };
            var edition = new Edition { Title = "The Long Book", Book = new Book { Author = author } };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = pattern;

            return builder.BuildBookFileName(author, edition, file, namingConfig, customFormats: new List<CustomFormat>());
        }

        private static BookFile File(string path, int releasePart, int releasePartCount)
        {
            return new BookFile
            {
                Path = path,
                Quality = new QualityModel(Quality.M4B),
                ReleasePart = releasePart,
                ReleasePartCount = releasePartCount
            };
        }

        [Test]
        public void should_mark_each_release_part_when_pattern_cannot_tell_them_apart()
        {
            var first = Build("{Book Title}/{Book Title}", File("/dl/a/book.m4b", 1, 2));
            var second = Build("{Book Title}/{Book Title}", File("/dl/b/book.m4b", 2, 2));

            Assert.That(first, Is.EqualTo(Path.Combine("The Long Book", "The Long Book (Part 1 of 2)")));
            Assert.That(second, Is.EqualTo(Path.Combine("The Long Book", "The Long Book (Part 2 of 2)")));
        }

        [Test]
        public void should_omit_total_from_marker_when_unknown()
        {
            Assert.That(Build("{Book Title}", File("/dl/book.m4b", 2, 0)), Is.EqualTo("The Long Book (Part 2)"));
        }

        [Test]
        public void should_not_mark_files_that_are_not_release_parts()
        {
            Assert.That(Build("{Book Title}", File("/dl/book.m4b", 0, 0)), Is.EqualTo("The Long Book"));
        }

        [Test]
        public void should_use_release_part_tokens_instead_of_the_marker()
        {
            var result = Build("{Book Title} - Disc {ReleasePart:00} of {ReleaseParts:00}", File("/dl/book.m4b", 2, 3));

            Assert.That(result, Is.EqualTo("The Long Book - Disc 02 of 03"));
        }

        [Test]
        public void should_blank_release_part_tokens_for_whole_books()
        {
            var result = Build("{Book Title}{ ReleasePart}", File("/dl/book.m4b", 0, 0));

            Assert.That(result, Is.EqualTo("The Long Book"));
        }
    }
}

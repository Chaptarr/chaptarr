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

        private FileNameBuilder _builder;
        private Author _author;
        private Edition _edition;

        [SetUp]
        public void Setup()
        {
            _builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                new CacheManager(),
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            _author = new Author { Name = "Rebecca Yarros" };
            _edition = new Edition { Title = "Iron Flame", Book = new Book { Author = _author } };
        }

        private string Build(string format, BookFile bookFile)
        {
            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = format;

            return _builder.BuildBookFileName(_author, _edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());
        }

        private static BookFile File(int releasePart, int releasePartCount, int part = 1, int partCount = 1, string path = "/downloads/a.m4b")
        {
            return new BookFile
            {
                Path = path,
                Quality = new QualityModel(Quality.M4B),
                Part = part,
                PartCount = partCount,
                ReleasePart = releasePart,
                ReleasePartCount = releasePartCount
            };
        }

        [Test]
        public void should_render_part_x_of_x()
        {
            var result = Build("{Book Title}/{Book Title}{ (Part ReleasePart of ReleasePartCount)}", File(1, 2));

            Assert.That(result, Is.EqualTo(Path.Combine("Iron Flame", "Iron Flame (Part 1 of 2)")));
        }

        [Test]
        public void should_render_release_part_with_free_text_prefix_and_track_number()
        {
            var result = Build("{Book Title}{ - Part ReleasePart}{ (PartNumber:smart)}", File(1, 2, part: 3, partCount: 10));

            Assert.That(result, Is.EqualTo("Iron Flame - Part 1 (03)"));
        }

        [Test]
        public void should_drop_count_when_unknown()
        {
            var result = Build("{Book Title}{ (Part ReleasePart of ReleasePartCount)}", File(2, 0));

            Assert.That(result, Is.EqualTo("Iron Flame (Part 2)"));
        }

        [Test]
        public void should_render_nothing_for_whole_book_releases()
        {
            var result = Build("{Book Title}{ (Part ReleasePart of ReleasePartCount)}", File(0, 0));

            Assert.That(result, Is.EqualTo("Iron Flame"));
        }

        [Test]
        public void should_not_confuse_release_part_count_with_part_count()
        {
            var result = Build("{Book Title}{ ReleasePartCount}{ PartNumber-PartCount}", File(1, 2, part: 1, partCount: 5));

            Assert.That(result, Is.EqualTo("Iron Flame 2 1-5"));
        }

        [Test]
        public void should_keep_original_file_names_when_format_has_no_release_part_token()
        {
            var format = "{Book Title}/{Book Title}{ (PartNumber:smart)}";
            var part1 = Build(format, File(1, 2, path: "/downloads/Fourth Wing - GraphicAudio - Part 1.m4b"));
            var part2 = Build(format, File(2, 2, path: "/downloads/Fourth Wing - GraphicAudio - Part 2.m4b"));

            Assert.That(part1, Is.EqualTo(Path.Combine("Iron Flame", "Fourth Wing - GraphicAudio - Part 1")));
            Assert.That(part2, Is.EqualTo(Path.Combine("Iron Flame", "Fourth Wing - GraphicAudio - Part 2")));
        }

        [Test]
        public void should_rename_whole_book_releases_as_before()
        {
            var result = Build("{Book Title}/{Book Title}{ (PartNumber:smart)}", File(0, 0));

            Assert.That(result, Is.EqualTo(Path.Combine("Iron Flame", "Iron Flame")));
        }
    }
}

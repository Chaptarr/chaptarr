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
    public class FileNameBuilderAuthorNameFirstLastFixture
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

        [TestCase("Martin, George R.R.", "George R.R. Martin", "George R.R. Martin")]
        [TestCase("Tolkien, J.R.R.", "J.R.R. Tolkien", "J.R.R. Tolkien")]
        [TestCase("Lewis, C. S.", "C. S. Lewis", "C. S. Lewis")]
        [TestCase("King, Stephen", "Stephen King", "Stephen King")]
        [TestCase("Smith, George Washington", "George Washington Smith", "George Smith")]
        public void should_render_author_folder_from_name_first_last_preserving_initials(string nameLastFirst, string name, string expectedFolder)
        {
            var cacheManager = new CacheManager();
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                cacheManager,
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Name = name,
                NameLastFirst = nameLastFirst
            };

            var namingConfig = NamingConfig.Default;
            namingConfig.AuthorFolderFormat = "{Author NameFirstLast}";
            namingConfig.EbookAuthorFolderFormat = "{Author NameFirstLast}";

            var audiobookFolder = builder.GetAuthorFolder(author, namingConfig, mediaType: "audiobook");
            var ebookFolder = builder.GetAuthorFolder(author, namingConfig, mediaType: "ebook");

            Assert.That(audiobookFolder, Is.EqualTo(expectedFolder));
            Assert.That(ebookFolder, Is.EqualTo(expectedFolder));
        }
    }
}

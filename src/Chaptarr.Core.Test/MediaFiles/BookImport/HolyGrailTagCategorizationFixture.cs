using System;
using System.Collections.Generic;
using System.Reflection;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class HolyGrailTagCategorizationFixture
    {
        [Test]
        public void should_classify_container_prefixed_comment_tags_as_comments()
        {
            var sut = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: null,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: null,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: null,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: LogManager.GetCurrentClassLogger());

            var method = typeof(FileMatchingService).GetMethod("CategorizeTagsForHolyGrail", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Never Finished: Unshackle Your Mind and Win the War Within" },
                ["MP4:©cmt"] = new List<string> { "Can’t Hurt Me, David Goggins’ smash hit memoir..." },
                ["ID3v2:COMM:eng"] = new List<string> { "Some comment frame" },
                ["MP4:©lyr"] = new List<string> { "Some lyrics" }
            };

            var categorized = (Dictionary<string, List<string>>)method.Invoke(sut, new object[] { tags });

            Assert.That(categorized.ContainsKey("TITLE"), Is.True);
            Assert.That(categorized.ContainsKey("MP4:©cmt"), Is.False);
            Assert.That(categorized.ContainsKey("ID3v2:COMM:eng"), Is.False);
            Assert.That(categorized.ContainsKey("MP4:©lyr"), Is.False);
        }
    }
}

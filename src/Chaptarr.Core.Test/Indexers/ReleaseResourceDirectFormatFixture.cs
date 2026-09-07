using System;
using Chaptarr.Api.V1.Indexers;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class ReleaseResourceDirectFormatFixture
    {
        [Test]
        public void should_classify_direct_release_format_type_from_container()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "Project Hail Mary",
                    Container = "epub",
                    DownloadProtocol = DownloadProtocol.Direct,
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Andy Weir",
                    BookTitle = "Project Hail Mary",
                    Quality = new QualityModel(Quality.Unknown)
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.FormatType, Is.EqualTo("ebook"));
        }
    }
}

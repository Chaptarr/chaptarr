using System.Collections.Generic;
using Chaptarr.Api.V1.Author;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorEditorBookshelfCompatibilityFixture
    {
        [Test]
        public void legacy_bulk_values_should_apply_to_both_formats_without_overriding_explicit_values()
        {
            var resource = new AuthorEditorResource
            {
                AuthorIds = new List<int> { 7 },
                Monitored = false,
                MonitorNewItems = NewItemMonitorTypes.New,
                QualityProfileId = 12,
                RootFolderPath = "/books",
                AudiobookMonitored = true,
                EbookQualityProfileId = 19
            };

            resource.ApplyBookshelfCompatibilityAliases();

            Assert.That(resource.AudiobookMonitored, Is.True);
            Assert.That(resource.EbookMonitored, Is.False);
            Assert.That(resource.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            Assert.That(resource.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            Assert.That(resource.AudiobookQualityProfileId, Is.EqualTo(12));
            Assert.That(resource.EbookQualityProfileId, Is.EqualTo(19));
            Assert.That(resource.AudiobookRootFolderPath, Is.EqualTo("/books"));
            Assert.That(resource.EbookRootFolderPath, Is.EqualTo("/books"));
        }
    }
}

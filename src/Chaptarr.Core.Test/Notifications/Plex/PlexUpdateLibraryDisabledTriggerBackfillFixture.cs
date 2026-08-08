using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;

namespace Chaptarr.Core.Test.Notifications.Plex
{
    [TestFixture]
    public class PlexUpdateLibraryDisabledTriggerBackfillFixture
    {
        [TestCase("{\"updateLibrary\":false}")]
        [TestCase("{\"UpdateLibrary\":false}")]
        [TestCase("{\"updateLibrary\":\"false\"}")]
        public void should_detect_disabled_update_library_settings(string settings)
        {
            Assert.That(PlexUpdateLibraryDisabledTriggerBackfill.HasDisabledUpdateLibrary(settings), Is.True);
        }

        [TestCase("{\"updateLibrary\":true}")]
        [TestCase("{}")]
        [TestCase("")]
        [TestCase("not-json")]
        public void should_ignore_settings_without_disabled_update_library(string settings)
        {
            Assert.That(PlexUpdateLibraryDisabledTriggerBackfill.HasDisabledUpdateLibrary(settings), Is.False);
        }
    }
}

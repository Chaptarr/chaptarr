using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport.Manual;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ManualImportUnresolvableSuggestionFixture
    {
        [Test]
        public void should_flag_local_author_with_missing_work_and_no_edition()
        {
            Assert.That(ManualImportService.SuggestionCannotBeAddedFromMetadata(true, null, false), Is.True);
            Assert.That(ManualImportService.SuggestionCannotBeAddedFromMetadata(true, "  ", false), Is.True);
        }

        [Test]
        public void should_not_flag_when_work_is_in_the_catalog()
        {
            Assert.That(ManualImportService.SuggestionCannotBeAddedFromMetadata(true, null, true), Is.False);
        }

        [Test]
        public void should_not_flag_when_an_edition_id_is_present()
        {
            Assert.That(ManualImportService.SuggestionCannotBeAddedFromMetadata(true, "hc:edition-1", false), Is.False);
        }

        [Test]
        public void should_not_flag_when_the_author_is_not_local()
        {
            Assert.That(ManualImportService.SuggestionCannotBeAddedFromMetadata(false, null, false), Is.False);
        }
    }
}

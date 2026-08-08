using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ImportSessionProgressTrackerFixture
    {
        [Test]
        public void begin_staging_pass_should_reset_only_the_pass_completion_flag()
        {
            const int commandId = 791503;

            try
            {
                ImportSessionProgressTracker.Activate(commandId);
                ImportSessionProgressTracker.AddDiscoveredAuthorFolders(commandId, new[] { "/library/Author" });
                ImportSessionProgressTracker.MarkAuthorFolderProcessed(commandId, "/library/Author");
                ImportSessionProgressTracker.MarkStagingComplete(commandId);

                ImportSessionProgressTracker.BeginStagingPass(commandId);

                Assert.Multiple(() =>
                {
                    Assert.That(ImportSessionProgressTracker.IsStagingComplete(commandId), Is.False);
                    Assert.That(ImportSessionProgressTracker.GetAuthorFolderProgress(commandId), Is.EqualTo((1, 1)));
                });
            }
            finally
            {
                ImportSessionProgressTracker.Clear(commandId);
            }
        }
    }
}

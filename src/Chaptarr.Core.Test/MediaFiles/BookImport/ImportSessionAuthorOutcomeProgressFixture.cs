using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ImportSessionAuthorOutcomeProgressFixture
    {
        [Test]
        public void author_outcomes_should_classify_each_physical_folder_once()
        {
            const int commandId = 791513;

            try
            {
                ImportSessionProgressTracker.Activate(commandId);
                ImportSessionProgressTracker.AddDiscoveredAuthorFolders(commandId, new[]
                {
                    "/library/Matched",
                    "/library/Unmatched",
                    "/library/Pending"
                });

                ImportSessionProgressTracker.MarkAuthorFolderOutcome(commandId, "/library/Matched", matched: true);
                ImportSessionProgressTracker.MarkAuthorFolderOutcome(commandId, "/library/Matched", matched: true);
                var progress = ImportSessionProgressTracker.MarkAuthorFolderOutcome(commandId, "/library/Unmatched", matched: false);

                Assert.Multiple(() =>
                {
                    Assert.That(progress.Processed, Is.EqualTo(2));
                    Assert.That(progress.Total, Is.EqualTo(3));
                    Assert.That(progress.Matched, Is.EqualTo(1));
                    Assert.That(progress.Unmatched, Is.EqualTo(1));
                });
            }
            finally
            {
                ImportSessionProgressTracker.Clear(commandId);
            }
        }

        [Test]
        public void a_later_success_should_move_a_folder_between_outcomes_without_double_counting()
        {
            const int commandId = 791523;

            try
            {
                ImportSessionProgressTracker.Activate(commandId);
                ImportSessionProgressTracker.AddDiscoveredAuthorFolders(commandId, new[] { "/library/Author" });
                ImportSessionProgressTracker.MarkAuthorFolderOutcome(commandId, "/library/Author", matched: false);

                var progress = ImportSessionProgressTracker.MarkAuthorFolderOutcome(commandId, "/library/Author", matched: true);

                Assert.That(progress, Is.EqualTo((1, 1, 1, 0)));
            }
            finally
            {
                ImportSessionProgressTracker.Clear(commandId);
            }
        }
    }
}

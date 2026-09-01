using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.AuthorStats
{
    [TestFixture]
    public class AuthorStatisticsImportDeferralFixture
    {
        private sealed class RecordingRepository : IAuthorStatisticsRepository
        {
            public int AllAuthorCalls { get; private set; }

            public List<BookStatistics> AuthorStatistics()
            {
                AllAuthorCalls++;
                return Stats();
            }

            public List<BookStatistics> AuthorStatistics(int authorId) => Stats();
            public List<BookStatistics> AuthorStatistics(string mediaType) => Stats();
            public List<BookStatistics> AuthorStatistics(int authorId, string mediaType) => Stats();
            private static List<BookStatistics> Stats() => new()
            {
                new BookStatistics { AuthorId = 1, BookId = 10, BookCount = 1, TotalBookCount = 1 }
            };
        }

        [Test]
        public void final_import_stage_should_invalidate_deferred_file_statistics_once()
        {
            const int commandId = 912311;
            var repository = new RecordingRepository();
            var service = new AuthorStatisticsService(repository, new CacheManager());
            var lifecycle = new ImportSessionProgressCleanupHandler();

            try
            {
                var start = Progress(ImportStage.ScanningFolders, commandId);
                lifecycle.Handle(start);
                service.AuthorStatistics();

                service.Handle(new BookFileAddedEvent(File(1)));
                service.Handle(new BookFileUpdatedEvent(File(1)));
                service.Handle(new BookFilesAddedEvent(new[] { File(1), File(1) }));
                service.AuthorStatistics();
                Assert.That(repository.AllAuthorCalls, Is.EqualTo(1));

                var complete = Progress(ImportStage.ImportComplete, commandId);
                lifecycle.Handle(complete);
                service.Handle(complete);
                service.AuthorStatistics();

                Assert.That(repository.AllAuthorCalls, Is.EqualTo(2));
            }
            finally
            {
                ImportSessionProgressTracker.Clear(commandId);
            }
        }

        [Test]
        public void overlapping_imports_should_flush_only_after_the_last_session_finishes()
        {
            const int firstCommandId = 912321;
            const int secondCommandId = 912322;
            var repository = new RecordingRepository();
            var service = new AuthorStatisticsService(repository, new CacheManager());
            var lifecycle = new ImportSessionProgressCleanupHandler();

            try
            {
                lifecycle.Handle(Progress(ImportStage.ScanningFolders, firstCommandId));
                lifecycle.Handle(Progress(ImportStage.ScanningFolders, secondCommandId));
                service.AuthorStatistics();
                service.Handle(new BookFileAddedEvent(File(1)));

                var firstComplete = Progress(ImportStage.ImportComplete, firstCommandId);
                lifecycle.Handle(firstComplete);
                service.Handle(firstComplete);
                service.AuthorStatistics();
                Assert.That(repository.AllAuthorCalls, Is.EqualTo(1));

                var secondComplete = Progress(ImportStage.ImportComplete, secondCommandId);
                lifecycle.Handle(secondComplete);
                service.Handle(secondComplete);
                service.AuthorStatistics();
                Assert.That(repository.AllAuthorCalls, Is.EqualTo(2));
            }
            finally
            {
                ImportSessionProgressTracker.Clear(firstCommandId);
                ImportSessionProgressTracker.Clear(secondCommandId);
            }
        }

        [Test]
        public void command_terminal_event_should_flush_after_failure_or_cancellation_without_import_complete()
        {
            const int commandId = 912331;
            var repository = new RecordingRepository();
            var service = new AuthorStatisticsService(repository, new CacheManager());
            var lifecycle = new ImportSessionProgressCleanupHandler();

            lifecycle.Handle(Progress(ImportStage.ScanningFolders, commandId));
            service.AuthorStatistics();
            service.Handle(new BookFileAddedEvent(File(1)));

            var terminal = new CommandExecutedEvent(new CommandModel { Id = commandId, Status = CommandStatus.Failed });
            lifecycle.Handle(terminal);
            service.Handle(terminal);
            service.AuthorStatistics();

            Assert.Multiple(() =>
            {
                Assert.That(repository.AllAuthorCalls, Is.EqualTo(2));
                Assert.That(ImportSessionProgressTracker.IsImportActive, Is.False);
            });
        }

        [Test]
        public void late_progress_after_command_cleanup_should_not_reactivate_the_session()
        {
            const int commandId = 912341;
            var lifecycle = new ImportSessionProgressCleanupHandler();

            lifecycle.Handle(Progress(ImportStage.ScanningFolders, commandId));
            lifecycle.Handle(new CommandExecutedEvent(new CommandModel { Id = commandId, Status = CommandStatus.Completed }));
            lifecycle.Handle(Progress(ImportStage.MatchingBooks, commandId));

            Assert.That(ImportSessionProgressTracker.IsActive(commandId), Is.False);
        }

        private static ImportStageProgressEvent Progress(ImportStage stage, int commandId)
        {
            return new ImportStageProgressEvent(stage, stage.ToString()) { CommandId = commandId };
        }

        private static BookFile File(int authorId)
        {
            return new BookFile { Author = new Author { Id = authorId } };
        }
    }
}

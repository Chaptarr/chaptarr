using System;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Commands
{
    [TestFixture]
    public class CommandQueueDiskAccessGroupFixture
    {
        // chaptarr #188: a command in a distinct DiskAccessGroup (e.g. RetryFailedImportCommand's
        // "downloadImport") must be able to start while a command in a different group (e.g.
        // ProcessMonitoredDownloadsCommand's implicit "default") is already running -- that's the
        // entire point of naming a separate group. A prior bug blocked a candidate whenever ANY
        // started disk command was in a different group, which serialized every group behind
        // whichever one happened to be running first -- observed live as RetryFailedImport and
        // ManualImport commands sitting "queued" for the full 15+ minutes of a ProcessMonitoredDownloads
        // sweep over a large library.
        [Test]
        public void command_in_a_different_disk_access_group_should_not_be_blocked()
        {
            var queue = new CommandQueue();
            queue.Add(BuildModel(new ProcessMonitoredDownloadsCommand(), CommandStatus.Started, CommandPriority.Normal, DateTime.UtcNow.AddMinutes(-5), id: 1));
            queue.Add(BuildModel(new RetryFailedImportCommand { DownloadId = "ABC" }, CommandStatus.Queued, CommandPriority.High, DateTime.UtcNow, id: 2));

            var found = queue.TryGet(out var selected);

            Assert.That(found, Is.True);
            Assert.That(selected.Body, Is.TypeOf<RetryFailedImportCommand>());
            Assert.That(selected.Status, Is.EqualTo(CommandStatus.Started));
        }

        // Same-group serialization must still hold: two commands that share a DiskAccessGroup (here,
        // both defaulting to "default") still respect that group's limit (1, absent an explicit
        // higher limit), same as before this fix. ManualImportCommand is the real-world example that
        // was starved by the bug: it shares "default" with ProcessMonitoredDownloadsCommand, so it's
        // still correctly serialized behind it (unlike RetryFailedImportCommand above).
        [Test]
        public void command_in_the_same_disk_access_group_should_still_be_blocked()
        {
            var queue = new CommandQueue();
            queue.Add(BuildModel(new ProcessMonitoredDownloadsCommand(), CommandStatus.Started, CommandPriority.Normal, DateTime.UtcNow.AddMinutes(-5), id: 1));
            queue.Add(BuildModel(new ManualImportCommand(), CommandStatus.Queued, CommandPriority.High, DateTime.UtcNow, id: 2));

            var found = queue.TryGet(out var selected);

            Assert.That(found, Is.False);
            Assert.That(selected, Is.Null);
        }

        [TestCaseSource(nameof(LibrarySearchCommands))]
        public void library_search_should_wait_for_active_disk_work(Command searchCommand)
        {
            var queue = new CommandQueue(_ => 8);
            queue.Add(BuildModel(new RescanFoldersCommand(), CommandStatus.Started, CommandPriority.Normal, DateTime.UtcNow.AddMinutes(-1), id: 1));
            queue.Add(BuildModel(searchCommand, CommandStatus.Queued, CommandPriority.High, DateTime.UtcNow, id: 2));

            var found = queue.TryGet(out var selected);

            Assert.That(found, Is.False);
            Assert.That(selected, Is.Null);
        }

        [Test]
        public void disk_work_should_wait_for_active_library_search()
        {
            var queue = new CommandQueue(_ => 8);
            queue.Add(BuildModel(new BookSearchCommand(new System.Collections.Generic.List<int> { 42 }), CommandStatus.Started, CommandPriority.Normal, DateTime.UtcNow.AddMinutes(-1), id: 1));
            queue.Add(BuildModel(new RescanFoldersCommand(), CommandStatus.Queued, CommandPriority.High, DateTime.UtcNow, id: 2));

            var found = queue.TryGet(out var selected);

            Assert.That(found, Is.False);
            Assert.That(selected, Is.Null);
        }

        [Test]
        public void high_priority_initial_scan_should_be_selected_before_a_queued_library_search()
        {
            var queue = new CommandQueue(_ => 8);
            queue.Add(BuildModel(new BookSearchCommand(new System.Collections.Generic.List<int> { 42 }), CommandStatus.Queued, CommandPriority.Normal, DateTime.UtcNow, id: 1));
            queue.Add(BuildModel(
                new RescanFoldersCommand(new System.Collections.Generic.List<string> { "/library" }, FilterFilesType.None, null, true),
                CommandStatus.Queued,
                CommandPriority.High,
                DateTime.UtcNow.AddSeconds(1),
                id: 2));

            var found = queue.TryGet(out var selected);

            Assert.That(found, Is.True);
            Assert.That(selected.Body, Is.TypeOf<RescanFoldersCommand>());
        }

        private static System.Collections.Generic.IEnumerable<TestCaseData> LibrarySearchCommands()
        {
            yield return new TestCaseData(new BookSearchCommand(new System.Collections.Generic.List<int> { 42 }))
                .SetName("BookSearchCommand waits for disk work");
            yield return new TestCaseData(new MissingBookSearchCommand(42))
                .SetName("MissingBookSearchCommand waits for disk work");
            yield return new TestCaseData(new CutoffUnmetBookSearchCommand(42))
                .SetName("CutoffUnmetBookSearchCommand waits for disk work");
            yield return new TestCaseData(new AuthorSearchCommand { AuthorId = 42 })
                .SetName("AuthorSearchCommand waits for disk work");
        }

        private static CommandModel BuildModel(
            Command command,
            CommandStatus status,
            CommandPriority priority,
            DateTime queuedAt,
            int id)
        {
            return new CommandModel
            {
                Id = id,
                Name = command.Name,
                Body = command,
                Priority = priority,
                Status = status,
                QueuedAt = queuedAt,
                StartedAt = status == CommandStatus.Started ? queuedAt : null
            };
        }
    }
}

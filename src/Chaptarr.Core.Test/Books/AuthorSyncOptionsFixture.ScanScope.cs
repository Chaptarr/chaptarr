using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Commands;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class AuthorSyncOptionsFixture
    {
        private static void ScanFolders(Harness h, List<string> folders, bool complete = true,
            Func<string, bool> folderExists = null, List<int> authorIds = null, string incompleteFolder = null)
        {
            var service = new DiskScanService(null,
                Proxy<IDiskProvider>((method, args) => method.Name == nameof(IDiskProvider.FolderExists)
                    ? folderExists?.Invoke((string)args[0]) ?? true
                    : throw new NotSupportedException(method.Name)), null,
                Proxy<IMediaFileService>((_, _) => new List<BookFile>()), null, null,
                Proxy<IImportOrchestrator>((_, args) => Task.FromResult(new OrchestratorImportResult
                {
                    CleanupSafe = complete && (string)args[0] != incompleteFolder,
                    StagingResults = new List<NzbDrone.Core.Datastore.ImportResult> { new() { AuthorId = h.Author.Id } }
                })), h.Authors, h.Roots,
                Proxy<IMediaFileTableCleanupService>((_, _) => null), h.Queue, h.Events, h.Logger);
            service.Scan(folders, authorIds: authorIds);
        }

        [Test]
        public void partial_disk_scan_must_not_consume_pending_format_setup(
            [Values(false, true)] bool postgres,
            [Values("audiobook", "ebook")] string pendingSide,
            [Values("other-format", "book-subfolder", "incomplete", "unavailable")] string scope)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request(pendingSide == "ebook" ? "audiobook" : "ebook", false));
                h.Scan();
                var request = h.Request(pendingSide, false);
                request.SearchForMissingBooks = true;
                h.Import(request);
                var author = h.Author;
                var expected = pendingSide == "ebook" ? author.EbookPath : author.AudiobookPath;
                var other = pendingSide == "ebook" ? author.AudiobookPath : author.EbookPath;
                var folder = scope == "other-format" ? other : scope == "book-subfolder" ? Path.Combine(expected, "One Book") : expected;
                h.Commands.Clear();
                Assert.That(author.AddOptions, Is.Not.Null);
                Assert.That(author.AddOptions.AwaitingMetadata, Is.False);
                var monitoring = h.Books.GetBooksByAuthor(author.Id).ToDictionary(book => book.Id, book => book.Monitored);

                ScanFolders(h, new List<string> { folder }, scope != "incomplete",
                    scope == "unavailable" ? _ => false : null, new List<int> { author.Id });

                Assert.Multiple(() =>
                {
                    Assert.That(h.Author.AddOptions, Is.Not.Null, "The other format or a partial inventory cannot finish initial setup.");
                    Assert.That(h.SearchTargets, Is.Empty);
                    Assert.That(h.Books.GetBooksByAuthor(author.Id).ToDictionary(book => book.Id, book => book.Monitored), Is.EquivalentTo(monitoring));
                });

                ScanFolders(h, new List<string> { expected });
                Assert.That(h.Author.AddOptions, Is.Null, "Root discovery must still complete setup without explicit author IDs.");
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void filtered_refresh_scans_pending_setup_not_the_other_format(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook")] string pendingSide)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var otherSide = pendingSide == "ebook" ? "audiobook" : "ebook";
                h.Import(h.Request(otherSide, false));
                h.Scan();
                h.Import(h.Request(pendingSide, false));
                h.Commands.Clear();
                HydrationRefresh(h, database, h.Info).Execute(new BulkRefreshAuthorCommand(
                    new List<int> { h.Author.Id }, refreshMetadata: false, rescanFolders: true) { MediaType = otherSide });

                var scan = h.Commands.OfType<RescanFoldersCommand>().Single();
                Assert.That(scan.Folders, Is.EqualTo(new[] { pendingSide == "ebook" ? h.Author.EbookPath : h.Author.AudiobookPath }));
                Assert.That(scan.MediaType, Is.EqualTo(pendingSide));
                Assert.That(h.Author.AddOptions, Is.Not.Null);
                ScanFolders(h, scan.Folders, authorIds: scan.AuthorIds);
                Assert.That(h.Author.AddOptions, Is.Null);
            });
        }

        [Test]
        public void initial_scan_sweep_tolerates_author_ids_removed_during_refresh([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request("ebook", false));
                h.Commands.Clear();
                // A completed server merge removes a source row before this same post-refresh scan sweep.
                var originalIds = new List<int> { h.Author.Id, h.Author.Id + 1000 };
                var refresh = HydrationRefresh(h, database, h.Info);
                Assert.DoesNotThrow(() => refresh.Execute(new BulkRefreshAuthorCommand(originalIds,
                    refreshMetadata: false, rescanFolders: true)));
                Assert.That(h.Commands.OfType<RescanFoldersCommand>().Single().AuthorIds, Is.EqualTo(new[] { h.Author.Id }));
            });
        }

        [Test]
        public void missing_author_folder_under_available_root_finishes_initial_setup([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var request = h.Request("ebook", false);
                request.SearchForMissingBooks = true;
                h.Import(request);
                ScanFolders(h, new List<string> { h.Author.EbookPath },
                    folderExists: path => path == h.EbookRoot.Path, authorIds: new List<int> { h.Author.Id });
                Assert.That(h.Author.AddOptions, Is.Null);
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void both_format_setup_waits_for_both_roots_then_finishes_once([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var request = h.Request("both", true);
                request.SearchForMissingBooks = true;
                h.Import(request);
                ScanFolders(h, new List<string> { h.AudioRoot.Path });
                Assert.That(h.Author.AddOptions, Is.Not.Null);
                Assert.That(h.SearchTargets, Is.Empty);
                ScanFolders(h, new List<string> { h.AudioRoot.Path, h.EbookRoot.Path });
                Assert.That(h.Author.AddOptions, Is.Null);
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
                ScanFolders(h, new List<string> { h.AudioRoot.Path, h.EbookRoot.Path });
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void author_scan_requires_its_additional_mapped_folder_to_finish([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var request = h.Request("ebook", false);
                request.SearchForMissingBooks = true;
                h.Import(request);
                var oldFolder = Path.Combine(h.EbookRoot.Path, "Old Author Spelling");
                var folders = new List<string> { h.Author.EbookPath, oldFolder };
                ScanFolders(h, folders, authorIds: new List<int> { h.Author.Id }, incompleteFolder: oldFolder);
                Assert.That(h.Author.AddOptions, Is.Not.Null);
                Assert.That(h.SearchTargets, Is.Empty);
                ScanFolders(h, folders, authorIds: new List<int> { h.Author.Id });
                Assert.That(h.Author.AddOptions, Is.Null);
                Assert.That(h.SearchTargets, Has.Count.EqualTo(1));
            });
        }
    }
}

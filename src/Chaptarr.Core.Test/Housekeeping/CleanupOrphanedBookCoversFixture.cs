using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Housekeeping.Housekeepers;

namespace Chaptarr.Core.Test.Housekeeping
{
    [TestFixture]
    public class CleanupOrphanedBookCoversFixture
    {
        private class AppFolderProxy : DispatchProxy
        {
            public string AppDataFolder { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name == "get_AppDataFolder"
                    ? AppDataFolder
                    : throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<int> BookIds { get; set; } = new();
            public List<int> LibraryIds { get; set; } = new();
            public List<bool> IncludeUnmonitoredArguments { get; } = new();
            public int LibraryBookIdCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBookIds))
                {
                    IncludeUnmonitoredArguments.Add((bool)args[0]);
                    return BookIds;
                }

                if (targetMethod?.Name == nameof(IBookService.GetLibraryBookIds))
                {
                    LibraryBookIdCalls++;
                    var ids = args?.Length > 0 ? args[0] as IEnumerable<int> : null;
                    return LibraryIds.Where(id => ids == null || ids.Contains(id)).ToList();
                }

                if (targetMethod?.Name == nameof(IBookService.GetExistingBooks))
                {
                    var ids = ((IEnumerable<int>)args[0]).ToArray();
                    return ids.Where(BookIds.Contains).Select(id => new Book { Id = id }).ToList();
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public bool RootExists { get; set; } = true;
            public List<string> Directories { get; } = new();
            public List<string> DeletedDirectories { get; } = new();
            public List<string> SizedDirectories { get; } = new();
            public Action<string> OnDelete { get; set; }
            public Action<int> OnEnumerate { get; set; }
            public Action<string> OnFolderSized { get; set; }
            public Func<string, long> FolderSize { get; set; } = _ => 4096L;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IDiskProvider.FolderExists) => RootExists,
                    nameof(IDiskProvider.GetDirectories) => EnumerateDirectories(),
                    nameof(IDiskProvider.GetFolderSize) => GetFolderSize((string)args[0]),
                    nameof(IDiskProvider.DeleteFolder) => Delete((string)args[0]),
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }

            private IEnumerable<string> EnumerateDirectories()
            {
                for (var i = 0; i < Directories.Count; i++)
                {
                    OnEnumerate?.Invoke(i);
                    yield return Directories[i];
                }
            }

            private long GetFolderSize(string path)
            {
                SizedDirectories.Add(path);
                OnFolderSized?.Invoke(path);
                return FolderSize(path);
            }

            private object Delete(string path)
            {
                OnDelete?.Invoke(path);
                DeletedDirectories.Add(path);
                return null;
            }
        }

        private class ConfigProxy : DispatchProxy
        {
            public CoverDownloadScope Scope { get; set; }
            protected override object Invoke(MethodInfo method, object[] args) => method.Name == "get_CoverDownloadScope"
                ? Scope : throw new NotImplementedException(method.Name);
        }

        private class CoversProxy : DispatchProxy
        {
            public IDiskProvider Disk { get; set; }
            public string Root { get; set; }
            public List<int> Restored { get; } = new();
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == nameof(IMapCoversToLocal.DeleteBookCovers))
                {
                    Disk.DeleteFolder(Path.Combine(Root, ((int)args[0]).ToString()), true);
                    return null;
                }

                if (method.Name == nameof(IMapCoversToLocal.EnsureBookCoversForBooks))
                {
                    Restored.AddRange(((List<Book>)args[0]).Select(book => book.Id));
                    return null;
                }

                throw new NotImplementedException(method.Name);
            }
        }

        private static IConfigService NewConfig(CoverDownloadScope scope)
        {
            var config = DispatchProxy.Create<IConfigService, ConfigProxy>();
            ((ConfigProxy)(object)config).Scope = scope;
            return config;
        }

        private static IMapCoversToLocal NewCovers(IDiskProvider disk, IAppFolderInfo appFolder)
        {
            var covers = DispatchProxy.Create<IMapCoversToLocal, CoversProxy>();
            var proxy = (CoversProxy)(object)covers;
            proxy.Disk = disk;
            proxy.Root = Path.Combine(appFolder.AppDataFolder, "MediaCover", "Books");
            return covers;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void library_scope_should_keep_eligible_covers_and_restore_a_book_monitored_during_cleanup(bool becomesMonitored)
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = @"C:\config".AsOsAgnostic();
            var coverRoot = Path.Combine(appFolder.AppDataFolder, "MediaCover", "Books");
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.Directories.AddRange(new[] { "101", "102", "103", "not-a-book" }.Select(id => Path.Combine(coverRoot, id)));
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookProxy = (BookServiceProxy)(object)books;
            bookProxy.BookIds = new List<int> { 101, 102, 103 };
            bookProxy.LibraryIds = new List<int> { 101, 103 };
            disk.OnDelete = _ =>
            {
                if (becomesMonitored)
                {
                    bookProxy.LibraryIds.Add(102);
                }
            };
            var covers = NewCovers(diskProvider, appFolder);
            var subject = new CleanupOrphanedBookCovers(books, diskProvider, appFolder,
                LogManager.GetCurrentClassLogger(), NewConfig(CoverDownloadScope.LibraryBooks), covers);

            subject.Execute(new CleanupBookCoversCommand());

            Assert.That(disk.DeletedDirectories, Is.EqualTo(new[] { Path.Combine(coverRoot, "102") }));
            Assert.That(((CoversProxy)(object)covers).Restored, Is.EqualTo(becomesMonitored ? new[] { 102 } : Array.Empty<int>()));
        }

        [Test]
        public void should_remove_only_numeric_cover_directories_without_a_book_row()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = @"C:\config".AsOsAgnostic();
            var coverRoot = Path.Combine(appFolder.AppDataFolder, "MediaCover", "Books");
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.Directories.AddRange(new[]
            {
                Path.Combine(coverRoot, "101"),
                Path.Combine(coverRoot, "102"),
                Path.Combine(coverRoot, "not-a-book"),
                Path.Combine(coverRoot, "00123")
            });
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var books = (BookServiceProxy)(object)bookService;
            books.BookIds = new List<int> { 101 };
            var subject = new CleanupOrphanedBookCovers(
                bookService,
                diskProvider,
                appFolder,
                LogManager.GetCurrentClassLogger(),
                NewConfig(CoverDownloadScope.AllBooks),
                NewCovers(diskProvider, appFolder));

            subject.Clean();

            Assert.That(disk.DeletedDirectories, Is.EqualTo(new[] { Path.Combine(coverRoot, "102") }));
            Assert.That(books.IncludeUnmonitoredArguments, Is.EqualTo(new[] { true }));
        }

        [TestCase(CoverDownloadScope.AllBooks, true)]
        [TestCase(CoverDownloadScope.LibraryBooks, true)]
        [TestCase(CoverDownloadScope.AllBooks, false)]
        [TestCase(CoverDownloadScope.LibraryBooks, false)]
        public void cover_added_after_snapshot_is_rechecked_without_reloading_the_catalog(CoverDownloadScope scope, bool eligible)
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = @"C:\config".AsOsAgnostic();
            var coverRoot = Path.Combine(appFolder.AppDataFolder, "MediaCover", "Books");
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.Directories.AddRange(new[] { 101, 102 }.Select(id => Path.Combine(coverRoot, id.ToString())));
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var books = (BookServiceProxy)(object)bookService;
            books.BookIds.Add(101);
            books.LibraryIds.Add(101);
            disk.OnEnumerate = index =>
            {
                if (index == 1 && eligible)
                {
                    Assert.That(books.IncludeUnmonitoredArguments, Has.Count.EqualTo(1));
                    books.BookIds.Add(102);
                    books.LibraryIds.Add(102);
                }
            };
            var covers = NewCovers(diskProvider, appFolder);
            var subject = new CleanupOrphanedBookCovers(bookService, diskProvider, appFolder,
                LogManager.GetCurrentClassLogger(), NewConfig(scope), covers);

            subject.Clean();

            Assert.Multiple(() =>
            {
                Assert.That(disk.DeletedDirectories, Is.EqualTo(new[] { Path.Combine(coverRoot, "102") }));
                Assert.That(((CoversProxy)(object)covers).Restored, Is.EqualTo(eligible ? new[] { 102 } : Array.Empty<int>()));
                Assert.That(books.IncludeUnmonitoredArguments, Is.EqualTo(new[] { true }), "No second full catalog read");
                Assert.That(disk.SizedDirectories, Is.Empty);
            });
        }

        [Test]
        public void cleanup_should_delete_without_sizing_directories_for_logging()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = @"C:\config".AsOsAgnostic();
            var coverRoot = Path.Combine(appFolder.AppDataFolder, "MediaCover", "Books");
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.Directories.Add(Path.Combine(coverRoot, "102"));
            disk.FolderSize = _ => throw new IOException("Size is unavailable");
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var covers = NewCovers(diskProvider, appFolder);
            var subject = new CleanupOrphanedBookCovers(
                bookService,
                diskProvider,
                appFolder,
                LogManager.GetCurrentClassLogger(),
                NewConfig(CoverDownloadScope.LibraryBooks),
                covers);

            subject.Execute(new CleanupBookCoversCommand());

            Assert.That(disk.DeletedDirectories, Is.EqualTo(new[] { Path.Combine(coverRoot, "102") }));
            Assert.That(disk.SizedDirectories, Is.Empty);
        }

        [Test]
        public void library_cleanup_estimate_should_measure_only_canonical_directories_without_mutating_covers()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = @"C:\config".AsOsAgnostic();
            var coverRoot = Path.Combine(appFolder.AppDataFolder, "MediaCover", "Books");
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.Directories.AddRange(new[] { "101", "102", "103", "not-a-book", "00104", "0", "-105" }
                .Select(id => Path.Combine(coverRoot, id)));
            disk.FolderSize = path => Path.GetFileName(path) switch
            {
                "101" => 100L,
                "102" => 2048L,
                "103" => 300L,
                _ => throw new InvalidOperationException("An ineligible directory was measured")
            };
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var books = (BookServiceProxy)(object)bookService;
            books.LibraryIds = new List<int> { 101, 103 };
            var covers = NewCovers(diskProvider, appFolder);
            var subject = new CleanupOrphanedBookCovers(bookService, diskProvider, appFolder,
                LogManager.GetCurrentClassLogger(), NewConfig(CoverDownloadScope.LibraryBooks), covers);

            var estimate = subject.GetLibraryCleanupEstimate(CancellationToken.None);

            Assert.That(estimate, Is.EqualTo((2048L, 1)));
            Assert.That(books.LibraryBookIdCalls, Is.EqualTo(1));
            Assert.That(books.IncludeUnmonitoredArguments, Is.Empty);
            Assert.That(disk.SizedDirectories, Is.EqualTo(new[] { Path.Combine(coverRoot, "102") }));
            Assert.That(disk.DeletedDirectories, Is.Empty);
            Assert.That(((CoversProxy)(object)covers).Restored, Is.Empty);
        }

        [Test]
        public void cleanup_processes_each_folder_before_enumerating_the_rest()
        {
            var (subject, disk, _) = CreateEstimateSubject(new[] { 101, 102, 103 });
            disk.OnEnumerate = index => Assert.That(disk.DeletedDirectories.Count, Is.EqualTo(index),
                "Cleanup must stream the disk listing, not retain every path before deleting the first folder");
            subject.Clean();
            Assert.That(disk.DeletedDirectories, Is.EqualTo(disk.Directories));
        }

        [Test]
        public void estimate_should_sample_at_most_500_distinct_folders_and_ignore_enumeration_order()
        {
            var (subject, disk, books) = CreateEstimateSubject(Enumerable.Range(100, 1001));
            books.LibraryIds.Add(100);
            disk.FolderSize = _ => 1024;

            var estimate = subject.GetLibraryCleanupEstimate(CancellationToken.None);
            var sample = disk.SizedDirectories.ToArray();

            Assert.That(estimate, Is.EqualTo((1024000L, 1000)));
            Assert.That(sample, Has.Length.EqualTo(500));
            Assert.That(sample.Distinct().Count(), Is.EqualTo(500));
            Assert.That(sample.Any(path => Path.GetFileName(path) == "100"), Is.False);
            Assert.That(books.LibraryBookIdCalls, Is.EqualTo(1));
            Assert.That(disk.DeletedDirectories, Is.Empty);

            disk.SizedDirectories.Clear();
            disk.Directories.Reverse();
            Assert.That(subject.GetLibraryCleanupEstimate(CancellationToken.None), Is.EqualTo(estimate));
            Assert.That(disk.SizedDirectories, Is.EqualTo(sample));
            Assert.That(books.LibraryBookIdCalls, Is.EqualTo(2));
        }

        [Test]
        public void a_sampled_folder_removed_during_estimation_should_contribute_zero_bytes()
        {
            var (subject, disk, _) = CreateEstimateSubject(new[] { 101, 102 });
            disk.FolderSize = path => Path.GetFileName(path) == "101"
                ? throw new DirectoryNotFoundException()
                : 1024;

            Assert.That(subject.GetLibraryCleanupEstimate(CancellationToken.None).Bytes, Is.EqualTo(1024));
            Assert.That(disk.DeletedDirectories, Is.Empty);
        }

        [Test]
        public void expired_estimate_budget_should_fail_without_returning_a_partial_total()
        {
            using var factory = new LogFactory();
            var target = new MemoryTarget { Layout = "${level}|${message}" };
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Warn, LogLevel.Fatal, target);
            factory.Configuration = logging;
            var (subject, disk, books) = CreateEstimateSubject(new[] { 101, 102 }, factory.GetLogger(nameof(CleanupOrphanedBookCovers)));

            Assert.Throws<TimeoutException>(() => subject.GetLibraryCleanupEstimate(CancellationToken.None, TimeSpan.Zero));
            Assert.That(target.Logs, Has.Count.EqualTo(1));
            Assert.That(target.Logs.Single(), Does.StartWith("Warn|").And.Contain("time budget"));
            Assert.That(disk.SizedDirectories, Is.Empty);
            Assert.That(disk.DeletedDirectories, Is.Empty);
            Assert.That(books.LibraryBookIdCalls, Is.Zero);
        }

        private static (CleanupOrphanedBookCovers Subject, DiskProviderProxy Disk, BookServiceProxy Books) CreateEstimateSubject(IEnumerable<int> ids, Logger logger = null)
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = @"C:\config".AsOsAgnostic();
            var root = Path.Combine(appFolder.AppDataFolder, "MediaCover", "Books");
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.Directories.AddRange(ids.Select(id => Path.Combine(root, id.ToString())));
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var books = (BookServiceProxy)(object)bookService;
            var subject = new CleanupOrphanedBookCovers(bookService, diskProvider, appFolder,
                logger ?? LogManager.GetCurrentClassLogger(), NewConfig(CoverDownloadScope.LibraryBooks), NewCovers(diskProvider, appFolder));
            return (subject, disk, books);
        }

        [Test]
        public void empty_cover_cache_should_return_zero_without_querying_books()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = @"C:\config".AsOsAgnostic();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var books = (BookServiceProxy)(object)bookService;
            var subject = new CleanupOrphanedBookCovers(bookService, diskProvider, appFolder,
                LogManager.GetCurrentClassLogger(), NewConfig(CoverDownloadScope.LibraryBooks), NewCovers(diskProvider, appFolder));

            disk.RootExists = false;
            Assert.That(subject.GetLibraryCleanupEstimate(CancellationToken.None), Is.EqualTo((0L, 0)));
            disk.RootExists = true;
            Assert.That(subject.GetLibraryCleanupEstimate(CancellationToken.None), Is.EqualTo((0L, 0)));

            Assert.That(books.LibraryBookIdCalls, Is.Zero);
            Assert.That(disk.SizedDirectories, Is.Empty);
        }

        [Test]
        public void folder_size_failure_should_fail_the_estimate_without_mutating_covers()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = @"C:\config".AsOsAgnostic();
            var coverRoot = Path.Combine(appFolder.AppDataFolder, "MediaCover", "Books");
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.Directories.AddRange(new[] { "101", "102" }.Select(id => Path.Combine(coverRoot, id)));
            disk.FolderSize = path => Path.GetFileName(path) == "101"
                ? 1024L
                : throw new IOException("The cover directory size is unavailable");
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var books = (BookServiceProxy)(object)bookService;
            var covers = NewCovers(diskProvider, appFolder);
            var subject = new CleanupOrphanedBookCovers(bookService, diskProvider, appFolder,
                LogManager.GetCurrentClassLogger(), NewConfig(CoverDownloadScope.LibraryBooks), covers);

            Assert.Throws<IOException>(() => subject.GetLibraryCleanupEstimate(CancellationToken.None));

            Assert.That(books.LibraryBookIdCalls, Is.EqualTo(1));
            Assert.That(disk.SizedDirectories, Is.EqualTo(new[] { Path.Combine(coverRoot, "101"), Path.Combine(coverRoot, "102") }));
            Assert.That(disk.DeletedDirectories, Is.Empty);
            Assert.That(((CoversProxy)(object)covers).Restored, Is.Empty);
        }

        [Test]
        public void cancellation_between_directories_should_not_return_a_partial_estimate()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = @"C:\config".AsOsAgnostic();
            var coverRoot = Path.Combine(appFolder.AppDataFolder, "MediaCover", "Books");
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.Directories.AddRange(new[] { "101", "102" }.Select(id => Path.Combine(coverRoot, id)));
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var books = (BookServiceProxy)(object)bookService;
            var covers = NewCovers(diskProvider, appFolder);
            var subject = new CleanupOrphanedBookCovers(bookService, diskProvider, appFolder,
                LogManager.GetCurrentClassLogger(), NewConfig(CoverDownloadScope.LibraryBooks), covers);
            using var cancellation = new CancellationTokenSource();
            disk.OnFolderSized = _ => cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => subject.GetLibraryCleanupEstimate(cancellation.Token));

            Assert.That(books.LibraryBookIdCalls, Is.EqualTo(1));
            Assert.That(disk.SizedDirectories, Is.EqualTo(new[] { Path.Combine(coverRoot, "101") }));
            Assert.That(disk.DeletedDirectories, Is.Empty);
            Assert.That(((CoversProxy)(object)covers).Restored, Is.Empty);
        }

        [Test]
        public void should_not_query_books_when_the_cover_root_is_absent()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = @"C:\config".AsOsAgnostic();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            ((DiskProviderProxy)(object)diskProvider).RootExists = false;
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var books = (BookServiceProxy)(object)bookService;
            var subject = new CleanupOrphanedBookCovers(
                bookService,
                diskProvider,
                appFolder,
                LogManager.GetCurrentClassLogger(),
                NewConfig(CoverDownloadScope.AllBooks),
                NewCovers(diskProvider, appFolder));

            subject.Clean();

            Assert.That(books.IncludeUnmonitoredArguments, Is.Empty);
        }
    }
}

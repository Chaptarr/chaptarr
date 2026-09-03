using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
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
            public List<bool> IncludeUnmonitoredArguments { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBookIds))
                {
                    IncludeUnmonitoredArguments.Add((bool)args[0]);
                    return BookIds;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public bool RootExists { get; set; } = true;
            public List<string> Directories { get; } = new();
            public List<string> DeletedDirectories { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IDiskProvider.FolderExists) => RootExists,
                    nameof(IDiskProvider.GetDirectories) => Directories,
                    nameof(IDiskProvider.DeleteFolder) => Delete((string)args[0]),
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }

            private object Delete(string path)
            {
                DeletedDirectories.Add(path);
                return null;
            }
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
                Path.Combine(coverRoot, "not-a-book")
            });
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var books = (BookServiceProxy)(object)bookService;
            books.BookIds = new List<int> { 101 };
            var subject = new CleanupOrphanedBookCovers(
                bookService,
                diskProvider,
                appFolder,
                LogManager.GetCurrentClassLogger());

            subject.Clean();

            Assert.That(disk.DeletedDirectories, Is.EqualTo(new[] { Path.Combine(coverRoot, "102") }));
            Assert.That(books.IncludeUnmonitoredArguments, Is.EqualTo(new[] { true }));
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
                LogManager.GetCurrentClassLogger());

            subject.Clean();

            Assert.That(books.IncludeUnmonitoredArguments, Is.Empty);
        }
    }
}

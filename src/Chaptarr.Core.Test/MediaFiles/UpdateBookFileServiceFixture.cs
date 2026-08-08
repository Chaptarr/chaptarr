using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class UpdateBookFileServiceFixture
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private class DiskProviderProxy : DispatchProxy
        {
            public DateTime LastWrite { get; set; }
            public List<string> CallLog { get; set; }
            public string SetPath { get; private set; }
            public DateTime? SetDate { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IDiskProvider.FileGetLastWrite):
                        return LastWrite;
                    case nameof(IDiskProvider.FileSetLastWriteTime):
                        CallLog?.Add("write");
                        SetPath = (string)args[0];
                        SetDate = (DateTime)args[1];
                        return null;
                    default:
                        throw new NotImplementedException($"Unexpected call to IDiskProvider.{targetMethod?.Name}");
                }
            }
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            public FileDateType FileDate { get; set; } = FileDateType.BookReleaseDate;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_FileDate" => FileDate,
                    _ => throw new NotImplementedException($"Unexpected call to IConfigService.{targetMethod?.Name}")
                };
            }
        }

        private class WatcherProxy : DispatchProxy
        {
            public List<string> CallLog { get; set; }
            public List<string> AnnouncedPaths { get; } = new List<string>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderWatchingService.ReportFileSystemChangeBeginning))
                {
                    CallLog?.Add("announce");
                    AnnouncedPaths.AddRange((string[])args[0]);
                    return null;
                }

                throw new NotImplementedException($"Unexpected call to IRootFolderWatchingService.{targetMethod?.Name}");
            }
        }

        private class ThrowingBookServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Unexpected call to IBookService.{targetMethod?.Name}");
            }
        }

        private class FileMutationSafetyProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IFileMutationSafetyService.EnsureMutableFile))
                {
                    return null;
                }

                throw new NotImplementedException($"Unexpected call to IFileMutationSafetyService.{targetMethod?.Name}");
            }
        }

        private List<string> _callLog;
        private DiskProviderProxy _diskProxy;
        private ConfigServiceProxy _configProxy;
        private WatcherProxy _watcherProxy;
        private IDiskProvider _diskProvider;
        private IConfigService _configService;
        private IBookService _bookService;
        private IRootFolderWatchingService _watcher;

        [SetUp]
        public void Setup()
        {
            _callLog = new List<string>();

            _diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            _diskProxy = (DiskProviderProxy)_diskProvider;
            _diskProxy.CallLog = _callLog;

            _configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            _configProxy = (ConfigServiceProxy)_configService;

            _watcher = DispatchProxy.Create<IRootFolderWatchingService, WatcherProxy>();
            _watcherProxy = (WatcherProxy)_watcher;
            _watcherProxy.CallLog = _callLog;

            _bookService = DispatchProxy.Create<IBookService, ThrowingBookServiceProxy>();
        }

        private UpdateBookFileService BuildService()
        {
            return new UpdateBookFileService(
                _diskProvider,
                _configService,
                _bookService,
                _watcher,
                DispatchProxy.Create<IFileMutationSafetyService, FileMutationSafetyProxy>(),
                LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_announce_write_to_watcher_before_setting_date()
        {
            var bookFile = new BookFile { Path = "/library/Some Author/book.m4b" };
            var book = new Book { ReleaseDate = new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc) };

            _diskProxy.LastWrite = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            BuildService().ChangeFileDateForFile(bookFile, new Author(), book);

            Assert.That(_callLog, Is.EqualTo(new[] { "announce", "write" }));
            Assert.That(_watcherProxy.AnnouncedPaths, Is.EqualTo(new[] { bookFile.Path }));
            Assert.That(_diskProxy.SetPath, Is.EqualTo(bookFile.Path));
            Assert.That(_diskProxy.SetDate, Is.EqualTo(book.ReleaseDate.Value));
        }

        [Test]
        public void should_not_write_or_announce_when_date_already_matches()
        {
            var releaseDate = new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            var bookFile = new BookFile { Path = "/library/Some Author/book.m4b" };
            var book = new Book { ReleaseDate = releaseDate };

            _diskProxy.LastWrite = releaseDate;

            BuildService().ChangeFileDateForFile(bookFile, new Author(), book);

            Assert.That(_callLog, Is.Empty);
        }

        [Test]
        public void should_clamp_pre_epoch_release_date_to_epoch()
        {
            if (OsInfo.IsWindows)
            {
                Assert.Ignore("Epoch clamp only applies on non-Windows");
            }

            var bookFile = new BookFile { Path = "/library/Some Author/classic.m4b" };
            var book = new Book { ReleaseDate = new DateTime(1950, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

            _diskProxy.LastWrite = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            BuildService().ChangeFileDateForFile(bookFile, new Author(), book);

            Assert.That(_callLog, Is.EqualTo(new[] { "announce", "write" }));
            Assert.That(_diskProxy.SetDate, Is.EqualTo(Epoch));
        }

        [Test]
        public void should_not_write_when_pre_epoch_file_already_at_epoch()
        {
            if (OsInfo.IsWindows)
            {
                Assert.Ignore("Epoch clamp only applies on non-Windows");
            }

            var bookFile = new BookFile { Path = "/library/Some Author/classic.m4b" };
            var book = new Book { ReleaseDate = new DateTime(1950, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

            _diskProxy.LastWrite = Epoch;

            BuildService().ChangeFileDateForFile(bookFile, new Author(), book);

            Assert.That(_callLog, Is.Empty);
        }

        [Test]
        public void should_skip_scanned_handler_when_file_date_disabled()
        {
            _configProxy.FileDate = FileDateType.None;

            Assert.DoesNotThrow(() => BuildService().Handle(new AuthorScannedEvent(new Author())));
        }
    }
}

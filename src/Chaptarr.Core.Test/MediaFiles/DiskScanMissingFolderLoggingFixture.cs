using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    [NonParallelizable]
    public class DiskScanMissingFolderLoggingFixture
    {
        private sealed class NoOpMediaFileTableCleanupService : IMediaFileTableCleanupService
        {
            public void Clean(string folder, List<string> filesOnDisk)
            {
            }

            public void Clean(string folder, List<string> filesOnDisk, string mediaType)
            {
            }
        }

        private LoggingConfiguration _previousConfiguration;

        [SetUp]
        public void SetUp()
        {
            _previousConfiguration = LogManager.Configuration;
        }

        [TearDown]
        public void TearDown()
        {
            LogManager.Configuration = _previousConfiguration;
            LogManager.ReconfigExistingLoggers();
        }

        [Test]
        public void missing_author_folder_under_available_root_cleans_stale_rows_without_mount_warning()
        {
            var root = "/books";
            var target = "/books/Missing Author";
            var memory = ConfigureLogging();
            var subject = CreateSubject(root, rootExists: true);

            subject.Scan(new List<string> { target }, authorIds: new List<int>());

            Assert.That(memory.Logs, Has.Some.EqualTo($"Info|Folder {target} no longer exists but root folder {root} is available; cleaning up its tracked files."));
            Assert.That(memory.Logs, Has.Some.EqualTo($"Debug|Cleaning up media files in DB for folder: {target}"));
            Assert.That(memory.Logs, Has.Some.EqualTo("Debug|Cleaning audiobook files"));
            Assert.That(memory.Logs, Has.Some.EqualTo("Debug|Cleaning ebook files"));
            Assert.That(memory.Logs.Any(log => log.StartsWith("Warn|", StringComparison.Ordinal)), Is.False);
        }

        [Test]
        public void missing_author_folder_under_unavailable_root_warns_about_mount()
        {
            var root = "/books";
            var target = "/books/Missing Author";
            var memory = ConfigureLogging();
            var subject = CreateSubject(root, rootExists: false);

            subject.Scan(new List<string> { target }, authorIds: new List<int>());

            Assert.That(memory.Logs, Has.Some.EqualTo($"Warn|Skipping scan cleanup for {target} because its root folder {root} is not visible. This may be a missing mount or unavailable root folder."));
        }

        [Test]
        public void rescan_reports_each_folder_as_a_command_progress_message()
        {
            var memory = ConfigureProgressLogging();
            var subject = CreateSubject("/books", rootExists: true);

            subject.ReportFolderProgress(2, 5, "/books/Author", isInitialImport: false);

            Assert.That(memory.Logs, Has.Some.EqualTo("Info|progress|Scanning folder 2/5: /books/Author"));
        }

        [Test]
        public void initial_import_keeps_folder_progress_out_of_the_command_message()
        {
            var memory = ConfigureProgressLogging();
            var subject = CreateSubject("/books", rootExists: true);

            subject.ReportFolderProgress(2, 5, "/books/Author", isInitialImport: true);

            Assert.That(memory.Logs, Has.Some.EqualTo("Debug|plain|Scanning folder 2/5: /books/Author"));
        }

        private static DiskScanService CreateSubject(string rootPath, bool rootExists)
        {
            var diskProvider = DispatchProxy.Create<NzbDrone.Common.Disk.IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.RootPath = rootPath;
            diskProxy.RootExists = rootExists;

            var rootService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootService).Root = new RootFolder
            {
                Id = 1,
                Path = rootPath,
                FolderType = FolderType.Mixed
            };

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var eventAggregator = DispatchProxy.Create<IEventAggregator, EventAggregatorProxy>();
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            var cleanupService = DispatchProxy.Create<IMediaFileTableCleanupService, MediaFileTableCleanupServiceProxy>();

            return new DiskScanService(
                configService: null,
                diskProvider: diskProvider,
                mediaFileAttributeService: null,
                calibre: null,
                mediaFileService: null,
                metadataTagService: null,
                ingestQueueRepository: null,
                importOrchestrator: null,
                authorService: authorService,
                rootFolderService: rootService,
                mediaFileTableCleanupService: new NoOpMediaFileTableCleanupService(),
                commandQueueManager: commandQueue,
                eventAggregator: eventAggregator,
                logger: LogManager.GetLogger(nameof(DiskScanMissingFolderLoggingFixture)));
        }

        private sealed class ProgressFlagTarget : TargetWithLayout
        {
            public List<string> Logs { get; } = new();

            protected override void Write(LogEventInfo logEvent)
            {
                var kind = logEvent.Properties.ContainsKey("Status") ? "progress" : "plain";
                Logs.Add($"{logEvent.Level}|{kind}|{logEvent.FormattedMessage}");
            }
        }

        private static ProgressFlagTarget ConfigureProgressLogging()
        {
            var target = new ProgressFlagTarget();
            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, target, nameof(DiskScanMissingFolderLoggingFixture));
            LogManager.Configuration = config;
            LogManager.ReconfigExistingLoggers();
            return target;
        }

        private static MemoryTarget ConfigureLogging(string layout = "${level}|${message}")
        {
            var memory = new MemoryTarget("disk-scan-memory")
            {
                Layout = layout
            };
            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, memory, nameof(DiskScanMissingFolderLoggingFixture));
            LogManager.Configuration = config;
            LogManager.ReconfigExistingLoggers();
            return memory;
        }

        public class DiskProviderProxy : DispatchProxy
        {
            public string RootPath { get; set; }
            public bool RootExists { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "FolderExists")
                {
                    return string.Equals((string)args[0], RootPath, StringComparison.Ordinal) && RootExists;
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        public class RootFolderServiceProxy : DispatchProxy
        {
            public RootFolder Root { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.GetBestRootFolder))
                {
                    return Root;
                }

                throw new NotImplementedException($"Test proxy does not implement IRootFolderService.{targetMethod?.Name}");
            }
        }

        public class AuthorServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthors))
                {
                    return new List<Author>();
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }
        }

        public class EventAggregatorProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return null;
            }
        }

        public class CommandQueueProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IManageCommandQueue.Get))
                {
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IManageCommandQueue.{targetMethod?.Name}");
            }
        }

        public class MediaFileTableCleanupServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileTableCleanupService.Clean))
                {
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IMediaFileTableCleanupService.{targetMethod?.Name}");
            }
        }
    }
}

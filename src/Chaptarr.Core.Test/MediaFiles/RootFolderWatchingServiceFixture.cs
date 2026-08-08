using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class RootFolderWatchingServiceFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class StubCommandQueueManager : IManageCommandQueue
        {
            public List<Command> Pushed { get; } = new();

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command
            {
                foreach (var cmd in commands ?? new List<TCommand>())
                {
                    Pushed.Add(cmd);
                }

                return new List<CommandModel>();
            }

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) where TCommand : Command
            {
                Pushed.Add(command);
                return new CommandModel { Name = command?.Name };
            }

            public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotImplementedException();
            public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken) => throw new NotImplementedException();
            public List<CommandModel> All() => throw new NotImplementedException();
            public CommandModel Get(int id) => throw new NotImplementedException();
            public List<CommandModel> GetStarted() => throw new NotImplementedException();
            public void SetMessage(CommandModel command, string message) => throw new NotImplementedException();
            public void TouchProgress(CommandModel command) => throw new NotImplementedException();
            public void SetResult(CommandModel command, CommandResult result) => throw new NotImplementedException();
            public void Start(CommandModel command) => throw new NotImplementedException();
            public void Complete(CommandModel command, string message) => throw new NotImplementedException();
            public void Fail(CommandModel command, string message, Exception e) => throw new NotImplementedException();
            public void Requeue() => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            public bool GranularFileSystemScanning { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_GranularFileSystemScanning")
                {
                    return GranularFileSystemScanning;
                }

                if (targetMethod?.Name == "get_WatchLibraryForChanges")
                {
                    return false;
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IConfigService).Name}.{targetMethod?.Name}");
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.FolderExists) &&
                    args?.Length == 1 &&
                    args[0] is string folderPath)
                {
                    return Directory.Exists(folderPath);
                }

                if (targetMethod?.Name == nameof(IDiskProvider.GetParentFolder) &&
                    args?.Length == 1 &&
                    args[0] is string path)
                {
                    try
                    {
                        return Directory.GetParent(path.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IDiskProvider).Name}.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_queue_parent_folder_for_deleted_file_in_granular_mode()
        {
            using var scope = new TempScope();
            var authorFolder = scope.CreateFolder("Stephen King");

            using var watcher = new FileSystemWatcher(scope.RootPath);
            var queue = new StubCommandQueueManager();
            var subject = CreateSubject(queue, granularScanning: true);

            InvokeWatcherChanged(subject, watcher, new FileSystemEventArgs(WatcherChangeTypes.Deleted, authorFolder, "The Little Sisters of Eluria.azw3"));
            InvokeScanPending(subject);

            var command = queue.Pushed.OfType<RescanFoldersCommand>().Single();
            Assert.That(command.Folders, Is.EquivalentTo(new[] { authorFolder }));
        }

        [Test]
        public void should_queue_old_and_new_parent_folders_for_cross_folder_rename()
        {
            using var scope = new TempScope();
            var oldFolder = scope.CreateFolder("Old");
            var newFolder = scope.CreateFolder("New");

            using var watcher = new FileSystemWatcher(scope.RootPath);
            var queue = new StubCommandQueueManager();
            var subject = CreateSubject(queue, granularScanning: true);

            var renamed = new RenamedEventArgs(
                WatcherChangeTypes.Renamed,
                scope.RootPath,
                Path.Combine("New", "book.azw3"),
                Path.Combine("Old", "book.azw3"));

            InvokeWatcherChanged(subject, watcher, renamed);
            InvokeScanPending(subject);

            var command = queue.Pushed.OfType<RescanFoldersCommand>().Single();
            Assert.That(command.Folders, Is.EquivalentTo(new[] { oldFolder, newFolder }));
        }

        [Test]
        public void should_fall_back_to_watched_root_when_renamed_path_leaves_root()
        {
            using var scope = new TempScope();
            var insideFolder = scope.CreateFolder("Inside");
            var outsideFolder = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(scope.RootPath), Guid.NewGuid().ToString("N"))).FullName;

            try
            {
                using var watcher = new FileSystemWatcher(scope.RootPath);
                var queue = new StubCommandQueueManager();
                var subject = CreateSubject(queue, granularScanning: true);

                var renamed = new RenamedEventArgs(
                    WatcherChangeTypes.Renamed,
                    Path.GetDirectoryName(scope.RootPath),
                    Path.Combine(Path.GetFileName(outsideFolder), "book.azw3"),
                    Path.Combine(Path.GetFileName(scope.RootPath), "Inside", "book.azw3"));

                InvokeWatcherChanged(subject, watcher, renamed);
                InvokeScanPending(subject);

                var command = queue.Pushed.OfType<RescanFoldersCommand>().Single();
                Assert.That(command.Folders, Is.EquivalentTo(new[] { insideFolder, scope.RootPath }));
            }
            finally
            {
                if (Directory.Exists(outsideFolder))
                {
                    Directory.Delete(outsideFolder, true);
                }
            }
        }

        [Test]
        public void should_ignore_non_media_file_changes_in_granular_mode()
        {
            using var scope = new TempScope();
            var authorFolder = scope.CreateFolder("Author");

            using var watcher = new FileSystemWatcher(scope.RootPath);
            var queue = new StubCommandQueueManager();
            var subject = CreateSubject(queue, granularScanning: true);

            InvokeWatcherChanged(subject, watcher, new FileSystemEventArgs(WatcherChangeTypes.Changed, authorFolder, "metadata.txt"));
            InvokeScanPending(subject);

            Assert.That(queue.Pushed, Is.Empty);
        }

        [Test]
        public void should_ignore_internal_conversion_folder_changes_in_granular_mode()
        {
            using var scope = new TempScope();
            var conversionFolder = scope.CreateFolder(Path.Combine("Author", ".chaptarr-conversions", "download", "work"));

            using var watcher = new FileSystemWatcher(scope.RootPath);
            var queue = new StubCommandQueueManager();
            var subject = CreateSubject(queue, granularScanning: true);

            InvokeWatcherChanged(subject, watcher, new FileSystemEventArgs(WatcherChangeTypes.Created, conversionFolder, "converted.m4b"));
            InvokeScanPending(subject);

            Assert.That(queue.Pushed, Is.Empty);
        }

        [Test]
        public void should_ignore_internal_conversion_folder_changes_in_legacy_mode()
        {
            using var scope = new TempScope();
            var conversionFolder = scope.CreateFolder(Path.Combine("Author", ".chaptarr-conversions", "download", "work"));

            using var watcher = new FileSystemWatcher(scope.RootPath);
            var queue = new StubCommandQueueManager();
            var subject = CreateSubject(queue, granularScanning: false);

            InvokeWatcherChanged(subject, watcher, new FileSystemEventArgs(WatcherChangeTypes.Created, conversionFolder, "converted.m4b"));
            InvokeScanPending(subject);

            Assert.That(queue.Pushed, Is.Empty);
        }

        private static RootFolderWatchingService CreateSubject(StubCommandQueueManager commandQueueManager, bool granularScanning)
        {
            var config = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();
            ((ConfigServiceProxy)(object)config).GranularFileSystemScanning = granularScanning;

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();

            return new RootFolderWatchingService(
                DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                commandQueueManager,
                config,
                diskProvider,
                LogManager.GetCurrentClassLogger());
        }

        private static void InvokeWatcherChanged(RootFolderWatchingService subject, FileSystemWatcher watcher, FileSystemEventArgs args)
        {
            var method = typeof(RootFolderWatchingService).GetMethod("Watcher_Changed", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(subject, new object[] { watcher, args });
        }

        private static void InvokeScanPending(RootFolderWatchingService subject)
        {
            var method = typeof(RootFolderWatchingService).GetMethod("ScanPending", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(subject, Array.Empty<object>());
        }

        private sealed class TempScope : IDisposable
        {
            public TempScope()
            {
                RootPath = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
            }

            public string RootPath { get; }

            public string CreateFolder(string relativePath)
            {
                return Directory.CreateDirectory(Path.Combine(RootPath, relativePath)).FullName;
            }

            public void Dispose()
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, true);
                }
            }
        }
    }
}

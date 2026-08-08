using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    public interface IRootFolderWatchingService
    {
        void ReportFileSystemChangeBeginning(params string[] paths);
    }

    public sealed class RootFolderWatchingService : IRootFolderWatchingService,
        IDisposable,
        IHandle<ModelEvent<RootFolder>>,
        IHandle<ApplicationStartedEvent>,
        IHandle<ConfigSavedEvent>
    {
        private const int DEBOUNCE_TIMEOUT_SECONDS = 30;

        private readonly ConcurrentDictionary<string, FileSystemWatcher> _fileSystemWatchers = new ConcurrentDictionary<string, FileSystemWatcher>();
        private readonly ConcurrentDictionary<string, int> _tempIgnoredPaths = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, string> _changedPaths = new ConcurrentDictionary<string, string>();

        private readonly IRootFolderService _rootFolderService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IConfigService _configService;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        private readonly Debouncer _scanDebouncer;
        private bool _watchForChanges;

        public RootFolderWatchingService(IRootFolderService rootFolderService,
                                         IManageCommandQueue commandQueueManager,
                                         IConfigService configService,
                                         IDiskProvider diskProvider,
                                         Logger logger)
        {
            _rootFolderService = rootFolderService;
            _commandQueueManager = commandQueueManager;
            _configService = configService;
            _diskProvider = diskProvider;
            _logger = logger;

            _scanDebouncer = new Debouncer(ScanPending, TimeSpan.FromSeconds(DEBOUNCE_TIMEOUT_SECONDS), true);
        }

        public void Dispose()
        {
            foreach (var watcher in _fileSystemWatchers.Values)
            {
                DisposeWatcher(watcher, false);
            }
        }

        public void ReportFileSystemChangeBeginning(params string[] paths)
        {
            foreach (var path in paths.Where(x => x.IsNotNullOrWhiteSpace()))
            {
                _logger.Trace($"reporting start of change to {path}");
                _tempIgnoredPaths.AddOrUpdate(path.CleanFilePathBasic(), 1, (key, value) => value + 1);
            }
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _watchForChanges = _configService.WatchLibraryForChanges;

            if (_watchForChanges)
            {
                _rootFolderService.All().ForEach(x => StartWatchingPath(x.Path));
            }
        }

        public void Handle(ConfigSavedEvent message)
        {
            var oldWatch = _watchForChanges;
            _watchForChanges = _configService.WatchLibraryForChanges;

            if (_watchForChanges != oldWatch)
            {
                if (_watchForChanges)
                {
                    _rootFolderService.All().ForEach(x => StartWatchingPath(x.Path));
                }
                else
                {
                    _rootFolderService.All().ForEach(x => StopWatchingPath(x.Path));
                }
            }
        }

        public void Handle(ModelEvent<RootFolder> message)
        {
            if (message.Action == ModelAction.Created && _watchForChanges)
            {
                StartWatchingPath(message.Model.Path);
            }
            else if (message.Action == ModelAction.Deleted)
            {
                StopWatchingPath(message.Model.Path);
            }
        }

        private void StartWatchingPath(string path)
        {
            Ensure.That(path, () => path).IsNotNullOrWhiteSpace();
            Ensure.That(path, () => path).IsValidPath(PathValidationType.CurrentOs);

            // Already being watched
            if (_fileSystemWatchers.ContainsKey(path))
            {
                return;
            }

            // Creating a FileSystemWatcher over the LAN can take hundreds of milliseconds, so wrap it in a Task to do them all in parallel
            Task.Run(() =>
            {
                try
                {
                    var newWatcher = new FileSystemWatcher(path, "*")
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 65536,
                        NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite
                    };

                    newWatcher.Created += Watcher_Changed;
                    newWatcher.Deleted += Watcher_Changed;
                    newWatcher.Renamed += Watcher_Changed;
                    newWatcher.Changed += Watcher_Changed;
                    newWatcher.Error += Watcher_Error;

                    if (_fileSystemWatchers.TryAdd(path, newWatcher))
                    {
                        newWatcher.EnableRaisingEvents = true;
                        _logger.Info("Watching directory {0}", path);
                    }
                    else
                    {
                        DisposeWatcher(newWatcher, false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error watching path: {0}", path);
                }
            });
        }

        private void StopWatchingPath(string path)
        {
            if (_fileSystemWatchers.TryGetValue(path, out var watcher))
            {
                DisposeWatcher(watcher, true);
            }
        }

        private void Watcher_Error(object sender, ErrorEventArgs e)
        {
            var ex = e.GetException();
            var dw = (FileSystemWatcher)sender;

            if (ex.GetType() == typeof(InternalBufferOverflowException))
            {
                _logger.Warn(ex, "The file system watcher experienced an internal buffer overflow for: {0}", dw.Path);

                _changedPaths.TryAdd(dw.Path, dw.Path);
                _scanDebouncer.Execute();
            }
            else
            {
                _logger.Error(ex, "Error in Directory watcher for: {0}", dw.Path);

                DisposeWatcher(dw, true);
            }
        }

        private void Watcher_Changed(object sender, FileSystemEventArgs e)
        {
            try
            {
                var rootFolder = ((FileSystemWatcher)sender).Path;
                var changedPaths = GetChangedPaths(e).ToList();

                if (changedPaths.Empty())
                {
                    throw new ArgumentNullException("path");
                }

                foreach (var path in changedPaths)
                {
                    _changedPaths.TryAdd(path, rootFolder);
                }

                _scanDebouncer.Execute();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Exception in ReportFileSystemChanged. Path: {0}", e.FullPath);
            }
        }

        private void ScanPending()
        {
            var pairs = _changedPaths.ToArray();
            _changedPaths.Clear();

            var ignored = _tempIgnoredPaths.Keys.ToArray();
            _tempIgnoredPaths.Clear();

            var granularScanning = _configService.GranularFileSystemScanning;
            var toScan = new HashSet<string>();
            var detailedPaths = new Dictionary<string, List<string>>();

            foreach (var item in pairs)
            {
                var path = item.Key.CleanFilePathBasic();
                var rootFolder = item.Value;

                if (granularScanning)
                {
                    var folderToScan = GetGranularScanFolder(rootFolder, path);

                    if (!ShouldIgnoreGranularChange(path, folderToScan, ignored))
                    {
                        _logger.Trace("Actioning change to {0}", path);

                        // Track specific folders to scan
                        toScan.Add(folderToScan);

                        // Also track for logging
                        if (!detailedPaths.ContainsKey(rootFolder))
                        {
                            detailedPaths[rootFolder] = new List<string>();
                        }

                        detailedPaths[rootFolder].Add(folderToScan);
                    }
                    else
                    {
                        _logger.Trace("Ignoring change to {0}", path);
                    }
                }
                else
                {
                    if (!ShouldIgnoreChange(path, ignored))
                    {
                        _logger.Trace("Actioning change to {0}", path);

                        // Legacy behavior - scan entire root folder
                        toScan.Add(rootFolder);
                    }
                    else
                    {
                        _logger.Trace("Ignoring change to {0}", path);
                    }
                }
            }

            if (toScan.Any())
            {
                if (granularScanning)
                {
                    _logger.Debug("[IMPORT-TRIGGER] RootFolderWatchingService: File system change detected, triggering granular scan for {0} specific folders", toScan.Count);
                    foreach (var root in detailedPaths)
                    {
                        _logger.Debug("Root {0}: {1} folders changed - {2}",
                            root.Key,
                            root.Value.Count,
                            string.Join(", ", root.Value.Take(5)) + (root.Value.Count > 5 ? "..." : ""));
                    }
                }
                else
                {
                    _logger.Debug("[IMPORT-TRIGGER] RootFolderWatchingService: File system change detected, triggering RescanFoldersCommand for {0} root folders: [{1}]", toScan.Count, string.Join(", ", toScan));
                }

                _commandQueueManager.Push(new RescanFoldersCommand(toScan.ToList(), FilterFilesType.Known, null));
            }
        }

        private bool ShouldIgnoreChange(string cleanPath, string[] ignoredPaths)
        {
            var cleaned = cleanPath.CleanFilePathBasic();

            // Skip partial/backup
            if (cleanPath.EndsWith(".partial~") ||
                cleanPath.EndsWith(".backup~"))
            {
                return true;
            }

            if (IsExcludedLibraryPath(cleaned))
            {
                return true;
            }

            // only proceed for directories and files with music extensions
            var extension = Path.GetExtension(cleaned);
            if (extension.IsNullOrWhiteSpace() && !Directory.Exists(cleaned))
            {
                return true;
            }

            if (extension.IsNotNullOrWhiteSpace() && !MediaFileExtensions.AllExtensions.Contains(extension))
            {
                return true;
            }

            // If the parent of an ignored path has a change event, ignore that too
            // Note that we can't afford to use the PathEquals or IsParentPath functions because
            // these rely on disk access which is too slow when trying to handle many update events
            return ignoredPaths.Any(i => i.Equals(cleaned, DiskProviderBase.PathStringComparison) ||
                                    i.StartsWith(cleaned + Path.DirectorySeparatorChar, DiskProviderBase.PathStringComparison) ||
                                    Path.GetDirectoryName(i).Equals(cleaned, DiskProviderBase.PathStringComparison));
        }

        private IEnumerable<string> GetChangedPaths(FileSystemEventArgs e)
        {
            if (e is RenamedEventArgs renamed && renamed.OldFullPath.IsNotNullOrWhiteSpace())
            {
                yield return renamed.OldFullPath;
            }

            if (e.FullPath.IsNotNullOrWhiteSpace())
            {
                yield return e.FullPath;
            }
        }

        private string GetGranularScanFolder(string rootFolder, string path)
        {
            var cleanedRootFolder = rootFolder.CleanFilePathBasic();
            var cleanedPath = path.CleanFilePathBasic();

            string folderToScan;

            if (_diskProvider.FolderExists(cleanedPath))
            {
                folderToScan = cleanedPath;
            }
            else
            {
                folderToScan = _diskProvider.GetParentFolder(cleanedPath);
            }

            if (folderToScan.IsNullOrWhiteSpace())
            {
                return cleanedRootFolder;
            }

            folderToScan = folderToScan.CleanFilePathBasic();

            if (folderToScan.PathEquals(cleanedRootFolder) || cleanedRootFolder.IsParentPath(folderToScan))
            {
                return folderToScan;
            }

            return cleanedRootFolder;
        }

        private bool ShouldIgnoreGranularChange(string rawPath, string scanFolder, string[] ignoredPaths)
        {
            var cleanedRawPath = rawPath.CleanFilePathBasic();

            if (cleanedRawPath.EndsWith(".partial~") ||
                cleanedRawPath.EndsWith(".backup~"))
            {
                return true;
            }

            if (IsExcludedLibraryPath(cleanedRawPath) || IsExcludedLibraryPath(scanFolder))
            {
                return true;
            }

            var extension = Path.GetExtension(cleanedRawPath);
            if (extension.IsNotNullOrWhiteSpace() && !MediaFileExtensions.AllExtensions.Contains(extension))
            {
                return true;
            }

            var cleanedScanFolder = scanFolder.CleanFilePathBasic();

            return ignoredPaths.Any(i => i.Equals(cleanedScanFolder, DiskProviderBase.PathStringComparison) ||
                                         i.StartsWith(cleanedScanFolder + Path.DirectorySeparatorChar, DiskProviderBase.PathStringComparison) ||
                                         string.Equals(Path.GetDirectoryName(i), cleanedScanFolder, DiskProviderBase.PathStringComparison));
        }

        private static bool IsExcludedLibraryPath(string path)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return false;
            }

            var cleaned = path.CleanFilePathBasic();
            return DiskScanService.ExcludedSubFoldersRegex.IsMatch(cleaned + Path.DirectorySeparatorChar);
        }

        private void DisposeWatcher(FileSystemWatcher watcher, bool removeFromList)
        {
            try
            {
                using (watcher)
                {
                    _logger.Info("Stopping directory watching for path {0}", watcher.Path);

                    watcher.Created -= Watcher_Changed;
                    watcher.Deleted -= Watcher_Changed;
                    watcher.Renamed -= Watcher_Changed;
                    watcher.Changed -= Watcher_Changed;
                    watcher.Error -= Watcher_Error;

                    try
                    {
                        watcher.EnableRaisingEvents = false;
                    }
                    catch (InvalidOperationException)
                    {
                        // Seeing this under mono on linux sometimes
                        // Collection was modified; enumeration operation may not execute.
                    }
                }
            }
            catch
            {
                // we don't care about exceptions disposing
            }
            finally
            {
                if (removeFromList)
                {
                    _fileSystemWatchers.TryRemove(watcher.Path, out _);
                }
            }
        }
    }
}

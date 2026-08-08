using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.RootFolders
{
    public interface IRootFolderService
    {
        List<RootFolder> All();
        List<RootFolder> AllWithSpaceStats();
        RootFolder Add(RootFolder rootFolder);
        RootFolder Update(RootFolder rootFolder);
        void Remove(int id);
        RootFolder Get(int id);
        List<RootFolder> AllForTag(int tagId);
        RootFolder GetBestRootFolder(string path);
        RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders);
        string GetBestRootFolderPath(string path);
        string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders);
    }

    public class RootFolderService : IRootFolderService, IHandle<ModelEvent<RemotePathMapping>>
    {
        private readonly IRootFolderRepository _rootFolderRepository;
        private readonly IIngestQueueRepository _ingestQueueRepository;
        private readonly IDiskProvider _diskProvider;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public RootFolderService(IRootFolderRepository rootFolderRepository,
                                 IIngestQueueRepository ingestQueueRepository,
                                 IDiskProvider diskProvider,
                                 IManageCommandQueue commandQueueManager,
                                 Logger logger)
        {
            _rootFolderRepository = rootFolderRepository;
            _ingestQueueRepository = ingestQueueRepository;
            _diskProvider = diskProvider;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public List<RootFolder> All()
        {
            var rootFolders = _rootFolderRepository.All().ToList();

            return rootFolders;
        }

        public List<RootFolder> AllWithSpaceStats()
        {
            var rootFolders = _rootFolderRepository.All().ToList();

            rootFolders.ForEach(folder =>
            {
                try
                {
                    if (folder.Path.IsPathValid(PathValidationType.CurrentOs))
                    {
                        GetDetails(folder);
                    }
                }

                //We don't want an exception to prevent the root folders from loading in the UI, so they can still be deleted
                catch (Exception ex)
                {
                    _logger.Error(ex, "Unable to get free space and unmapped folders for root folder {0}", folder.Path);
                }
            });

            return rootFolders;
        }

        private void VerifyRootFolder(RootFolder rootFolder)
        {
            if (string.IsNullOrWhiteSpace(rootFolder.Path) || !Path.IsPathRooted(rootFolder.Path))
            {
                throw new ArgumentException("Invalid path");
            }

            if (!_diskProvider.FolderExists(rootFolder.Path))
            {
                throw new DirectoryNotFoundException("Can't add root directory that doesn't exist.");
            }

            if (!_diskProvider.FolderWritable(rootFolder.Path))
            {
                var user = ProcessUserInfo.GetUserNameWithIds();
                var dockerEnv = ProcessUserInfo.GetDockerUserEnvSummary();
                var dockerHint = dockerEnv == null ? string.Empty : $" (env: {dockerEnv})";

                throw new UnauthorizedAccessException(
                    $"Folder '{rootFolder.Path}' is not writable by user '{user}'. Permission denied - please ensure the Chaptarr process has write access to this directory and its subdirectories.{dockerHint} " +
                    "Chaptarr determines this by attempting to create and delete a temporary file in the folder. " +
                    "If you're using Docker bind mounts, make sure the host folder exists (Docker may create missing folders as root:root) and that its ownership/permissions allow writes for this UID/GID.");
            }
        }

	        public RootFolder Add(RootFolder rootFolder)
	        {
	            // CRITICAL: Log the incoming FolderType to ensure it's set correctly
	            _logger.Info($"Adding root folder '{rootFolder.Path}' with type {rootFolder.FolderType} (numeric: {(int)rootFolder.FolderType})");

	            VerifyRootFolder(rootFolder);

	            if (All().Exists(r => r.Path.PathEquals(rootFolder.Path)))
	            {
	                throw new InvalidOperationException("Root folder already exists.");
	            }

	            try
	            {
	                _ingestQueueRepository.PurgeUnderPath(rootFolder.Path);
	            }
	            catch (Exception ex)
	            {
	                _logger.Debug(ex, "[STAGING-DB] Failed to purge staging entries under new root folder '{0}'", rootFolder.Path);
	            }

	            // REMOVED RESTRICTION: Now allowing multiple root folders per media type
	            // This was previously blocking users from having multiple audiobook or ebook folders
	            // Users can now organize their libraries across multiple drives/locations
	            // Original restriction code preserved below for reference:
	            /*
            if (rootFolder.FolderType != FolderType.Mixed)
            {
                var existingFolderOfType = All().FirstOrDefault(r => r.FolderType == rootFolder.FolderType);
                if (existingFolderOfType != null)
                {
                    var folderTypeName = rootFolder.FolderType == FolderType.Audiobook ? "audiobook" : "ebook";
                    throw new InvalidOperationException($"A {folderTypeName} root folder already exists: {existingFolderOfType.Path}");
                }
            }
            */

            // Note: FolderType.Unknown no longer exists - Mixed is now the default
            // All root folders should have a valid FolderType (Mixed, Audiobook, or Ebook)

            _rootFolderRepository.Insert(rootFolder);

            _logger.Info($"Successfully inserted root folder '{rootFolder.Path}' with type {rootFolder.FolderType}");

            GetDetails(rootFolder);

            // Start an initial scan so existing on-disk files can be discovered and imported.
            _logger.Info($"Starting initial scan of new root folder '{rootFolder.Path}'");
            _commandQueueManager.Push(new RescanFoldersCommand(new List<string> { rootFolder.Path }, FilterFilesType.None, null, true));

            return rootFolder;
        }

        public RootFolder Update(RootFolder rootFolder)
        {
            VerifyRootFolder(rootFolder);

            _rootFolderRepository.Update(rootFolder);

            GetDetails(rootFolder);

            return rootFolder;
        }

        public void Remove(int id)
        {
            RootFolder rootFolder = null;
            try
            {
                rootFolder = _rootFolderRepository.Get(id);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to load root folder {0} before removal", id);
            }

            if (rootFolder != null && !string.IsNullOrWhiteSpace(rootFolder.Path))
            {
                try
                {
                    _ingestQueueRepository.PurgeUnderPath(rootFolder.Path);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[STAGING-DB] Failed to purge staging entries under root folder '{0}'", rootFolder.Path);
                }
            }

            _rootFolderRepository.Delete(id);
        }

        public RootFolder Get(int id)
        {
            var rootFolder = _rootFolderRepository.Get(id);
            GetDetails(rootFolder);

            return rootFolder;
        }

        public List<RootFolder> AllForTag(int tagId)
        {
            return All().Where(r => r.DefaultTags.Contains(tagId)).ToList();
        }

        public RootFolder GetBestRootFolder(string path)
        {
            var folders = All();
            return GetBestRootFolder(path, folders);
        }

        public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders)
        {
            return allRootFolders.Where(r => PathEqualityComparer.Instance.Equals(r.Path, path) || r.Path.IsParentPath(path))
                .MaxBy(r => r.Path.Length);
        }

        public string GetBestRootFolderPath(string path)
        {
            var folders = All();
            return GetBestRootFolderPath(path, folders);
        }

        public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders)
        {
            var possibleRootFolder = GetBestRootFolder(path, allRootFolders);

            if (possibleRootFolder == null)
            {
                var osPath = new OsPath(path);

                return osPath.Directory.ToString().TrimEnd(osPath.IsUnixPath ? '/' : '\\');
            }

            return possibleRootFolder?.Path;
        }

        private void GetDetails(RootFolder rootFolder)
        {
            Task.Run(() =>
            {
                if (_diskProvider.FolderExists(rootFolder.Path))
                {
                    rootFolder.Accessible = true;
                    rootFolder.FreeSpace = _diskProvider.GetAvailableSpace(rootFolder.Path);
                    rootFolder.TotalSpace = _diskProvider.GetTotalSize(rootFolder.Path);
                }
            }).Wait(5000);
        }

        public void Handle(ModelEvent<RemotePathMapping> message)
        {
            var commands = All()
                .Where(x => x.IsCalibreLibrary &&
                       x.CalibreSettings.Host == message.Model.Host &&
                       x.Path.StartsWith(message.Model.LocalPath))
                .Select(x => new RescanFoldersCommand(new List<string> { x.Path }, FilterFilesType.None, null))
                .ToList();

            if (commands.Any())
            {
                _commandQueueManager.PushMany(commands);
            }
        }
    }
}

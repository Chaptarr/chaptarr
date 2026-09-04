using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public class DirectDownloadClientStateStore
    {
        private const string StateFileName = "direct-download-state.json";

        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public DirectDownloadClientStateStore(IDiskProvider diskProvider, Logger logger)
        {
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public IEnumerable<DirectDownloadClientState> LoadAll(string stagingFolder, int clientId)
        {
            var clientRoot = GetClientRoot(stagingFolder, clientId);
            if (!_diskProvider.FolderExists(clientRoot))
            {
                return Array.Empty<DirectDownloadClientState>();
            }

            return _diskProvider.GetFiles(clientRoot, true)
                .Where(path => Path.GetFileName(path).Equals(StateFileName, StringComparison.Ordinal))
                .Select(Load)
                .Where(state => state != null)
                .OrderBy(state => state.CreatedAtUtc)
                .ToList();
        }

        public DirectDownloadClientState Load(string stateFilePath)
        {
            if (!_diskProvider.FileExists(stateFilePath))
            {
                return null;
            }

            string json;
            try
            {
                json = _diskProvider.ReadAllText(stateFilePath);
            }
            catch (IOException)
            {
                return null;
            }

            if (!Json.TryDeserialize<DirectDownloadClientState>(json, out var state) || state == null)
            {
                _logger.Warn("Ignoring malformed Direct download state file '{0}'.", stateFilePath);
                return null;
            }

            return state;
        }

        public DirectDownloadClientState Find(string stagingFolder, int clientId, string downloadId)
        {
            return Load(GetStateFilePath(stagingFolder, clientId, downloadId));
        }

        public void Save(string stagingFolder, int clientId, DirectDownloadClientState state)
        {
            var stateFilePath = GetStateFilePath(stagingFolder, clientId, state.DownloadId);
            _diskProvider.EnsureFolder(Path.GetDirectoryName(stateFilePath));
            state.UpdatedAtUtc = DateTime.UtcNow;
            _diskProvider.WriteAllText(stateFilePath, state.ToJson());
        }

        public void Delete(string stagingFolder, int clientId, string downloadId)
        {
            var stateFilePath = GetStateFilePath(stagingFolder, clientId, downloadId);
            if (_diskProvider.FileExists(stateFilePath))
            {
                _diskProvider.DeleteFile(stateFilePath);
            }
        }

        public string GetDownloadDirectory(string stagingFolder, int clientId, string downloadId)
        {
            var clientRoot = GetClientRoot(stagingFolder, clientId);
            var downloadDir = Path.Combine(clientRoot, downloadId);
            var normalizedStaging = Path.GetFullPath(clientRoot + Path.DirectorySeparatorChar);
            var normalizedDownload = Path.GetFullPath(downloadDir + Path.DirectorySeparatorChar);

            if (!normalizedDownload.StartsWith(normalizedStaging, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Download directory escapes the staging folder boundary.");
            }

            return downloadDir;
        }

        private string GetStateFilePath(string stagingFolder, int clientId, string downloadId)
        {
            return Path.Combine(GetDownloadDirectory(stagingFolder, clientId, downloadId), StateFileName);
        }

        private static string GetClientRoot(string stagingFolder, int clientId)
        {
            return Path.Combine(stagingFolder ?? string.Empty, $"client-{clientId}");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.RemotePathMappings
{
    public interface IRemotePathMappingService
    {
        List<RemotePathMapping> All();
        RemotePathMapping Add(RemotePathMapping mapping);
        void Remove(int id);
        RemotePathMapping Get(int id);
        RemotePathMapping Update(RemotePathMapping mapping);

        OsPath RemapRemoteToLocal(string host, OsPath remotePath);
        OsPath RemapLocalToRemote(string host, OsPath localPath);
        OsPath RemapRemoteToLocal(int downloadClientId, string host, OsPath remotePath);
        OsPath RemapLocalToRemote(int downloadClientId, string host, OsPath localPath);
        RemotePathMappingTestResult Test(RemotePathMapping mapping);
    }

    public class RemotePathMappingService : IRemotePathMappingService, IHandle<ProviderDeletedEvent<IDownloadClient>>
    {
        private readonly IDownloadClientRepository _downloadClientRepository;
        private readonly IRemotePathMappingRepository _remotePathMappingRepository;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        private readonly ICached<List<RemotePathMapping>> _cache;

        public RemotePathMappingService(IDownloadClientRepository downloadClientRepository,
                                        IRemotePathMappingRepository remotePathMappingRepository,
                                        IDiskProvider diskProvider,
                                        ICacheManager cacheManager,
                                        Logger logger)
        {
            _downloadClientRepository = downloadClientRepository;
            _remotePathMappingRepository = remotePathMappingRepository;
            _diskProvider = diskProvider;
            _logger = logger;

            _cache = cacheManager.GetCache<List<RemotePathMapping>>(GetType());
        }

        public List<RemotePathMapping> All()
        {
            return AllStored().Select(WithEffectiveHost).ToList();
        }

        public RemotePathMapping Add(RemotePathMapping mapping)
        {
            mapping = PrepareMapping(mapping);

            var all = AllStored();

            ValidateMapping(all, mapping);

            var result = _remotePathMappingRepository.Insert(mapping);

            _cache.Clear();

            return result;
        }

        public void Remove(int id)
        {
            _remotePathMappingRepository.Delete(id);

            _cache.Clear();
        }

        public RemotePathMapping Get(int id)
        {
            return WithEffectiveHost(_remotePathMappingRepository.Get(id));
        }

        public RemotePathMapping Update(RemotePathMapping mapping)
        {
            mapping = PrepareMapping(mapping);

            var existing = AllStored().Where(v => v.Id != mapping.Id).ToList();

            ValidateMapping(existing, mapping);

            var result = _remotePathMappingRepository.Update(mapping);

            _cache.Clear();

            return result;
        }

        public RemotePathMappingTestResult Test(RemotePathMapping mapping)
        {
            mapping = PrepareMapping(mapping);

            ValidateMapping(new List<RemotePathMapping>(), mapping, false, false);

            var remotePath = new OsPath(mapping.RemotePath);
            var mappedPath = RemapRemoteToLocal(new[] { mapping }, mapping.DownloadClientId, mapping.Host, remotePath);
            var localPathExists = _diskProvider.FolderExists(mapping.LocalPath);
            var mappedPathExists = _diskProvider.FolderExists(mappedPath.FullPath);

            return new RemotePathMappingTestResult
            {
                DownloadClientId = mapping.DownloadClientId,
                Host = mapping.Host,
                RemotePath = mapping.RemotePath,
                LocalPath = mapping.LocalPath,
                MappedPath = mappedPath.FullPath,
                IsMapped = mappedPath.FullPath.PathNotEquals(remotePath.FullPath),
                LocalPathExists = localPathExists,
                LocalPathWritable = localPathExists && _diskProvider.FolderWritable(mapping.LocalPath),
                MappedPathExists = mappedPathExists,
                MappedPathWritable = mappedPathExists && _diskProvider.FolderWritable(mappedPath.FullPath)
            };
        }

        private List<RemotePathMapping> AllStored()
        {
            return _cache.Get("all", () => _remotePathMappingRepository.All().ToList(), TimeSpan.FromSeconds(10));
        }

        private RemotePathMapping PrepareMapping(RemotePathMapping mapping)
        {
            mapping.LocalPath = new OsPath(mapping.LocalPath).AsDirectory().FullPath;
            mapping.RemotePath = new OsPath(mapping.RemotePath).AsDirectory().FullPath;

            if (mapping.DownloadClientId > 0)
            {
                mapping.Host = GetRequiredDownloadClientHost(mapping.DownloadClientId);
            }

            return mapping;
        }

        private RemotePathMapping WithEffectiveHost(RemotePathMapping mapping)
        {
            if (mapping == null)
            {
                return null;
            }

            var result = new RemotePathMapping
            {
                Id = mapping.Id,
                DownloadClientId = mapping.DownloadClientId,
                Host = mapping.Host,
                RemotePath = mapping.RemotePath,
                LocalPath = mapping.LocalPath
            };

            if (result.DownloadClientId > 0 && TryGetDownloadClientHost(result.DownloadClientId, out var host))
            {
                result.Host = host;
            }

            return result;
        }

        private bool TryGetDownloadClientHost(int downloadClientId, out string host)
        {
            host = null;

            var client = _downloadClientRepository?.Find(downloadClientId);
            if (client == null)
            {
                return false;
            }

            host = client.Settings?.GetType().GetProperty("Host")?.GetValue(client.Settings)?.ToString();

            return host.IsNotNullOrWhiteSpace();
        }

        private string GetRequiredDownloadClientHost(int downloadClientId)
        {
            if (_downloadClientRepository?.Find(downloadClientId) == null)
            {
                throw ValidationError("DownloadClientId", "DownloadClientId does not reference a configured download client.");
            }

            if (!TryGetDownloadClientHost(downloadClientId, out var host))
            {
                throw ValidationError("DownloadClientId", "Selected download client does not have a configured host.");
            }

            return host;
        }

        private void ValidateMapping(List<RemotePathMapping> existing, RemotePathMapping mapping, bool requireLocalPathExists = true, bool checkDuplicates = true)
        {
            if (mapping.Host.IsNullOrWhiteSpace())
            {
                throw ValidationError("Host", "Invalid Host");
            }

            if (mapping.DownloadClientId < 0)
            {
                throw ValidationError("DownloadClientId", "Invalid DownloadClientId");
            }

            var remotePath = new OsPath(mapping.RemotePath);
            var localPath = new OsPath(mapping.LocalPath);

            if (remotePath.IsEmpty)
            {
                throw ValidationError("RemotePath", "Invalid RemotePath. RemotePath cannot be empty.");
            }

            if (localPath.IsEmpty || !localPath.IsRooted)
            {
                throw ValidationError("LocalPath", "Invalid LocalPath. LocalPath cannot be empty and must not be the root.");
            }

            if (requireLocalPathExists && !_diskProvider.FolderExists(localPath.FullPath))
            {
                throw ValidationError("LocalPath", "Can't add mount point directory that doesn't exist.");
            }

            if (!checkDuplicates)
            {
                return;
            }

            if (mapping.DownloadClientId > 0)
            {
                if (existing.Exists(r => r.DownloadClientId == mapping.DownloadClientId && new OsPath(r.RemotePath).AsDirectory() == remotePath))
                {
                    throw ValidationError("RemotePath", "RemotePath already configured for this download client.");
                }

                return;
            }

            if (existing.Exists(r => r.DownloadClientId == 0 &&
                                     string.Equals(r.Host, mapping.Host, StringComparison.InvariantCultureIgnoreCase) &&
                                     new OsPath(r.RemotePath).AsDirectory() == remotePath))
            {
                throw ValidationError("RemotePath", "RemotePath already configured.");
            }
        }

        private static ValidationException ValidationError(string propertyName, string message)
        {
            return new ValidationException(new[] { new ValidationFailure(propertyName, message) });
        }

        public OsPath RemapRemoteToLocal(string host, OsPath remotePath)
        {
            return RemapRemoteToLocal(0, host, remotePath);
        }

        public OsPath RemapRemoteToLocal(int downloadClientId, string host, OsPath remotePath)
        {
            if (remotePath.IsEmpty)
            {
                return remotePath;
            }

            var mappings = All();

            return RemapRemoteToLocal(mappings, downloadClientId, host, remotePath);
        }

        private OsPath RemapRemoteToLocal(IEnumerable<RemotePathMapping> mappings, int downloadClientId, string host, OsPath remotePath)
        {
            var mappingsList = mappings.ToList();
            if (mappingsList.Empty())
            {
                return remotePath;
            }

            _logger.Trace("Evaluating remote path remote mappings for match to download client [{0}], host [{1}] and remote path [{2}]", downloadClientId, host, remotePath.FullPath);

            foreach (var mapping in GetMappingsForScope(mappingsList, downloadClientId, host, m => m.RemotePath))
            {
                _logger.Trace("Checking configured remote path mapping: {0} - {1} - {2}", mapping.DownloadClientId, mapping.Host, mapping.RemotePath);
                if (new OsPath(mapping.RemotePath).Contains(remotePath))
                {
                    var localPath = new OsPath(mapping.LocalPath) + (remotePath - new OsPath(mapping.RemotePath));
                    _logger.Debug("Remapped remote path [{0}] to local path [{1}] for download client [{2}], host [{3}]", remotePath, localPath, downloadClientId, host);

                    return localPath;
                }
            }

            return remotePath;
        }

        public OsPath RemapLocalToRemote(string host, OsPath localPath)
        {
            return RemapLocalToRemote(0, host, localPath);
        }

        public OsPath RemapLocalToRemote(int downloadClientId, string host, OsPath localPath)
        {
            if (localPath.IsEmpty)
            {
                return localPath;
            }

            var mappings = All();

            return RemapLocalToRemote(mappings, downloadClientId, host, localPath);
        }

        private OsPath RemapLocalToRemote(IEnumerable<RemotePathMapping> mappings, int downloadClientId, string host, OsPath localPath)
        {
            var mappingsList = mappings.ToList();
            if (mappingsList.Empty())
            {
                return localPath;
            }

            _logger.Trace("Evaluating remote path local mappings for match to download client [{0}], host [{1}] and local path [{2}]", downloadClientId, host, localPath.FullPath);

            foreach (var mapping in GetMappingsForScope(mappingsList, downloadClientId, host, m => m.LocalPath))
            {
                _logger.Trace("Checking configured remote path mapping {0} - {1} - {2}", mapping.DownloadClientId, mapping.Host, mapping.RemotePath);
                if (new OsPath(mapping.LocalPath).Contains(localPath))
                {
                    var remotePath = new OsPath(mapping.RemotePath) + (localPath - new OsPath(mapping.LocalPath));
                    _logger.Debug("Remapped local path [{0}] to remote path [{1}] for download client [{2}], host [{3}]", localPath, remotePath, downloadClientId, host);

                    return remotePath;
                }
            }

            return localPath;
        }

        private IEnumerable<RemotePathMapping> GetMappingsForScope(List<RemotePathMapping> mappings, int downloadClientId, string host, Func<RemotePathMapping, string> pathSelector)
        {
            var clientMappings = downloadClientId > 0
                ? mappings.Where(m => m.DownloadClientId == downloadClientId)
                : Enumerable.Empty<RemotePathMapping>();

            var hostMappings = mappings.Where(m =>
                m.DownloadClientId == 0 &&
                host.IsNotNullOrWhiteSpace() &&
                host.Equals(m.Host, StringComparison.InvariantCultureIgnoreCase));

            return clientMappings
                .OrderByDescending(m => new OsPath(pathSelector(m)).FullPath.Length)
                .Concat(hostMappings.OrderByDescending(m => new OsPath(pathSelector(m)).FullPath.Length));
        }

        public void Handle(ProviderDeletedEvent<IDownloadClient> message)
        {
            var mappings = AllStored().Where(m => m.DownloadClientId == message.ProviderId).ToList();

            if (mappings.Empty())
            {
                return;
            }

            foreach (var mapping in mappings)
            {
                _remotePathMappingRepository.Delete(mapping.Id);
            }

            _cache.Clear();
        }
    }

    public class RemotePathMappingTestResult
    {
        public int DownloadClientId { get; set; }
        public string Host { get; set; }
        public string RemotePath { get; set; }
        public string LocalPath { get; set; }
        public string MappedPath { get; set; }
        public bool IsMapped { get; set; }
        public bool LocalPathExists { get; set; }
        public bool LocalPathWritable { get; set; }
        public bool MappedPathExists { get; set; }
        public bool MappedPathWritable { get; set; }
    }
}

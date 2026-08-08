using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.DiskSpace
{
    public interface IDiskSpaceService
    {
        List<DiskSpace> GetFreeSpace();
    }

    public class DiskSpaceService : IDiskSpaceService
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IRootFolderService _rootFolderService;
        private readonly Logger _logger;

        private static readonly Regex _regexSpecialDrive = new Regex("^/var/lib/(docker|rancher|kubelet)(/|$)|^/(boot|etc)(/|$)|/docker(/var)?/aufs(/|$)", RegexOptions.Compiled);

        public DiskSpaceService(IDiskProvider diskProvider,
                                IRootFolderService rootFolderService,
                                Logger logger)
        {
            _diskProvider = diskProvider;
            _rootFolderService = rootFolderService;
            _logger = logger;
        }

        public List<DiskSpace> GetFreeSpace()
        {
            var importantMountRoots = GetMountRootPaths(GetRootPaths())
                .Distinct(PathEqualityComparer.Instance)
                .ToList();

            var optionalMountRoots = GetFixedDisksRootPaths()
                .Except(importantMountRoots, PathEqualityComparer.Instance)
                .Distinct(PathEqualityComparer.Instance)
                .ToList();

            return GetDiskSpace(importantMountRoots)
                .Concat(GetDiskSpace(optionalMountRoots, true))
                .ToList();
        }

        private IEnumerable<string> GetRootPaths()
        {
            return _rootFolderService.All()
                .Select(x => x.Path)
                .Where(path => path.IsPathValid(PathValidationType.CurrentOs) && _diskProvider.FolderExists(path))
                .Distinct(PathEqualityComparer.Instance);
        }

        private IEnumerable<string> GetFixedDisksRootPaths()
        {
            return _diskProvider.GetMounts()
                .Where(d => d.DriveType == DriveType.Fixed)
                .Where(d => !_regexSpecialDrive.IsMatch(d.RootDirectory))
                .Select(d => d.RootDirectory);
        }

        private IEnumerable<string> GetMountRootPaths(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                var mountRoot = GetMountRootPath(path);

                yield return mountRoot ?? path.CleanFilePathBasic();
            }
        }

        private string GetMountRootPath(string path)
        {
            try
            {
                return _diskProvider.GetMount(path)?.RootDirectory;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to resolve mount root for: " + path);
                return null;
            }
        }

        private IEnumerable<DiskSpace> GetDiskSpace(IEnumerable<string> paths, bool suppressWarnings = false)
        {
            foreach (var path in paths)
            {
                DiskSpace diskSpace = null;

                try
                {
                    var freeSpace = _diskProvider.GetAvailableSpace(path);
                    var totalSpace = _diskProvider.GetTotalSize(path);

                    if (!freeSpace.HasValue || !totalSpace.HasValue)
                    {
                        continue;
                    }

                    diskSpace = new DiskSpace
                    {
                        Path = path,
                        FreeSpace = freeSpace.Value,
                        TotalSpace = totalSpace.Value
                    };

                    diskSpace.Label = _diskProvider.GetVolumeLabel(path);
                }
                catch (Exception ex)
                {
                    if (!suppressWarnings)
                    {
                        _logger.Warn(ex, "Unable to get free space for: " + path);
                    }
                }

                if (diskSpace != null)
                {
                    yield return diskSpace;
                }
            }
        }
    }
}

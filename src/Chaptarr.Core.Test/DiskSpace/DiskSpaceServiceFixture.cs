using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.DiskSpace;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.DiskSpace
{
    [TestFixture]
    public class DiskSpaceServiceFixture
    {
        private sealed class TestMount : IMount
        {
            public TestMount(string rootDirectory)
            {
                RootDirectory = rootDirectory;
                DriveType = DriveType.Fixed;
                IsReady = true;
                MountOptions = new MountOptions(new Dictionary<string, string>());
            }

            public long AvailableFreeSpace { get; } = 0;
            public string DriveFormat { get; } = string.Empty;
            public DriveType DriveType { get; }
            public bool IsReady { get; }
            public MountOptions MountOptions { get; }
            public string Name { get; } = string.Empty;
            public string RootDirectory { get; }
            public long TotalFreeSpace { get; } = 0;
            public long TotalSize { get; } = 0;
            public string VolumeLabel { get; } = null;
            public string VolumeName { get; } = string.Empty;
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public List<IMount> Mounts { get; set; } = new();
            public Func<string, bool> FolderExistsHandler { get; set; } = _ => true;
            public Func<string, IMount> GetMountHandler { get; set; } = _ => null;
            public Func<string, long?> GetAvailableSpaceHandler { get; set; } = _ => null;
            public Func<string, long?> GetTotalSizeHandler { get; set; } = _ => null;
            public Func<string, string> GetVolumeLabelHandler { get; set; } = _ => null;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IDiskProvider.FolderExists):
                        return FolderExistsHandler((string)args[0]);
                    case nameof(IDiskProvider.GetMounts):
                        return Mounts;
                    case nameof(IDiskProvider.GetMount):
                        return GetMountHandler((string)args[0]);
                    case nameof(IDiskProvider.GetAvailableSpace):
                        return GetAvailableSpaceHandler((string)args[0]);
                    case nameof(IDiskProvider.GetTotalSize):
                        return GetTotalSizeHandler((string)args[0]);
                    case nameof(IDiskProvider.GetVolumeLabel):
                        return GetVolumeLabelHandler((string)args[0]);
                    default:
                        throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
                }
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            public List<RootFolder> RootFolders { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.All))
                {
                    return RootFolders;
                }

                throw new NotImplementedException($"Test proxy does not implement IRootFolderService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_coalesce_entries_by_mount_root_and_normalize_paths()
        {
            var mounts = new List<IMount>
            {
                new TestMount("/config"),
                new TestMount("/backup"),
                new TestMount("/downloads"),
                new TestMount("/audiobooks"),
                new TestMount("/books")
            };

            IMount ResolveMount(string path)
            {
                return mounts
                    .Where(m => m.RootDirectory.PathEquals(path) || m.RootDirectory.IsParentPath(path))
                    .OrderByDescending(m => m.RootDirectory.Length)
                    .FirstOrDefault();
            }

            var spaceByMountRoot = new Dictionary<string, (long free, long total)>
            {
                ["/config"] = (2, 3),
                ["/backup"] = (4, 5),
                ["/downloads"] = (6, 7),
                ["/audiobooks"] = (8, 9),
                ["/books"] = (10, 11)
            };

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProviderProxy = (DiskProviderProxy)(object)diskProvider;
            diskProviderProxy.Mounts = mounts;
            diskProviderProxy.FolderExistsHandler = _ => true;
            diskProviderProxy.GetMountHandler = ResolveMount;
            diskProviderProxy.GetAvailableSpaceHandler = path =>
            {
                var mount = ResolveMount(path);
                return mount == null ? null : spaceByMountRoot[mount.RootDirectory].free;
            };
            diskProviderProxy.GetTotalSizeHandler = path =>
            {
                var mount = ResolveMount(path);
                return mount == null ? null : spaceByMountRoot[mount.RootDirectory].total;
            };
            diskProviderProxy.GetVolumeLabelHandler = _ => null;

            var rootFolderService = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            var rootFolderProxy = (RootFolderServiceProxy)(object)rootFolderService;
            rootFolderProxy.RootFolders = new List<RootFolder>
            {
                new RootFolder { Path = "/audiobooks/" },
                new RootFolder { Path = "/audiobooks/audiobooks/" },
                new RootFolder { Path = "/books/" }
            };

            var sut = new DiskSpaceService(diskProvider, rootFolderService, LogManager.GetCurrentClassLogger());

            var result = sut.GetFreeSpace();
            var paths = result.Select(x => x.Path).ToList();

            Assert.That(paths, Is.Unique);
            Assert.That(paths, Is.EquivalentTo(new[] { "/audiobooks", "/books", "/config", "/backup", "/downloads" }));
        }
    }
}

using System;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Mono.Disk;

namespace Chaptarr.Core.Test.Common
{
    internal static class RealDiskProvider
    {
        public static IDiskProvider Create(Logger logger)
        {
            return OperatingSystem.IsWindows()
                ? new NzbDrone.Windows.Disk.DiskProvider()
                : new NzbDrone.Mono.Disk.DiskProvider(new ProcMountProvider(logger),
                    new SymbolicLinkResolver(logger), new RefLinkCreator(logger), logger);
        }
    }
}

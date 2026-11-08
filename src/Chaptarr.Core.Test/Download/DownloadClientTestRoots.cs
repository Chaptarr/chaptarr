using System.Collections.Generic;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Download
{
    internal static class DownloadClientTestRoots
    {
        public static IRootFolderService Empty => InterfaceProxy.Proxy<IRootFolderService>((method, args) =>
            method.Name == nameof(IRootFolderService.All) ? new List<RootFolder>() : throw new AssertionException(method.Name));
    }
}

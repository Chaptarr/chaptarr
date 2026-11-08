using System;
using System.Collections.Generic;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
namespace Chaptarr.Core.Test.Common
{
    internal static class EmptyDeletionExtras
    {
        public static IExtraFileRepository<T> Repository<T>() where T : ExtraFile, new() => InterfaceProxy.Proxy<IExtraFileRepository<T>>((method, _) =>
            method.ReturnType.IsAssignableFrom(typeof(List<T>)) ? new List<T>() : throw new NotImplementedException(method.Name));
        public static IMetadataFileService Metadata() => InterfaceProxy.Proxy<IMetadataFileService>((method, _) =>
            method.Name == "DeleteBookFiles" ? null : throw new NotImplementedException(method.Name));
        public static IOtherExtraFileService Other() => InterfaceProxy.Proxy<IOtherExtraFileService>((method, _) =>
            method.Name == "DeleteBookFiles" ? null : throw new NotImplementedException(method.Name));
    }
}

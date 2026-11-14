using System;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.Common
{
    public static class MetadataHealthGateStub
    {
        public static IMetadataServerHealthGate Ready() => InterfaceProxy.Proxy<IMetadataServerHealthGate>((method, args) =>
        {
            if (method.Name != nameof(IMetadataServerHealthGate.CanBeginRequest)) throw new NotImplementedException(method.Name);
            args[0] = TimeSpan.Zero;
            return true;
        });
    }
}

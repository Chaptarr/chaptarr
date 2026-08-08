using DryIoc;
using NzbDrone.Common.Http.Proxy;

namespace NzbDrone.Core.Http
{
    public static class CompositionExtensions
    {
        public static IContainer AddIndexerProxyProvider(this IContainer container)
        {
            // Register the standard proxy settings provider for general HTTP requests
            container.Register<IHttpProxySettingsProvider, StandardProxySettingsProvider>(Reuse.Singleton, ifAlreadyRegistered: IfAlreadyRegistered.Replace);

            return container;
        }
    }
}

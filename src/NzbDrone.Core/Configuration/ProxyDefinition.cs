using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Configuration
{
    public class ProxyDefinition : ModelBase
    {
        public string Name { get; set; }
        public ProxyType ProxyType { get; set; }
        public string Hostname { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public bool BypassLocalAddresses { get; set; }
        public string BypassFilter { get; set; }
    }
}

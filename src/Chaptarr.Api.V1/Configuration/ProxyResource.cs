using Chaptarr.Http.REST;
using NzbDrone.Common.Http.Proxy;

namespace Chaptarr.Api.V1.Configuration
{
    public class ProxyResource : RestResource
    {
        public string Name { get; set; }
        public ProxyType Type { get; set; }
        public string Hostname { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }
}

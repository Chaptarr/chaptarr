using System;

namespace NzbDrone.Common.Http.Proxy
{
    public class ProxyConfigurationException : Exception
    {
        public ProxyConfigurationException(string message)
            : base(message)
        {
        }
    }
}

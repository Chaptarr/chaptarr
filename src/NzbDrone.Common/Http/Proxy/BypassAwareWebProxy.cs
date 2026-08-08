using System;
using System.Net;

namespace NzbDrone.Common.Http.Proxy
{
    internal sealed class BypassAwareWebProxy : IWebProxy
    {
        private readonly IWebProxy _inner;
        private readonly bool _bypassLocalAddress;
        private readonly string[] _bypassList;

        public BypassAwareWebProxy(IWebProxy inner, bool bypassLocalAddress, string[] bypassList)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _bypassLocalAddress = bypassLocalAddress;
            _bypassList = bypassList ?? Array.Empty<string>();
        }

        public ICredentials Credentials
        {
            get => _inner.Credentials;
            set => _inner.Credentials = value;
        }

        public Uri GetProxy(Uri destination)
        {
            return _inner.GetProxy(destination);
        }

        public bool IsBypassed(Uri destination)
        {
            return ProxyBypassEvaluator.ShouldBypass(destination, _bypassLocalAddress, _bypassList, _inner);
        }
    }
}

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NzbDrone.Common.Http;

namespace Chaptarr.Http.Authentication
{
    /// <summary>
    /// Rejects OIDC backchannel requests whose destination resolves to a private or reserved address
    /// unless an operator explicitly allows a private issuer.
    /// </summary>
    internal sealed class OidcBackchannelTargetPolicyHandler : DelegatingHandler
    {
        private readonly bool _allowPrivateIssuer;

        public OidcBackchannelTargetPolicyHandler(bool allowPrivateIssuer, HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
            _allowPrivateIssuer = allowPrivateIssuer;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (!_allowPrivateIssuer)
            {
                if (uri == null || !uri.IsAbsoluteUri)
                {
                    throw new HttpRequestException("OIDC provider request destination is not an absolute URI.");
                }

                var host = uri.DnsSafeHost;
                IPAddress[] addresses;
                if (IPAddress.TryParse(host, out var address))
                {
                    addresses = new[] { address };
                }
                else
                {
                    addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                }

                if (addresses.Length == 0 || Array.Exists(addresses, resolved => !NetworkAddressPolicy.IsPublicAddress(resolved)))
                {
                    throw new HttpRequestException("OIDC provider resolved to a private or reserved network address.");
                }
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http.Proxy;

namespace NzbDrone.Common.Http.Dispatchers
{
    public interface ICreateManagedSocketsHttpHandler
    {
        SocketsHttpHandler CreateHandler(HttpProxySettings proxySettings,
            bool allowAutoRedirect,
            bool useCookies,
            ICredentials credentials,
            bool preAuthenticate,
            int maxConnectionsPerServer,
            HttpRequestTargetPolicy targetPolicy = null);
    }

    public class ManagedSocketsHttpHandlerFactory : ICreateManagedSocketsHttpHandler
    {
        private const int ConnectionEstablishTimeoutMs = 2000;
        private static bool _useIPv6 = Socket.OSSupportsIPv6;
        private static bool _hasResolvedIPv6Availability;

        private readonly ICreateManagedWebProxy _createManagedWebProxy;
        private readonly ICertificateValidationService _certificateValidationService;
        private readonly Logger _logger;

        public ManagedSocketsHttpHandlerFactory(ICreateManagedWebProxy createManagedWebProxy,
            ICertificateValidationService certificateValidationService,
            Logger logger)
        {
            _createManagedWebProxy = createManagedWebProxy;
            _certificateValidationService = certificateValidationService;
            _logger = logger;
        }

        public SocketsHttpHandler CreateHandler(HttpProxySettings proxySettings,
            bool allowAutoRedirect,
            bool useCookies,
            ICredentials credentials,
            bool preAuthenticate,
            int maxConnectionsPerServer,
            HttpRequestTargetPolicy targetPolicy = null)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
                UseProxy = proxySettings?.IsDirectConnection != true,
                UseCookies = useCookies,
                AllowAutoRedirect = allowAutoRedirect,
                Credentials = credentials,
                PreAuthenticate = preAuthenticate,
                MaxConnectionsPerServer = maxConnectionsPerServer,
                ConnectCallback = OnConnectAsync,
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = _certificateValidationService.ShouldByPassValidationError
                }
            };

            if (proxySettings != null && !proxySettings.IsDirectConnection)
            {
                handler.Proxy = _createManagedWebProxy.GetWebProxy(proxySettings);
            }

            if (targetPolicy != null)
            {
                ApplyTargetPolicy(handler, targetPolicy);
            }

            return handler;
        }

        internal void ApplyTargetPolicy(SocketsHttpHandler handler, HttpRequestTargetPolicy policy,
            Func<string, CancellationToken, Task<IPAddress[]>> resolve = null)
        {
            var connect = handler.ConnectCallback;
            var proxy = handler.UseProxy ? handler.Proxy ?? System.Net.Http.HttpClient.DefaultProxy : null;
            handler.ConnectCallback = async (context, token) =>
            {
                // .NET builds the CONNECT request object with the proxy URI and
                // the origin authority in Host (sent as the CONNECT target).
                // This socket connects to the proxy; the image URL is checked before dispatch.
                if (proxy != null && context.InitialRequestMessage.Method == HttpMethod.Connect)
                {
                    return await connect(context, token).ConfigureAwait(false);
                }

                var uri = context.InitialRequestMessage.RequestUri;
                policy.ValidateUrl(uri);

                async Task<IPAddress[]> ResolveDestination(string host)
                {
                    context.InitialRequestMessage.Options.TryGetValue(HttpRequestTargetPolicy.DnsResolutionKey, out var dns);
                    dns?.Start(host);
                    var addresses = IPAddress.TryParse(host, out var literal)
                        ? new[] { literal }
                        : await (resolve == null
                            ? Dns.GetHostAddressesAsync(host, token)
                            : resolve(host, token)).WaitAsync(token).ConfigureAwait(false);
                    if (addresses == null || addresses.Length == 0)
                    {
                        throw new SocketException((int)SocketError.HostNotFound);
                    }

                    dns?.Complete();

                    foreach (var address in addresses)
                    {
                        policy.ValidateAddress(uri, address);
                    }

                    return addresses;
                }

                if (proxy != null && !proxy.IsBypassed(uri) && proxy.GetProxy(uri) is Uri proxyUri && proxyUri != uri)
                {
                    if (proxyUri.Scheme.Equals("socks4", StringComparison.OrdinalIgnoreCase))
                    {
                        // SOCKS4 has no remote-name resolution. Preserve its local
                        // address check; HTTP/SOCKS5 rely on the configured proxy.
                        await ResolveDestination(uri.IdnHost).ConfigureAwait(false);
                    }

                    return await connect(context, token).ConfigureAwait(false);
                }

                // Direct requests connect to these exact validated addresses, without
                // a second hostname lookup or an unchecked DNS fallback.
                var addresses = await ResolveDestination(context.DnsEndPoint.Host).ConfigureAwait(false);

                return await ConnectWithFallback((family, attemptToken) => ConnectAddresses(family,
                    addresses.Where(address => address.AddressFamily == family).ToArray(),
                    context.DnsEndPoint.Port, context.DnsEndPoint.Host, attemptToken), token).ConfigureAwait(false);
            };
        }

        private bool HasRoutableIPv4Address()
        {
            try
            {
                var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();

                return networkInterfaces.Any(ni =>
                    ni.OperationalStatus == OperationalStatus.Up &&
                    ni.GetIPProperties().UnicastAddresses.Any(ip =>
                        ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ip.Address)));
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Caught exception while GetAllNetworkInterfaces assuming IPv4 connectivity: {0}", e.Message);
                return true;
            }
        }

        private ValueTask<Stream> OnConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            return ConnectWithFallback((family, token) => AttemptConnection(family, context, token), cancellationToken);
        }

        internal async ValueTask<Stream> ConnectWithFallback(Func<AddressFamily, CancellationToken, ValueTask<Stream>> connect,
            CancellationToken cancellationToken)
        {
            if (_useIPv6)
            {
                try
                {
                    if (!_hasResolvedIPv6Availability)
                    {
                        using var quickFailCts = new CancellationTokenSource(ConnectionEstablishTimeoutMs);
                        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quickFailCts.Token);
                        return await connect(AddressFamily.InterNetworkV6, linkedTokenSource.Token);
                    }

                    return await connect(AddressFamily.InterNetworkV6, cancellationToken);
                }
                catch
                {
                    var routableIPv4 = HasRoutableIPv4Address();
                    _logger.Info("IPv4 is available: {0}, IPv6 will be {1}", routableIPv4, routableIPv4 ? "disabled" : "left enabled");
                    _useIPv6 = !routableIPv4;
                }
                finally
                {
                    _hasResolvedIPv6Availability = true;
                }
            }

            return await connect(AddressFamily.InterNetwork, cancellationToken);
        }

        private static async ValueTask<Stream> AttemptConnection(AddressFamily addressFamily, SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            EndPoint endpoint = context.DnsEndPoint;

            if (addressFamily == AddressFamily.InterNetwork &&
                TryGetPlexDirectIpAddress(context.DnsEndPoint.Host, out var plexDirectIp))
            {
                endpoint = new IPEndPoint(plexDirectIp, context.DnsEndPoint.Port);
            }

            if (endpoint is IPEndPoint ipEndPoint)
            {
                EnsureAllowedTarget(ipEndPoint.Address, context.DnsEndPoint.Host);
                return await ConnectSocket(addressFamily, ipEndPoint, cancellationToken).ConfigureAwait(false);
            }

            if (endpoint is not DnsEndPoint dnsEndPoint)
            {
                return await ConnectSocket(addressFamily, endpoint, cancellationToken).ConfigureAwait(false);
            }

            // Resolve the hostname ourselves so we can validate the resolved IPs (DNS rebinding defense-in-depth).
            IPAddress[] resolvedAddresses;
            try
            {
                resolvedAddresses = await Dns.GetHostAddressesAsync(dnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Fall back to letting the socket resolve if DNS resolution fails here.
                return await ConnectSocket(addressFamily, dnsEndPoint, cancellationToken).ConfigureAwait(false);
            }

            var candidates = resolvedAddresses.Where(a => a.AddressFamily == addressFamily).ToArray();
            if (candidates.Length == 0)
            {
                return await ConnectSocket(addressFamily, dnsEndPoint, cancellationToken).ConfigureAwait(false);
            }

            return await ConnectAddresses(addressFamily, candidates, dnsEndPoint.Port, dnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        }

        private static async ValueTask<Stream> ConnectAddresses(AddressFamily addressFamily, IPAddress[] candidates,
            int port, string host, CancellationToken cancellationToken)
        {
            Exception lastException = null;
            foreach (var address in candidates)
            {
                EnsureAllowedTarget(address, host);

                try
                {
                    return await ConnectSocket(addressFamily, new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            throw lastException ?? new SocketException((int)SocketError.HostUnreachable);
        }

        private static async ValueTask<Stream> ConnectSocket(AddressFamily addressFamily, EndPoint endpoint, CancellationToken cancellationToken)
        {
            var socket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static void EnsureAllowedTarget(IPAddress ip, string host)
        {
            if (CloudMetadataTargetPolicy.IsBlocked(ip))
            {
                throw new WebException($"Request target '{host}' resolved to a disallowed address.", WebExceptionStatus.ProtocolError);
            }
        }

        private static bool TryGetPlexDirectIpAddress(string host, out IPAddress ipAddress)
        {
            ipAddress = null;

            if (host.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (!host.EndsWith(".plex.direct", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var firstDot = host.IndexOf('.');
            if (firstDot <= 0)
            {
                return false;
            }

            var ipCandidate = host.Substring(0, firstDot).Replace('-', '.');

            if (!IPAddress.TryParse(ipCandidate, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            ipAddress = parsed;
            return true;
        }
    }
}

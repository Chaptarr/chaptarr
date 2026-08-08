using System;
using System.Security.Cryptography;
using System.Text;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Common.Http.Proxy
{
    public class HttpProxySettings
    {
        private HttpProxySettings(bool isDirectConnection)
        {
            IsDirectConnection = isDirectConnection;
            Host = string.Empty;
            Username = string.Empty;
            Password = string.Empty;
            BypassFilter = string.Empty;
        }

        public HttpProxySettings(ProxyType type, string host, int port, string bypassFilter, bool bypassLocalAddress, string username = null, string password = null)
        {
            Type = type;
            Host = host.IsNullOrWhiteSpace() ? "127.0.0.1" : host;
            Port = port;
            Username = username ?? string.Empty;
            Password = password ?? string.Empty;
            BypassFilter = bypassFilter ?? string.Empty;
            BypassLocalAddress = bypassLocalAddress;
        }

        public ProxyType Type { get; private set; }
        public string Host { get; private set; }
        public int Port { get; private set; }
        public string Username { get; private set; }
        public string Password { get; private set; }
        public string BypassFilter { get; private set; }
        public bool BypassLocalAddress { get; private set; }
        public bool IsDirectConnection { get; private set; }

        public static HttpProxySettings DirectConnection { get; } = new HttpProxySettings(true);

        public string[] BypassListAsArray
        {
            get => ProxyBypassEvaluator.ParseBypassList(BypassFilter);
        }

        public string Key => IsDirectConnection
            ? "direct"
            : string.Join("_",
                Type,
                Host,
                Port,
                Username,
                GetPasswordFingerprint(Password),
                BypassFilter,
                BypassLocalAddress);

        private static string GetPasswordFingerprint(string password)
        {
            if (password.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes);
        }
    }
}

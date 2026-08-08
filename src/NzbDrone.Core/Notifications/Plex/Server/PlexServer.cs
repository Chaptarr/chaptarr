using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications.Plex.PlexTv;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Plex.Server
{
    public class PlexServer : NotificationBase<PlexServerSettings>, IResolveProviderPendingSecrets
    {
        private readonly IPlexServerService _plexServerService;
        private readonly IPlexTvService _plexTvService;
        private readonly IPendingProviderSecretService _pendingProviderSecretService;
        private readonly Logger _logger;
        private static readonly TimeSpan PlexConnectionProbeTimeout = TimeSpan.FromSeconds(3);

        private class PlexUpdateQueue
        {
            public Dictionary<int, Author> Pending { get; } = new();
            public bool Refreshing { get; set; }
        }

        private class PlexServerConnectionOption
        {
            public string Value { get; set; }
            public string Name { get; set; }
            public string Hint { get; set; }
            public string Host { get; set; }
            public int Port { get; set; }
            public bool UseSsl { get; set; }
            public bool Reachable { get; set; }
            public int Rank { get; set; }
            public int TieBreakRank { get; set; }
        }

        private class PlexServerConnectionCandidate
        {
            public PlexTvResourceResponse Server { get; set; }
            public PlexTvResourceConnection Connection { get; set; }
            public int Index { get; set; }
            public string Host { get; set; }
            public int Port { get; set; }
            public bool UseSsl { get; set; }
        }

        private readonly ICached<PlexUpdateQueue> _pendingAuthorsCache;

        public PlexServer(IPlexServerService plexServerService,
                          IPlexTvService plexTvService,
                          IPendingProviderSecretService pendingProviderSecretService,
                          ICacheManager cacheManager,
                          Logger logger)
        {
            _plexServerService = plexServerService;
            _plexTvService = plexTvService;
            _pendingProviderSecretService = pendingProviderSecretService;
            _logger = logger;

            _pendingAuthorsCache = cacheManager.GetRollingCache<PlexUpdateQueue>(GetType(), "pendingAuthors", TimeSpan.FromDays(1));
        }

        public override string Link => "https://www.plex.tv/";
        public override string Name => "Plex Media Server";

        public override bool HasPendingQueue
        {
            get
            {
                var queue = _pendingAuthorsCache.Find(Settings.Host);
                if (queue == null)
                {
                    return false;
                }

                lock (queue)
                {
                    return !queue.Refreshing && queue.Pending.Any();
                }
            }
        }

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            UpdateIfEnabled(message.Author);
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            UpdateIfEnabled(author);
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            UpdateIfEnabled(message.Author);
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            if (deleteMessage.DeletedFiles)
            {
                UpdateIfEnabled(deleteMessage.Book.Author);
            }
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            var author = deleteMessage?.BookFile?.Author ?? deleteMessage?.Book?.Author;
            if (author != null)
            {
                UpdateIfEnabled(author);
            }
        }

        public override void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            if (deleteMessage.DeletedFiles)
            {
                UpdateIfEnabled(deleteMessage.Author);
            }
        }

        private void UpdateIfEnabled(Author author)
        {
            _plexTvService.Ping(Settings.AuthToken);

            _logger.Debug("Scheduling library update for author {0} {1}", author.Id, author.Name);
            var queue = _pendingAuthorsCache.Get(Settings.Host, () => new PlexUpdateQueue());
            lock (queue)
            {
                queue.Pending[author.Id] = author;
            }
        }

        public override void ProcessQueue()
        {
            var queue = _pendingAuthorsCache.Find(Settings.Host);

            if (queue == null)
            {
                return;
            }

            lock (queue)
            {
                if (queue.Refreshing)
                {
                    return;
                }

                queue.Refreshing = true;
            }

            try
            {
                while (true)
                {
                    List<Author> refreshingAuthors;
                    lock (queue)
                    {
                        if (queue.Pending.Empty())
                        {
                            queue.Refreshing = false;
                            return;
                        }

                        refreshingAuthors = queue.Pending.Values.ToList();
                        queue.Pending.Clear();
                    }

                    _logger.Debug("Performing library update for {0} authors", refreshingAuthors.Count);
                    _plexServerService.UpdateLibrary(refreshingAuthors, Settings);
                }
            }
            catch
            {
                lock (queue)
                {
                    queue.Refreshing = false;
                }

                throw;
            }
        }

        public override ValidationResult Test()
        {
            _plexTvService.Ping(Settings.AuthToken);

            var failures = new List<ValidationFailure>();

            failures.AddIfNotNull(_plexServerService.Test(Settings));

            return new ValidationResult(failures);
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "startOAuth")
            {
                Settings.Validate().Filter("ConsumerKey", "ConsumerSecret").ThrowOnError();

                if (!query.TryGetValue("callbackUrl", out var callbackUrl) || callbackUrl.IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("QueryParam callbackUrl invalid.");
                }

                var pin = _plexTvService.CreatePin();

                return _plexTvService.GetSignInUrl(callbackUrl, pin.Id, pin.Code);
            }
            else if (action == "getOAuthToken")
            {
                Settings.Validate().Filter("ConsumerKey", "ConsumerSecret").ThrowOnError();

                if (query["pinId"].IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("QueryParam pinId invalid.");
                }

                var authToken = _plexTvService.GetAuthToken(Convert.ToInt32(query["pinId"]));

                if (authToken.IsNullOrWhiteSpace())
                {
                    return new
                    {
                        authorized = false
                    };
                }

                var result = new Dictionary<string, object>
                {
                    { "authToken", _pendingProviderSecretService.Create(authToken) },
                    { "authorized", true }
                };

                return result;
            }
            else if (action == "getPlexServers")
            {
                try
                {
                    if (Settings.AuthToken.IsNullOrWhiteSpace())
                    {
                        return new { options = new List<object>() };
                    }

                    var resources = _plexTvService.GetResources(Settings.AuthToken);
                    var options = GetPlexConnectionOptions(resources)
                        .OrderBy(o => o.Rank)
                        .ThenBy(o => o.Name, StringComparer.InvariantCultureIgnoreCase)
                        .ToList();

                    var selected = GetSelectedPlexConnection(options);

                    return new
                    {
                        options = options.Select(o => new
                        {
                            Value = o.Value,
                            Name = o.Name,
                            Hint = o.Hint,
                            IsDisabled = false,
                            IsHidden = false,
                            AdditionalProperties = new Dictionary<string, object>
                            {
                                { "host", o.Host },
                                { "port", o.Port },
                                { "useSsl", o.UseSsl }
                            }
                        }).ToList(),
                        selectOption = selected
                    };
                }
                catch (Exception e)
                {
                    _logger.Debug(e, "Unable to retrieve Plex server connections");
                    return new { options = new List<object>() };
                }
            }
            else if (action == "getPlexLibraries")
            {
                try
                {
                    if (Settings.AuthToken.IsNullOrWhiteSpace() || Settings.Host.IsNullOrWhiteSpace() || Settings.Port <= 0)
                    {
                        return new { options = new List<object>() };
                    }

                    // Ensure plex.direct suffix is discovered when connecting to an IP over HTTPS.
                    // This keeps subsequent section discovery calls from failing with TLS name mismatch.
                    _plexServerService.Test(Settings);

                    var sections = _plexServerService.GetSections(Settings)
                        .OrderBy(s => s?.Title ?? string.Empty, StringComparer.InvariantCultureIgnoreCase)
                        .ToList();

                    var options = new List<object>
                    {
                        new { Value = "", Name = "Auto (match by path)" }
                    };

                    options.AddRange(sections.Select(s => new
                    {
                        Value = s.Id.ToString(),
                        Name = (s.Title.IsNotNullOrWhiteSpace() ? s.Title : $"Library {s.Id}")
                    }));

                    var selected = Settings.LibrarySectionId;
                    if (selected.IsNullOrWhiteSpace() && sections.Count == 1)
                    {
                        selected = sections[0].Id.ToString();
                    }

                    return new
                    {
                        options,
                        selectOption = selected
                    };
                }
                catch (Exception e)
                {
                    _logger.Debug(e, "Unable to retrieve Plex libraries");
                    return new { options = new List<object>() };
                }
            }

            return new { };
        }

        private string GetSelectedPlexConnection(List<PlexServerConnectionOption> options)
        {
            if (options.Empty())
            {
                return null;
            }

            var matchingCurrentSettings = options.FirstOrDefault(o =>
                string.Equals(Settings.Host, o.Host, StringComparison.InvariantCultureIgnoreCase) &&
                Settings.Port == o.Port &&
                Settings.UseSsl == o.UseSsl);

            if (matchingCurrentSettings != null)
            {
                return matchingCurrentSettings.Value;
            }

            if (Settings.Host.IsNotNullOrWhiteSpace())
            {
                return null;
            }

            return options.FirstOrDefault(o => o.Reachable)?.Value;
        }

        private List<PlexServerConnectionOption> GetPlexConnectionOptions(List<PlexTvResourceResponse> resources)
        {
            var options = new List<PlexServerConnectionOption>();
            var servers = (resources ?? new List<PlexTvResourceResponse>())
                .Where(r => r?.Provides != null && r.Provides.IndexOf("server", StringComparison.OrdinalIgnoreCase) >= 0)
                .GroupBy(GetPlexServerKey)
                .OrderBy(g => GetPlexServerName(g.FirstOrDefault()), StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            foreach (var serverGroup in servers)
            {
                var server = serverGroup.FirstOrDefault();
                var candidates = new List<PlexServerConnectionCandidate>();
                var serverOptions = new ConcurrentBag<PlexServerConnectionOption>();
                var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var connections = serverGroup.SelectMany(s => s.Connections ?? new List<PlexTvResourceConnection>());
                var index = 0;

                foreach (var connection in connections)
                {
                    index++;

                    if (!TryGetConnectionEndpoint(connection, out var host, out var port, out var useSsl))
                    {
                        continue;
                    }

                    if (!seenEndpoints.Add(GetConnectionEndpointKey(host, port, useSsl)))
                    {
                        continue;
                    }

                    candidates.Add(new PlexServerConnectionCandidate
                    {
                        Server = server,
                        Connection = connection,
                        Index = index,
                        Host = host,
                        Port = port,
                        UseSsl = useSsl
                    });
                }

                Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = 8 }, candidate =>
                {
                    var probeSettings = new PlexServerSettings
                    {
                        Host = candidate.Host,
                        Port = candidate.Port,
                        UseSsl = candidate.UseSsl,
                        UrlBase = Settings.UrlBase
                    };

                    var reachable = _plexServerService.CanConnect(probeSettings, PlexConnectionProbeTimeout, out var message);
                    var serverName = GetPlexServerName(candidate.Server);

                    serverOptions.Add(new PlexServerConnectionOption
                    {
                        Value = $"{candidate.Server?.ClientIdentifier}|{candidate.Index}|{candidate.Host}|{candidate.Port}|{candidate.UseSsl}",
                        Name = serverName,
                        Hint = GetConnectionHint(candidate.Connection, candidate.Host, candidate.Port, candidate.UseSsl, reachable, message),
                        Host = candidate.Host,
                        Port = candidate.Port,
                        UseSsl = candidate.UseSsl,
                        Reachable = reachable,
                        Rank = GetConnectionRank(candidate.Connection, candidate.UseSsl, candidate.Host, reachable),
                        TieBreakRank = GetEndpointTieBreakRank(candidate.Host)
                    });
                });

                var bestConnection = serverOptions
                    .OrderBy(o => o.Rank)
                    .ThenBy(o => o.TieBreakRank)
                    .ThenBy(o => o.Host, StringComparer.InvariantCultureIgnoreCase)
                    .FirstOrDefault();

                if (bestConnection != null)
                {
                    options.Add(bestConnection);
                }
            }

            return options;
        }

        private static string GetPlexServerKey(PlexTvResourceResponse server)
        {
            if (server?.ClientIdentifier.IsNotNullOrWhiteSpace() == true)
            {
                return server.ClientIdentifier;
            }

            return GetPlexServerName(server);
        }

        private static string GetPlexServerName(PlexTvResourceResponse server)
        {
            return server?.Name.IsNotNullOrWhiteSpace() == true ? server.Name : "Plex Server";
        }

        private static string GetConnectionEndpointKey(string host, int port, bool useSsl)
        {
            return $"{host?.Trim().ToLowerInvariant()}|{port}|{useSsl}";
        }

        private static bool TryGetConnectionEndpoint(PlexTvResourceConnection connection, out string host, out int port, out bool useSsl)
        {
            host = null;
            port = 0;
            useSsl = false;

            if (connection?.Uri.IsNotNullOrWhiteSpace() == true &&
                Uri.TryCreate(connection.Uri, UriKind.Absolute, out var uri) &&
                uri.Host.IsNotNullOrWhiteSpace())
            {
                host = uri.Host;
                useSsl = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
                port = uri.Port > 0 ? uri.Port : GetPlexPort(connection.Port, useSsl);
                return true;
            }

            if (connection?.Address.IsNullOrWhiteSpace() != false)
            {
                return false;
            }

            host = connection.Address;
            useSsl = string.Equals(connection.Protocol, "https", StringComparison.OrdinalIgnoreCase);
            port = GetPlexPort(connection.Port, useSsl);
            return true;
        }

        private static int GetPlexPort(int port, bool useSsl)
        {
            if (port > 0)
            {
                return port;
            }

            return useSsl ? 443 : 32400;
        }

        private static int GetConnectionRank(PlexTvResourceConnection connection, bool useSsl, string host, bool reachable)
        {
            var rank = reachable ? 0 : 1000;

            if (!connection.Local)
            {
                rank += 100;
            }

            if (connection.Relay)
            {
                rank += 50;
            }

            if (!useSsl)
            {
                rank += 25;
            }

            if (!host.Contains(".plex.direct", StringComparison.OrdinalIgnoreCase))
            {
                rank += 5;
            }

            return rank;
        }

        private static int GetEndpointTieBreakRank(string host)
        {
            if (!TryParsePlexHostIp(host, out var ipAddress))
            {
                return 10;
            }

            if (IsDockerBridgeGateway(ipAddress))
            {
                return 100;
            }

            if (IsPrivateIpAddress(ipAddress))
            {
                return 0;
            }

            return 20;
        }

        private static bool TryParsePlexHostIp(string host, out IPAddress ipAddress)
        {
            ipAddress = null;

            if (host.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (IPAddress.TryParse(host, out ipAddress))
            {
                return true;
            }

            var firstLabel = host.Split('.')[0];
            var encodedIp = firstLabel.Replace('-', '.');

            return IPAddress.TryParse(encodedIp, out ipAddress);
        }

        private static bool IsDockerBridgeGateway(IPAddress ipAddress)
        {
            var bytes = ipAddress.GetAddressBytes();

            return bytes.Length == 4 &&
                   bytes[0] == 172 &&
                   bytes[1] >= 16 &&
                   bytes[1] <= 31 &&
                   bytes[2] == 0 &&
                   bytes[3] == 1;
        }

        private static bool IsPrivateIpAddress(IPAddress ipAddress)
        {
            var bytes = ipAddress.GetAddressBytes();

            return bytes.Length == 4 &&
                   (bytes[0] == 10 ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168));
        }

        private static string GetConnectionHint(PlexTvResourceConnection connection, string host, int port, bool useSsl, bool reachable, string message)
        {
            var hints = new List<string>
            {
                $"{host}:{port}",
                reachable ? "Reachable" : "Unavailable",
                connection.Local ? "Local" : "Remote"
            };

            if (useSsl)
            {
                hints.Add("Secure");
            }

            if (connection.Relay)
            {
                hints.Add("Relay");
            }

            if (!reachable && message.IsNotNullOrWhiteSpace())
            {
                hints.Add(message);
            }

            return string.Join(", ", hints);
        }

        public void ResolveProviderPendingSecrets(bool consume)
        {
            Settings.AuthToken = _pendingProviderSecretService.Resolve(Settings.AuthToken, consume);
        }
    }
}

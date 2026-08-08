using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Plex.Server
{
    public interface IPlexServerService
    {
        void UpdateLibrary(Author author, PlexServerSettings settings);
        void UpdateLibrary(IEnumerable<Author> authors, PlexServerSettings settings);
        bool CanConnect(PlexServerSettings settings, TimeSpan timeout, out string message);
        ValidationFailure Test(PlexServerSettings settings);
        List<PlexSection> GetSections(PlexServerSettings settings);
    }

    public class PlexServerService : IPlexServerService
    {
        private readonly ICached<Version> _versionCache;
        private readonly IPlexServerProxy _plexServerProxy;
        private readonly IRootFolderService _rootFolderService;
        private readonly Logger _logger;

        public PlexServerService(ICacheManager cacheManager, IPlexServerProxy plexServerProxy, IRootFolderService rootFolderService, Logger logger)
        {
            _versionCache = cacheManager.GetCache<Version>(GetType(), "versionCache");
            _plexServerProxy = plexServerProxy;
            _rootFolderService = rootFolderService;
            _logger = logger;
        }

        public void UpdateLibrary(Author author, PlexServerSettings settings)
        {
            UpdateLibrary(new[] { author }, settings);
        }

        public void UpdateLibrary(IEnumerable<Author> authors, PlexServerSettings settings)
        {
            try
            {
                _logger.Debug("Sending Update Request to Plex Server");
                var watch = Stopwatch.StartNew();

                var version = _versionCache.Get(settings.Host, () => GetVersion(settings), TimeSpan.FromHours(2));
                ValidateVersion(version);

                if (settings.LibrarySectionId.IsNotNullOrWhiteSpace() &&
                    int.TryParse(settings.LibrarySectionId, out var librarySectionId) &&
                    librarySectionId > 0)
                {
                    _logger.Debug("Refreshing Plex library section {0}", librarySectionId);
                    _plexServerProxy.Update(librarySectionId, null, settings);

                    _logger.Debug("Finished sending Update Request to Plex Server (took {0} ms)", watch.ElapsedMilliseconds);
                    return;
                }

                var sections = GetSections(settings);

                foreach (var author in authors)
                {
                    UpdateSections(author, sections, settings);
                }

                _logger.Debug("Finished sending Update Request to Plex Server (took {0} ms)", watch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to update Plex host: " + settings.Host);
                throw;
            }
        }

        public bool CanConnect(PlexServerSettings settings, TimeSpan timeout, out string message)
        {
            return _plexServerProxy.CanConnect(settings, timeout, out message);
        }

        public List<PlexSection> GetSections(PlexServerSettings settings)
        {
            _logger.Debug("Getting sections from Plex host: {0}", settings.Host);

            return _plexServerProxy.GetTvSections(settings).ToList();
        }

        private void ValidateVersion(Version version)
        {
            if (version >= new Version(1, 3, 0) && version < new Version(1, 3, 1))
            {
                throw new PlexVersionException("Found version {0}, upgrade to PMS 1.3.1 to fix library updating and then restart Chaptarr", version);
            }
        }

        private Version GetVersion(PlexServerSettings settings)
        {
            _logger.Debug("Getting version from Plex host: {0}", settings.Host);

            var rawVersion = _plexServerProxy.Version(settings);
            var version = new Version(Regex.Match(rawVersion, @"^(\d+[.-]){4}").Value.Trim('.', '-'));

            return version;
        }

        private void UpdateSections(Author author, List<PlexSection> sections, PlexServerSettings settings)
        {
            var rootFolderPath = _rootFolderService.GetBestRootFolderPath(author.Path);
            var authorRelativePath = rootFolderPath.GetRelativePath(author.Path);

            // Try to update a matching section location before falling back to updating all section locations.
            foreach (var section in sections)
            {
                foreach (var location in section.Locations)
                {
                    var rootFolder = new OsPath(rootFolderPath);
                    var mappedPath = rootFolder;

                    if (settings.MapTo.IsNotNullOrWhiteSpace())
                    {
                        mappedPath = new OsPath(settings.MapTo) + (rootFolder - new OsPath(settings.MapFrom));

                        _logger.Trace("Mapping Path from {0} to {1} for partial scan", rootFolder, mappedPath);
                    }

                    if (location.Path.PathEquals(mappedPath.FullPath))
                    {
                        _logger.Debug("Updating matching section location, {0}", location.Path);
                        UpdateSectionPath(authorRelativePath, section, location, settings);

                        return;
                    }
                }
            }

            _logger.Debug("Unable to find matching section location, updating all Music sections");

            foreach (var section in sections)
            {
                foreach (var location in section.Locations)
                {
                    UpdateSectionPath(authorRelativePath, section, location, settings);
                }
            }
        }

        private void UpdateSectionPath(string authorRelativePath, PlexSection section, PlexSectionLocation location, PlexServerSettings settings)
        {
            var separator = location.Path.Contains('\\') ? "\\" : "/";
            var locationRelativePath = authorRelativePath.Replace("\\", separator).Replace("/", separator);

            // Plex location paths trim trailing extraneous separator characters, so it doesn't need to be trimmed
            var pathToUpdate = $"{location.Path}{separator}{locationRelativePath}";

            _logger.Debug("Updating section location, {0}", location.Path);
            _plexServerProxy.Update(section.Id, pathToUpdate, settings);
        }

        public ValidationFailure Test(PlexServerSettings settings)
        {
            try
            {
                _versionCache.Remove(settings.Host);
                var sections = GetSections(settings);

                if (sections.Empty())
                {
                    return new ValidationFailure("Host", "At least one Music library is required");
                }

                if (settings.LibrarySectionId.IsNotNullOrWhiteSpace() &&
                    int.TryParse(settings.LibrarySectionId, out var librarySectionId) &&
                    librarySectionId > 0 &&
                    sections.All(s => s.Id != librarySectionId))
                {
                    return new ValidationFailure(nameof(PlexServerSettings.LibrarySectionId), "Selected Plex library could not be found");
                }
            }
            catch (PlexAuthenticationException ex)
            {
                _logger.Error(ex, "Unable to connect to Plex Media Server");
                return new ValidationFailure("AuthToken", "Invalid authentication token");
            }
            catch (Exception ex)
            {
                // Attempt a secure, user-friendly auto-fix:
                // - If Plex requires HTTPS, enable it.
                // - If user configured an IP, derive the plex.direct suffix from the Plex TLS certificate so HTTPS validates.
                var originalUseSsl = settings.UseSsl;
                var originalPlexDirectSuffix = settings.PlexDirectSuffix;

                if (!settings.UseSsl)
                {
                    settings.UseSsl = true;
                }

                if (settings.UseSsl)
                {
                    TryPopulatePlexDirectSuffix(settings);
                }

                if (settings.UseSsl != originalUseSsl || settings.PlexDirectSuffix != originalPlexDirectSuffix)
                {
                    try
                    {
                        _versionCache.Remove(settings.Host);
                        var sections = GetSections(settings);

                        if (sections.Empty())
                        {
                            return new ValidationFailure("Host", "At least one Music library is required");
                        }

                        if (settings.LibrarySectionId.IsNotNullOrWhiteSpace() &&
                            int.TryParse(settings.LibrarySectionId, out var librarySectionId) &&
                            librarySectionId > 0 &&
                            sections.All(s => s.Id != librarySectionId))
                        {
                            return new ValidationFailure(nameof(PlexServerSettings.LibrarySectionId), "Selected Plex library could not be found");
                        }

                        return null;
                    }
                    catch (PlexAuthenticationException retryAuth)
                    {
                        _logger.Error(retryAuth, "Unable to connect to Plex Media Server");
                        return new ValidationFailure("AuthToken", "Invalid authentication token");
                    }
                    catch (PlexException retryPlex)
                    {
                        return new NzbDroneValidationFailure("Host", retryPlex.Message);
                    }
                    catch (Exception retryEx)
                    {
                        ex = retryEx;
                    }
                }

                _logger.Error(ex, "Unable to connect to Plex Media Server");

                if (ex is PlexException plexException)
                {
                    return new NzbDroneValidationFailure("Host", plexException.Message)
                    {
                        DetailedDescription = plexException.InnerException?.Message ?? plexException.Message
                    };
                }

                return new NzbDroneValidationFailure("Host", "Unable to connect to Plex Media Server")
                {
                    DetailedDescription = ex.Message
                };
            }

            return null;
        }

        private static readonly Regex PlexDirectSuffixRegex = new Regex(@"(?i)\*\.([0-9a-f]{32})\.plex\.direct", RegexOptions.Compiled);

        private static bool IsSafePlexDirectProbeTarget(IPAddress ipAddress)
        {
            if (ipAddress == null)
            {
                return false;
            }

            // Map back to IPv4 if mapped to IPv6, for example "::ffff:1.2.3.4" to "1.2.3.4".
            if (ipAddress.IsIPv4MappedToIPv6)
            {
                ipAddress = ipAddress.MapToIPv4();
            }

            if (IPAddress.IsLoopback(ipAddress))
            {
                return true;
            }

            if (ipAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            var bytes = ipAddress.GetAddressBytes();

            // Only allow RFC1918 ranges here; avoid link-local (169.254.0.0/16) to reduce SSRF/metadata-probe risk.
            // - 10.0.0.0/8
            // - 172.16.0.0/12
            // - 192.168.0.0/16
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        private bool TryPopulatePlexDirectSuffix(PlexServerSettings settings)
        {
            if (!settings.UseSsl)
            {
                return false;
            }

            if (settings.PlexDirectSuffix.IsNotNullOrWhiteSpace())
            {
                return true;
            }

            if (!IPAddress.TryParse(settings.Host, out var ipAddress) || ipAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            // Only attempt on RFC1918/loopback to reduce risk of accepting an untrusted cert on the public internet,
            // and to avoid probing link-local/cloud-metadata endpoints.
            if (!IsSafePlexDirectProbeTarget(ipAddress))
            {
                return false;
            }

            try
            {
                var timeout = TimeSpan.FromSeconds(3);
                using var tcpClient = new TcpClient();

                var connectTask = tcpClient.ConnectAsync(ipAddress, settings.Port);
                if (!connectTask.Wait(timeout))
                {
                    return false;
                }

                using var networkStream = tcpClient.GetStream();
                try
                {
                    networkStream.ReadTimeout = (int)timeout.TotalMilliseconds;
                    networkStream.WriteTimeout = (int)timeout.TotalMilliseconds;
                }
                catch
                {
                    // best-effort only
                }

                using var sslStream = new SslStream(networkStream, false, (_, _, _, _) => true);

                // The hostname doesn't validate here (we're probing), but Plex will still present the correct certificate.
                using var handshakeCancellation = new CancellationTokenSource(timeout);
                sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = settings.Host,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, handshakeCancellation.Token).GetAwaiter().GetResult();

                if (sslStream.RemoteCertificate == null)
                {
                    return false;
                }

                var cert2 = new X509Certificate2(sslStream.RemoteCertificate);
                var suffix = ExtractPlexDirectSuffix(cert2);

                if (suffix.IsNullOrWhiteSpace())
                {
                    return false;
                }

                settings.PlexDirectSuffix = suffix;
                _logger.Debug("Discovered Plex plex.direct suffix for {0}:{1}", settings.Host, settings.Port);

                return true;
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Failed to discover plex.direct suffix for {0}:{1}", settings.Host, settings.Port);
                return false;
            }
        }

        private static string ExtractPlexDirectSuffix(X509Certificate2 certificate)
        {
            // Typical: CN = *.00000000000000000000000000000000.plex.direct
            var subjectMatch = PlexDirectSuffixRegex.Match(certificate.Subject ?? string.Empty);
            if (subjectMatch.Success)
            {
                return subjectMatch.Groups[1].Value.ToLowerInvariant();
            }

            // Fallback to SAN
            foreach (var ext in certificate.Extensions)
            {
                if (ext?.Oid?.Value != "2.5.29.17")
                {
                    continue;
                }

                var formatted = ext.Format(false) ?? string.Empty;
                var sanMatch = PlexDirectSuffixRegex.Match(formatted);
                if (sanMatch.Success)
                {
                    return sanMatch.Groups[1].Value.ToLowerInvariant();
                }
            }

            return null;
        }
    }
}

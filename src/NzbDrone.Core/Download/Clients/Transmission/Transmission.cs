using System;
using System.Linq;
using System.Text.RegularExpressions;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download.Clients.Transmission
{
    public class Transmission : TransmissionBase
    {
        public Transmission(ITransmissionProxy proxy,
                            ITorrentFileInfoReader torrentFileInfoReader,
                            IHttpClient httpClient,
                            IConfigService configService,
                            IDiskProvider diskProvider,
                            IRemotePathMappingService remotePathMappingService,
                            IBlocklistService blocklistService,
                            Logger logger)
            : base(proxy, torrentFileInfoReader, httpClient, configService, diskProvider, remotePathMappingService, blocklistService, logger)
        {
        }

        protected override ValidationFailure ValidateVersion()
        {
            var versionString = _proxy.GetClientVersion(Settings);

            _logger.Debug("Transmission version information: {0}", versionString);

            var versionResult = Regex.Match(versionString, @"(?<!\(|(\d|\.)+)(\d|\.)+(?!\)|(\d|\.)+)").Value;
            var version = Version.Parse(versionResult);

            if (version < new Version(2, 40))
            {
                return new ValidationFailure(string.Empty, "Transmission version not supported, should be 2.40 or higher.");
            }

            return null;
        }

        public override DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt)
        {
            var result = item.Clone();

            if (item.DownloadId.IsNullOrWhiteSpace())
            {
                _logger.Debug("No torrent hash found for torrent {0} in Transmission; using existing output path.", item.Title);
                return result;
            }

            TransmissionTorrent torrent;
            try
            {
                torrent = _proxy.GetTorrentDetails(item.DownloadId.ToLowerInvariant(), Settings);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to fetch Transmission file list for torrent {0}; using existing output path.", item.Title);
                return result;
            }

            if (torrent?.Files == null || torrent.Files.Count == 0)
            {
                _logger.Debug("No files found for torrent {0} in Transmission", item.Title);
                return result;
            }

            if (torrent.DownloadDir.IsNullOrWhiteSpace())
            {
                _logger.Debug("No download directory found for torrent {0} in Transmission; using existing output path.", item.Title);
                return result;
            }

            var downloadDir = new OsPath(torrent.DownloadDir);
            var filePaths = torrent.Files
                .Select((file, index) => new { File = file, Index = index })
                .Where(x => IsWanted(torrent, x.Index))
                .Select(x => x.File.Name)
                .Where(p => p.IsNotNullOrWhiteSpace())
                .Select(p => RemapRemoteToLocal(TorrentClientPathHelper.CombineClientPath(downloadDir, p)).FullPath)
                .Where(p => p.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            if (filePaths.Count == 0)
            {
                _logger.Debug("No usable file paths found for torrent {0} in Transmission", item.Title);
                return result;
            }

            result.FilePaths = filePaths;
            result.FileListConfidence = DownloadClientFileListConfidence.Authoritative;

            return result;
        }

        private static bool IsWanted(TransmissionTorrent torrent, int index)
        {
            return torrent.FileStats == null ||
                   index >= torrent.FileStats.Count ||
                   torrent.FileStats[index].Wanted;
        }

        public override string Name => "Transmission";
    }
}

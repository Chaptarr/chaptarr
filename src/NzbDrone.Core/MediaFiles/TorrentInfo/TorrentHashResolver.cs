using System;
using System.Linq;
using MonoTorrent;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.MediaFiles.TorrentInfo
{
    internal sealed class UnsupportedTorrentHashException : NotSupportedException
    {
        public UnsupportedTorrentHashException(string message)
            : base(message)
        {
        }
    }

    internal static class TorrentHashResolver
    {
        public static string GetSupportedHashOrNull(InfoHashes infoHashes)
        {
            return infoHashes?.V1?.ToHex()?.ToUpperInvariant();
        }

        public static string GetSupportedHashOrThrow(InfoHashes infoHashes, string sourceDescription)
        {
            var v1Hash = GetSupportedHashOrNull(infoHashes);
            if (!string.IsNullOrWhiteSpace(v1Hash))
            {
                return v1Hash;
            }

            if (infoHashes?.V2 != null)
            {
                throw new UnsupportedTorrentHashException($"{sourceDescription} is BitTorrent v2-only. Chaptarr currently requires a v1 info hash to track torrent downloads.");
            }

            return null;
        }

        public static string NormalizeKnownHash(string infoHash)
        {
            if (infoHash.IsNullOrWhiteSpace())
            {
                return null;
            }

            var normalized = infoHash.Trim().ToUpperInvariant();

            if (normalized.Length == 64 && normalized.All(Uri.IsHexDigit))
            {
                return null;
            }

            return normalized;
        }
    }
}

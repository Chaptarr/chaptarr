using System;
using System.IO;
using System.Security.Cryptography;

namespace NzbDrone.Core.MediaFiles
{
    public static class ConversionArtifactFingerprint
    {
        public static string ComputeSha256(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);

            return Convert.ToHexString(SHA256.HashData(stream));
        }

        public static bool Matches(string path, string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256) || !File.Exists(path))
            {
                return false;
            }

            return string.Equals(ComputeSha256(path), expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
    }
}

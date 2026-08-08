using System;
using System.IO;

namespace Chaptarr.Http.Frontend.Mappers
{
    internal static class LogFileNameValidator
    {
        private static readonly string[] ReservedDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public static bool IsSafeTxtLogFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Windows ADS / drive-relative paths.
            if (fileName.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }

            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            // Only allow the same conservative character set as the API route constraint:
            // `[-.a-zA-Z0-9]+\.txt`
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                return false;
            }

            // Windows reserved device names are checked against the portion before the first dot.
            var deviceCandidate = baseName.Split('.', 2, StringSplitOptions.None)[0].TrimEnd(' ', '.');
            foreach (var reserved in ReservedDeviceNames)
            {
                if (reserved.Equals(deviceCandidate, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            foreach (var ch in baseName)
            {
                var isAsciiLetter = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
                var isDigit = ch >= '0' && ch <= '9';
                if (!isAsciiLetter && !isDigit && ch != '-' && ch != '.')
                {
                    return false;
                }
            }

            return true;
        }
    }
}

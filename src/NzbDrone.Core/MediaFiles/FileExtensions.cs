using System;
using System.Collections.Generic;

namespace NzbDrone.Core.MediaFiles
{
    public static class FileExtensions
    {
        public static IReadOnlySet<string> ArchiveExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".7z", ".bz2", ".gz", ".r00", ".rar", ".tar.bz2", ".tar.gz",
            ".tar", ".tb2", ".tbz2", ".tgz", ".zip"
        };

        public static IReadOnlySet<string> DangerousExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".arj", ".lnk", ".lzh", ".ps1", ".scr", ".vbs", ".zipx"
        };

        public static IReadOnlySet<string> ExecutableExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".bat", ".cmd", ".exe", ".sh"
        };
    }
}

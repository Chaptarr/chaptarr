using System.IO;
using NzbDrone.Common.EnvironmentInfo;

namespace Chaptarr.Core.Test
{
    public static class TestPathExtensions
    {
        // Ported from the upstream *arr test convention (NzbDrone.Test.Common.StringExtensions):
        // author test paths Windows-style and adapt them per-OS, so code that resolves paths
        // via Path.GetFullPath sees a properly rooted path on both platforms.
        public static string AsOsAgnostic(this string path)
        {
            if (OsInfo.IsNotWindows)
            {
                if (path.Length > 2 && path[1] == ':')
                {
                    path = path.Replace(":", "");
                    path = Path.DirectorySeparatorChar + path;
                }

                path = path.Replace("\\", Path.DirectorySeparatorChar.ToString());
            }

            return path;
        }
    }
}

using System;

namespace NzbDrone.Core.MetadataSource.Goodreads
{
    public static class GoodreadsAndroidUserAgent
    {
        private static readonly string[] UserAgents = new[]
        {
            "Dalvik/2.1.0 (Linux; U; Android 14; Pixel 8 Pro Build/UD1A.230803.041)",
            "Dalvik/2.1.0 (Linux; U; Android 14; SM-S928U Build/UP1A.231005.007)",
            "Dalvik/2.1.0 (Linux; U; Android 14; SM-S918U Build/UP1A.231005.007)",
            "Dalvik/2.1.0 (Linux; U; Android 13; SM-S908U Build/TP1A.220624.014)",
            "Dalvik/2.1.0 (Linux; U; Android 13; CPH2451 Build/TP1A.220905.001)"
        };

        public static string GetRandom()
        {
            return UserAgents[Random.Shared.Next(UserAgents.Length)];
        }
    }
}


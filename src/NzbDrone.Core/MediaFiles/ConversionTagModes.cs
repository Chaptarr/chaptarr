using System;

namespace NzbDrone.Core.MediaFiles
{
    public static class ConversionTagModes
    {
        public const string Preserve = "preserve";
        public const string Clean = "clean";

        public static string Normalize(string mode)
        {
            if (string.Equals(mode, Clean, StringComparison.OrdinalIgnoreCase))
            {
                return Clean;
            }

            return Preserve;
        }
    }
}

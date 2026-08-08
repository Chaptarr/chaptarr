using NzbDrone.Common.Extensions;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.ImportLists
{
    public static class ImportListProviderIdHelper
    {
        public static string Normalize(string value, string defaultPrefix)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return null;
            }

            value = value.Trim();

            if (value.Contains(":"))
            {
                try
                {
                    return ProviderIdHelper.Normalize(value, defaultPrefix: null);
                }
                catch
                {
                    return value;
                }
            }

            if (defaultPrefix.IsNullOrWhiteSpace())
            {
                return value;
            }

            return ProviderIdHelper.WithPrefix(defaultPrefix, value);
        }
    }
}

using System;
using System.Globalization;

namespace NzbDrone.Common.Options;

public static class ServerConfigurationDefaults
{
    public const int DefaultPort = 8789;

    public static int Port
    {
        get
        {
            var configuredDefault = Environment.GetEnvironmentVariable("CHAPTARR_DEFAULT_PORT");
            if (string.IsNullOrWhiteSpace(configuredDefault))
            {
                return DefaultPort;
            }

            if (int.TryParse(configuredDefault, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535)
            {
                return port;
            }

            throw new InvalidOperationException("CHAPTARR_DEFAULT_PORT must be an integer from 1 to 65535.");
        }
    }
}

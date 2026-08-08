using System.Collections.Generic;
using NLog;

namespace NzbDrone.Common.Instrumentation
{
    public static class LoggerExtensions
    {
        /// <summary>
        /// Logs a debug message with automatic sanitization of sensitive data
        /// </summary>
        public static void DebugSanitized(this Logger logger, string message, params object[] args)
        {
            logger.Debug(SensitiveDataSanitizer.SanitizeLogMessage(message, args));
        }

        /// <summary>
        /// Logs an info message with automatic sanitization of sensitive data
        /// </summary>
        public static void InfoSanitized(this Logger logger, string message, params object[] args)
        {
            logger.Info(SensitiveDataSanitizer.SanitizeLogMessage(message, args));
        }

        /// <summary>
        /// Logs HTTP request details with sanitized headers
        /// </summary>
        public static void LogHttpRequest(this Logger logger, string url, Dictionary<string, string> headers = null, LogLevel level = null)
        {
            level = level ?? LogLevel.Debug;
            var sanitizedUrl = SensitiveDataSanitizer.SanitizeUrl(url);
            logger.Log(level, "HTTP Request: {0}", sanitizedUrl);
            if (headers != null && headers.Count > 0)
            {
                var sanitizedHeaders = SensitiveDataSanitizer.SanitizeHeaders(headers);
                foreach (var header in sanitizedHeaders)
                {
                    logger.Log(level, "  {0}: {1}", header.Key, header.Value);
                }
            }
        }

        /// <summary>
        /// Logs a sanitized version of a settings object
        /// </summary>
        public static void LogSanitizedSettings(this Logger logger, object settings, LogLevel level = null)
        {
            level = level ?? LogLevel.Debug;
            if (settings == null)
            {
                logger.Log(level, "Settings: null");
                return;
            }

            var type = settings.GetType();
            logger.Log(level, "Settings for {0}:", type.Name);
            foreach (var prop in type.GetProperties())
            {
                var value = prop.GetValue(settings);
                var propName = prop.Name;

                // Check if property contains sensitive data
                if (propName.ToLowerInvariant().Contains("apikey") ||
                    propName.ToLowerInvariant().Contains("password") ||
                    propName.ToLowerInvariant().Contains("token") ||
                    propName.ToLowerInvariant().Contains("mamid") ||
                    propName.ToLowerInvariant().Contains("secret"))
                {
                    if (value != null)
                    {
                        logger.Log(level, "  {0}: <REDACTED>", propName);
                    }
                    else
                    {
                        logger.Log(level, "  {0}: null", propName);
                    }
                }
                else
                {
                    logger.Log(level, "  {0}: {1}", propName, value ?? "null");
                }
            }
        }
    }
}

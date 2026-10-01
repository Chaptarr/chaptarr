using System;
using NLog;

namespace NzbDrone.Common.Instrumentation
{
    public static class LogSanitizingExtensions
    {
        public static void TraceSafe(this Logger logger, string message, params object[] parameters)
        {
            WriteSafe(logger, LogLevel.Trace, null, message, parameters);
        }

        public static void TraceSafe(this Logger logger, Exception exception, string message, params object[] parameters)
        {
            WriteSafe(logger, LogLevel.Trace, exception, message, parameters);
        }

        public static void DebugSafe(this Logger logger, string message, params object[] parameters)
        {
            WriteSafe(logger, LogLevel.Debug, null, message, parameters);
        }

        public static void DebugSafe(this Logger logger, Exception exception, string message, params object[] parameters)
        {
            WriteSafe(logger, LogLevel.Debug, exception, message, parameters);
        }

        public static void InfoSafe(this Logger logger, string message, params object[] parameters)
        {
            WriteSafe(logger, LogLevel.Info, null, message, parameters);
        }

        public static void InfoSafe(this Logger logger, Exception exception, string message, params object[] parameters)
        {
            WriteSafe(logger, LogLevel.Info, exception, message, parameters);
        }

        public static void WarnSafe(this Logger logger, string message, params object[] parameters)
        {
            WriteSafe(logger, LogLevel.Warn, null, message, parameters);
        }

        public static void WarnSafe(this Logger logger, Exception exception, string message, params object[] parameters)
        {
            WriteSafe(logger, LogLevel.Warn, exception, message, parameters);
        }

        public static void ErrorSafe(this Logger logger, string message, params object[] parameters)
        {
            WriteSafe(logger, LogLevel.Error, null, message, parameters);
        }

        public static void ErrorSafe(this Logger logger, Exception exception, string message, params object[] parameters)
        {
            WriteSafe(logger, LogLevel.Error, exception, message, parameters);
        }

        public static void FatalSafe(this Logger logger, string message, params object[] parameters)
        {
            WriteSafe(logger, LogLevel.Fatal, null, message, parameters);
        }

        public static void FatalSafe(this Logger logger, Exception exception, string message, params object[] parameters)
        {
            WriteSafe(logger, LogLevel.Fatal, exception, message, parameters);
        }

        private static void WriteSafe(Logger logger, LogLevel level, Exception exception, string message, object[] parameters)
        {
            if (!logger.IsEnabled(level))
            {
                return;
            }

            var formattedMessage = new LogEventInfo(level, logger.Name, null, message, parameters).FormattedMessage;
            var safeMessage = CleanseLogMessage.Cleanse(formattedMessage);
            var logEvent = new LogEventInfo
            {
                Level = level,
                LoggerName = logger.Name,
                Message = safeMessage,
                Exception = exception
            };

            logger.Log(logEvent);
        }
    }
}

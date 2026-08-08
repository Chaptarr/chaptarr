using System;
using System.Data;
using Microsoft.Data.Sqlite;
using NLog;
using NLog.Common;
using NLog.Config;
using NLog.Targets;
using Npgsql;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Instrumentation
{
    public class DatabaseTarget : TargetWithLayout, IHandle<ApplicationShutdownRequested>
    {
        private const string INSERT_COMMAND = "INSERT INTO \"Logs\" (\"Message\",\"Time\",\"Logger\",\"Exception\",\"ExceptionType\",\"Level\") " +
                                      "VALUES(@Message,@Time,@Logger,@Exception,@ExceptionType,@Level)";

        private readonly IConnectionStringFactory _connectionStringFactory;

        public DatabaseTarget(IConnectionStringFactory connectionStringFactory)
        {
            _connectionStringFactory = connectionStringFactory;
        }

        public void Register()
        {
            var target = new SlowRunningAsyncTargetWrapper(this) { TimeToSleepBetweenBatches = 500 };

            Rule = new LoggingRule("*", LogLevel.Warn, target);

            LogManager.Configuration.AddTarget("DbLogger", target);
            LogManager.Configuration.LoggingRules.Add(Rule);
            LogManager.ConfigurationReloaded += OnLogManagerOnConfigurationReloaded;
            LogManager.ReconfigExistingLoggers();
        }

        public void UnRegister()
        {
            LogManager.ConfigurationReloaded -= OnLogManagerOnConfigurationReloaded;
            LogManager.Configuration.RemoveTarget("DbLogger");
            LogManager.Configuration.LoggingRules.Remove(Rule);
            LogManager.ReconfigExistingLoggers();
            Dispose();
        }

        private void OnLogManagerOnConfigurationReloaded(object sender, LoggingConfigurationReloadedEventArgs args)
        {
            Register();
        }

        public LoggingRule Rule { get; set; }

        protected override void Write(LogEventInfo logEvent)
        {
            try
            {
                var log = new Log
                {
                    Time = logEvent.TimeStamp,
                    Logger = logEvent.LoggerName,
                    Level = logEvent.Level.Name
                };

                if (log.Logger.StartsWith("NzbDrone."))
                {
                    log.Logger = log.Logger.Remove(0, 9);
                }

                var message = logEvent.FormattedMessage;

                if (logEvent.Exception != null)
                {
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        message = logEvent.Exception.Message;
                    }
                    else
                    {
                        message += ": " + logEvent.Exception.Message;
                    }

                    log.Exception = CleanseLogMessage.Cleanse(logEvent.Exception.ToString());
                    log.ExceptionType = logEvent.Exception.GetType().ToString();
                }

                log.Message = CleanseLogMessage.Cleanse(message);

                var connectionInfo = _connectionStringFactory.LogDbConnection;

                if (connectionInfo.DatabaseType == DatabaseType.SQLite)
                {
                    WriteSqliteLog(log, connectionInfo.ConnectionString);
                }
                else
                {
                    WritePostgresLog(log, connectionInfo.ConnectionString);
                }
            }
            catch (SqliteException ex)
            {
                InternalLogger.Error(ex, "Unable to save log event to database");
                throw;
            }
            catch (NpgsqlException ex)
            {
                InternalLogger.Error(ex, "Unable to save log event to database");
                throw;
            }
            catch (Exception ex)
            {
                InternalLogger.Error(ex, "Unexpected error while saving log event to database");
                // Don't throw to prevent logging from breaking the application
            }
        }

        private void WritePostgresLog(Log log, string connectionString)
        {
            using (var connection =
                new NpgsqlConnection(connectionString))
            {
                connection.Open();
                using (var sqlCommand = connection.CreateCommand())
                {
                    sqlCommand.CommandText = INSERT_COMMAND;
                    sqlCommand.Parameters.Add(new NpgsqlParameter("Message", DbType.String) { Value = log.Message });
                    sqlCommand.Parameters.Add(new NpgsqlParameter("Time", DbType.DateTime) { Value = log.Time.ToUniversalTime() });
                    sqlCommand.Parameters.Add(new NpgsqlParameter("Logger", DbType.String) { Value = log.Logger });
                    sqlCommand.Parameters.Add(new NpgsqlParameter("Exception", DbType.String) { Value = log.Exception == null ? DBNull.Value : log.Exception });
                    sqlCommand.Parameters.Add(new NpgsqlParameter("ExceptionType", DbType.String) { Value = log.ExceptionType == null ? DBNull.Value : log.ExceptionType });
                    sqlCommand.Parameters.Add(new NpgsqlParameter("Level", DbType.String) { Value = log.Level });

                    sqlCommand.ExecuteNonQuery();
                }
            }
        }

        private void WriteSqliteLog(Log log, string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var sqlCommand = connection.CreateCommand())
                {
                    sqlCommand.CommandText = INSERT_COMMAND;
                    sqlCommand.Parameters.Add(new SqliteParameter("Message", DbType.String) { Value = log.Message });
                    sqlCommand.Parameters.Add(new SqliteParameter("Time", DbType.DateTime) { Value = log.Time.ToUniversalTime() });
                    sqlCommand.Parameters.Add(new SqliteParameter("Logger", DbType.String) { Value = log.Logger });
                    sqlCommand.Parameters.Add(new SqliteParameter("Exception", DbType.String) { Value = log.Exception });
                    sqlCommand.Parameters.Add(new SqliteParameter("ExceptionType", DbType.String) { Value = log.ExceptionType });
                    sqlCommand.Parameters.Add(new SqliteParameter("Level", DbType.String) { Value = log.Level });
                    sqlCommand.ExecuteNonQuery();
                }
            }
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            if (LogManager.Configuration != null && LogManager.Configuration.LoggingRules.Contains(Rule))
            {
                UnRegister();
            }
        }
    }
}

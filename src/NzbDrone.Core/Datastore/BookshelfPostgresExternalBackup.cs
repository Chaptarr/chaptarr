using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Npgsql;

namespace NzbDrone.Core.Datastore
{
    public sealed record ExternalToolResult(int ExitCode, string StandardOutput, string StandardError);

    public interface IExternalToolRunner
    {
        ExternalToolResult Run(string executable, IReadOnlyCollection<string> arguments, IReadOnlyDictionary<string, string> environment);
    }

    public sealed class ExternalToolRunner : IExternalToolRunner
    {
        public ExternalToolResult Run(
            string executable,
            IReadOnlyCollection<string> arguments,
            IReadOnlyDictionary<string, string> environment)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var name in startInfo.Environment.Keys
                         .Where(name => name.StartsWith("PG", StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                startInfo.Environment.Remove(name);
            }

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var item in environment)
            {
                startInfo.Environment[item.Key] = item.Value;
            }

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new InvalidOperationException(
                    "PostgreSQL migration backup requires pg_dump and pg_restore to be installed and available on PATH.",
                    exception);
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return new ExternalToolResult(
                process.ExitCode,
                standardOutput.GetAwaiter().GetResult(),
                standardError.GetAwaiter().GetResult());
        }
    }

    public sealed class BookshelfPostgresExternalBackup
    {
        private readonly IExternalToolRunner _runner;
        private readonly Func<string, string> _currentSchema;
        private readonly Action<string> _ensureSourceDatabaseIsIdle;

        public BookshelfPostgresExternalBackup(
            IExternalToolRunner runner,
            Func<string, string> currentSchema = null,
            Action<string> ensureSourceDatabaseIsIdle = null)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _currentSchema = currentSchema ?? (connectionString => GetCurrentSchema(new NpgsqlConnectionStringBuilder(connectionString)));
            _ensureSourceDatabaseIsIdle = ensureSourceDatabaseIsIdle ?? EnsureSourceDatabaseIsIdle;
        }

        public void VerifyTools()
        {
            Run("pg_dump", new[] { "--version" }, new Dictionary<string, string>());
            Run("pg_restore", new[] { "--version" }, new Dictionary<string, string>());
        }

        public void ValidateConnectionForExternalTools(string connectionString)
        {
            var connection = CreatePostgresConnection(connectionString);
            if (string.IsNullOrWhiteSpace(connection.Username))
            {
                throw new InvalidDataException(
                    "The Windows PostgreSQL handoff requires PostgresUser to be explicitly configured so pg_dump uses the same database identity as ChaptarrNG.");
            }
        }

        public void ValidateDatabaseConnection(string connectionString)
        {
            ValidateConnectionForExternalTools(connectionString);
            _ = _currentSchema(connectionString);
        }

        public void EnsureSourceDatabaseIsIdle(string connectionString)
        {
            var connection = CreatePostgresConnection(connectionString);
            ValidateConnectionForExternalTools(connectionString);
            NpgsqlConnection.ClearAllPools();
            connection.Pooling = false;
            using var database = new NpgsqlConnection(connection.ConnectionString);
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(*)
                  FROM pg_stat_activity
                 WHERE datname = current_database()
                   AND pid <> pg_backend_pid()
                   AND backend_type = 'client backend';";
            var otherConnections = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (otherConnections > 0)
            {
                throw new InvalidOperationException(
                    $"The PostgreSQL database still has {otherConnections} other client connection(s). Stop every process using the database, then retry. No database changes were applied.");
            }
        }

        public void CreateDump(string connectionString, string destinationPath)
        {
            var connection = CreatePostgresConnection(connectionString);
            ValidateConnectionForExternalTools(connectionString);
            _ensureSourceDatabaseIsIdle(connection.ConnectionString);
            destinationPath = Path.GetFullPath(destinationPath);
            var directory = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidOperationException("The PostgreSQL backup file must have a parent directory.");

            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException($"The PostgreSQL backup directory does not exist: {directory}");
            }

            if (File.Exists(destinationPath))
            {
                throw new IOException("The PostgreSQL backup destination already exists.");
            }

            var passFile = WriteTemporaryPassFile(connection, directory);
            try
            {
                var schema = _currentSchema(connection.ConnectionString);
                var arguments = new List<string>
                {
                    "--format=custom",
                    "--no-owner",
                    "--no-privileges",
                    "--no-password",
                    "--schema",
                    QuoteSchemaPattern(schema),
                    "--file", destinationPath
                };
                AddConnectionArguments(arguments, connection);
                arguments.Add("--dbname");
                arguments.Add(connection.Database);

                Run("pg_dump", arguments, BuildEnvironment(connection, passFile));

                var info = new FileInfo(destinationPath);
                if (!info.Exists || info.Length == 0)
                {
                    throw new InvalidDataException("pg_dump returned success without creating a non-empty PostgreSQL backup.");
                }

                Run("pg_restore", new[] { "--list", destinationPath }, BuildEnvironment(connection, passFile));
            }
            catch
            {
                TryDelete(destinationPath);
                throw;
            }
            finally
            {
                TryDelete(passFile);
            }
        }

        public void RestoreDump(string connectionString, string backupPath)
        {
            var connection = CreatePostgresConnection(connectionString);
            ValidateConnectionForExternalTools(connectionString);
            backupPath = Path.GetFullPath(backupPath);
            if (!File.Exists(backupPath) || new FileInfo(backupPath).Length == 0)
            {
                throw new FileNotFoundException("The independent PostgreSQL migration backup was not found or is empty.", backupPath);
            }

            ValidateDump(backupPath);
            _ensureSourceDatabaseIsIdle(connection.ConnectionString);

            var directory = Path.GetDirectoryName(backupPath)
                ?? throw new InvalidOperationException("The PostgreSQL backup file must have a parent directory.");
            var passFile = WriteTemporaryPassFile(connection, directory);
            try
            {
                var arguments = new List<string>
                {
                    "--single-transaction",
                    "--clean",
                    "--if-exists",
                    "--no-owner",
                    "--no-privileges",
                    "--exit-on-error",
                    "--no-password"
                };
                AddConnectionArguments(arguments, connection);
                arguments.Add("--dbname");
                arguments.Add(connection.Database);
                arguments.Add(backupPath);

                Run("pg_restore", arguments, BuildEnvironment(connection, passFile));
            }
            finally
            {
                TryDelete(passFile);
            }
        }

        public void ValidateDump(string backupPath)
        {
            backupPath = Path.GetFullPath(backupPath);
            var dump = new FileInfo(backupPath);
            if (!dump.Exists)
            {
                throw new FileNotFoundException("The independent PostgreSQL migration backup was not found.", backupPath);
            }

            if ((dump.Attributes & FileAttributes.ReparsePoint) != 0 || dump.Length == 0)
            {
                throw new InvalidDataException("The independent PostgreSQL migration backup must be a non-empty regular file, not a reparse point or empty file: " + backupPath);
            }

            Run("pg_restore", new[] { "--list", backupPath }, new Dictionary<string, string>());
        }

        internal static string BuildPassFileLine(NpgsqlConnectionStringBuilder connection)
        {
            if ((connection.Password ?? string.Empty).Contains('\r') ||
                (connection.Password ?? string.Empty).Contains('\n'))
            {
                throw new InvalidDataException("The PostgreSQL password cannot contain a newline for an external pg_dump credential file.");
            }

            return string.Join(":",
                EscapePassFileValue(connection.Host),
                connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EscapePassFileValue(connection.Database),
                EscapePassFileValue(connection.Username),
                EscapePassFileValue(connection.Password));
        }

        private ExternalToolResult Run(
            string executable,
            IReadOnlyCollection<string> arguments,
            IReadOnlyDictionary<string, string> environment)
        {
            var result = _runner.Run(executable, arguments, environment);
            if (result.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput.Trim()
                    : result.StandardError.Trim();
                throw new InvalidOperationException(
                    $"{executable} exited with code {result.ExitCode}" +
                    (string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}"));
            }

            return result;
        }

        private static NpgsqlConnectionStringBuilder CreatePostgresConnection(string connectionString)
        {
            var connection = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(connection.Database))
            {
                throw new InvalidDataException("The selected PostgreSQL connection has no database name.");
            }

            return connection;
        }

        private static string GetCurrentSchema(NpgsqlConnectionStringBuilder connection)
        {
            connection.Pooling = false;
            using var database = new NpgsqlConnection(connection.ConnectionString);
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "SELECT current_schema();";
            var schema = command.ExecuteScalar()?.ToString();
            if (string.IsNullOrWhiteSpace(schema))
            {
                throw new InvalidDataException("The configured PostgreSQL connection has no active schema.");
            }

            return schema;
        }

        private static string QuoteSchemaPattern(string schema)
        {
            return "\"" + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        private static void AddConnectionArguments(ICollection<string> arguments, NpgsqlConnectionStringBuilder connection)
        {
            if (!string.IsNullOrWhiteSpace(connection.Host))
            {
                arguments.Add("--host");
                arguments.Add(connection.Host);
            }

            arguments.Add("--port");
            arguments.Add(connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(connection.Username))
            {
                arguments.Add("--username");
                arguments.Add(connection.Username);
            }
        }

        private static IReadOnlyDictionary<string, string> BuildEnvironment(
            NpgsqlConnectionStringBuilder connection,
            string passFile)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result["PGPASSFILE"] = passFile;

            result["PGSSLMODE"] = connection.SslMode switch
            {
                SslMode.Disable => "disable",
                SslMode.Allow => "allow",
                SslMode.Prefer => "prefer",
                SslMode.Require => "require",
                SslMode.VerifyCA => "verify-ca",
                SslMode.VerifyFull => "verify-full",
                _ => "prefer"
            };

            if (!string.IsNullOrWhiteSpace(connection.RootCertificate))
            {
                result["PGSSLROOTCERT"] = connection.RootCertificate;
            }

            return result;
        }

        private static string WriteTemporaryPassFile(NpgsqlConnectionStringBuilder connection, string directory)
        {
            var path = Path.Combine(directory, ".pgpass-" + Guid.NewGuid().ToString("N"));
            var line = BuildPassFileLine(connection) + Environment.NewLine;
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            try
            {
                using var stream = new FileStream(path, options);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(line);
                return path;
            }
            catch
            {
                TryDelete(path);
                throw;
            }
        }

        private static string EscapePassFileValue(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace(":", "\\:", StringComparison.Ordinal);
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // The dump or passfile is task-owned; leave it for manual recovery if busy.
            }
            catch (UnauthorizedAccessException)
            {
                // The dump or passfile is task-owned; leave it for manual recovery if access changed.
            }
        }
    }
}

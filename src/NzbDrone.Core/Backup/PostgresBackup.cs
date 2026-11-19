using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Dapper;
using Npgsql;

namespace NzbDrone.Core.Backup
{
    internal static class PostgresBackup
    {
        internal static int ServerMajor(string connectionString)
        {
            using var connection = Open(connectionString);
            return connection.ExecuteScalar<int>("SHOW server_version_num") / 10000;
        }

        internal static BackupManifest.PostgresDatabaseSettings DatabaseSettings(NpgsqlConnection connection, string name = null)
        {
            return connection.QuerySingle<BackupManifest.PostgresDatabaseSettings>(@"
                SELECT pg_encoding_to_char(encoding) AS Encoding, datcollate AS Collation, datctype AS CharacterType,
                    COALESCE(to_jsonb(d)->>'datlocprovider', 'c') AS Provider,
                    COALESCE(to_jsonb(d)->>'datlocale', to_jsonb(d)->>'daticulocale') AS Locale,
                    to_jsonb(d)->>'daticurules' AS IcuRules, to_jsonb(d)->>'datcollversion' AS CollationVersion
                FROM pg_database d WHERE datname = COALESCE(@Name, current_database())", new { Name = name });
        }

        internal static string CreateDatabaseSql(string name, BackupManifest.PostgresDatabaseSettings settings, int major)
        {
            var sql = "CREATE DATABASE " + Identifier(name) + " TEMPLATE template0 ENCODING " + Literal(settings.Encoding) +
                " LC_COLLATE " + Literal(settings.Collation) + " LC_CTYPE " + Literal(settings.CharacterType);
            if (settings.Provider == "i" && major >= 15)
            {
                sql += " LOCALE_PROVIDER icu ICU_LOCALE " + Literal(settings.Locale);
                if (!string.IsNullOrEmpty(settings.IcuRules))
                {
                    if (major < 16)
                    {
                        throw new InvalidDataException("The backup requires PostgreSQL support for ICU collation rules.");
                    }
                    sql += " ICU_RULES " + Literal(settings.IcuRules);
                }
            }
            else if (settings.Provider == "b" && major >= 17)
            {
                sql += " LOCALE_PROVIDER builtin BUILTIN_LOCALE " + Literal(settings.Locale);
            }
            else if (settings.Provider != "c")
            {
                throw new InvalidDataException("The backup's locale provider is not supported by the destination PostgreSQL server.");
            }
            else if (major >= 15)
            {
                sql += " LOCALE_PROVIDER libc";
            }
            return sql;
        }

        private static string Literal(string value) => "E'" + (value ?? throw new InvalidDataException("Missing PostgreSQL locale setting."))
            .Replace("\\", "\\\\").Replace("'", "''") + "'";

        internal static NpgsqlConnection Open(string connectionString, string database = null)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
            if (database != null)
            {
                builder.Database = database;
            }

            var connection = new NpgsqlConnection(builder.ConnectionString);
            try
            {
                connection.Open();
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        internal static string Tool(string name, int major, CancellationToken cancellationToken = default)
        {
            var executable = name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
            var candidates = new[]
            {
                Path.Combine("/usr/lib/postgresql", major.ToString(CultureInfo.InvariantCulture), "bin", executable),
                OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", major.ToString(CultureInfo.InvariantCulture), "bin", executable) : null
            }.Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.Combine(path, executable))).Distinct();

            foreach (var path in candidates.Where(File.Exists))
            {
                var version = Run(path, new[] { "--version" }, null, cancellationToken, timeout: TimeSpan.FromSeconds(10));
                var match = Regex.Match(version, @"PostgreSQL\)\s+(\d+)\.");
                if (match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == major)
                {
                    return path;
                }
            }

            throw new InvalidOperationException($"Install PostgreSQL {major} client tools (pg_dump and pg_restore) on the Chaptarr host and add their bin directory to PATH. No database backup or restore was applied.");
        }

        internal static void Dump(string connectionString, string path, CancellationToken cancellationToken = default)
        {
            var timer = Stopwatch.StartNew();
            var major = ServerMajor(connectionString);
            Run(Tool("pg_dump", major, cancellationToken), new[] { "--format=custom", "--no-owner", "--no-acl", "--file", path }, connectionString, cancellationToken, Path.GetDirectoryName(path));
            ValidateArchive(path, major, cancellationToken);
            NLog.LogManager.GetCurrentClassLogger().Info("PostgreSQL snapshot verified: {0} bytes in {1} ms", new FileInfo(path).Length, timer.ElapsedMilliseconds);
        }

        internal static void ValidateArchive(string path, int major, CancellationToken cancellationToken = default)
        {
            Run(Tool("pg_restore", major, cancellationToken), new[] { "--list", path }, null, cancellationToken);
        }

        internal static void Restore(string connectionString, string path, int major, CancellationToken cancellationToken = default)
        {
            var timer = Stopwatch.StartNew();
            Run(Tool("pg_restore", major, cancellationToken), new[] { "--exit-on-error", "--single-transaction", "--no-owner", "--no-acl", path }, connectionString, cancellationToken, Path.GetDirectoryName(path));
            NLog.LogManager.GetCurrentClassLogger().Info("PostgreSQL staged restore loaded: {0} bytes in {1} ms", new FileInfo(path).Length, timer.ElapsedMilliseconds);
        }

        internal static string Identifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

        private static string PasswordField(string value)
        {
            if (value.Contains('\n') || value.Contains('\r'))
            {
                throw new InvalidOperationException("The PostgreSQL password contains a line break and cannot be passed safely to the standard client tools.");
            }

            return value.Replace("\\", "\\\\").Replace(":", "\\:");
        }

        internal static string Run(string executable, IEnumerable<string> arguments, string connectionString, CancellationToken cancellationToken, string workingFolder = null, TimeSpan? timeout = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var operationToken = linked.Token;
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            // The general process provider logs environments and raw output; credentials require a private boundary.
            foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                info.Environment.Remove(key);
            }

            string privateFolder = null;
            var applicationName = "chaptarr_backup_" + Guid.NewGuid().ToString("N");
            try
            {
                if (connectionString != null)
                {
                    var connection = new NpgsqlConnectionStringBuilder(connectionString);
                    privateFolder = Path.Combine(workingFolder ?? Path.GetTempPath(), "chaptarr_pgpass_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N"));
                    BackupService.CreatePrivateFolder(privateFolder);
                    var passwordFile = Path.Combine(privateFolder, "pgpass");
                    File.WriteAllText(passwordFile, string.Join(":", new[] { "*", "*", "*", PasswordField(connection.Username), PasswordField(connection.Password ?? string.Empty) }) + "\n");
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(passwordFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }

                    info.Environment["PGPASSFILE"] = passwordFile;
                    info.Environment["PGHOST"] = connection.Host;
                    info.Environment["PGPORT"] = connection.Port.ToString(CultureInfo.InvariantCulture);
                    info.Environment["PGUSER"] = connection.Username;
                    info.Environment["PGDATABASE"] = connection.Database;
                    info.Environment["PGAPPNAME"] = applicationName;
                    info.ArgumentList.Add("--dbname");
                    info.ArgumentList.Add("dbname='" + connection.Database.Replace("\\", "\\\\").Replace("'", "\\'") + "'");
                    info.Environment["PGCONNECT_TIMEOUT"] = connection.Timeout.ToString(CultureInfo.InvariantCulture);
                    info.Environment["PGSSLMODE"] = connection.SslMode.ToString().ToLowerInvariant().Replace("verifyfull", "verify-full").Replace("verifyca", "verify-ca");
                    if (!string.IsNullOrEmpty(connection.RootCertificate))
                    {
                        info.Environment["PGSSLROOTCERT"] = connection.RootCertificate;
                    }

                    info.ArgumentList.Add("--no-password");
                }

                using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start PostgreSQL client tools.");
                var output = process.StandardOutput.ReadToEndAsync(operationToken);
                var error = process.StandardError.ReadToEndAsync(operationToken);
                try
                {
                    process.WaitForExitAsync(operationToken).GetAwaiter().GetResult();
                    var result = output.GetAwaiter().GetResult();
                    var diagnostic = error.GetAwaiter().GetResult();
                    if (process.ExitCode != 0)
                    {
                        // Tool errors can echo COPY rows containing stored credentials; never surface raw diagnostics.
                        var reason = diagnostic.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase) ? "Authentication failed." :
                            diagnostic.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ? "The database role or backup folder lacks required permissions." :
                            diagnostic.Contains("unsupported version", StringComparison.OrdinalIgnoreCase) ? "The archive and client versions are incompatible." :
                            diagnostic.Contains("connection", StringComparison.OrdinalIgnoreCase) ? "The database connection failed." :
                            "Check the archive, database permissions and available disk space.";
                        throw new InvalidOperationException($"PostgreSQL {Path.GetFileNameWithoutExtension(executable)} failed (exit {process.ExitCode}). {reason}");
                    }

                    return result;
                }
                catch (OperationCanceledException)
                {
                    // Killing a blocked client does not cancel its server-side lock wait.
                    if (connectionString != null)
                    {
                        try
                        {
                            using var connection = Open(connectionString);
                            connection.Execute("SELECT pg_cancel_backend(pid) FROM pg_stat_activity WHERE application_name = @Name AND usename = CURRENT_USER AND datname = current_database()",
                                new { Name = applicationName }, commandTimeout: 10);
                        }
                        catch (Exception cancellationError)
                        {
                            NLog.LogManager.GetCurrentClassLogger().Warn(cancellationError, "Unable to cancel the timed-out PostgreSQL backup connection");
                        }
                    }
                    if (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                        throw new TimeoutException($"PostgreSQL {Path.GetFileNameWithoutExtension(executable)} exceeded its {timeout ?? TimeSpan.FromMinutes(30)} time limit. No completed backup/restore was reported.");
                    throw;
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        try
                        {
                            process.Kill(true);
                        }
                        catch (InvalidOperationException) when (process.HasExited)
                        {
                        }
                        process.WaitForExit();
                    }
                }
            }
            finally
            {
                if (privateFolder != null && Directory.Exists(privateFolder))
                {
                    Directory.Delete(privateFolder, true);
                }
            }
        }
    }
}

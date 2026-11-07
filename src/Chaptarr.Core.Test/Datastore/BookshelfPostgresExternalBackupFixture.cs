using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class BookshelfPostgresExternalBackupFixture
    {
        private string _directory;
        private FakeExternalToolRunner _runner;
        private BookshelfPostgresExternalBackup _backup;
        private string _connectionString;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "chaptarr-pg-backup-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _runner = new FakeExternalToolRunner();
            _backup = new BookshelfPostgresExternalBackup(_runner, _ => "public", _ => { });
            _connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = "postgres.example",
                Port = 5444,
                Database = "readarr:main",
                Username = "operator:user",
                Password = "secret:value\\tail",
                SslMode = SslMode.VerifyFull,
                RootCertificate = "C:\\certificates\\root cert.pem"
            }.ConnectionString;
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [Test]
        public void should_create_and_validate_a_custom_dump_without_exposing_password_in_arguments()
        {
            var dumpPath = Path.Combine(_directory, "postgres.dump");

            _backup.CreateDump(_connectionString, dumpPath);

            Assert.That(File.Exists(dumpPath), Is.True);
            Assert.That(_runner.Calls.SelectMany(call => call.Arguments), Does.Not.Contain("secret:value\\tail"));
            Assert.That(_runner.Calls[0].Executable, Is.EqualTo("pg_dump"));
            Assert.That(_runner.Calls[0].Arguments, Does.Contain("--format=custom"));
            Assert.That(_runner.Calls[0].Arguments, Does.Contain("--no-owner"));
            Assert.That(_runner.Calls[0].Arguments, Does.Contain("--no-privileges"));
            Assert.That(_runner.Calls[0].Arguments, Does.Contain("--schema"));
            Assert.That(_runner.Calls[0].Arguments, Does.Contain("\"public\""));
            Assert.That(_runner.Calls[0].Arguments, Does.Contain("readarr:main"));
            Assert.That(_runner.Calls[1].Executable, Is.EqualTo("pg_restore"));
            Assert.That(_runner.Calls[1].Arguments, Does.Contain("--list"));
            Assert.That(_runner.Calls[0].PassFileContents, Is.EqualTo("postgres.example:5444:readarr\\:main:operator\\:user:secret\\:value\\\\tail" + Environment.NewLine));
            Assert.That(_runner.Calls[0].Environment["PGSSLMODE"], Is.EqualTo("verify-full"));
            Assert.That(_runner.Calls[0].Environment["PGSSLROOTCERT"], Is.EqualTo("C:\\certificates\\root cert.pem"));
            Assert.That(File.Exists(_runner.Calls[0].PassFilePath), Is.False);
            if (!OperatingSystem.IsWindows())
            {
                Assert.That(_runner.Calls[0].PassFileMode, Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
            }
        }

        [Test]
        public void should_delete_partial_dump_and_temporary_credentials_when_pg_dump_fails()
        {
            _runner.FailDump = true;
            var dumpPath = Path.Combine(_directory, "postgres.dump");

            Assert.Throws<InvalidOperationException>(() => _backup.CreateDump(_connectionString, dumpPath));

            Assert.That(File.Exists(dumpPath), Is.False);
            var dumpCall = _runner.Calls.Single(call => call.Arguments.Contains("--file"));
            Assert.That(File.Exists(dumpCall.PassFilePath), Is.False);
        }

        [Test]
        public void should_restore_the_independent_dump_with_clean_and_fail_fast_options()
        {
            var dumpPath = Path.Combine(_directory, "postgres.dump");
            File.WriteAllText(dumpPath, "verified fixture archive");

            _backup.RestoreDump(_connectionString, dumpPath);

            var call = _runner.Calls.Last();
            Assert.That(call.Executable, Is.EqualTo("pg_restore"));
            Assert.That(call.Arguments, Does.Contain("--clean"));
            Assert.That(call.Arguments, Does.Contain("--if-exists"));
            Assert.That(call.Arguments, Does.Contain("--exit-on-error"));
            Assert.That(call.Arguments, Does.Contain("--single-transaction"));
            Assert.That(call.Arguments, Does.Contain("--no-owner"));
            Assert.That(call.Arguments, Does.Contain("--no-privileges"));
            Assert.That(call.Arguments, Does.Contain("readarr:main"));
            Assert.That(call.Arguments, Does.Not.Contain("secret:value\\tail"));
            Assert.That(call.Environment.ContainsKey("PGPASSFILE"), Is.True);
            Assert.That(File.Exists(call.PassFilePath), Is.False);
            Assert.That(_runner.Calls.First().Arguments, Does.Contain("--list"));
        }

        [Test]
        public void should_reject_a_dump_that_pg_restore_cannot_list()
        {
            var dumpPath = Path.Combine(_directory, "invalid.dump");
            File.WriteAllText(dumpPath, "invalid fixture archive");
            _runner.FailArchiveList = true;

            Assert.Throws<InvalidOperationException>(() => _backup.ValidateDump(dumpPath));
            Assert.That(_runner.Calls.Single().Arguments, Does.Contain("--list"));
        }

        [Test]
        public void should_require_an_explicit_database_user_for_external_tools()
        {
            var connectionString = new NpgsqlConnectionStringBuilder(_connectionString)
            {
                Username = string.Empty
            }.ConnectionString;

            Assert.Throws<InvalidDataException>(() => _backup.ValidateConnectionForExternalTools(connectionString));
            Assert.That(_runner.Calls, Is.Empty);
        }

        [Test]
        public void should_use_an_empty_private_passfile_instead_of_a_profile_default_when_password_is_empty()
        {
            var connectionString = new NpgsqlConnectionStringBuilder(_connectionString)
            {
                Password = string.Empty
            }.ConnectionString;

            _backup.CreateDump(connectionString, Path.Combine(_directory, "empty-password.dump"));

            Assert.That(_runner.Calls[0].Environment.ContainsKey("PGPASSFILE"), Is.True);
            Assert.That(_runner.Calls[0].PassFileContents, Is.EqualTo("postgres.example:5444:readarr\\:main:operator\\:user:" + Environment.NewLine));
        }

        [Test]
        public void should_restore_a_real_postgres_schema_dump_after_database_mutation()
        {
            var connectionString = Environment.GetEnvironmentVariable("CHAPTARRNG_POSTGRES_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Assert.Ignore("The real PostgreSQL dump/restore probe runs in the disposable PostgreSQL validation job.");
            }

            var schema = "chaptarr_backup_" + Guid.NewGuid().ToString("N");
            var adminConnection = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false
            };
            var schemaConnection = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
                SearchPath = schema
            };
            var dumpPath = Path.Combine(_directory, "postgres-real.dump");

            try
            {
                using (var database = new NpgsqlConnection(adminConnection.ConnectionString))
                {
                    database.Open();
                    using var command = database.CreateCommand();
                    command.CommandText = $"CREATE SCHEMA {schema};";
                    command.ExecuteNonQuery();
                }

                using (var database = new NpgsqlConnection(schemaConnection.ConnectionString))
                {
                    database.Open();
                    using var command = database.CreateCommand();
                    command.CommandText = "CREATE TABLE rollback_probe (id integer PRIMARY KEY, value text NOT NULL); INSERT INTO rollback_probe VALUES (1, 'before');";
                    command.ExecuteNonQuery();
                }

                var backup = new BookshelfPostgresExternalBackup(new ExternalToolRunner());
                backup.VerifyTools();
                backup.CreateDump(schemaConnection.ConnectionString, dumpPath);

                using (var database = new NpgsqlConnection(schemaConnection.ConnectionString))
                {
                    database.Open();
                    using var command = database.CreateCommand();
                    command.CommandText = "UPDATE rollback_probe SET value = 'after'; INSERT INTO rollback_probe VALUES (2, 'also-after');";
                    command.ExecuteNonQuery();
                }

                backup.RestoreDump(schemaConnection.ConnectionString, dumpPath);

                using (var database = new NpgsqlConnection(schemaConnection.ConnectionString))
                {
                    database.Open();
                    using var command = database.CreateCommand();
                    command.CommandText = "SELECT id::text || ':' || value FROM rollback_probe ORDER BY id;";
                    using var reader = command.ExecuteReader();
                    var restoredRows = new List<string>();
                    while (reader.Read())
                    {
                        restoredRows.Add(reader.GetString(0));
                    }

                    Assert.That(restoredRows, Is.EqualTo(new[] { "1:before" }));
                }
            }
            finally
            {
                using var database = new NpgsqlConnection(adminConnection.ConnectionString);
                database.Open();
                using var command = database.CreateCommand();
                command.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE;";
                command.ExecuteNonQuery();
            }
        }

        private sealed record ToolCall(
            string Executable,
            string[] Arguments,
            Dictionary<string, string> Environment,
            string PassFilePath,
            string PassFileContents,
            UnixFileMode? PassFileMode);

        private sealed class FakeExternalToolRunner : IExternalToolRunner
        {
            public List<ToolCall> Calls { get; } = new();
            public bool FailDump { get; set; }
            public bool FailArchiveList { get; set; }

            public ExternalToolResult Run(
                string executable,
                IReadOnlyCollection<string> arguments,
                IReadOnlyDictionary<string, string> environment)
            {
                var args = arguments.ToArray();
                var env = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
                env.TryGetValue("PGPASSFILE", out var passFilePath);
                var passFileContents = passFilePath is not null && File.Exists(passFilePath)
                    ? File.ReadAllText(passFilePath)
                    : null;
                UnixFileMode? passFileMode = passFilePath is not null && File.Exists(passFilePath) && !OperatingSystem.IsWindows()
                    ? File.GetUnixFileMode(passFilePath)
                    : null;

                Calls.Add(new ToolCall(executable, args, env, passFilePath, passFileContents, passFileMode));

                if (string.Equals(executable, "pg_dump", StringComparison.Ordinal) && args.Contains("--file"))
                {
                    var dumpPath = args[Array.IndexOf(args, "--file") + 1];
                    File.WriteAllText(dumpPath, "fixture archive");
                    return FailDump
                        ? new ExternalToolResult(1, string.Empty, "controlled pg_dump failure")
                        : new ExternalToolResult(0, string.Empty, string.Empty);
                }

                if (string.Equals(executable, "pg_restore", StringComparison.Ordinal) && args.Contains("--list") && FailArchiveList)
                {
                    return new ExternalToolResult(1, string.Empty, "controlled archive validation failure");
                }

                return new ExternalToolResult(0, "PostgreSQL client tools", string.Empty);
            }
        }
    }
}

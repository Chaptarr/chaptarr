using System.IO;
using System.Collections.Generic;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Exceptions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class ConnectionStringFactoryFixture
    {
        private const string Secret = "s3cret-value";

        private InterfaceStub<IConfigFileProvider> _config;
        private InterfaceStub<IAppFolderInfo> _folders;
        private IConfigFileProvider _configInstance;
        private IAppFolderInfo _foldersInstance;
        private StubPostgresDatabaseInspector _postgresInspector;

        [SetUp]
        public void Setup()
        {
            _config = InterfaceStub<IConfigFileProvider>.Create(out _configInstance);
            Set("PostgresHost", "db.example");
            Set("PostgresPort", 5432);
            Set("PostgresUser", "user");
            Set("PostgresPassword", Secret);
            Set("PostgresMainDb", "main");
            Set("PostgresLogDb", "log");
            Set("PostgresCacheDb", "cache");
            Set("PostgresNoResetOnClose", true);

            _postgresInspector = new StubPostgresDatabaseInspector();

            _folders = InterfaceStub<IAppFolderInfo>.Create(out _foldersInstance);
            _folders.Values["AppDataFolder"] = "/tmp/chaptarr-test";
        }

        private void Set(string name, object value) => _config.Values[name] = value;

        private ConnectionStringFactory Build() => new ConnectionStringFactory(_foldersInstance, _configInstance, _postgresInspector);

        private static NpgsqlConnectionStringBuilder Parse(DatabaseConnectionInfo info) => new NpgsqlConnectionStringBuilder(info.ConnectionString);

        [Test]
        public void should_skip_connection_reset_on_all_postgres_databases_by_default()
        {
            var factory = Build();

            Assert.That(Parse(factory.MainDbConnection).NoResetOnClose, Is.True);
            Assert.That(Parse(factory.LogDbConnection).NoResetOnClose, Is.True);
            Assert.That(Parse(factory.CacheDbConnection).NoResetOnClose, Is.True);
        }

        [Test]
        public void should_allow_connection_reset_to_be_restored()
        {
            Set("PostgresNoResetOnClose", false);

            Assert.That(Parse(Build().MainDbConnection).NoResetOnClose, Is.False);
        }

        [Test]
        public void should_leave_tls_settings_untouched_when_not_configured()
        {
            var csb = Parse(Build().MainDbConnection);

            Assert.That(csb.SslMode, Is.EqualTo(new NpgsqlConnectionStringBuilder().SslMode));
            Assert.That(csb.RootCertificate, Is.Null);
        }

        [TestCase("disable", SslMode.Disable)]
        [TestCase("prefer", SslMode.Prefer)]
        [TestCase("require", SslMode.Require)]
        [TestCase("verify-ca", SslMode.VerifyCA)]
        [TestCase("Verify-Full", SslMode.VerifyFull)]
        [TestCase("VerifyFull", SslMode.VerifyFull)]
        public void should_apply_ssl_mode_to_every_postgres_database(string value, SslMode expected)
        {
            Set("PostgresSslMode", value);

            var factory = Build();

            Assert.That(Parse(factory.MainDbConnection).SslMode, Is.EqualTo(expected));
            Assert.That(Parse(factory.LogDbConnection).SslMode, Is.EqualTo(expected));
            Assert.That(Parse(factory.CacheDbConnection).SslMode, Is.EqualTo(expected));
        }

        [Test]
        public void should_reject_unknown_ssl_mode_without_leaking_password()
        {
            Set("PostgresSslMode", "bogus");

            var ex = Assert.Throws<ChaptarrStartupException>(() => Build());

            Assert.That(ex.Message, Does.Contain("bogus"));
            Assert.That(ex.Message, Does.Not.Contain(Secret));
        }

        [Test]
        public void should_apply_existing_root_certificate()
        {
            var path = Path.GetTempFileName();

            try
            {
                Set("PostgresRootCert", path);

                Assert.That(Parse(Build().CacheDbConnection).RootCertificate, Is.EqualTo(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void should_fail_clearly_when_root_certificate_is_missing()
        {
            var path = Path.Combine(Path.GetTempPath(), "missing-root-" + System.Guid.NewGuid() + ".crt");
            Set("PostgresRootCert", path);

            var ex = Assert.Throws<ChaptarrStartupException>(() => Build());

            Assert.That(ex.Message, Does.Contain(path));
            Assert.That(ex.Message, Does.Not.Contain(Secret));
        }

        [Test]
        public void should_not_apply_postgres_settings_to_sqlite()
        {
            Set("PostgresHost", string.Empty);
            Set("PostgresSslMode", "bogus");
            Set("PostgresRootCert", "/nonexistent.crt");

            var factory = Build();

            Assert.That(factory.MainDbConnection.DatabaseType, Is.EqualTo(DatabaseType.SQLite));
            Assert.That(factory.CacheDbConnection.DatabaseType, Is.EqualTo(DatabaseType.SQLite));
        }

        [Test]
        public void should_select_bookshelf_postgres_defaults_for_an_implicit_bookshelf_main_database()
        {
            Set("PostgresMainDb", BookshelfPostgresDatabaseSelector.ChaptarrMainDb);
            Set("PostgresLogDb", BookshelfPostgresDatabaseSelector.ChaptarrLogDb);
            Set("PostgresCacheDb", BookshelfPostgresDatabaseSelector.ChaptarrCacheDb);
            _postgresInspector.States[BookshelfPostgresDatabaseSelector.BookshelfMainDb] =
                new PostgresDatabaseState(true, DatabaseLineage.BookshelfNG, false);

            var factory = Build();

            Assert.That(Parse(factory.MainDbConnection).Database, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfMainDb));
            Assert.That(Parse(factory.LogDbConnection).Database, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfLogDb));
            Assert.That(Parse(factory.CacheDbConnection).Database, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfCacheDb));
        }

        private sealed class StubPostgresDatabaseInspector : IPostgresDatabaseInspector
        {
            public Dictionary<string, PostgresDatabaseState> States { get; } = new Dictionary<string, PostgresDatabaseState>();

            public PostgresDatabaseState Inspect(string connectionString)
            {
                var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
                return States.TryGetValue(database, out var state)
                    ? state
                    : new PostgresDatabaseState(false, DatabaseLineage.Empty, false);
            }
        }
    }
}

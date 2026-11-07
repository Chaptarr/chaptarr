using System;
using System.IO;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class BookshelfPostgresDatabaseSelectionFixture
    {
        private string _baseConnectionString;
        private NpgsqlConnectionStringBuilder _baseBuilder;
        private bool _ownsBookshelfMain;
        private bool _ownsChaptarrMain;
        private InterfaceStub<IConfigFileProvider> _config;
        private IConfigFileProvider _configInstance;
        private InterfaceStub<IAppFolderInfo> _folders;
        private IAppFolderInfo _foldersInstance;

        [SetUp]
        public void SetUp()
        {
            _baseConnectionString = Environment.GetEnvironmentVariable("CHAPTARRNG_POSTGRES_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(_baseConnectionString))
            {
                Assert.Ignore("Set CHAPTARRNG_POSTGRES_TEST_CONNECTION to run the PostgreSQL database-selection fixture.");
            }

            _baseBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
            {
                Pooling = false
            };

            if (DatabaseExists(BookshelfPostgresDatabaseSelector.BookshelfMainDb) ||
                DatabaseExists(BookshelfPostgresDatabaseSelector.ChaptarrMainDb))
            {
                Assert.Ignore("The PostgreSQL test server already contains a database name reserved by this isolated fixture.");
            }

            try
            {
                CreateBookshelfMainDatabase();
                _ownsBookshelfMain = true;
            }
            catch (PostgresException exception) when (exception.SqlState == "42501")
            {
                Assert.Ignore("The PostgreSQL test role cannot create isolated test databases.");
            }

            CreateBookshelfSchema();
            BuildConnectionConfig();
        }

        [TearDown]
        public void TearDown()
        {
            if (string.IsNullOrWhiteSpace(_baseConnectionString))
            {
                return;
            }

            if (_ownsChaptarrMain)
            {
                DropChaptarrMainDatabase();
            }

            if (_ownsBookshelfMain)
            {
                DropBookshelfMainDatabase();
            }
        }

        [Test]
        public void should_select_and_keep_legacy_database_names_after_conversion()
        {
            var sourceSelection = BuildFactory();

            Assert.That(Parse(sourceSelection.MainDbConnection).Database, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfMainDb));
            Assert.That(Parse(sourceSelection.LogDbConnection).Database, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfLogDb));
            Assert.That(Parse(sourceSelection.CacheDbConnection).Database, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfCacheDb));

            ConvertTestSchemaToChaptarr();
            var restartSelection = BuildFactory();

            Assert.That(Parse(restartSelection.MainDbConnection).Database, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfMainDb));
            Assert.That(Parse(restartSelection.LogDbConnection).Database, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfLogDb));
            Assert.That(Parse(restartSelection.CacheDbConnection).Database, Is.EqualTo(BookshelfPostgresDatabaseSelector.BookshelfCacheDb));
        }

        [Test]
        public void should_stop_when_default_database_is_unsupported_and_legacy_database_has_bookshelf_data()
        {
            CreateChaptarrMainDatabase();
            _ownsChaptarrMain = true;
            using (var connection = OpenDatabase(BookshelfPostgresDatabaseSelector.ChaptarrMainDb))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE \"NotChaptarr\" (\"Id\" integer);";
                command.ExecuteNonQuery();
            }

            var exception = Assert.Throws<InvalidDataException>(() => BuildFactory());

            Assert.That(exception.Message, Does.Contain(BookshelfPostgresDatabaseSelector.ChaptarrMainDb));
            Assert.That(exception.Message, Does.Contain(BookshelfPostgresDatabaseSelector.BookshelfMainDb));
        }

        private ConnectionStringFactory BuildFactory()
        {
            return new ConnectionStringFactory(
                _foldersInstance,
                _configInstance,
                new PostgresDatabaseInspector());
        }

        private void BuildConnectionConfig()
        {
            _config = InterfaceStub<IConfigFileProvider>.Create(out _configInstance);
            _config.Values[nameof(IConfigFileProvider.PostgresHost)] = _baseBuilder.Host;
            _config.Values[nameof(IConfigFileProvider.PostgresPort)] = _baseBuilder.Port;
            _config.Values[nameof(IConfigFileProvider.PostgresUser)] = _baseBuilder.Username;
            _config.Values[nameof(IConfigFileProvider.PostgresPassword)] = _baseBuilder.Password;
            _config.Values[nameof(IConfigFileProvider.PostgresMainDb)] = BookshelfPostgresDatabaseSelector.ChaptarrMainDb;
            _config.Values[nameof(IConfigFileProvider.PostgresLogDb)] = BookshelfPostgresDatabaseSelector.ChaptarrLogDb;
            _config.Values[nameof(IConfigFileProvider.PostgresCacheDb)] = BookshelfPostgresDatabaseSelector.ChaptarrCacheDb;
            _config.Values[nameof(IConfigFileProvider.PostgresNoResetOnClose)] = false;

            _folders = InterfaceStub<IAppFolderInfo>.Create(out _foldersInstance);
            _folders.Values["AppDataFolder"] = Path.Combine(Path.GetTempPath(), "chaptarr-postgres-selection");
        }

        private void CreateBookshelfSchema()
        {
            using var connection = OpenDatabase(BookshelfPostgresDatabaseSelector.BookshelfMainDb);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE "Authors" ("Id" integer PRIMARY KEY, "AuthorMetadataId" integer);
                CREATE TABLE "AuthorMetadata" ("Id" integer PRIMARY KEY);
                CREATE TABLE "Books" ("Id" integer PRIMARY KEY, "AuthorMetadataId" integer, "ForeignBookId" text);
                """;
            command.ExecuteNonQuery();
        }

        private void ConvertTestSchemaToChaptarr()
        {
            using var connection = OpenDatabase(BookshelfPostgresDatabaseSelector.BookshelfMainDb);
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE "Authors" ADD COLUMN "AudiobookRootFolderPath" text;
                ALTER TABLE "Authors" ADD COLUMN "EbookRootFolderPath" text;
                CREATE TABLE "Config" ("Key" text, "Value" text);
                INSERT INTO "Config" ("Key", "Value") VALUES ('BookshelfNGDatabaseMigrated', 'true');
                """;
            command.ExecuteNonQuery();
        }

        private bool DatabaseExists(string database)
        {
            using var connection = new NpgsqlConnection(_baseConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @database);";
            command.Parameters.AddWithValue("database", database);
            return (bool)command.ExecuteScalar();
        }

        private void CreateBookshelfMainDatabase()
        {
            using var connection = new NpgsqlConnection(_baseConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE DATABASE \"readarr-main\";";
            command.ExecuteNonQuery();
        }

        private void CreateChaptarrMainDatabase()
        {
            using var connection = new NpgsqlConnection(_baseConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE DATABASE \"chaptarr-main\";";
            command.ExecuteNonQuery();
        }

        private void DropBookshelfMainDatabase()
        {
            using var connection = new NpgsqlConnection(_baseConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP DATABASE IF EXISTS \"readarr-main\";";
            command.ExecuteNonQuery();
            _ownsBookshelfMain = false;
        }

        private void DropChaptarrMainDatabase()
        {
            using var connection = new NpgsqlConnection(_baseConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP DATABASE IF EXISTS \"chaptarr-main\";";
            command.ExecuteNonQuery();
            _ownsChaptarrMain = false;
        }

        private NpgsqlConnection OpenDatabase(string database)
        {
            var connectionBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
            {
                Database = database,
                Pooling = false
            };
            var connection = new NpgsqlConnection(connectionBuilder.ConnectionString);
            connection.Open();
            return connection;
        }

        private static NpgsqlConnectionStringBuilder Parse(DatabaseConnectionInfo connection)
        {
            return new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        }

    }
}

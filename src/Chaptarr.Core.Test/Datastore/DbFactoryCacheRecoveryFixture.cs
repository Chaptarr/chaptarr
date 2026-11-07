using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Exceptions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class DbFactoryCacheRecoveryFixture
    {
        private const string CachePath = "/data/cache.db";

        private InterfaceStub<IMigrationController> _migrations;
        private InterfaceStub<IDiskProvider> _disk;
        private DbFactory _factory;
        private int _remainingFailures;

        private int MigrateCalls => _migrations.Calls.Count(c => c.Name == "Migrate");
        private List<string> Deleted => _disk.Calls.Where(c => c.Name == "DeleteFile").Select(c => (string)c.Args[0]).ToList();

        [SetUp]
        public void Setup()
        {
            _migrations = InterfaceStub<IMigrationController>.Create(out var migrations);
            _disk = InterfaceStub<IDiskProvider>.Create(out var disk);
            var connections = InterfaceStub<IConnectionStringFactory>.Create(out var connectionsInstance);
            InterfaceStub<IRestoreDatabase>.Create(out var restore);

            var info = new DatabaseConnectionInfo(DatabaseType.SQLite, "Data Source=" + CachePath);
            connections.Values["CacheDbConnection"] = info;
            connections.Values["MainDbConnection"] = info;
            connections.Values["GetDatabasePath"] = CachePath;

            _migrations.Handlers["Migrate"] = _ =>
            {
                if (_remainingFailures-- > 0)
                {
                    throw new InvalidOperationException("migration failed");
                }
            };

            _factory = new DbFactory(migrations, connectionsInstance, disk, restore);
        }

        [Test]
        public void should_recreate_cache_database_when_migration_fails_once()
        {
            _remainingFailures = 1;

            Assert.DoesNotThrow(() => _factory.Create(MigrationType.Cache));

            Assert.That(MigrateCalls, Is.EqualTo(2));
            Assert.That(Deleted, Is.SupersetOf(new[] { CachePath, CachePath + "-wal", CachePath + "-shm" }));
        }

        [Test]
        public void should_not_touch_cache_database_when_migration_succeeds()
        {
            _factory.Create(MigrationType.Cache);

            Assert.That(MigrateCalls, Is.EqualTo(1));
            Assert.That(Deleted, Is.Empty);
        }

        [Test]
        public void should_throw_when_cache_migration_fails_after_recreation()
        {
            _remainingFailures = 2;

            Assert.Throws<ChaptarrStartupException>(() => _factory.Create(MigrationType.Cache));

            Assert.That(MigrateCalls, Is.EqualTo(2));
        }

        [Test]
        public void should_not_delete_main_database_when_its_migration_fails()
        {
            _remainingFailures = 1;

            Assert.Throws<ChaptarrStartupException>(() => _factory.Create(MigrationType.Main));

            Assert.That(Deleted, Is.Empty);
        }
    }
}

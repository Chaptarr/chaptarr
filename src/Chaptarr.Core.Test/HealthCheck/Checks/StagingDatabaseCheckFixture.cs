using System;
using System.Collections.Generic;
using System.IO;
using Chaptarr.Core.Test.Common;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using HealthResult = NzbDrone.Core.HealthCheck.HealthCheck;

namespace Chaptarr.Core.Test.HealthCheck.Checks
{
    [TestFixture]
    public class StagingDatabaseCheckFixture
    {
        private string _folder;
        private StagingDbContext _staging;
        private ManualTimeProvider _clock;
        private HealthCheckService _health;
        private readonly List<IEvent> _events = new List<IEvent>();
        private int _stateReads;

        [SetUp]
        public void Setup()
        {
            _folder = Path.Combine(Path.GetTempPath(), "chaptarr-staging-health-" + Guid.NewGuid().ToString("N"));
            var logger = LogManager.GetCurrentClassLogger();
            _clock = new ManualTimeProvider();
            _staging = new StagingDbContext(InterfaceProxy.Proxy<IAppFolderInfo>((_, _) => _folder), logger, _clock);
            var state = InterfaceProxy.Proxy<IStagingDbContext>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo("get_InitializationError"), "Health checks must not open or initialize the database");
                _stateReads++;
                return _staging.InitializationError;
            });
            var check = new StagingDatabaseCheck(state, InterfaceProxy.Proxy<IAppFolderInfo>((_, _) => _folder), InterfaceProxy.Proxy<ILocalizationService>((_, args) => args[0] + " " + ((Dictionary<string, object>)args[1])["path"]));
            _events.Clear();
            _stateReads = 0;
            _health = new HealthCheckService(new[] { check },
                InterfaceProxy.Proxy<IServerSideNotificationService>((_, _) => new List<HealthResult>()),
                null,
                InterfaceProxy.Proxy<IEventAggregator>((_, args) =>
                {
                    _events.Add((IEvent)args[0]);
                    return null;
                }),
                new CacheManager(),
                InterfaceProxy.Proxy<IRuntimeInfo>((_, _) => DateTime.UtcNow.AddHours(-1)),
                logger);
        }

        [TearDown]
        public void Cleanup()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_folder, "staging.db"),
                Cache = SqliteCacheMode.Private,
                Pooling = true
            }.ToString());
            SqliteConnection.ClearPool(connection);
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        private void CacheStartupHealth()
        {
            _health.HandleAsync(new ApplicationStartedEvent());
            _health.HandleAsync((IEvent)new ApplicationStartedEvent());
            _events.Clear();
            _stateReads = 0;
        }

        [TestCase(CommandStatus.Completed, true)]
        [TestCase(CommandStatus.Failed, true)]
        [TestCase(CommandStatus.Cancelled, true)]
        [TestCase(CommandStatus.Completed, false)]
        [TestCase(CommandStatus.Failed, false)]
        [TestCase(CommandStatus.Cancelled, false)]
        public void command_completion_rechecks_cached_warning_against_actual_staging_recovery(CommandStatus status, bool recover)
        {
            var failure = Assert.Throws<SqliteException>(() => _staging.InitializeDatabase());
            CacheStartupHealth();
            Assert.That(_health.Results(), Has.Count.EqualTo(1));
            Assert.That(_health.Results()[0].Message, Is.EqualTo("StagingDatabaseUnavailable " + Path.Combine(_folder, "staging.db")));

            if (recover)
            {
                Directory.CreateDirectory(_folder);
                _clock.Advance(StagingDbContext.InitializationRetryInterval);
                using var connection = _staging.OpenConnection();
                Assert.That(_staging.InitializationError, Is.Null);
            }

            Assert.That(_health.Results(), Has.Count.EqualTo(1), "Recovery alone has not refreshed the cached result");
            _health.HandleAsync(new CommandExecutedEvent(new CommandModel { Status = status }));

            Assert.That(_stateReads, Is.EqualTo(1));
            Assert.That(_events, Has.Exactly(1).InstanceOf<HealthCheckCompleteEvent>());
            Assert.That(_events, Has.None.InstanceOf<HealthCheckFailedEvent>());
            if (recover)
            {
                Assert.That(_health.Results(), Is.Empty);
                _events.Clear();
                _health.HandleAsync(new CommandExecutedEvent(new CommandModel { Status = status }));
                Assert.That(_stateReads, Is.EqualTo(1), "Healthy checks must not run again for later commands");
                Assert.That(_events, Is.Empty, "Skipped checks must not trigger health refreshes");
            }
            else
            {
                Assert.That(_staging.InitializationError, Is.SameAs(failure));
                Assert.That(_health.Results(), Has.Count.EqualTo(1));
                Assert.That(_health.Results()[0].Type, Is.EqualTo(HealthCheckResult.Error));
            }
        }

        [Test]
        public void healthy_staging_is_not_rechecked_on_command_completion()
        {
            Directory.CreateDirectory(_folder);
            _staging.InitializeDatabase();
            CacheStartupHealth();

            _health.HandleAsync(new CommandExecutedEvent(new CommandModel { Status = CommandStatus.Completed }));

            Assert.That(_health.Results(), Is.Empty);
            Assert.That(_stateReads, Is.Zero);
            Assert.That(_events, Is.Empty);
        }
    }
}

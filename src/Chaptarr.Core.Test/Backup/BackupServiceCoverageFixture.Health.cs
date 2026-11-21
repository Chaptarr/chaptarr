using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Chaptarr.Api.V1.System.Backup;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Backup;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Events;
using HealthResult = NzbDrone.Core.HealthCheck.HealthCheck;

namespace Chaptarr.Core.Test.Backup
{
    public partial class BackupServiceCoverageFixture
    {
        [Test]
        public void restore_state_changes_refresh_cached_health(
            [Values(false, true)] bool postgres,
            [Values("cancel", "cancel-with-failed", "dismiss", "dismiss-with-retained", "blocked-cancel", "blocked-dismiss")] string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var folders = new AppFolders(Path.Combine(_root, "health"));
                var cancel = scenario.Contains("cancel", StringComparison.Ordinal);
                var blocked = scenario.StartsWith("blocked", StringComparison.Ordinal);
                var retainFailure = scenario is "cancel-with-failed" or "dismiss-with-retained";
                var failed = folders.GetDatabaseRestoreBundle() + ".failed." + Guid.NewGuid().ToString("N");
                if (cancel || blocked) File.WriteAllText(folders.GetDatabaseRestore(), "pending restore");
                if (!cancel || retainFailure) Directory.CreateDirectory(failed);
                if (scenario == "dismiss-with-retained") Directory.CreateDirectory(folders.GetDatabaseRestoreBundle() + ".failed.unrecognized");
                if (scenario == "blocked-cancel")
                {
                    Directory.CreateDirectory(folders.GetDatabaseRestoreBundle());
                    File.WriteAllText(Path.Combine(folders.GetDatabaseRestoreBundle(), "restore.json"), "{}");
                }

                var published = new List<IEvent>();
                HealthCheckService health = null;
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, args) =>
                {
                    var message = (IEvent)args[0];
                    published.Add(message);
                    health?.HandleAsync(message);
                    return null;
                });
                var backup = CreateService(database, folders, events: events);
                var statusReads = 0;
                var source = InterfaceProxy.Proxy<IBackupService>((method, _) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IBackupService.GetStatus)));
                    statusReads++;
                    return backup.GetStatus();
                });
                var serverChecks = 0;
                health = new HealthCheckService(
                    new[] { new BackupCheck(source, InterfaceProxy.Proxy<ILocalizationService>((_, args) => args[0])) },
                    InterfaceProxy.Proxy<IServerSideNotificationService>((_, _) => { serverChecks++; return new List<HealthResult>(); }),
                    null, events, new CacheManager(),
                    InterfaceProxy.Proxy<IRuntimeInfo>((_, _) => DateTime.UtcNow), _logger);
                health.HandleAsync(new ApplicationStartedEvent());
                Assert.That(health.Results(), Has.Count.EqualTo(1));
                var before = health.Results().Single().Message;
                Assert.That(backup.GetStatus().DatabaseBackupUnavailableReason, Is.Null);
                published.Clear();
                statusReads = 0;
                serverChecks = 0;
                var controller = new BackupController(backup, folders, _disk, null);
                TestDelegate action = () => { if (cancel) controller.CancelRestore(); else controller.DismissFailedRestores(); };

                if (blocked)
                {
                    if (cancel) Assert.Throws<JsonException>(action);
                    else Assert.Throws<NzbDrone.Core.Exceptions.NzbDroneClientException>(action);
                    Assert.That(health.Results().Single().Message, Is.EqualTo(before));
                    Assert.That(File.Exists(folders.GetDatabaseRestore()), Is.True);
                    Assert.That(published, Is.Empty);
                    Assert.That(statusReads, Is.Zero);
                }
                else
                {
                    action();
                    var actual = backup.GetStatus();
                    Assert.That(actual.PendingRestore, Is.False);
                    Assert.That(actual.RestoreFailed, Is.EqualTo(retainFailure));
                    Assert.That(health.Results().Select(result => result.Message),
                        Is.EqualTo(retainFailure ? new[] { "BackupRestoreFailedWarning" } : Array.Empty<string>()));
                    Assert.That(statusReads, Is.EqualTo(1), "Only the affected health check should reread state");
                    Assert.That(published, Has.Exactly(1).InstanceOf<HealthCheckCompleteEvent>(), "The existing health endpoint and SignalR refresh must be notified");
                    Assert.That(published, Has.None.InstanceOf<HealthCheckFailedEvent>());
                }
                Assert.That(serverChecks, Is.Zero, "Restore cleanup must not rerun remote server checks");
            });
        }
    }
}

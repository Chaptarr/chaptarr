using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Deluge;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    public partial class DelugePreservedLabelFixture
    {
        private static DelugeLabelChangeCheck LabelCheck(params Deluge[] clients) => new(
            Proxy<IDownloadClientFactory>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IDownloadClientFactory.GetAvailableProviders)));
                return clients.Cast<IDownloadClient>().ToList();
            }),
            Proxy<ILocalizationService>((method, args) => (string)args[0] == "CountMore" ? $"{((Dictionary<string, object>)args[1])["count"]} more" : "Pending label changes: {0}"));

        [TestCase("unreachable", "Deluge is not reachable")]
        [TestCase("rejected", "Deluge rejected setting the label: Label plugin refused the command")]
        [TestCase("pause", "Deluge did not confirm the pause")]
        [TestCase("label", "Deluge did not confirm the label")]
        [TestCase("seeding", "Deluge did not confirm unlimited seeding")]
        [TestCase("auto", "Deluge did not confirm auto-management")]
        [TestCase("resume", "Deluge did not confirm the resume")]
        public void pending_label_failure_should_surface_reason_and_clear_on_recovery(string reason, string expected)
        {
            var session = new Session();
            var history = new History();
            var client = Client(session, history.Repository);
            client.Definition.Name = "Deluge seedbox";
            var check = LabelCheck(client);
            var health = new HealthCheckService(new IProvideHealthCheck[] { check }, null, null,
                Proxy<IEventAggregator>((_, _) => null), new CacheManager(),
                Proxy<IRuntimeInfo>((method, _) => method.Name == "get_StartTime" ? DateTime.UtcNow : throw new AssertionException(method.Name)), Logger);
            void RefreshHealth() => health.HandleAsync(new TrackedDownloadRefreshedEvent(new List<TrackedDownload>()));
            RefreshHealth();
            Assert.That(health.Results(), Is.Empty);
            switch (reason)
            {
                case "unreachable": session.FailBefore = "label.set_torrent"; break;
                case "rejected": session.RejectCall = "label.set_torrent"; break;
                case "pause": session.IgnorePause = true; break;
                case "label": session.IgnoreLabel = true; break;
                case "seeding": session.IgnoreSeedSettings = true; break;
                case "auto": session.IgnoreAutoManaged = true; break;
                case "resume": session.IgnoreResume = true; break;
            }
            try { client.MarkItemAsImported(Item()); } catch (DownloadClientException) { }
            var beforeHealth = session.Calls.Count;
            RefreshHealth();
            var warning = health.Results().Single();
            Assert.That(warning.Type, Is.EqualTo(HealthCheckResult.Warning));
            Assert.That(warning.Message, Does.Contain("Deluge seedbox").And.Contain("Frank Herbert - Dune").And.Contain(expected));
            Assert.That(session.Calls.Count, Is.EqualTo(beforeHealth), "Checking status must not run recovery or contact Deluge");
            session.RejectCall = null; session.IgnorePause = session.IgnoreLabel = session.IgnoreSeedSettings = session.IgnoreAutoManaged = session.IgnoreResume = false;
            client.RecoverPendingOperations();
            RefreshHealth();
            Assert.That(health.Results(), Is.Empty);
            Assert.That(client.GetLabelChangeFailures(), Is.Empty);
            Assert.That(session.Paused, Is.False);
        }

        [Test]
        public void label_warning_should_cap_display_only_and_recover_every_torrent()
        {
            var sessions = Enumerable.Range(1, 7).Select(_ => new Session { IgnoreSeedSettings = true }).ToArray();
            var clients = sessions.Select((session, index) =>
            {
                var history = new History(); history.Saved.SourceTitle = "Dune recording " + (index + 1);
                var client = Client(session, history.Repository); client.Definition.Name = "Deluge " + (index + 1);
                Assert.Throws<DownloadClientException>(() => client.MarkItemAsImported(Item()));
                return client;
            }).ToArray();
            var check = LabelCheck(clients);
            var warning = check.Check();
            Assert.That(warning.Type, Is.EqualTo(HealthCheckResult.Warning));
            Assert.That(warning.Message, Does.Contain("Dune recording 5").And.Contain("2 more").And.Not.Contain("Dune recording 6"));
            foreach (var session in sessions) session.IgnoreSeedSettings = false;
            foreach (var client in clients) client.RecoverPendingOperations();
            Assert.That(check.Check().Type, Is.EqualTo(HealthCheckResult.Ok));
            Assert.That(sessions.All(session => !session.Paused), Is.True);
        }
    }
}

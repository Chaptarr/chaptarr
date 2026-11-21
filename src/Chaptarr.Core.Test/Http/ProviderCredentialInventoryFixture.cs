using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class ProviderCredentialInventoryFixture
    {
        private enum Wire { Text, Basic, Header, Body, Hashed, Smtp, PrivateUrl }
        private const string Secret = "syntheticcredential123456789";

        // Audit of the actual provider proxies, not UI field labels. Non-HTTP and
        // unresolved destinations stay explicit; this is not a universal secrecy claim.
        private static readonly Dictionary<string, (Wire Kind, string Shape)> Audit = new()
        {
            ["Aria2Settings.SecretToken"] = (Wire.Text, "<string>token:{secret}</string>"),
            ["DelugeSettings.Password"] = (Wire.Text, "auth.login(\"{secret}\")"),
            ["DownloadStationSettings.Password"] = (Wire.Text, "https://client.example/?passwd={secret}"),
            ["FloodSettings.Password"] = (Wire.Basic, ""),
            ["HadoukenSettings.Password"] = (Wire.Basic, ""),
            ["NzbVortexSettings.ApiKey"] = (Wire.Hashed, "NzbVortexProxy.AuthenticateClient hashes nonce:cnonce:ApiKey; raw key is never sent"),
            ["NzbgetSettings.Password"] = (Wire.Basic, ""),
            ["QBittorrentSettings.ApiKey"] = (Wire.Header, "Authorization"),
            ["QBittorrentSettings.Password"] = (Wire.Text, "password={secret}"),
            ["SabnzbdSettings.ApiKey"] = (Wire.Text, "https://provider.example/api?apikey={secret}"),
            ["SabnzbdSettings.Password"] = (Wire.Text, "https://sab.example/api?ma_password={secret}"),
            ["TransmissionSettings.Password"] = (Wire.Basic, ""),
            ["RTorrentSettings.Password"] = (Wire.Basic, ""),
            ["UTorrentSettings.Password"] = (Wire.Basic, ""),
            ["ChaptarrSettings.ApiKey"] = (Wire.Header, "Authorization"),
            ["HardcoverLibraryImportListSettings.ApiToken"] = (Wire.Header, "Authorization"),
            ["LazyLibrarianImportSettings.ApiKey"] = (Wire.Text, "https://provider.example/api?apikey={secret}"),
            ["FileListSettings.Passkey"] = (Wire.Basic, ""),
            ["GazelleSettings.Password"] = (Wire.Text, "password={secret}"),
            ["MyAnonaMouseSettings.MamId"] = (Wire.Text, "Cookie: mam_id={secret}"),
            ["NewznabSettings.ApiKey"] = (Wire.Text, "https://provider.example/api?apikey={secret}"),
            ["NewznabSettings.NarratorMetadataApiKey"] = (Wire.Header, "Authorization"),
            ["TorrentRssIndexerSettings.Cookie"] = (Wire.Text, "Cookie: {secret}"),
            ["TorrentleechSettings.ApiKey"] = (Wire.Text, "https://rss.torrentleech.org/{secret}"),
            ["AppriseSettings.ConfigurationKey"] = (Wire.Text, "https://apprise.example/notify/{secret}"),
            ["AppriseSettings.StatelessUrls"] = (Wire.Body, ""),
            ["AppriseSettings.AuthPassword"] = (Wire.Basic, ""),
            ["AudioBookShelfSettings.ApiKey"] = (Wire.Header, "Authorization"),
            ["DiscordSettings.WebHookUrl"] = (Wire.Text, "https://discord.com/api/webhooks/123/{secret}"),
            ["EmailSettings.Password"] = (Wire.Smtp, "Email.SendEmail authenticates MailKit; no HTTP request or credential logging"),
            ["GotifySettings.AppToken"] = (Wire.Text, "https://gotify.example/message?token={secret}"),
            ["JoinSettings.ApiKey"] = (Wire.Text, "https://provider.example/api?apikey={secret}"),
            ["KavitaSettings.ApiKey"] = (Wire.Text, "https://provider.example/api?apikey={secret}"),
            ["MailgunSettings.ApiKey"] = (Wire.Basic, ""),
            ["NotifiarrSettings.APIKey"] = (Wire.Header, "Authorization"),
            ["NtfySettings.AccessToken"] = (Wire.Header, "Authorization"),
            ["NtfySettings.Password"] = (Wire.Basic, ""),
            ["PlexServerSettings.AuthToken"] = (Wire.Text, "https://plex.example/?X-Plex-Token={secret}"),
            ["ProwlSettings.ApiKey"] = (Wire.Text, "apikey={secret}"),
            ["PushBulletSettings.ApiKey"] = (Wire.Basic, ""),
            ["PushoverSettings.ApiKey"] = (Wire.Text, "token={secret}"),
            ["SendGridSettings.ApiKey"] = (Wire.Header, "Authorization"),
            ["SignalSettings.SenderNumber"] = (Wire.Body, ""),
            ["SignalSettings.AuthPassword"] = (Wire.Basic, ""),
            ["SimplepushSettings.Key"] = (Wire.Text, "key={secret}"),
            ["SlackSettings.WebHookUrl"] = (Wire.Text, "https://hooks.slack.com/services/T/B/{secret}"),
            ["TelegramSettings.BotToken"] = (Wire.Text, "https://api.telegram.org/bot123:{secret}/sendMessage"),
            ["TwitterSettings.ConsumerKey"] = (Wire.Header, "Authorization"),
            ["TwitterSettings.ConsumerSecret"] = (Wire.Header, "Authorization"),
            ["TwitterSettings.AccessToken"] = (Wire.Header, "Authorization"),
            ["TwitterSettings.AccessTokenSecret"] = (Wire.Header, "Authorization"),
            ["WebhookSettings.Url"] = (Wire.PrivateUrl, "PrivateNotificationLoggingFixture exercises the real proxy, shared HTTP logging and error replies"),
            ["WebhookSettings.Password"] = (Wire.Basic, ""),
        };

        [Test]
        public void every_private_provider_field_requires_an_explicit_wire_audit()
        {
            var fields = typeof(IProviderConfig).Assembly.GetTypes()
                .Where(t => typeof(IProviderConfig).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                .Where(p => p.GetCustomAttribute<FieldDefinitionAttribute>()?.Privacy is PrivacyLevel.ApiKey or PrivacyLevel.Password)
                .Select(p => p.DeclaringType.Name + "." + p.Name).Distinct().ToArray();
            Assert.That(Audit.Keys, Is.EquivalentTo(fields),
                "A private provider setting changed. Audit its real proxy, logged wire shapes, and failure responses; add a regression, not a UI-label guess.");
        }

        private static IEnumerable<TestCaseData> LoggedShapes() => Audit
            .Where(pair => pair.Value.Kind is Wire.Text or Wire.Basic or Wire.Header or Wire.Body or Wire.PrivateUrl)
            .Select(pair => new TestCaseData(pair.Key).SetName("audited_provider_wire_" + pair.Key));

        [TestCaseSource(nameof(LoggedShapes))]
        public void audited_http_log_shapes_do_not_expose_credentials(string field)
        {
            var entry = Audit[field];
            var request = new HttpRequest("https://provider.example/api");
            if (entry.Kind == Wire.Text)
            {
                var wire = entry.Shape.Replace("{secret}", Secret);
                Assert.That(wire, Does.Contain(Secret));
                Assert.That(CleanseLogMessage.Cleanse(wire), Does.Not.Contain(Secret));
                return;
            }

            if (entry.Kind == Wire.Basic) request.Credentials = new BasicNetworkCredential("user", Secret);
            if (entry.Kind == Wire.Header) request.Headers[entry.Shape] = "Bearer " + Secret;
            if (entry.Kind == Wire.Body) request.SetContent("{\"value\":\"" + Secret + "\"}");
            if (entry.Kind == Wire.PrivateUrl)
            {
                request.Url = new HttpUri("https://provider.example/" + Secret);
                request.RedactUrlInLogs = true;
            }
            // These proxies do not add a ContentSummary. Their raw header/body/basic
            // credentials must remain absent from the shared request log formatter.
            Assert.That(request.ToString(), Does.Not.Contain(Secret));
        }
    }
}

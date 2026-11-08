using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Common.Instrumentation
{
    public static class CleanseLogMessage
    {
        // A literal '<' may belong to a credential; only actual markup ends the value.
        private const string MarkupBoundary = @"<(?:/?[\p{L}_:][^<>]*>|[!?])";
        private const string SecretValue =
            @"(?:(?:""(?<secret>(?:\\.|[^""\\])*)""|'(?<secret>(?:\\.|[^'\\])*)')(?=[&;=<>\s,\]}]|/?>|[\w.-]+=|$)|" +
            @"(?<secret>(?:(?!" + MarkupBoundary + @")[^&;=\s])+?)(?=[""']?(?:/?>|[&;=\s]|$)|" + MarkupBoundary + @"|[""'][,\]}]|[""'](?=[\w.-]+=)|\]\]>))";

        // JSON/RPC strings escape quotes with a backslash, unlike connection strings.
        private const string DoubleQuotedSecretValue = @"""(?<secret>(?:\\.|[^""\\])+)""";

        private static readonly Regex[] CleansingRules =
        {
            // Url
            new (@"(?<=^|\s|\?|&|;|: |[""'])(?:(?:(?:(?:api|auth|pass)[-_]?)?key|rss[-_]?key|(?:access[-_]?)?token|auth|user|uid|api|[a-z_]*apikey|account|passwd|pwd|_sid|sessionid|mam_id|torrent_pass)=|feed=torrents_notify_)" + SecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"\b(?:authorization_code|code_verifier|code|client_secret|id_token|refresh_token|secret)=" + SecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"(?<=\?|&)[^&;=<>\s]*?(username|password)=" + SecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),
            // Session identifiers are exact keys: "inside" and "residual" are metadata.
            new (@"""(?:sid|sessionid|mam_id)""\s*:\s*" + DoubleQuotedSecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),
            // Email local parts may contain Unicode combining marks or form-encoded @.
            new (@"(?<![\p{L}\p{M}\p{N}._%+-])(?<secret>[\p{L}\p{M}\p{N}._%+-]+)(?:@|%40)[\p{L}\p{M}\p{N}.-]+\.[\p{L}\p{M}]{2,}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            // URL userinfo (http(s)://user:pass@host)
            new (@"(?<=://)(?<secret>[^/@\s]+:[^@/\s]+)(?=@)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            // HTTP headers that sometimes get embedded into logs/exception messages
            new (@"\bAuthorization:\s*(?<secret>[^\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"\bProxy-Authorization:\s*(?<secret>[^\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"\b(?:X[-_]?)?API[-_]Key:\s*(?<secret>[^\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"\bCookie:\s*(?<secret>[^\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"\bSet-Cookie:\s*(?<secret>[^\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"""[^""]*(?:api[-_]?key|passkey|authkey|rss[-_]?key|torrent_pass|token|secret|password|passwd|pwd|authorization|cookie)[^""]*""\s*:\s*" + DoubleQuotedSecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"(?<=\?|&)(X-Plex-Token|X-Plex-Client-Identifier)=" + SecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),
            // Connection strings (e.g. Host=...;Username=...;Password=...;)
            new (@"(?<=(?<![?&]|&amp;)\b(?:password|pwd)\s*=\s*)(?:""(?<secret>(?:""""|[^""])*)""|'(?<secret>(?:''|[^'])*)'|(?<secret>[^;\r\n]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"rss(24h)?\.torrentleech\.org/(?!rss)(?<secret>[0-9a-z]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"torrentleech\.org/rss/download/[0-9]+/(?<secret>[0-9a-z]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"iptorrents\.com/[/a-z0-9?&;]*?(?:[?&;](u|tp)=(?<secret>[^&=;]+?))+(?= |;|&|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"/fetch/[a-z0-9]{32}/(?<secret>[a-z0-9]{32})", RegexOptions.Compiled),
            new (@"(?:getnzb|rss)[^\s""'<>]*?(?<=\?|&|;)r=" + SecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"\b(\w*)?(_?(?<!use|get_)token|username|passwo?rd)=" + SecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"-hd.me/torrent/[a-z0-9-]\.[0-9]+\.(?<secret>[0-9a-z]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),

            // Aria2 authenticates in a traced XML-RPC string, not a named query field.
            new (@"<string>token:(?<secret>[^<]+)</string>", RegexOptions.Compiled | RegexOptions.IgnoreCase),

            // Trackers Announce Keys; Designed for Qbit Json; should work for all in theory
            new (@"announce(\.php)?(/|%2f|%3fpasskey%3d)(?<secret>[a-z0-9]{16,})|(?<secret>[a-z0-9]{16,})(/|%2f)announce", RegexOptions.Compiled | RegexOptions.IgnoreCase),

            // Path
            new (@"C:\\Users\\(?<secret>[^\""]+?)(\\|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"/(home|Users)/(?<secret>[^/""]+?)(/|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),

            // NzbGet
            new (@"""Name""\s*:\s*""[^""]*(username|password)""\s*,\s*""Value""\s*:\s*" + DoubleQuotedSecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),

            // Sabnzbd
            new (@"""[^""]*(username|password|api_?key|nzb_key)""\s*:\s*" + DoubleQuotedSecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"""email_(account|to|from|pwd)""\s*:\s*" + DoubleQuotedSecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),

            // uTorrent
            new (@"\[""[a-z._]*(username|password)"",\d," + DoubleQuotedSecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"\[""(boss_key|boss_key_salt|proxy\.proxy)"",\d," + DoubleQuotedSecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),

            // Deluge
            new (@"auth.login\(" + DoubleQuotedSecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),

            // BroadcastheNet
            new (@"""?method""?\s*:\s*""(getTorrents)"",\s*""?params""?\s*:\s*\[\s*" + DoubleQuotedSecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new (@"getTorrents\(" + DoubleQuotedSecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),
            // Good Reads
            new (@"(?<=""(token|tokensecret)"":\s)" + DoubleQuotedSecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase),

            // Webhooks
            // Slack incoming webhook path is a credential, including on GovSlack.
            new (@"https?://hooks\.(?:slack\.com|slack-gov\.com)/services/(?<secret>[^?\s""'<>]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            // Apprise's supported persistent-configuration endpoint also uses a path key.
            new (@"https?://(?:(?!://)[^\s""'<>?])+/notify/(?<secret>[^/?\s""'<>]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),

            // Discord
            new (@"(?:discord|discordapp)\.com/api/webhooks/((?<secret>[\w-]+)/)?(?<secret>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),

            // Telegram
            new (@"api.telegram.org/bot(?<id>[\d]+):(?<secret>[\w-]+)/", RegexOptions.Compiled | RegexOptions.IgnoreCase)
        };

        // Scope short credentials to a Subsonic URL, then consume each query value once.
        private static readonly Regex SubsonicUrlRegex = new (
            @"/rest/(?:(?!/rest/)[^?<>\s""'])+\?(?:[^<>\s""']|(?<==)""(?:\\.|[^""\\])*""|(?<==)'(?:\\.|[^'\\])*')*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SubsonicCredentialRegex = new (
            @"(?<=\?|&|&amp;)(?:u|p)=" + SecretValue, RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex DownloadUrlFieldRegex = new (
            @"(?<prefix>""(?:dl|dl[-_]?link|download[-_]?(?:urls?|links?))""\s*:\s*)(?<value>""(?:\\.|[^""\\])*""|\[(?:\s*""(?:\\.|[^""\\])*""\s*,?\s*)*\])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex JsonArrayStringRegex = new (@"""(?:\\.|[^""\\])*""", RegexOptions.Compiled);

        private static readonly Regex CleanseRemoteIPRegex = new(@"(?:Auth-\w+(?<!Failure|Unauthorized) ip|from) (\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})", RegexOptions.Compiled);

        public static string Cleanse(string message)
        {
            if (message.IsNullOrWhiteSpace())
            {
                return message;
            }

            message = SubsonicUrlRegex.Replace(message, match => SubsonicCredentialRegex.Replace(match.Value, CleanseCaptures));

            foreach (var regex in CleansingRules)
            {
                message = regex.Replace(message, CleanseCaptures);
            }

            // Download URL fields may carry credentials in provider-specific path segments.
            message = DownloadUrlFieldRegex.Replace(message, match =>
            {
                var prefix = match.Groups["prefix"].Value;
                var value = match.Groups["value"].Value;
                var cleansedValue = value.StartsWith("[", System.StringComparison.Ordinal)
                    ? JsonArrayStringRegex.Replace(value, "\"(removed)\"")
                    : "\"(removed)\"";

                return prefix + cleansedValue;
            });

            message = CleanseRemoteIPRegex.Replace(message, CleanseRemoteIP);

            return message;
        }

        private static string CleanseCaptures(Match match)
        {
            var value = match.Value;
            foreach (var capture in match.Groups["secret"].Captures.OfType<Capture>().Reverse())
            {
                value = value.Replace(capture.Index - match.Index, capture.Length, "(removed)");
            }

            return value;
        }

        public static string Cleanse(string message, IEnumerable<string> privateValues)
        {
            if (string.IsNullOrEmpty(message))
            {
                return message;
            }

            // Caller-supplied secrets supplement the shared rules; no global secret cache.
            foreach (var value in privateValues.Where(value => !string.IsNullOrEmpty(value))
                .SelectMany(value => new[] { value, System.Text.Json.JsonSerializer.Serialize(value)[1..^1] })
                .Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length))
            {
                message = message.Replace(value, "(removed)", StringComparison.Ordinal);
            }

            return Cleanse(message);
        }

        private static string CleanseRemoteIP(Match match)
        {
            var group = match.Groups[1];
            var valueIP = group.Value;

            if (IPAddress.TryParse(valueIP, out var address) && !address.IsLocalAddress())
            {
                var prefix = match.Value.Substring(0, group.Index - match.Index);
                var postfix = match.Value.Substring(group.Index + group.Length - match.Index);
                var items = valueIP.Split('.');

                return $"{prefix}{items[0]}.*.*.{items[3]}{postfix}";
            }

            return match.Value;
        }
    }
}

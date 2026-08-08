using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class MyAnonaMouseJsonParserDurationFixture
    {
        [Test]
        public void should_extract_prefixed_duration_and_ignore_reseed_instruction()
        {
            var torrent = ParseSingleTorrent(new JObject
            {
                ["id"] = 70290,
                ["title"] = "House Harkonnen",
                ["dl"] = "hash",
                ["size"] = "759.8 MiB",
                ["seeders"] = 23,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["author_info"] = JsonConvert.SerializeObject(new Dictionary<string, string>
                {
                    { "1", "Brian Herbert" },
                    { "2", "Kevin J Anderson" }
                }),
                ["tags"] = "Unabridged - FAAC/m4b (iTunes-ready) 64 kbs, 44.1 kHz",
                ["description"] = @"This unabridged version of Dune: House Harkonnen is read by Scott Brick.

Title: Dune: House Harkonnen
Authors: Brian Herbert & Kevin J. Anderson
Narrator: Scott Brick
Duration: 26 Hrs, 23 Mins
Format: FAAC/m4b from CD
Please reseed for at least 48 hours.",
                ["category"] = 39,
                ["main_cat"] = "13",
                ["mediatype"] = 1,
                ["catname"] = "Audiobooks - Sci-Fi"
            });

            Assert.That(torrent.Duration, Is.EqualTo("26h 23m"));
        }

        [Test]
        public void should_not_infer_duration_from_reseed_note_alone_in_description()
        {
            var torrent = ParseSingleTorrent(new JObject
            {
                ["id"] = 70291,
                ["title"] = "House Harkonnen",
                ["dl"] = "hash",
                ["size"] = "759.8 MiB",
                ["seeders"] = 23,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["author_info"] = JsonConvert.SerializeObject(new Dictionary<string, string>
                {
                    { "1", "Brian Herbert" },
                    { "2", "Kevin J Anderson" }
                }),
                ["description"] = "Please reseed for at least 48 hours.",
                ["category"] = 39,
                ["main_cat"] = "13",
                ["mediatype"] = 1,
                ["catname"] = "Audiobooks - Sci-Fi"
            });

            Assert.That(torrent.Duration, Is.Null);
        }

        [Test]
        public void vip_user_should_treat_mam_vip_torrent_as_vip_only_freeleech_without_wedge()
        {
            var torrent = ParseSingleTorrent(new JObject
            {
                ["id"] = 70292,
                ["title"] = "VIP Audiobook",
                ["dl"] = "hash",
                ["size"] = "759.8 MiB",
                ["seeders"] = 23,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["filetype"] = "m4b",
                ["vip"] = true,
                ["category"] = 39,
                ["main_cat"] = 13,
                ["mediatype"] = 1,
                ["catname"] = "Audiobooks - Sci-Fi"
            }, new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net",
                IsVip = true,
                UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred,
                UseFreeleechOnlyForAudiobooks = true
            });

            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.VipExclusive), Is.True);
            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.Freeleech), Is.True);
            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.VipFreeleech), Is.True);
            Assert.That(torrent.DownloadUrl, Does.Not.Contain("canUseToken=true"));
        }

        [Test]
        public void vip_user_should_parse_documented_string_flags_for_mam_vip_torrent()
        {
            var torrent = ParseSingleTorrent(new JObject
            {
                ["id"] = 70297,
                ["title"] = "VIP Audiobook",
                ["dl"] = "hash",
                ["size"] = "759.8 MiB",
                ["seeders"] = 23,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["filetype"] = "m4b",
                ["free"] = "0",
                ["personal_freeleech"] = "0",
                ["fl_vip"] = "1",
                ["vip"] = "1",
                ["downloadvolumefactor"] = "1.0",
                ["category"] = 39,
                ["main_cat"] = 13,
                ["mediatype"] = 1,
                ["catname"] = "Audiobooks - Sci-Fi"
            }, new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net",
                IsVip = true,
                UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred,
                UseFreeleechOnlyForAudiobooks = true
            });

            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.VipExclusive), Is.True);
            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.Freeleech), Is.True);
            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.VipFreeleech), Is.True);
            Assert.That(torrent.DownloadUrl, Does.Not.Contain("canUseToken=true"));
        }

        [Test]
        public void non_vip_user_should_treat_mam_vip_torrent_as_vip_only_not_freeleech_and_not_offer_wedge()
        {
            var torrent = ParseSingleTorrent(new JObject
            {
                ["id"] = 70294,
                ["title"] = "VIP Audiobook",
                ["dl"] = "hash",
                ["size"] = "759.8 MiB",
                ["seeders"] = 23,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["filetype"] = "m4b",
                ["vip"] = true,
                ["category"] = 39,
                ["main_cat"] = 13,
                ["mediatype"] = 1,
                ["catname"] = "Audiobooks - Sci-Fi"
            }, new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net",
                IsVip = false,
                UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred,
                UseFreeleechOnlyForAudiobooks = true
            });

            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.VipExclusive), Is.True);
            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.Freeleech), Is.False);
            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.VipFreeleech), Is.False);
            Assert.That(torrent.DownloadUrl, Does.Not.Contain("canUseToken=true"));
        }

        [Test]
        public void should_mark_non_free_audiobook_as_wedge_eligible()
        {
            var torrent = ParseSingleTorrent(new JObject
            {
                ["id"] = 70293,
                ["title"] = "Regular Audiobook",
                ["dl"] = "hash",
                ["size"] = "759.8 MiB",
                ["seeders"] = 23,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["filetype"] = "m4b",
                ["category"] = 39,
                ["main_cat"] = "13",
                ["mediatype"] = 1,
                ["catname"] = "Audiobooks - Sci-Fi"
            }, new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net",
                IsVip = true,
                UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred,
                UseFreeleechOnlyForAudiobooks = true
            });

            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.Freeleech), Is.False);
            Assert.That(torrent.MainCategory, Is.EqualTo(13));
            Assert.That(torrent.DownloadUrl, Does.Contain("canUseToken=true"));
            Assert.That(torrent.DownloadUrl, Does.Contain("isAudiobook=true"));
        }

        [Test]
        public void should_mark_non_free_ebook_as_eligible_without_calling_it_an_audiobook()
        {
            var torrent = ParseSingleTorrent(new JObject
            {
                ["id"] = 70301,
                ["title"] = "Regular Ebook",
                ["dl"] = "hash",
                ["size"] = "759.8 MiB",
                ["seeders"] = 23,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["filetype"] = "epub",
                ["category"] = 39,
                ["main_cat"] = 14,
                ["mediatype"] = 2,
                ["catname"] = "E-Books - Sci-Fi"
            }, new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net",
                UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred,
                UseFreeleechOnlyForAudiobooks = true
            });

            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.Freeleech), Is.False);
            Assert.That(torrent.DownloadUrl, Does.Contain("canUseToken=true"));
            Assert.That(torrent.DownloadUrl, Does.Not.Contain("isAudiobook=true"));
        }

        [Test]
        public void should_keep_wedge_eligibility_independent_from_current_policy()
        {
            var torrent = ParseSingleTorrent(new JObject
            {
                ["id"] = 70302,
                ["title"] = "Regular Ebook",
                ["dl"] = "hash",
                ["size"] = "759.8 MiB",
                ["seeders"] = 23,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["filetype"] = "epub",
                ["category"] = 39,
                ["main_cat"] = 14,
                ["mediatype"] = 2,
                ["catname"] = "E-Books - Sci-Fi"
            }, new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net",
                UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred,
                UseFreeleechOnlyForAudiobooks = false
            });

            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.Freeleech), Is.False);
            Assert.That(torrent.DownloadUrl, Does.Contain("canUseToken=true"));
            Assert.That(torrent.DownloadUrl, Does.Not.Contain("isAudiobook=true"));
        }

        [Test]
        public void should_parse_without_requesting_user_specific_download_hash()
        {
            var torrent = ParseSingleTorrent(new JObject
            {
                ["id"] = 70303,
                ["title"] = "No Download Hash Required",
                ["size"] = "42 MiB",
                ["seeders"] = 7,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["filetype"] = "m4b",
                ["category"] = 39,
                ["main_cat"] = 13,
                ["catname"] = "Audiobooks"
            });

            Assert.That(torrent.DownloadUrl, Does.Contain("tid=70303"));
        }

        [Test]
        public void should_prefer_structured_media_info_for_duration_and_codec()
        {
            var torrent = ParseSingleTorrent(new JObject
            {
                ["id"] = 70304,
                ["title"] = "Structured Media Info",
                ["size"] = "42 MiB",
                ["seeders"] = 7,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["description"] = "Duration: 1 Hr, 10 Mins",
                ["mediainfo"] = JsonConvert.SerializeObject(new
                {
                    General = new { Duration = "11:22:58" },
                    Audio1 = new { Format = "AAC", BitRate = "63k", Channels = 2 }
                }),
                ["category"] = 39,
                ["main_cat"] = 13,
                ["catname"] = "Audiobooks"
            });

            Assert.That(torrent.Duration, Is.EqualTo("11h 22m"));
            Assert.That(torrent.FileType, Is.EqualTo("AAC"));
            Assert.That(torrent.Codec, Is.EqualTo("AAC"));
        }

        [Test]
        public void should_parse_mam_language_code_into_release_languages()
        {
            var torrents = ParseTorrents(new JObject
            {
                ["id"] = 70296,
                ["title"] = "Гарри Поттер и философский камень",
                ["dl"] = "hash",
                ["size"] = "42 MiB",
                ["seeders"] = 7,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["lang_code"] = "RUS",
                ["category"] = 39,
                ["main_cat"] = 13,
                ["catname"] = "Audiobooks - Sci-Fi"
            }, new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net"
            });

            Assert.That(torrents, Has.Count.EqualTo(1));
            Assert.That(torrents[0].Title, Is.EqualTo("Гарри Поттер и философский камень"));
            Assert.That(torrents[0].Languages, Is.EquivalentTo(new[] { Language.Russian }));
        }

        [Test]
        public void should_not_filter_language_in_parser()
        {
            var torrents = ParseTorrents(new JObject
            {
                ["id"] = 70297,
                ["title"] = "Гарри Поттер и философский камень",
                ["dl"] = "hash",
                ["size"] = "42 MiB",
                ["seeders"] = 7,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["lang_code"] = "RUS",
                ["category"] = 39,
                ["main_cat"] = 13,
                ["catname"] = "Audiobooks - Sci-Fi"
            }, new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net"
            });

            Assert.That(torrents, Has.Count.EqualTo(1));
            Assert.That(torrents[0].Languages, Is.EquivalentTo(new[] { Language.Russian }));
        }

        [Test]
        public void should_allow_missing_mam_language_code()
        {
            var torrents = ParseTorrents(new JObject
            {
                ["id"] = 70298,
                ["title"] = "Language Not Provided",
                ["dl"] = "hash",
                ["size"] = "42 MiB",
                ["seeders"] = 7,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["category"] = 39,
                ["main_cat"] = 13,
                ["catname"] = "Audiobooks - Sci-Fi"
            }, new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net"
            });

            Assert.That(torrents, Has.Count.EqualTo(1));
            Assert.That(torrents[0].Languages, Is.Empty);
        }

        [Test]
        public void should_parse_current_mam_name_field_when_title_is_absent()
        {
            var torrent = ParseSingleTorrent(new JObject
            {
                ["id"] = 70295,
                ["name"] = "Current API Title Field",
                ["dl"] = "hash",
                ["size"] = "42 MiB",
                ["seeders"] = 7,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["free"] = true,
                ["category"] = 39,
                ["main_cat"] = 13,
                ["catname"] = "Audiobooks - Sci-Fi"
            });

            Assert.That(torrent.Title, Is.EqualTo("Current API Title Field"));
            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.Freeleech), Is.True);
        }

        [Test]
        public void should_parse_documented_filetypes_field_when_filetype_is_absent()
        {
            var torrent = ParseSingleTorrent(new JObject
            {
                ["id"] = 70299,
                ["name"] = "Documented Filetypes",
                ["dl"] = "hash",
                ["size"] = "42 MiB",
                ["seeders"] = 7,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["filetypes"] = "m4b",
                ["category"] = 39,
                ["main_cat"] = 13,
                ["catname"] = "Audiobooks - Sci-Fi"
            });

            Assert.That(torrent.FileType, Is.EqualTo("m4b"));
        }

        [Test]
        public void should_not_treat_fl_vip_alone_as_vip_exclusive()
        {
            var torrent = ParseSingleTorrent(new JObject
            {
                ["id"] = 70300,
                ["title"] = "Combined Display Flag",
                ["dl"] = "hash",
                ["size"] = "42 MiB",
                ["seeders"] = 7,
                ["leechers"] = 0,
                ["added"] = "2026-04-12 01:07:14",
                ["fl_vip"] = true,
                ["category"] = 39,
                ["main_cat"] = 13,
                ["catname"] = "Audiobooks - Sci-Fi"
            });

            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.Freeleech), Is.True);
            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.VipExclusive), Is.False);
            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.VipFreeleech), Is.False);
        }

        private static List<TorrentInfo> ParseTorrents(JObject torrent, MyAnonaMouseSettings settings = null)
        {
            settings ??= new MyAnonaMouseSettings
            {
                BaseUrl = "https://www.myanonamouse.net"
            };

            var payload = new JObject
            {
                ["data"] = new JArray { torrent }
            };

            var request = new HttpRequest("https://example.com/api");
            var indexerRequest = new IndexerRequest(request);
            var headers = new HttpHeader { ContentType = "application/json" };
            var response = new HttpResponse(request, headers, payload.ToString(Formatting.None), HttpStatusCode.OK);
            var indexerResponse = new IndexerResponse(indexerRequest, response);

            var parser = new MyAnonaMouseJsonParser(settings);
            return parser.ParseResponse(indexerResponse).OfType<TorrentInfo>().ToList();
        }

        private static TorrentInfo ParseSingleTorrent(JObject torrent, MyAnonaMouseSettings settings = null)
        {
            return ParseTorrents(torrent, settings).Single();
        }
    }
}

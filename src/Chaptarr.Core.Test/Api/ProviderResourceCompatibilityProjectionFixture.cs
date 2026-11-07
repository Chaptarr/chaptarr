using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Api.V1.DownloadClient;
using Chaptarr.Api.V1.ImportLists;
using Chaptarr.Api.V1;
using Chaptarr.Api.V1.Metadata;
using Chaptarr.Http.ClientSchema;
using NzbDrone.Core.Indexers.DirectDownload;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Notifications.Plex.Server;
using NzbDrone.Core.ThingiProvider;
using System.Text.Json;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class ProviderResourceCompatibilityProjectionFixture
    {
        [TestCase(0, "Nzb.su", "https://api.nzb.life", "https://api.nzb.su")]
        [TestCase(7, "Nzb.su", "https://api.nzb.life", "https://api.nzb.life")]
        [TestCase(0, "Custom", "https://api.nzb.life", "https://api.nzb.life")]
        [TestCase(0, "Nzb.su", "https://other.example", "https://other.example")]
        public void should_preserve_only_legacy_nzbsu_template_endpoint(int id, string name, string url, string expected)
        {
            var resource = new IndexerResource { Id = id, Name = name, ConfigContract = "NewznabSettings", Fields = new List<Field> { new Field { Name = "baseUrl", Value = url } } };
            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyTemplateDefaults(resource);
            Assert.That(resource.Fields.Single().Value, Is.EqualTo(expected));
        }

        [TestCase("DelugeSettings", "deluge")]
        [TestCase("NzbgetSettings", "tegbzn6789")]
        public void legacy_templates_should_retain_public_source_defaults_without_touching_saved_credentials(string contract, string expected)
        {
            var resource = new DownloadClientResource
            {
                ConfigContract = contract,
                Fields = new List<Field> { new() { Name = "password", Value = string.Empty } }
            };
            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyTemplateDefaults(resource);
            Assert.That(resource.Fields.Single().Value, Is.EqualTo(expected));

            resource.Id = 42;
            resource.Fields.Single().Value = "existing-private-value";
            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyTemplateDefaults(resource);
            Assert.That(resource.Fields.Single().Value, Is.EqualTo("existing-private-value"));
            Assert.That(new NzbDrone.Core.Download.Clients.Deluge.DelugeSettings().Password, Is.Null.Or.Empty);
            Assert.That(new NzbDrone.Core.Download.Clients.Nzbget.NzbgetSettings().Password, Is.Null.Or.Empty);
        }

        [TestCase("TorrentDownloadStation")]
        [TestCase("UsenetDownloadStation")]
        public void legacy_download_station_template_should_preserve_its_name_and_clone_shared_messages(string implementation)
        {
            var message = new ProviderMessage("Chaptarr native message", ProviderMessageType.Warning);
            var resource = new DownloadClientResource
            {
                Implementation = implementation,
                ImplementationName = "Download Station (native protocol label)",
                Message = message
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyTemplateDefaults(resource);

            Assert.That(resource.ImplementationName, Is.EqualTo("Download Station"));
            Assert.That(resource.Message.Message, Is.EqualTo("Readarr is unable to connect to Download Station if 2-Factor Authentication is enabled on your DSM account"));
            Assert.That(message.Message, Is.EqualTo("Chaptarr native message"));
            Assert.That(resource.Message, Is.Not.SameAs(message));
        }

        [Test]
        public void legacy_plex_template_should_preserve_source_defaults_and_capabilities()
        {
            var resource = new Chaptarr.Api.V1.Notifications.NotificationResource
            {
                ConfigContract = "PlexServerSettings",
                SupportsOnBookFileDelete = true,
                SupportsOnBookFileDeleteForUpgrade = true,
                Fields = new List<Field> { new() { Name = "useSsl", Value = true } }
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyTemplateDefaults(resource);

            Assert.That(resource.Fields.Single().Value, Is.EqualTo(false));
            Assert.That(resource.SupportsOnBookFileDelete, Is.False);
            Assert.That(resource.SupportsOnBookFileDeleteForUpgrade, Is.False);
            Assert.That(new PlexServerSettings().UseSsl, Is.True);
        }

        [TestCase("Flood", "Readarr will handle automatic removal of torrents based on the current seed criteria in Settings -> Indexers")]
        [TestCase("RTorrent", "rTorrent will not pause torrents when they meet the seed criteria. Readarr will handle automatic removal of torrents based on the current seed criteria in Settings->Indexers only when Remove Completed is enabled.  After importing it will also set \"readarr_imported\" as an rTorrent view, which can be used in rTorrent scripts to customize behavior.")]
        public void legacy_template_info_should_preserve_the_predecessor_contract_without_rewriting_errors(string implementation, string expected)
        {
            var resource = new DownloadClientResource
            {
                Implementation = implementation,
                Message = new ProviderMessage("Native information", ProviderMessageType.Info)
            };
            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyTemplateDefaults(resource);
            Assert.That(resource.Message.Message, Is.EqualTo(expected));

            resource.Message = new ProviderMessage("Opaque provider failure", ProviderMessageType.Error);
            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyTemplateDefaults(resource);
            Assert.That(resource.Message.Message, Is.EqualTo("Opaque provider failure"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_apply_legacy_plex_global_switch_and_preserve_it_when_omitted(bool enabled)
        {
            var settings = new PlexServerSettings { UpdateLibrary = !enabled, AuthToken = "retained-token" };
            var resource = new Chaptarr.Api.V1.Notifications.NotificationResource
            {
                Fields = new List<Field> { new() { Name = "updateLibrary", Value = JsonSerializer.SerializeToElement(enabled) } }
            };

            ProviderResourceCompatibilityProjection.ApplyBookshelfLegacySettings(resource, settings);
            Assert.That(settings.UpdateLibrary, Is.EqualTo(enabled));
            resource.Fields.Clear();
            ProviderResourceCompatibilityProjection.ApplyBookshelfLegacySettings(resource, settings);
            Assert.That(settings.UpdateLibrary, Is.EqualTo(enabled));
            Assert.That(settings.AuthToken, Is.EqualTo("retained-token"));
        }

        [Test]
        public void should_preserve_legacy_hadouken_category_and_flood_tags_in_templates()
        {
            var hadouken = new DownloadClientResource
            {
                ConfigContract = "HadoukenSettings",
                Fields = new List<Field> { new() { Name = "category", Value = "chaptarr-music" } }
            };
            var flood = new DownloadClientResource
            {
                ConfigContract = "FloodSettings",
                Fields = new List<Field>
                {
                    new() { Name = "tags", Value = new[] { "chaptarr" } },
                    new() { Name = "audiobookTags", Value = new[] { "chaptarr" } },
                    new() { Name = "ebookTags", Value = new[] { "chaptarr" } }
                }
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyTemplateDefaults(hadouken);
            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyTemplateDefaults(flood);

            Assert.That(hadouken.Fields[0].Value, Is.EqualTo("readarr-music"));
            Assert.That(flood.Fields[0].Value, Is.EqualTo(new[] { "readarr" }));
            Assert.That(flood.Fields[1].Value, Is.Null);
            Assert.That(flood.Fields[2].Value, Is.Null);
        }

        [TestCase("DelugeSettings", "readarr")]
        [TestCase("QBittorrentSettings", "readarr")]
        [TestCase("RTorrentSettings", "readarr")]
        [TestCase("UTorrentSettings", "readarr")]
        [TestCase("NzbgetSettings", "Readarr")]
        [TestCase("NzbVortexSettings", "Readarr")]
        [TestCase("SabnzbdSettings", "Readarr")]
        public void should_preserve_legacy_download_template_categories(string contract, string expected)
        {
            var resource = new DownloadClientResource
            {
                ConfigContract = contract,
                Fields = new List<Field>
                {
                    new() { Name = "musicCategory", Value = "chaptarr" },
                    new() { Name = "audiobookCategory", Value = "chaptarr" },
                    new() { Name = "ebookCategory", Value = "chaptarr" }
                }
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyTemplateDefaults(resource);

            Assert.That(resource.Fields[0].Value, Is.EqualTo(expected));
            Assert.That(resource.Fields[1].Value, Is.Null);
            Assert.That(resource.Fields[2].Value, Is.Null);
        }

        [TestCase(0, "readarr", "readarr")]
        [TestCase(109, "operator", "audio-only")]
        public void should_roundtrip_legacy_category_defaults_and_preserve_saved_format_categories(
            int id, string expectedCategory, string expectedAudioCategory)
        {
            var settings = new QBittorrentSettings
            {
                MusicCategory = "operator",
                AudiobookCategory = "audio-only",
                EbookCategory = "ebook-only"
            };
            var resource = new DownloadClientResource
            {
                Id = id,
                ConfigContract = nameof(QBittorrentSettings),
                Fields = new List<Field>
                {
                    new() { Name = "musicCategory", Value = settings.MusicCategory },
                    new() { Name = "audiobookCategory", Value = settings.AudiobookCategory },
                    new() { Name = "ebookCategory", Value = settings.EbookCategory }
                }
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyTemplateDefaults(resource);
            var roundtrip = (QBittorrentSettings)SchemaBuilder.ReadFromSchema(resource.Fields, typeof(QBittorrentSettings));

            Assert.That(roundtrip.MusicCategory, Is.EqualTo(expectedCategory));
            Assert.That(roundtrip.AudiobookCategory, Is.EqualTo(expectedAudioCategory));
            Assert.That(roundtrip.EbookCategory, Is.EqualTo(id == 0 ? "readarr" : "ebook-only"));
            Assert.That(new QBittorrentSettings().MusicCategory, Is.EqualTo("chaptarr"));
        }

        [TestCase(0, false, 0, false)]
        [TestCase(120, true, 2, true)]
        public void should_preserve_bookshelf_mam_template_defaults_without_overwriting_saved_settings(
            int id, bool expectedEnabled, int expectedSearchType, bool expectedSeriesSearch)
        {
            var resource = new IndexerResource
            {
                Id = id,
                ConfigContract = "MyAnonaMouseSettings",
                Enable = true,
                EnableRss = true,
                EnableAutomaticSearch = true,
                EnableInteractiveSearch = true,
                Fields = new List<Field>
                {
                    new() { Name = "searchType", Value = 2 },
                    new() { Name = "searchInSeries", Value = true },
                    new() { Name = "mamId", Value = id == 0 ? null : "saved-session" },
                    new() { Name = "baseUrl", Value = "https://www.myanonamouse.net" }
                }
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyTemplateDefaults(resource);

            Assert.Multiple(() =>
            {
                Assert.That(resource.Enable, Is.EqualTo(expectedEnabled));
                Assert.That(resource.EnableRss, Is.EqualTo(expectedEnabled));
                Assert.That(resource.EnableAutomaticSearch, Is.EqualTo(expectedEnabled));
                Assert.That(resource.EnableInteractiveSearch, Is.EqualTo(expectedEnabled));
                Assert.That(resource.Fields.Single(f => f.Name == "searchType").Value, Is.EqualTo(expectedSearchType));
                Assert.That(resource.Fields.Single(f => f.Name == "searchInSeries").Value, Is.EqualTo(expectedSeriesSearch));
                Assert.That(resource.Fields.Single(f => f.Name == "mamId").Value, Is.EqualTo(id == 0 ? string.Empty : "saved-session"));
                Assert.That(resource.Fields.Single(f => f.Name == "baseUrl").Value,
                    Is.EqualTo(id == 0 ? "https://www.myanonamouse.net/" : "https://www.myanonamouse.net"));
            });
        }

        [Test]
        public void should_project_direct_download_urls_to_the_bookshelf_tag_field_shape()
        {
            var resource = new IndexerResource
            {
                ConfigContract = nameof(DirectDownloadSettings),
                Fields = new List<Field>
                {
                    new()
                    {
                        // SchemaBuilder camel-cases field names, so the runtime API uses "urls".
                        Name = "urls",
                        Type = "textArea",
                        Value = "https://primary.example\nhttps://secondary.example"
                    }
                }
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyFields(resource);

            var urls = resource.Fields.Single();
            Assert.Multiple(() =>
            {
                Assert.That(urls.Type, Is.EqualTo("tag"));
                Assert.That(urls.Value, Is.EqualTo(new[]
                {
                    "https://primary.example",
                    "https://secondary.example"
                }));
            });
        }

        [Test]
        public void should_project_chaptarr_import_provider_as_readarr_for_bookshelf_clients()
        {
            var resource = new ImportListResource
            {
                Id = 0,
                Name = "ChaptarrImport",
                Implementation = "ChaptarrImport",
                ImplementationName = "Chaptarr",
                ConfigContract = "ChaptarrSettings"
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyProviderIdentity(resource);

            Assert.Multiple(() =>
            {
                Assert.That(resource.Implementation, Is.EqualTo("ReadarrImport"));
                Assert.That(resource.ImplementationName, Is.EqualTo("Readarr"));
                Assert.That(resource.ConfigContract, Is.EqualTo("ReadarrSettings"));
                Assert.That(resource.Name, Is.EqualTo("ReadarrImport"));
            });
        }

        [Test]
        public void should_preserve_saved_import_provider_name_when_projecting_legacy_identity()
        {
            var resource = new ImportListResource
            {
                Id = 42,
                Name = "Personal book list",
                Implementation = "ChaptarrImport",
                ImplementationName = "Chaptarr",
                ConfigContract = "ChaptarrSettings"
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyProviderIdentity(resource);

            Assert.That(resource.Name, Is.EqualTo("Personal book list"));
        }

        [TestCase(NzbDrone.Core.ImportLists.ImportListType.Program, 0)]
        [TestCase(NzbDrone.Core.ImportLists.ImportListType.Goodreads, 1)]
        [TestCase(NzbDrone.Core.ImportLists.ImportListType.Hardcover, 2)]
        [TestCase(NzbDrone.Core.ImportLists.ImportListType.Other, 3)]
        public void should_keep_the_bookshelf_import_list_order_numbers(
            NzbDrone.Core.ImportLists.ImportListType listType,
            int expectedListOrder)
        {
            var resource = new ImportListResource { ListType = listType };

            Assert.That(resource.ListOrder, Is.EqualTo(expectedListOrder));
        }

        [Test]
        public void should_accept_readarr_import_identity_from_bookshelf_clients()
        {
            var resource = new ImportListResource
            {
                Implementation = "ReadarrImport",
                ImplementationName = "Readarr",
                ConfigContract = "ReadarrSettings"
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfNativeProviderIdentity(resource);

            Assert.Multiple(() =>
            {
                Assert.That(resource.Implementation, Is.EqualTo("ChaptarrImport"));
                Assert.That(resource.ImplementationName, Is.EqualTo("Chaptarr"));
                Assert.That(resource.ConfigContract, Is.EqualTo("ChaptarrSettings"));
            });
        }

        [Test]
        public void should_project_legacy_audiobookshelf_metadata_consumer_identity()
        {
            var resource = new MetadataResource
            {
                Implementation = "LegacyAudiobookshelfMetadata",
                ConfigContract = "NullConfig"
            };

            ProviderResourceCompatibilityProjection.ProjectBookshelfLegacyProviderIdentity(resource);
            Assert.That(resource.Implementation, Is.EqualTo("AudiobookshelfMetadata"));

            ProviderResourceCompatibilityProjection.ProjectBookshelfNativeProviderIdentity(resource);
            Assert.That(resource.Implementation, Is.EqualTo("LegacyAudiobookshelfMetadata"));
        }
    }
}

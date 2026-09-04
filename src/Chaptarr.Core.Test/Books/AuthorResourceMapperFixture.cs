using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Http.Middleware;
using CoreMediaCover = NzbDrone.Core.MediaCover.MediaCover;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.RootFolders;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorResourceMapperFixture
    {
        [Test]
        public void should_map_legacy_single_fields_to_the_ebook_side_for_an_ebook_root_idempotently()
        {
            var resource = new AuthorResource
            {
                AuthorName = "Ted Chiang",
                ForeignAuthorId = "gr:130698",
                QualityProfileId = 1,
                MetadataProfileId = 2,
                RootFolderPath = "/ebooks",
                Monitored = true,
                MonitorNewItems = "all",
                Tags = new HashSet<int> { 4 }
            };
            var ebookRoot = new RootFolder
            {
                Path = "/ebooks",
                FolderType = FolderType.Ebook
            };

            AuthorResourceMapper.NormalizeLegacySingleFields(resource, null, ebookRoot);
            AuthorResourceMapper.NormalizeLegacySingleFields(resource, null);

            Assert.Multiple(() =>
            {
                Assert.That(resource.EbookQualityProfileId, Is.EqualTo(1));
                Assert.That(resource.EbookMetadataProfileId, Is.EqualTo(2));
                Assert.That(resource.EbookRootFolderPath, Is.EqualTo("/ebooks"));
                Assert.That(resource.EbookMonitored, Is.True);
                Assert.That(resource.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(resource.EbookTags, Is.EquivalentTo(new[] { 4 }));
                Assert.That(resource.AudiobookQualityProfileId, Is.Null);
                Assert.That(resource.AudiobookMetadataProfileId, Is.Null);
                Assert.That(resource.AudiobookRootFolderPath, Is.Null);
                Assert.That(resource.AudiobookMonitored, Is.Null);
                Assert.That(resource.AudiobookMonitorNewItems, Is.Null);
                Assert.That(resource.AudiobookTags, Is.Null);
            });
        }

        [Test]
        public void should_map_legacy_single_fields_to_both_sides_for_a_mixed_root()
        {
            var resource = new AuthorResource
            {
                QualityProfileId = 1,
                MetadataProfileId = 2,
                RootFolderPath = "/books",
                Monitored = true
            };

            AuthorResourceMapper.NormalizeLegacySingleFields(resource, null, new RootFolder
            {
                Path = "/books",
                FolderType = FolderType.Mixed
            });

            Assert.Multiple(() =>
            {
                Assert.That(resource.AudiobookQualityProfileId, Is.EqualTo(1));
                Assert.That(resource.EbookQualityProfileId, Is.EqualTo(1));
                Assert.That(resource.AudiobookMetadataProfileId, Is.EqualTo(2));
                Assert.That(resource.EbookMetadataProfileId, Is.EqualTo(2));
                Assert.That(resource.AudiobookRootFolderPath, Is.EqualTo("/books"));
                Assert.That(resource.EbookRootFolderPath, Is.EqualTo("/books"));
            });
        }

        [Test]
        public void should_normalize_last_selected_media_type_on_update()
        {
            var author = new AuthorResource
            {
                LastSelectedMediaType = " EBOOK "
            }.ToModel();

            Assert.That(author.LastSelectedMediaType, Is.EqualTo("ebook"));
        }

        [Test]
        public void should_derive_ended_from_death_date_instead_of_status()
        {
            var datedResource = new AuthorResource
            {
                Status = AuthorStatusType.Continuing,
                Died = new System.DateTime(1996, 7, 9)
            };
            var staleStatusResource = new AuthorResource
            {
                Status = AuthorStatusType.Ended
            };

            Assert.That(datedResource.Ended, Is.True);
            Assert.That(staleStatusResource.Ended, Is.False);

            var resource = new NzbDrone.Core.Books.Author
            {
                Status = AuthorStatusType.Continuing,
                Died = new System.DateTime(1996, 7, 9)
            }.ToResource();

            Assert.That(resource.Status, Is.EqualTo(AuthorStatusType.Ended));
        }

        [Test]
        public void should_expose_per_media_author_folders_without_controller_post_processing()
        {
            var resource = new NzbDrone.Core.Books.Author
            {
                AudiobookPath = "/audiobooks/Joe Abercrombie",
                EbookPath = "/ebooks/Joe Abercrombie"
            }.ToResource();

            Assert.That(resource.AudiobookFolder, Is.EqualTo("/audiobooks/Joe Abercrombie"));
            Assert.That(resource.EbookFolder, Is.EqualTo("/ebooks/Joe Abercrombie"));
        }

        [Test]
        public void should_apply_binary_monitoring_fields_on_put_update()
        {
            var existing = new NzbDrone.Core.Books.Author
            {
                Id = 1,
                Name = "Joe Abercrombie",
                Monitored = true,
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks",
                AudiobookQualityProfileId = 2,
                EbookQualityProfileId = 3,
                AudiobookMonitored = true,
                AudiobookMonitorNewItems = NewItemMonitorTypes.New,
                EbookMonitored = true,
                EbookMonitorNewItems = NewItemMonitorTypes.New
            };

            var resource = new AuthorResource
            {
                Id = 1,
                AuthorName = "Joe Abercrombie",
                Monitored = true,
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks",
                AudiobookQualityProfileId = 2,
                EbookQualityProfileId = 3,
                AudiobookMonitored = false,
                AudiobookMonitorNewItems = NewItemMonitorTypes.None,
                EbookMonitored = true,
                EbookMonitorNewItems = NewItemMonitorTypes.None
            };

            var updated = resource.ToModel(existing);

            Assert.That(updated.AudiobookMonitored, Is.False);
            Assert.That(updated.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(updated.EbookMonitored, Is.True);
            Assert.That(updated.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(updated.AudiobookSettingsManuallyOverridden, Is.True);
            Assert.That(updated.EbookSettingsManuallyOverridden, Is.True);
        }

        [Test]
        public void should_translate_deprecated_per_media_monitoring_fields()
        {
            var resource = new AuthorResource
            {
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = true,
                EbookMonitorExisting = 1,
                EbookMonitorFuture = false
            };

            var model = resource.ToModel();

            Assert.Multiple(() =>
            {
                Assert.That(model.AudiobookMonitored, Is.True);
                Assert.That(model.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                Assert.That(resource.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.None));
                Assert.That(model.EbookMonitored, Is.True);
                Assert.That(model.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(resource.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
            });
        }

        [Test]
        public void should_project_binary_monitoring_for_deprecated_per_media_clients()
        {
            var resource = new NzbDrone.Core.Books.Author
            {
                AudiobookMonitored = true,
                AudiobookMonitorNewItems = NewItemMonitorTypes.New,
                EbookMonitored = false,
                EbookMonitorNewItems = NewItemMonitorTypes.None
            }.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(resource.AudiobookMonitorExisting, Is.EqualTo(2));
                Assert.That(resource.AudiobookMonitorFuture, Is.True);
                Assert.That(resource.EbookMonitorExisting, Is.EqualTo(0));
                Assert.That(resource.EbookMonitorFuture, Is.False);
            });
        }

        [Test]
        public void should_not_wipe_binary_monitoring_fields_when_not_provided_on_put_update()
        {
            var existing = new NzbDrone.Core.Books.Author
            {
                Id = 1,
                Name = "Joe Abercrombie",
                Monitored = true,
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks",
                AudiobookQualityProfileId = 2,
                EbookQualityProfileId = 3,
                AudiobookMonitored = true,
                AudiobookMonitorNewItems = NewItemMonitorTypes.New,
                EbookMonitored = true,
                EbookMonitorNewItems = NewItemMonitorTypes.New,
                AudiobookSettingsManuallyOverridden = false,
                EbookSettingsManuallyOverridden = false
            };

            var resource = new AuthorResource
            {
                Id = 1,
                AuthorName = "Joe Abercrombie",
                Monitored = false,
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks",
                AudiobookQualityProfileId = 2,
                EbookQualityProfileId = 3
            };

            var updated = resource.ToModel(existing);

            Assert.That(updated.AudiobookMonitored, Is.True);
            Assert.That(updated.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            Assert.That(updated.EbookMonitored, Is.True);
            Assert.That(updated.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            Assert.That(updated.AudiobookSettingsManuallyOverridden, Is.False);
            Assert.That(updated.EbookSettingsManuallyOverridden, Is.False);
        }

        [Test]
        public void should_apply_per_type_metadata_profiles_on_put_update()
        {
            var existing = new NzbDrone.Core.Books.Author
            {
                Id = 1,
                Name = "Joe Abercrombie",
                Monitored = true,
                AudiobookMetadataProfileId = 1,
                EbookMetadataProfileId = 2
            };

            var resource = new AuthorResource
            {
                Id = 1,
                AuthorName = "Joe Abercrombie",
                Monitored = true,
                AudiobookMetadataProfileId = 4,
                EbookMetadataProfileId = 5
            };

            var updated = resource.ToModel(existing);

            Assert.That(updated.AudiobookMetadataProfileId, Is.EqualTo(4));
            Assert.That(updated.EbookMetadataProfileId, Is.EqualTo(5));
        }

        [Test]
        public void should_not_wipe_per_type_metadata_profiles_when_not_provided_on_put_update()
        {
            var existing = new NzbDrone.Core.Books.Author
            {
                Id = 1,
                Name = "Joe Abercrombie",
                Monitored = true,
                AudiobookMetadataProfileId = 4,
                EbookMetadataProfileId = 5
            };

            var resource = new AuthorResource
            {
                Id = 1,
                AuthorName = "Joe Abercrombie",
                Monitored = true
            };

            var updated = resource.ToModel(existing);

            Assert.That(updated.AudiobookMetadataProfileId, Is.EqualTo(4));
            Assert.That(updated.EbookMetadataProfileId, Is.EqualTo(5));
        }

	        [Test]
	        public void should_not_map_numeric_foreign_author_id_without_facade()
	        {
	            var resource = new AuthorResource
	            {
	                ForeignAuthorId = "12345",
                QualityProfileId = 7,
                MetadataProfileId = 8,
                RootFolderPath = "/books",
                Monitored = true,
                MonitorNewItems = "none",
                Tags = new HashSet<int> { 4 },
                AddOptions = new AddAuthorOptions
                {
                    BooksToMonitor = new List<string> { "hc:999" }
                }
            };

	            var model = resource.ToModel();

	            Assert.That(model.HardcoverAuthorId, Is.Null);
	            Assert.That(model.GoodreadsAuthorId, Is.Null);
	            Assert.That(model.AudnexusAuthorId, Is.Null);
	            Assert.That(model.AudiobookQualityProfileId, Is.EqualTo(7));
	            Assert.That(model.EbookQualityProfileId, Is.EqualTo(7));
            Assert.That(model.AudiobookMetadataProfileId, Is.EqualTo(8));
            Assert.That(model.EbookMetadataProfileId, Is.EqualTo(8));
            Assert.That(model.AudiobookRootFolderPath, Is.EqualTo("/books"));
            Assert.That(model.EbookRootFolderPath, Is.EqualTo("/books"));
            Assert.That(model.AudiobookMonitored, Is.True);
            Assert.That(model.EbookMonitored, Is.True);
            Assert.That(model.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(model.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(model.AudiobookTags, Is.EquivalentTo(new[] { 4 }));
            Assert.That(model.EbookTags, Is.EquivalentTo(new[] { 4 }));
	            Assert.That(model.AddOptions.Monitor, Is.EqualTo(MonitorTypes.SpecificBook));
	        }

        [Test]
        public void should_map_numeric_foreign_author_id_as_facade_dialect()
        {
            var hcResource = new AuthorResource
            {
                ForeignAuthorId = "12345"
            };

            var grResource = new AuthorResource
            {
                ForeignAuthorId = "173491"
            };

            var hcModel = hcResource.ToModel(new ReadarrFacadeContext("hc", "audiobook", "/readarr/hc/audiobook"));
            var grModel = grResource.ToModel(new ReadarrFacadeContext("gr", "ebook", "/readarr/gr/ebook"));

            Assert.That(hcModel.HardcoverAuthorId, Is.EqualTo("hc:12345"));
            Assert.That(grModel.GoodreadsAuthorId, Is.EqualTo("gr:173491"));
        }

        [Test]
        public void should_log_one_aggregate_warning_for_facade_author_identity_gaps()
        {
            var originalConfiguration = LogManager.Configuration;
            try
            {
                var logs = ConfigureLogging();

                AuthorResourceMapper.WarnFacadeIdentityGaps(new[]
                {
                    new AuthorResource { Id = 1, ForeignAuthorId = string.Empty },
                    new AuthorResource { Id = 2, ForeignAuthorId = null },
                    new AuthorResource { Id = 3, ForeignAuthorId = "149559" }
                },
                new ReadarrFacadeContext("hc", "ebook", "/readarr/hc/ebook"),
                "author response");

                Assert.That(logs.Logs, Has.Count.EqualTo(1));
                Assert.That(logs.Logs.Single(), Does.Contain("Warn|"));
                Assert.That(logs.Logs.Single(), Does.Contain("Emitted 2 author resource(s) without hc identity from author response"));
            }
            finally
            {
                LogManager.Configuration = originalConfiguration;
                LogManager.ReconfigExistingLoggers();
            }
        }

        [Test]
        public void should_map_bare_books_to_monitor_as_facade_dialect()
        {
            var hcResource = new AuthorResource
            {
                MonitorNewItems = "none",
                AddOptions = new AddAuthorOptions
                {
                    BooksToMonitor = new List<string> { "12345", "gr:999", "not-a-provider-id" }
                }
            };

            var grResource = new AuthorResource
            {
                MonitorNewItems = "none",
                AddOptions = new AddAuthorOptions
                {
                    BooksToMonitor = new List<string> { "94932951", "hc:2514970", "not-a-provider-id" }
                }
            };

            var hcModel = hcResource.ToModel(new ReadarrFacadeContext("hc", "audiobook", "/readarr/hc/audiobook"));
            var grModel = grResource.ToModel(new ReadarrFacadeContext("gr", "ebook", "/readarr/gr/ebook"));

            Assert.That(hcModel.AddOptions.Monitor, Is.EqualTo(MonitorTypes.SpecificBook));
            Assert.That(hcModel.AddOptions.BooksToMonitor, Is.EqualTo(new[] { "hc:12345", "gr:999", "not-a-provider-id" }));
            Assert.That(grModel.AddOptions.Monitor, Is.EqualTo(MonitorTypes.SpecificBook));
            Assert.That(grModel.AddOptions.BooksToMonitor, Is.EqualTo(new[] { "gr:94932951", "hc:2514970", "not-a-provider-id" }));
        }

        [Test]
        public void should_project_legacy_author_fields_to_facade_media_side_only()
        {
            var resource = new AuthorResource
            {
                ForeignAuthorId = "173491",
                QualityProfileId = 7,
                MetadataProfileId = 8,
                RootFolderPath = "/ebooks",
                Monitored = true,
                MonitorNewItems = "all",
                Tags = new HashSet<int> { 4, 5 }
            };

            var model = resource.ToModel(new ReadarrFacadeContext("gr", "ebook", "/readarr/gr/ebook"));

            Assert.That(model.GoodreadsAuthorId, Is.EqualTo("gr:173491"));
            Assert.That(model.EbookQualityProfileId, Is.EqualTo(7));
            Assert.That(model.EbookMetadataProfileId, Is.EqualTo(8));
            Assert.That(model.EbookRootFolderPath, Is.EqualTo("/ebooks"));
            Assert.That(model.EbookTags, Is.EquivalentTo(new[] { 4, 5 }));
            Assert.That(model.EbookMonitored, Is.True);
            Assert.That(model.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
            Assert.That(model.AudiobookQualityProfileId, Is.Null);
            Assert.That(model.AudiobookMetadataProfileId, Is.Null);
            Assert.That(model.AudiobookRootFolderPath, Is.Null);
            Assert.That(model.AudiobookTags, Is.Null);
            Assert.That(model.AudiobookMonitored, Is.Null);
            Assert.That(model.AudiobookMonitorNewItems, Is.Null);
        }

        [Test]
        public void should_translate_legacy_import_monitoring_into_the_binary_model()
        {
            var selected = AuthorController.ResolveImportMonitoring(new AuthorImportResource
            {
                MonitorExisting = "select",
                MonitorFuture = false
            }, BookMediaType.Audiobook);
            var selectedFuture = AuthorController.ResolveImportMonitoring(new AuthorImportResource
            {
                MonitorExisting = "select",
                MonitorFuture = true
            }, BookMediaType.Audiobook);
            var all = AuthorController.ResolveImportMonitoring(new AuthorImportResource
            {
                MonitorExisting = "all",
                MonitorFuture = false
            }, BookMediaType.Ebook);

            Assert.Multiple(() =>
            {
                Assert.That(selected.Monitored, Is.True);
                Assert.That(selected.MonitorExistingMode, Is.EqualTo(MonitorTypes.None));
                Assert.That(selected.MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                Assert.That(selectedFuture.Monitored, Is.True);
                Assert.That(selectedFuture.MonitorExistingMode, Is.EqualTo(MonitorTypes.None));
                Assert.That(selectedFuture.MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                Assert.That(all.Monitored, Is.True);
                Assert.That(all.MonitorExistingMode, Is.EqualTo(MonitorTypes.All));
                Assert.That(all.MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
            });
        }

        [Test]
        public void legacy_select_should_target_the_requested_book_on_the_book_import_contract()
        {
            var monitoring = AuthorController.ResolveImportMonitoring(new AuthorImportResource
            {
                MonitorExisting = "select",
                MonitorFuture = false
            }, BookMediaType.Ebook, legacySelectTargetsSpecificBook: true);

            Assert.Multiple(() =>
            {
                Assert.That(monitoring.Monitored, Is.True);
                Assert.That(monitoring.MonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
                Assert.That(monitoring.MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            });
        }

        [Test]
        public void explicit_import_monitoring_should_override_legacy_fields()
        {
            var monitoring = AuthorController.ResolveImportMonitoring(new AuthorImportResource
            {
                Monitor = "all",
                MonitorExisting = "all",
                MonitorFuture = true,
                EbookMonitored = false,
                EbookMonitorExistingMode = MonitorTypes.None,
                EbookMonitorNewItems = NewItemMonitorTypes.None
            }, BookMediaType.Ebook);

            Assert.Multiple(() =>
            {
                Assert.That(monitoring.Monitored, Is.False);
                Assert.That(monitoring.MonitorExistingMode, Is.EqualTo(MonitorTypes.None));
                Assert.That(monitoring.MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            });
        }

        [Test]
        public void initial_book_monitoring_should_not_rewrite_an_existing_media_catalog()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AuthorController.ShouldApplyInitialBookMonitoring(false, MonitorTypes.All), Is.True);
                Assert.That(AuthorController.ShouldApplyInitialBookMonitoring(false, MonitorTypes.None), Is.True);
                Assert.That(AuthorController.ShouldApplyInitialBookMonitoring(true, MonitorTypes.All), Is.False);
                Assert.That(AuthorController.ShouldApplyInitialBookMonitoring(true, MonitorTypes.Missing), Is.False);
                Assert.That(AuthorController.ShouldApplyInitialBookMonitoring(true, MonitorTypes.SpecificBook), Is.True);
                Assert.That(AuthorController.ShouldApplyInitialBookMonitoring(false, null), Is.False);
            });
        }

        [Test]
        public void should_preserve_sibling_and_omitted_fields_on_facade_author_update()
        {
            var existing = new NzbDrone.Core.Books.Author
            {
                Id = 1,
                Name = "E. Lockhart",
                Path = "/authors/E Lockhart",
                AudiobookQualityProfileId = 2,
                EbookQualityProfileId = 3,
                AudiobookMetadataProfileId = 4,
                EbookMetadataProfileId = 5,
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks",
                AudiobookTags = new HashSet<int> { 10 },
                EbookTags = new HashSet<int> { 20 },
                Tags = new HashSet<int> { 10, 20 },
                AudiobookMonitored = true,
                AudiobookMonitorNewItems = NewItemMonitorTypes.New,
                EbookMonitored = true,
                EbookMonitorNewItems = NewItemMonitorTypes.New,
                LastSelectedMediaType = "ebook"
            };

            var resource = new AuthorResource
            {
                Id = 1,
                AuthorName = "E. Lockhart",
                QualityProfileId = 7,
                Monitored = false,
                MonitorNewItems = "none",
                AudiobookMonitored = true,
                AudiobookMonitorNewItems = NewItemMonitorTypes.All
            };

            var updated = resource.ToModel(existing, new ReadarrFacadeContext("gr", "audiobook", "/readarr/gr/audiobook"));

            Assert.That(updated.AudiobookQualityProfileId, Is.EqualTo(7));
            Assert.That(updated.AudiobookMonitored, Is.False);
            Assert.That(updated.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(updated.AudiobookRootFolderPath, Is.EqualTo("/audiobooks"));
            Assert.That(updated.AudiobookTags, Is.EquivalentTo(new[] { 10 }));
            Assert.That(updated.EbookQualityProfileId, Is.EqualTo(3));
            Assert.That(updated.EbookMetadataProfileId, Is.EqualTo(5));
            Assert.That(updated.EbookRootFolderPath, Is.EqualTo("/ebooks"));
            Assert.That(updated.EbookTags, Is.EquivalentTo(new[] { 20 }));
            Assert.That(updated.EbookMonitored, Is.True);
            Assert.That(updated.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            Assert.That(updated.Path, Is.EqualTo("/authors/E Lockhart"));
            Assert.That(updated.LastSelectedMediaType, Is.EqualTo("ebook"));
            Assert.That(updated.Tags, Is.EquivalentTo(new[] { 10, 20 }));
        }

        [Test]
        public void should_emit_bare_author_id_for_facade_dialect()
        {
            var author = new NzbDrone.Core.Books.Author
            {
                Name = "E. Lockhart",
                HardcoverAuthorId = "hc:149559",
                GoodreadsAuthorId = "gr:173491"
            };

            var hcResource = author.ToResource(new ReadarrFacadeContext("hc", "ebook", "/readarr/hc/ebook"));
            var grResource = author.ToResource(new ReadarrFacadeContext("gr", "ebook", "/readarr/gr/ebook"));

            Assert.That(hcResource.ForeignAuthorId, Is.EqualTo("149559"));
            Assert.That(grResource.ForeignAuthorId, Is.EqualTo("173491"));
        }

	        [Test]
	        public void should_round_trip_openlibrary_and_google_author_provider_ids()
	        {
	            var openLibrary = new AuthorResource { ForeignAuthorId = "ol:OL123A" }.ToModel();
	            var googleBooks = new AuthorResource { ForeignAuthorId = "gb:abc123" }.ToModel();

	            Assert.That(openLibrary.OpenLibraryAuthorId, Is.EqualTo("ol:OL123A"));
	            Assert.That(googleBooks.GoogleBooksAuthorId, Is.EqualTo("gb:abc123"));
	            Assert.That(new NzbDrone.Core.Books.Author { OpenLibraryAuthorId = "ol:OL123A" }.ToResource().ForeignAuthorId, Is.EqualTo("ol:OL123A"));
	            Assert.That(new NzbDrone.Core.Books.Author { GoogleBooksAuthorId = "gb:abc123" }.ToResource().ForeignAuthorId, Is.EqualTo("gb:abc123"));
	        }

	        [Test]
	        public void should_apply_per_media_tags_on_put_update()
        {
            var existing = new NzbDrone.Core.Books.Author
            {
                Id = 1,
                Name = "Joe Abercrombie",
                AudiobookTags = new HashSet<int> { 99 },
                EbookTags = new HashSet<int> { 98 },
                Tags = new HashSet<int> { 98, 99 }
            };

            var resource = new AuthorResource
            {
                Id = 1,
                AuthorName = "Joe Abercrombie",
                AudiobookTags = new HashSet<int> { 1, 2 },
                EbookTags = new HashSet<int> { 3 }
            };

            var updated = resource.ToModel(existing);

            Assert.That(updated.AudiobookTags, Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(updated.EbookTags, Is.EquivalentTo(new[] { 3 }));
            Assert.That(updated.Tags, Is.EquivalentTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void should_fallback_to_legacy_tags_when_per_media_tags_not_provided_on_put_update()
        {
            var existing = new NzbDrone.Core.Books.Author
            {
                Id = 1,
                Name = "Joe Abercrombie",
                AudiobookTags = new HashSet<int> { 1 },
                EbookTags = new HashSet<int> { 2 },
                Tags = new HashSet<int> { 1, 2 }
            };

            var resource = new AuthorResource
            {
                Id = 1,
                AuthorName = "Joe Abercrombie",
                Tags = new HashSet<int> { 5, 6 }
            };

            var updated = resource.ToModel(existing);

            Assert.That(updated.AudiobookTags, Is.EquivalentTo(new[] { 5, 6 }));
            Assert.That(updated.EbookTags, Is.EquivalentTo(new[] { 5, 6 }));
            Assert.That(updated.Tags, Is.EquivalentTo(new[] { 5, 6 }));
        }

        [Test]
        public void should_clone_mutable_collections_when_mapping_to_resource()
        {
            var model = new NzbDrone.Core.Books.Author
            {
                Name = "Brandon Sanderson",
                Links = new List<Links>
                {
                    new Links { Name = "goodreads", Url = "https://www.goodreads.com/author/show/38550" }
                },
                Genres = new List<string> { "Fantasy" },
                AudiobookTags = new HashSet<int> { 1, 2 },
                EbookTags = new HashSet<int> { 3 },
                Ratings = new Ratings { Votes = 5, Value = 4.7m },
                Images = new List<CoreMediaCover>
                {
                    new CoreMediaCover(MediaCoverTypes.Poster, "https://example.com/poster.jpg")
                }
            };

            var resource = model.ToResource();

            model.Links[0].Url = "https://mutated.example.com";
            model.Genres.Add("Sci-Fi");
            model.AudiobookTags.Add(99);
            model.Ratings.Value = 1.1m;
            model.Images[0].Url = "https://mutated.example.com/poster.jpg";

            Assert.That(resource.Links[0].Url, Is.EqualTo("https://www.goodreads.com/author/show/38550"));
            Assert.That(resource.Genres, Is.EquivalentTo(new[] { "Fantasy" }));
            Assert.That(resource.AudiobookTags, Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(resource.Ratings.Value, Is.EqualTo(4.7m));
            Assert.That(resource.Images[0].Url, Is.EqualTo("https://example.com/poster.jpg"));
        }

        [Test]
        public void should_not_expose_known_provider_placeholder_or_stale_selection()
        {
            const string placeholder = "https://assets.hardcover.app/author/910001/provider-default.jpg";
            const string realPhoto = "https://images.example/real-author.jpg";
            MediaCoverRendition.RegisterKnownPlaceholderImage(placeholder, "47736d50a054c80f646c094ecd9c00c3fb12f5c585817fa5a581140c364da30e");
            var model = new NzbDrone.Core.Books.Author
            {
                Id = 910001,
                Name = "Example Author",
                SelectedPosterHash = "stale-placeholder-selection",
                Images = new List<CoreMediaCover>
                {
                    new(MediaCoverTypes.Poster, placeholder),
                    new(MediaCoverTypes.Poster, realPhoto)
                }
            };

            var resource = model.ToResource();

            Assert.That(resource.Images.Select(image => image.Url), Is.EqualTo(new[] { realPhoto }));
            Assert.That(resource.SelectedPosterHash, Is.Null);
        }

        [Test]
        public void should_map_per_media_tags_on_get()
        {
            var author = new NzbDrone.Core.Books.Author
            {
                Id = 1,
                Name = "Joe Abercrombie",
                AudiobookTags = new HashSet<int> { 1 },
                EbookTags = new HashSet<int> { 2 },
                Tags = new HashSet<int> { 1, 2 }
            };

            var resource = author.ToResource();

            Assert.That(resource.AudiobookTags, Is.EquivalentTo(new[] { 1 }));
            Assert.That(resource.EbookTags, Is.EquivalentTo(new[] { 2 }));
            Assert.That(resource.Tags, Is.EquivalentTo(new[] { 1, 2 }));
        }

        [Test]
        public void should_not_wipe_other_media_tags_when_only_one_media_is_updated()
        {
            var existing = new NzbDrone.Core.Books.Author
            {
                Id = 1,
                Name = "Joe Abercrombie",
                Monitored = true,
                Path = "/books/Joe Abercrombie",
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks",
                AudiobookTags = new HashSet<int> { 1 },
                EbookTags = new HashSet<int> { 2 },
                Tags = new HashSet<int> { 1, 2 }
            };

            var resource = new AuthorResource
            {
                Id = 1,
                AuthorName = "Joe Abercrombie",
                Monitored = true,
                Path = "/books/Joe Abercrombie",
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks",
                EbookTags = new HashSet<int> { 3 }
            };

            var updated = resource.ToModel(existing);

            Assert.That(updated.AudiobookTags, Is.EquivalentTo(new[] { 1 }));
            Assert.That(updated.EbookTags, Is.EquivalentTo(new[] { 3 }));
            Assert.That(updated.Tags, Is.EquivalentTo(new[] { 1, 3 }));
        }

        [Test]
        public void should_not_leak_legacy_tags_into_unconfigured_media_type_on_get()
        {
            var author = new NzbDrone.Core.Books.Author
            {
                Id = 1,
                Name = "Joe Abercrombie",
                AudiobookTags = null,
                EbookTags = new HashSet<int> { 2 },
                Tags = new HashSet<int> { 2 }
            };

            var resource = author.ToResource();

            Assert.That(resource.AudiobookTags, Is.Empty);
            Assert.That(resource.EbookTags, Is.EquivalentTo(new[] { 2 }));
            Assert.That(resource.Tags, Is.EquivalentTo(new[] { 2 }));
        }

        private static MemoryTarget ConfigureLogging()
        {
            var memoryTarget = new MemoryTarget("memory")
            {
                Layout = "${level}|${logger}|${message}"
            };

            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, memoryTarget, "Chaptarr.Api.V1.Author.*");
            LogManager.Configuration = config;
            LogManager.ReconfigExistingLoggers();

            return memoryTarget;
        }
    }
}

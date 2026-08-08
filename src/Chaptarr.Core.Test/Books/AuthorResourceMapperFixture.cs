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
using NUnit.Framework;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorResourceMapperFixture
    {
        [Test]
        public void should_apply_tri_state_monitoring_fields_on_put_update()
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
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = true,
                EbookMonitorExisting = 1,
                EbookMonitorFuture = true
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
                AudiobookMonitorExisting = 0,
                AudiobookMonitorFuture = false,
                EbookMonitorExisting = 2,
                EbookMonitorFuture = false
            };

            var updated = resource.ToModel(existing);

            Assert.That(updated.AudiobookMonitorExisting, Is.EqualTo(0));
            Assert.That(updated.AudiobookMonitorFuture, Is.False);
            Assert.That(updated.EbookMonitorExisting, Is.EqualTo(2));
            Assert.That(updated.EbookMonitorFuture, Is.False);
            Assert.That(updated.AudiobookSettingsManuallyOverridden, Is.True);
            Assert.That(updated.EbookSettingsManuallyOverridden, Is.True);
        }

        [Test]
        public void should_not_wipe_tri_state_monitoring_fields_when_not_provided_on_put_update()
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
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = true,
                EbookMonitorExisting = 1,
                EbookMonitorFuture = true,
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

            Assert.That(updated.AudiobookMonitorExisting, Is.EqualTo(2));
            Assert.That(updated.AudiobookMonitorFuture, Is.True);
            Assert.That(updated.EbookMonitorExisting, Is.EqualTo(1));
            Assert.That(updated.EbookMonitorFuture, Is.True);
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
                RootFolderPath = "/books",
                MonitorNewItems = "none",
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
            Assert.That(model.AudiobookRootFolderPath, Is.EqualTo("/books"));
            Assert.That(model.EbookRootFolderPath, Is.EqualTo("/books"));
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
                RootFolderPath = "/ebooks",
                Monitored = true,
                Tags = new HashSet<int> { 4, 5 }
            };

            var model = resource.ToModel(new ReadarrFacadeContext("gr", "ebook", "/readarr/gr/ebook"));

            Assert.That(model.GoodreadsAuthorId, Is.EqualTo("gr:173491"));
            Assert.That(model.EbookQualityProfileId, Is.EqualTo(7));
            Assert.That(model.EbookRootFolderPath, Is.EqualTo("/ebooks"));
            Assert.That(model.EbookTags, Is.EquivalentTo(new[] { 4, 5 }));
            Assert.That(model.EbookMonitorExisting, Is.EqualTo(1));
            Assert.That(model.EbookMonitorFuture, Is.True);
            Assert.That(model.AudiobookQualityProfileId, Is.Null);
            Assert.That(model.AudiobookRootFolderPath, Is.Null);
            Assert.That(model.AudiobookTags, Is.Null);
            Assert.That(model.AudiobookMonitorExisting, Is.Null);
            Assert.That(model.AudiobookMonitorFuture, Is.Null);
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
                AudiobookMonitorExisting = 2,
                AudiobookMonitorFuture = true,
                EbookMonitorExisting = 1,
                EbookMonitorFuture = true,
                LastSelectedMediaType = "ebook"
            };

            var resource = new AuthorResource
            {
                Id = 1,
                AuthorName = "E. Lockhart",
                QualityProfileId = 7,
                Monitored = true
            };

            var updated = resource.ToModel(existing, new ReadarrFacadeContext("gr", "audiobook", "/readarr/gr/audiobook"));

            Assert.That(updated.AudiobookQualityProfileId, Is.EqualTo(7));
            Assert.That(updated.AudiobookRootFolderPath, Is.EqualTo("/audiobooks"));
            Assert.That(updated.AudiobookTags, Is.EquivalentTo(new[] { 10 }));
            Assert.That(updated.EbookQualityProfileId, Is.EqualTo(3));
            Assert.That(updated.EbookMetadataProfileId, Is.EqualTo(5));
            Assert.That(updated.EbookRootFolderPath, Is.EqualTo("/ebooks"));
            Assert.That(updated.EbookTags, Is.EquivalentTo(new[] { 20 }));
            Assert.That(updated.EbookMonitorExisting, Is.EqualTo(1));
            Assert.That(updated.EbookMonitorFuture, Is.True);
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

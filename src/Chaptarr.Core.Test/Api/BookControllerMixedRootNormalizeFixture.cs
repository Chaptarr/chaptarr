using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.MediaTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using FluentValidation;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookControllerMixedRootNormalizeFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            private readonly List<RootFolder> _rootFolders;

            public StubRootFolderService(List<RootFolder> rootFolders)
            {
                _rootFolders = rootFolders;
            }

            public List<RootFolder> All() => _rootFolders;
            public RootFolder GetBestRootFolder(string path) => GetBestRootFolder(path, _rootFolders);
            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders)
            {
                return allRootFolders
                    .Where(r => r.Path.PathEquals(path) || r.Path.IsParentPath(path))
                    .OrderByDescending(r => r.Path.Length)
                    .FirstOrDefault();
            }

            public List<RootFolder> AllWithSpaceStats() => throw new NotImplementedException();
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => throw new NotImplementedException();
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => throw new NotImplementedException();
        }

        private sealed class TestableBookController : BookController
        {
            private static readonly MethodInfo NormalizeMethod = typeof(BookController).GetMethod(
                "NormalizeReadarrSingleFields",
                BindingFlags.Instance | BindingFlags.NonPublic);

            public TestableBookController(IRootFolderService rootFolderService, IAuthorService authorService = null)
                : base(
                    authorService: authorService ?? DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                    bookService: null,
                    addBookService: null,
                    editionService: null,
                    editionSelector: null,
                    seriesBookLinkService: null,
                    authorStatisticsService: null,
                    mediaFileService: null,
                    coverMapper: null,
                    upgradableSpecification: null,
                    signalRBroadcaster: null,
                    commandQueueManager: null,
                    eventAggregator: null,
                    rootFolderService: rootFolderService,
                    qualityProfileExistsValidator: new QualityProfileExistsValidator(new TestQualityProfileService()),
                    metadataProfileExistsValidator: new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                    logger: LogManager.GetCurrentClassLogger())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            }

            public void Normalize(BookResource bookResource, bool wantAudiobook, bool wantEbook)
            {
                NormalizeMethod.Invoke(this, new object[] { bookResource, wantAudiobook, wantEbook });
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ambiguous_shared_fields_book_add_checks_available_scope_before_refusing_without_writes(bool hasMixedRoot)
        {
            var lookups = 0;
            var authors = Common.InterfaceProxy.Proxy<IAuthorService>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IAuthorService.FindByProviderId)));
                lookups++;
                return null;
            });
            var roots = hasMixedRoot ? new List<RootFolder> { new RootFolder { Path = "/books", FolderType = FolderType.Mixed } } : new List<RootFolder>();
            var controller = new TestableBookController(new StubRootFolderService(roots), authors);
            var result = await controller.AddBook(null, new BookResource
            {
                ForeignBookId = "hc:42",
                Author = new AuthorResource { ForeignAuthorId = "gr:130698", MetadataProfileId = 2, RootFolderPath = "/books" }
            });
            Assert.That(result.Result, Is.TypeOf<BadRequestObjectResult>());
            Assert.That(((BadRequestObjectResult)result.Result).Value, Is.EqualTo(MediaTypeParameterParser.FormatRequiredMessage));
            Assert.That(lookups, Is.EqualTo(1));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void explicit_media_scope_maps_shared_profiles_without_guessing_from_profile_type(bool ebook)
        {
            var root = new RootFolder { Path = "/books", FolderType = ebook ? FolderType.Ebook : FolderType.Audiobook };
            root.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 2 });
            root.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 3, MetadataProfileId = 4 });
            var controller = new TestableBookController(new StubRootFolderService(new List<RootFolder> { root }));
            var resource = new BookResource
            {
                Author = new AuthorResource { RootFolderPath = root.Path, QualityProfileId = 7, MetadataProfileId = 8 }
            };
            controller.Normalize(resource, wantAudiobook: !ebook, wantEbook: ebook);
            Assert.That(ebook ? resource.Author.EbookQualityProfileId : resource.Author.AudiobookQualityProfileId, Is.EqualTo(7));
            Assert.That(ebook ? resource.Author.EbookMetadataProfileId : resource.Author.AudiobookMetadataProfileId, Is.EqualTo(8));
            Assert.That(ebook ? resource.Author.AudiobookQualityProfileId : resource.Author.EbookQualityProfileId, Is.Null);
        }

        [Test]
        public void should_fill_sibling_config_for_single_format_add_from_sync_mixed_root()
        {
            var mixedRoot = BuildMixedRoot(defaultSync: true);
            var controller = new TestableBookController(new StubRootFolderService(new List<RootFolder> { mixedRoot }));
            var resource = new BookResource
            {
                Author = new AuthorResource
                {
                    RootFolderPath = "/books"
                }
            };

            controller.Normalize(resource, wantAudiobook: true, wantEbook: false);

            Assert.Multiple(() =>
            {
                Assert.That(resource.Author.AudiobookRootFolderPath, Is.EqualTo("/books"));
                Assert.That(resource.Author.AudiobookQualityProfileId, Is.EqualTo(10));
                Assert.That(resource.Author.AudiobookMetadataProfileId, Is.EqualTo(20));
                Assert.That(resource.Author.EbookRootFolderPath, Is.EqualTo("/books"));
                Assert.That(resource.Author.EbookQualityProfileId, Is.EqualTo(11));
                Assert.That(resource.Author.EbookMetadataProfileId, Is.EqualTo(21));
            });
        }

        [Test]
        public void should_not_fill_sibling_config_when_mixed_root_sync_default_is_off()
        {
            var mixedRoot = BuildMixedRoot(defaultSync: false);
            var controller = new TestableBookController(new StubRootFolderService(new List<RootFolder> { mixedRoot }));
            var resource = new BookResource
            {
                Author = new AuthorResource
                {
                    RootFolderPath = "/books"
                }
            };

            controller.Normalize(resource, wantAudiobook: true, wantEbook: false);

            Assert.Multiple(() =>
            {
                Assert.That(resource.Author.AudiobookRootFolderPath, Is.EqualTo("/books"));
                Assert.That(resource.Author.AudiobookQualityProfileId, Is.EqualTo(10));
                Assert.That(resource.Author.EbookRootFolderPath, Is.Null);
                Assert.That(resource.Author.EbookQualityProfileId, Is.Null);
            });
        }

        [Test]
        public void should_not_replace_explicit_sibling_config_from_sync_mixed_root()
        {
            var mixedRoot = BuildMixedRoot(defaultSync: true);
            var ebookRoot = new RootFolder { Path = "/ebooks", FolderType = FolderType.Ebook };
            ebookRoot.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 44, MetadataProfileId = 45 });

            var controller = new TestableBookController(new StubRootFolderService(new List<RootFolder> { mixedRoot, ebookRoot }));
            var resource = new BookResource
            {
                Author = new AuthorResource
                {
                    RootFolderPath = "/books",
                    EbookRootFolderPath = "/ebooks",
                    EbookQualityProfileId = 44,
                    EbookMetadataProfileId = 45
                }
            };

            controller.Normalize(resource, wantAudiobook: true, wantEbook: false);

            Assert.Multiple(() =>
            {
                Assert.That(resource.Author.AudiobookRootFolderPath, Is.EqualTo("/books"));
                Assert.That(resource.Author.EbookRootFolderPath, Is.EqualTo("/ebooks"));
                Assert.That(resource.Author.EbookQualityProfileId, Is.EqualTo(44));
                Assert.That(resource.Author.EbookMetadataProfileId, Is.EqualTo(45));
            });
        }

        [Test]
        public void should_not_expand_ebook_only_readarr_root_into_audiobook_model_settings()
        {
            var ebookRoot = new RootFolder { Path = "/ebooks", FolderType = FolderType.Ebook };
            ebookRoot.SetEbookSettings(new MediaTypeSettings
            {
                QualityProfileId = 1,
                MetadataProfileId = 2,
                MonitorExistingBooks = false,
                MonitorNewItems = NzbDrone.Core.Books.NewItemMonitorTypes.New
            });

            var controller = new TestableBookController(new StubRootFolderService(new List<RootFolder> { ebookRoot }));
            var resource = new BookResource
            {
                Author = new AuthorResource
                {
                    RootFolderPath = "/ebooks"
                }
            };

            controller.Normalize(resource, wantAudiobook: false, wantEbook: true);
            var model = resource.Author.ToModel();

            Assert.Multiple(() =>
            {
                Assert.That(resource.Author.RootFolderPath, Is.Null);
                Assert.That(model.AudiobookRootFolderPath, Is.Null);
                Assert.That(model.AudiobookQualityProfileId, Is.Null);
                Assert.That(model.AudiobookMetadataProfileId, Is.Null);
                Assert.That(model.EbookRootFolderPath, Is.EqualTo("/ebooks"));
                Assert.That(model.EbookQualityProfileId, Is.EqualTo(1));
                Assert.That(model.EbookMetadataProfileId, Is.EqualTo(2));
            });
        }

        [TestCase(FolderType.Ebook)]
        [TestCase(FolderType.Mixed)]
        public void explicit_both_formats_should_not_silently_drop_a_side_with_an_incompatible_or_incomplete_root(FolderType folderType)
        {
            var root = new RootFolder
            {
                Path = @"C:\Books".AsOsAgnostic(),
                FolderType = folderType
            };
            if (folderType == FolderType.Mixed) root.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 10 });
            root.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 11, MetadataProfileId = 21 });
            var controller = new TestableBookController(new StubRootFolderService(new List<RootFolder> { root }));
            var resource = new BookResource
            {
                Author = new AuthorResource { RootFolderPath = root.Path, AudiobookMonitored = true, EbookMonitored = true }
            };

            var error = Assert.Throws<TargetInvocationException>(() => controller.Normalize(resource, wantAudiobook: true, wantEbook: true));
            Assert.That(error.InnerException, Is.TypeOf<ValidationException>());
            Assert.That(((ValidationException)error.InnerException).Errors.Single().PropertyName, Is.EqualTo("author.audiobookRootFolderPath"));
            Assert.That(resource.Author.EbookRootFolderPath, Is.Null, "Neither side should be set up when the requested root fails validation");
        }

        [Test]
        public void omitted_media_type_should_use_distinct_configured_media_roots()
        {
            var audiobookRoot = new RootFolder
            {
                Path = @"C:\Audio".AsOsAgnostic(),
                FolderType = FolderType.Audiobook
            };
            audiobookRoot.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 10, MetadataProfileId = 20 });
            var ebookRoot = new RootFolder
            {
                Path = @"C:\Books".AsOsAgnostic(),
                FolderType = FolderType.Ebook
            };
            ebookRoot.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 11, MetadataProfileId = 21 });
            var controller = new TestableBookController(new StubRootFolderService(new List<RootFolder> { audiobookRoot, ebookRoot }));
            var resource = new BookResource
            {
                Author = new AuthorResource
                {
                    AudiobookRootFolderPath = audiobookRoot.Path,
                    EbookRootFolderPath = ebookRoot.Path
                }
            };

            controller.Normalize(resource, wantAudiobook: true, wantEbook: true);

            Assert.Multiple(() =>
            {
                Assert.That(resource.Author.AudiobookRootFolderPath, Is.EqualTo(audiobookRoot.Path));
                Assert.That(resource.Author.AudiobookQualityProfileId, Is.EqualTo(10));
                Assert.That(resource.Author.EbookRootFolderPath, Is.EqualTo(ebookRoot.Path));
                Assert.That(resource.Author.EbookQualityProfileId, Is.EqualTo(11));
            });
        }

        [Test]
        public void omitted_media_type_should_reject_invalid_explicit_side_root(
            [Values] BookMediaType mediaType)
        {
            var sharedRoot = new RootFolder
            {
                Path = mediaType == BookMediaType.Audiobook
                    ? @"C:\Books".AsOsAgnostic()
                    : @"C:\Audio".AsOsAgnostic(),
                FolderType = mediaType == BookMediaType.Audiobook ? FolderType.Ebook : FolderType.Audiobook
            };
            if (mediaType == BookMediaType.Audiobook)
            {
                sharedRoot.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 11, MetadataProfileId = 21 });
            }
            else
            {
                sharedRoot.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 10, MetadataProfileId = 20 });
            }

            var selectedRoot = new RootFolder
            {
                Path = mediaType == BookMediaType.Audiobook
                    ? @"C:\Audio".AsOsAgnostic()
                    : @"C:\Books".AsOsAgnostic(),
                FolderType = mediaType == BookMediaType.Audiobook ? FolderType.Audiobook : FolderType.Ebook
            };
            if (mediaType == BookMediaType.Audiobook)
            {
                selectedRoot.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 10, MetadataProfileId = 20 });
            }
            else
            {
                selectedRoot.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 11, MetadataProfileId = 21 });
            }

            var explicitPath = System.IO.Path.Combine(selectedRoot.Path, "typo");
            var controller = new TestableBookController(new StubRootFolderService(new List<RootFolder> { sharedRoot, selectedRoot }));
            var resource = new BookResource { Author = new AuthorResource { RootFolderPath = sharedRoot.Path } };
            if (mediaType == BookMediaType.Audiobook)
            {
                resource.Author.AudiobookRootFolderPath = explicitPath;
            }
            else
            {
                resource.Author.EbookRootFolderPath = explicitPath;
            }

            var error = Assert.Throws<TargetInvocationException>(() =>
                controller.Normalize(resource, wantAudiobook: true, wantEbook: true));

            Assert.That(error.InnerException, Is.TypeOf<ValidationException>());
            Assert.That(((ValidationException)error.InnerException).Errors.Single().ErrorMessage, Does.Contain(explicitPath));
        }

        [Test]
        public void explicit_per_media_root_descendant_should_not_match_configured_root(
            [Values] BookMediaType mediaType)
        {
            var rootPath = mediaType == BookMediaType.Audiobook
                ? @"C:\Audio".AsOsAgnostic()
                : @"C:\Books".AsOsAgnostic();
            var root = new RootFolder
            {
                Path = rootPath,
                FolderType = mediaType == BookMediaType.Audiobook ? FolderType.Audiobook : FolderType.Ebook
            };
            if (mediaType == BookMediaType.Audiobook)
            {
                root.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 2 });
            }
            else
            {
                root.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 2 });
            }

            var explicitPath = System.IO.Path.Combine(rootPath, "typo");
            var controller = new TestableBookController(new StubRootFolderService(new List<RootFolder> { root }));
            var resource = new BookResource { Author = new AuthorResource() };
            if (mediaType == BookMediaType.Audiobook)
            {
                resource.Author.AudiobookRootFolderPath = explicitPath;
            }
            else
            {
                resource.Author.EbookRootFolderPath = explicitPath;
            }

            var error = Assert.Throws<TargetInvocationException>(() =>
                controller.Normalize(resource, mediaType == BookMediaType.Audiobook, mediaType == BookMediaType.Ebook));

            Assert.That(error.InnerException, Is.TypeOf<ValidationException>());
            Assert.That(((ValidationException)error.InnerException).Errors.Single().ErrorMessage, Does.Contain(explicitPath));
        }

        [Test]
        public void explicit_exact_media_root_should_be_accepted([Values] BookMediaType mediaType)
        {
            var rootPath = mediaType == BookMediaType.Audiobook
                ? @"C:\Audio".AsOsAgnostic()
                : @"C:\Books".AsOsAgnostic();
            var root = new RootFolder
            {
                Path = rootPath,
                FolderType = mediaType == BookMediaType.Audiobook ? FolderType.Audiobook : FolderType.Ebook
            };
            if (mediaType == BookMediaType.Audiobook)
            {
                root.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 2 });
            }
            else
            {
                root.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 2 });
            }

            var controller = new TestableBookController(new StubRootFolderService(new List<RootFolder> { root }));
            var resource = new BookResource { Author = new AuthorResource() };
            if (mediaType == BookMediaType.Audiobook)
            {
                resource.Author.AudiobookRootFolderPath = rootPath;
            }
            else
            {
                resource.Author.EbookRootFolderPath = rootPath;
            }

            controller.Normalize(resource, mediaType == BookMediaType.Audiobook, mediaType == BookMediaType.Ebook);

            Assert.That(mediaType == BookMediaType.Audiobook
                    ? resource.Author.AudiobookRootFolderPath
                    : resource.Author.EbookRootFolderPath,
                Is.EqualTo(rootPath));
        }

        [Test]
        public void explicitly_selected_root_should_not_be_replaced(
            [Values] BookMediaType mediaType, [Values(false, true)] bool perSidePath,
            [Values("missing", "quality", "metadata", "type")] string problem)
        {
            var selected = new RootFolder { Path = @"C:\selected".AsOsAgnostic(), FolderType = FolderType.Mixed };
            var settings = new MediaTypeSettings { QualityProfileId = problem == "quality" ? null : 10, MetadataProfileId = problem == "metadata" ? null : 20 };
            if (mediaType == BookMediaType.Audiobook) { selected.SetAudiobookSettings(settings); }
            else { selected.SetEbookSettings(settings); }
            if (problem == "type")
            {
                selected.FolderType = mediaType == BookMediaType.Audiobook ? FolderType.Ebook : FolderType.Audiobook;
            }

            var fallback = BuildMixedRoot(false);
            fallback.Path = @"C:\fallback".AsOsAgnostic();
            var roots = new List<RootFolder> { fallback };
            if (problem != "missing") { roots.Add(selected); }
            var controller = new TestableBookController(new StubRootFolderService(roots));
            var resource = new BookResource { Author = new AuthorResource() };
            if (!perSidePath) { resource.Author.RootFolderPath = selected.Path; }
            else if (mediaType == BookMediaType.Audiobook) { resource.Author.AudiobookRootFolderPath = selected.Path; }
            else { resource.Author.EbookRootFolderPath = selected.Path; }

            var error = Assert.Throws<TargetInvocationException>(() =>
                controller.Normalize(resource, mediaType == BookMediaType.Audiobook, mediaType == BookMediaType.Ebook));

            Assert.That(error.InnerException, Is.TypeOf<ValidationException>());
            Assert.That(((ValidationException)error.InnerException).Errors.Single().ErrorMessage, Does.Contain(selected.Path));
            Assert.That(resource.Author.RootFolderPath == selected.Path ||
                resource.Author.AudiobookRootFolderPath == selected.Path || resource.Author.EbookRootFolderPath == selected.Path, Is.True);
        }

        [Test]
        public void omitted_root_should_skip_profileless_or_incompatible_defaults([Values] BookMediaType mediaType)
        {
            var incomplete = new RootFolder { Path = @"C:\incomplete".AsOsAgnostic(), FolderType = FolderType.Mixed };
            var wrongType = new RootFolder
            {
                Path = @"C:\wrong-type".AsOsAgnostic(),
                FolderType = mediaType == BookMediaType.Audiobook ? FolderType.Ebook : FolderType.Audiobook
            };
            if (mediaType == BookMediaType.Audiobook)
            {
                incomplete.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 1 });
                wrongType.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 2 });
            }
            else
            {
                incomplete.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 1 });
                wrongType.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 1, MetadataProfileId = 2 });
            }

            var usable = BuildMixedRoot(false);
            usable.Path = @"C:\usable".AsOsAgnostic();
            var controller = new TestableBookController(new StubRootFolderService(new List<RootFolder> { incomplete, wrongType, usable }));
            var resource = new BookResource { Author = new AuthorResource() };

            controller.Normalize(resource, mediaType == BookMediaType.Audiobook, mediaType == BookMediaType.Ebook);

            Assert.That(mediaType == BookMediaType.Audiobook ? resource.Author.AudiobookRootFolderPath : resource.Author.EbookRootFolderPath, Is.EqualTo(usable.Path));
            Assert.That(mediaType == BookMediaType.Audiobook ? resource.Author.AudiobookMetadataProfileId : resource.Author.EbookMetadataProfileId, Is.EqualTo(mediaType == BookMediaType.Audiobook ? 20 : 21));
        }

        [TestCase(BookMediaType.Audiobook, 53)]
        [TestCase(BookMediaType.Ebook, 53)]
        [TestCase(BookMediaType.Audiobook, 63)]
        [TestCase(BookMediaType.Ebook, 63)]
        public void explicit_media_metadata_should_survive_root_defaults_and_configured_opposite_side(BookMediaType mediaType, int selectedMetadataProfileId)
        {
            var root = BuildMixedRoot(defaultSync: false);
            var controller = new TestableBookController(new StubRootFolderService(new List<RootFolder> { root }));
            var audio = mediaType == BookMediaType.Audiobook;
            var resource = new BookResource
            {
                MediaType = audio ? "audiobook" : "ebook",
                Author = new AuthorResource
                {
                    AudiobookRootFolderPath = root.Path,
                    EbookRootFolderPath = root.Path,
                    AudiobookQualityProfileId = 10,
                    EbookQualityProfileId = 11,
                    AudiobookMetadataProfileId = audio ? selectedMetadataProfileId : 99,
                    EbookMetadataProfileId = audio ? 99 : selectedMetadataProfileId,
                    AudiobookMonitored = false,
                    EbookMonitored = false,
                    AudiobookTags = new HashSet<int>(),
                    EbookTags = new HashSet<int> { 7 }
                }
            };

            controller.Normalize(resource, wantAudiobook: audio, wantEbook: !audio);
            var model = resource.ToModel();

            Assert.Multiple(() =>
            {
                Assert.That(audio ? model.Author.AudiobookMetadataProfileId : model.Author.EbookMetadataProfileId, Is.EqualTo(selectedMetadataProfileId));
                Assert.That(audio ? model.Author.EbookMetadataProfileId : model.Author.AudiobookMetadataProfileId, Is.EqualTo(99));
                Assert.That(model.Author.AudiobookMonitored, Is.False);
                Assert.That(model.Author.EbookMonitored, Is.False);
                Assert.That(model.Author.AudiobookTags, Is.Empty);
                Assert.That(model.Author.EbookTags, Is.EquivalentTo(new[] { 7 }));
            });
        }

        private static RootFolder BuildMixedRoot(bool defaultSync)
        {
            var root = new RootFolder
            {
                Path = "/books",
                FolderType = FolderType.Mixed,
                DefaultSyncMonitoredAcrossFormats = defaultSync
            };

            root.SetAudiobookSettings(new MediaTypeSettings
            {
                QualityProfileId = 10,
                MetadataProfileId = 20,
                MonitorExistingBooks = true,
                MonitorNewItems = NzbDrone.Core.Books.NewItemMonitorTypes.All
            });
            root.SetEbookSettings(new MediaTypeSettings
            {
                QualityProfileId = 11,
                MetadataProfileId = 21,
                MonitorExistingBooks = true,
                MonitorNewItems = NzbDrone.Core.Books.NewItemMonitorTypes.All
            });

            return root;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
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

            public TestableBookController(IRootFolderService rootFolderService)
                : base(
                    authorService: DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
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
                    metadataProfileService: null,
                    qualityProfileService: null,
                    rootFolderService: rootFolderService,
                    qualityProfileExistsValidator: new QualityProfileExistsValidator(new TestQualityProfileService()),
                    metadataProfileExistsValidator: new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                    logger: LogManager.GetCurrentClassLogger())
            {
            }

            public void Normalize(BookResource bookResource, bool wantAudiobook, bool wantEbook)
            {
                NormalizeMethod.Invoke(this, new object[] { bookResource, wantAudiobook, wantEbook });
            }
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
                MonitorExisting = 2,
                MonitorFuture = true
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
                MonitorExisting = 1,
                MonitorFuture = true
            });
            root.SetEbookSettings(new MediaTypeSettings
            {
                QualityProfileId = 11,
                MetadataProfileId = 21,
                MonitorExisting = 1,
                MonitorFuture = true
            });

            return root;
        }
    }
}

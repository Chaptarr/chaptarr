using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.RootFolders
{
    [TestFixture]
    public class RootFolderScanServiceMixedFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public List<string> Files { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.Name switch
                {
                    nameof(IDiskProvider.FolderExists) => true,
                    nameof(IDiskProvider.GetFiles) => Files,
                    _ => throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}")
                };
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author UpdatedAuthor { get; private set; }
            public int UpdateAuthorCallCount { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IAuthorService.UpdateAuthor))
                {
                    UpdatedAuthor = (Author)args[0];
                    UpdateAuthorCallCount++;
                    return UpdatedAuthor;
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();
            public List<Book> UpdatedBooks { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    return Books;
                }

                if (targetMethod.Name == nameof(IBookService.UpdateMany))
                {
                    UpdatedBooks = new List<Book>((IEnumerable<Book>)args[0]);
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
            }
        }

        private class RootFolderSettingsResolverProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IRootFolderSettingsResolver.ResolveSettings) &&
                    args[0] is RootFolder rootFolder &&
                    args[1] is BookMediaType mediaType)
                {
                    var settings = mediaType == BookMediaType.Audiobook
                        ? rootFolder.GetAudiobookSettings()
                        : rootFolder.GetEbookSettings();

                    return new ResolvedRootFolderSettings
                    {
                        QualityProfileId = settings?.QualityProfileId,
                        MetadataProfileId = settings?.MetadataProfileId,
                        MonitorExisting = settings?.MonitorExisting,
                        MonitorFuture = settings?.MonitorFuture,
                        Tags = settings?.Tags ?? new List<int>(),
                        IsConfigured = settings != null,
                        Source = settings != null ? "MediaSpecific" : "Unconfigured"
                    };
                }

                throw new NotImplementedException($"Test proxy does not implement IRootFolderSettingsResolver.{targetMethod?.Name}");
            }
        }

        [Test]
        public void mixed_root_scan_should_link_only_audiobook_side_when_only_audio_files_exist()
        {
            var root = BuildMixedRoot();
            var author = new Author { Id = 1, Name = "Example Author" };
            var service = BuildSubject(new List<string> { "/library/Example Author/Book.mp3" });

            var update = service.LinkAuthorToFolder(author, root, "/library/Example Author");

            Assert.Multiple(() =>
            {
                Assert.That(update, Is.Not.Null);
                Assert.That(author.AudiobookRootFolderPath, Is.EqualTo("/library"));
                Assert.That(author.AudiobookPath, Is.EqualTo("/library/Example Author"));
                Assert.That(author.AudiobookQualityProfileId, Is.EqualTo(10));
                Assert.That(author.EbookRootFolderPath, Is.Null);
                Assert.That(author.EbookPath, Is.Null);
                Assert.That(author.EbookQualityProfileId, Is.Null);
            });
        }

        [Test]
        public void mixed_root_scan_should_fill_missing_ebook_side_without_changing_audiobook_side()
        {
            var root = BuildMixedRoot();
            var author = new Author
            {
                Id = 1,
                Name = "Example Author",
                AudiobookRootFolderPath = "/audio",
                AudiobookPath = "/audio/Example Author",
                AudiobookQualityProfileId = 90
            };
            var service = BuildSubject(new List<string> { "/library/Example Author/Book.epub" });

            var update = service.LinkAuthorToFolder(author, root, "/library/Example Author");

            Assert.Multiple(() =>
            {
                Assert.That(update, Is.Not.Null);
                Assert.That(author.AudiobookRootFolderPath, Is.EqualTo("/audio"));
                Assert.That(author.AudiobookPath, Is.EqualTo("/audio/Example Author"));
                Assert.That(author.AudiobookQualityProfileId, Is.EqualTo(90));
                Assert.That(author.EbookRootFolderPath, Is.EqualTo("/library"));
                Assert.That(author.EbookPath, Is.EqualTo("/library/Example Author"));
                Assert.That(author.EbookQualityProfileId, Is.EqualTo(11));
            });
        }

        [Test]
        public void mixed_root_scan_should_not_overwrite_existing_ebook_root_from_another_folder()
        {
            var root = BuildMixedRoot();
            var author = new Author
            {
                Id = 1,
                Name = "Example Author",
                EbookRootFolderPath = "/ebooks",
                EbookQualityProfileId = 44
            };
            var service = BuildSubject(new List<string> { "/library/Example Author/Book.epub" }, out var authorService);

            var update = service.LinkAuthorToFolder(author, root, "/library/Example Author");

            Assert.Multiple(() =>
            {
                Assert.That(update, Is.Null);
                Assert.That(author.EbookRootFolderPath, Is.EqualTo("/ebooks"));
                Assert.That(author.EbookPath, Is.Null);
                Assert.That(author.EbookQualityProfileId, Is.EqualTo(44));
                Assert.That(authorService.UpdateAuthorCallCount, Is.EqualTo(0));
            });
        }

        private static RootFolderScanService BuildSubject(List<string> files)
        {
            return BuildSubject(files, out _);
        }

        private static RootFolderScanService BuildSubject(List<string> files, out AuthorServiceProxy authorServiceProxy)
        {
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            ((DiskProviderProxy)(object)diskProvider).Files = files;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            authorServiceProxy = (AuthorServiceProxy)(object)authorService;

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var settingsResolver = DispatchProxy.Create<IRootFolderSettingsResolver, RootFolderSettingsResolverProxy>();

            return new RootFolderScanService(
                authorService,
                bookService,
                diskProvider,
                settingsResolver,
                LogManager.GetCurrentClassLogger());
        }

        private static RootFolder BuildMixedRoot()
        {
            var root = new RootFolder
            {
                Path = "/library",
                FolderType = FolderType.Mixed
            };

            root.SetAudiobookSettings(new MediaTypeSettings
            {
                QualityProfileId = 10,
                MetadataProfileId = 20,
                MonitorExisting = 0,
                MonitorFuture = true
            });
            root.SetEbookSettings(new MediaTypeSettings
            {
                QualityProfileId = 11,
                MetadataProfileId = 21,
                MonitorExisting = 0,
                MonitorFuture = true
            });

            return root;
        }
    }
}

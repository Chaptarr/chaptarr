using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
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
            public List<Book> BooksWithFiles { get; set; } = new();
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

                if (targetMethod.Name == nameof(IBookService.GetAuthorBooksWithFiles))
                {
                    return BooksWithFiles;
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
                        Monitored = settings?.Monitored,
                        MonitorExistingMode = settings?.MonitorExistingMode,
                        MonitorNewItems = settings?.MonitorNewItems,
                        Tags = settings?.Tags ?? new List<int>(),
                        IsConfigured = RootFolderSettingsResolver.HasRequiredProfiles(settings),
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
                Assert.That(author.AudiobookMonitored, Is.True);
                Assert.That(author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                Assert.That(author.EbookRootFolderPath, Is.Null);
                Assert.That(author.EbookPath, Is.Null);
                Assert.That(author.EbookQualityProfileId, Is.Null);
                Assert.That(author.EbookMonitored, Is.Null);
                Assert.That(author.EbookMonitorNewItems, Is.Null);
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
                Assert.That(author.EbookMonitored, Is.True);
                Assert.That(author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            });
        }

        [Test]
        public void mixed_root_scan_should_link_the_complete_side_and_skip_the_incomplete_side()
        {
            var root = BuildMixedRoot();
            root.SetEbookSettings(new MediaTypeSettings
            {
                QualityProfileId = 11,
                Monitored = true
            });
            var author = new Author { Id = 1, Name = "Example Author" };
            var files = new List<string>
            {
                "/library/Example Author/Audio.mp3".AsOsAgnostic(),
                "/library/Example Author/Text.epub".AsOsAgnostic()
            };
            var service = BuildSubject(files);

            var update = service.LinkAuthorToFolder(
                author,
                root,
                "/library/Example Author".AsOsAgnostic());

            Assert.Multiple(() =>
            {
                Assert.That(update, Is.Not.Null);
                Assert.That(author.AudiobookRootFolderPath, Is.EqualTo(root.Path));
                Assert.That(author.AudiobookQualityProfileId, Is.EqualTo(10));
                Assert.That(author.EbookRootFolderPath, Is.Null);
                Assert.That(author.EbookPath, Is.Null);
                Assert.That(author.EbookQualityProfileId, Is.Null);
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

        [TestCase(MonitorTypes.All, true, true)]
        [TestCase(MonitorTypes.Missing, false, true)]
        [TestCase(MonitorTypes.Existing, true, false)]
        [TestCase(MonitorTypes.None, false, false)]
        public void root_folder_scan_should_apply_the_one_time_book_mode_without_changing_the_author_gate(
            MonitorTypes mode,
            bool fileBackedExpected,
            bool missingExpected)
        {
            var root = BuildMixedRoot();
            var audiobookSettings = root.GetAudiobookSettings();
            audiobookSettings.MonitorExistingMode = mode;
            root.SetAudiobookSettings(audiobookSettings);
            var author = new Author
            {
                Id = 1,
                Name = "Example Author",
                AudiobookMonitored = false
            };
            var fileBacked = new Book
            {
                Id = 10,
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = !fileBackedExpected
            };
            var missing = new Book
            {
                Id = 11,
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = !missingExpected
            };
            var service = BuildSubject(
                new List<string> { "/library/Example Author/Book.mp3" },
                out _,
                out var bookService);
            bookService.Books = new List<Book> { fileBacked, missing };
            bookService.BooksWithFiles = new List<Book> { fileBacked };

            service.LinkAuthorToFolder(author, root, "/library/Example Author");

            Assert.Multiple(() =>
            {
                Assert.That(fileBacked.AudiobookMonitored, Is.EqualTo(fileBackedExpected));
                Assert.That(missing.AudiobookMonitored, Is.EqualTo(missingExpected));
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(bookService.UpdatedBooks, Has.Count.EqualTo(2));
            });
        }

        private static RootFolderScanService BuildSubject(List<string> files)
        {
            return BuildSubject(files, out _);
        }

        private static RootFolderScanService BuildSubject(List<string> files, out AuthorServiceProxy authorServiceProxy)
        {
            return BuildSubject(files, out authorServiceProxy, out _);
        }

        private static RootFolderScanService BuildSubject(
            List<string> files,
            out AuthorServiceProxy authorServiceProxy,
            out BookServiceProxy bookServiceProxy)
        {
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            ((DiskProviderProxy)(object)diskProvider).Files = files;

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            authorServiceProxy = (AuthorServiceProxy)(object)authorService;

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            bookServiceProxy = (BookServiceProxy)(object)bookService;
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
                MonitorExistingMode = MonitorTypes.None,
                Monitored = true,
                MonitorNewItems = NewItemMonitorTypes.New
            });
            root.SetEbookSettings(new MediaTypeSettings
            {
                QualityProfileId = 11,
                MetadataProfileId = 21,
                MonitorExistingMode = MonitorTypes.None,
                Monitored = true,
                MonitorNewItems = NewItemMonitorTypes.New
            });

            return root;
        }
    }
}

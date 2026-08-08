using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class AuthorFolderPathResolverFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class BuildFileNamesProxy : DispatchProxy
        {
            public Func<Author, string, string> AuthorFolderFactory { get; set; }
            public Func<Author, Edition, BookFile, string> BookFileNameFactory { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBuildFileNames.GetAuthorFolder))
                {
                    var author = args?[0] as Author;
                    var mediaType = args != null && args.Length > 2 ? args[2] as string : null;
                    return AuthorFolderFactory?.Invoke(author, mediaType);
                }

                if (targetMethod?.Name == nameof(IBuildFileNames.BuildBookFileName))
                {
                    return BookFileNameFactory?.Invoke(args?[0] as Author, args?[1] as Edition, args?[2] as BookFile);
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IBuildFileNames).Name}.{targetMethod?.Name}");
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public HashSet<string> ExistingFolders { get; } = new(PathEqualityComparer.Instance);
            public HashSet<string> ExistingFiles { get; } = new(PathEqualityComparer.Instance);
            public Dictionary<string, string[]> DirectoriesByParent { get; } = new(PathEqualityComparer.Instance);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.FolderExists) && args?[0] is string folderPath)
                {
                    return ExistingFolders.Contains(folderPath);
                }

                if (targetMethod?.Name == nameof(IDiskProvider.FileExists) && args?[0] is string filePath)
                {
                    return ExistingFiles.Contains(filePath);
                }

                if (targetMethod?.Name == nameof(IDiskProvider.GetDirectories) && args?[0] is string parentPath)
                {
                    return DirectoriesByParent.TryGetValue(parentPath, out var directories)
                        ? directories.ToList()
                        : new List<string>();
                }

                if (targetMethod?.Name == nameof(IDiskProvider.RemoveEmptySubfolders))
                {
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IDiskProvider).Name}.{targetMethod?.Name}");
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            public Func<int, List<Edition>> GetEditionsByBookFactory { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEditionsByBook) &&
                    args?.Length == 1 &&
                    args[0] is int bookId)
                {
                    return GetEditionsByBookFactory?.Invoke(bookId);
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IEditionService).Name}.{targetMethod?.Name}");
            }
        }

        private class RootFolderWatchingServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderWatchingService.ReportFileSystemChangeBeginning))
                {
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IRootFolderWatchingService).Name}.{targetMethod?.Name}");
            }
        }

        private class NonColocatingPlannerProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEbookColocationPlanner.Plan))
                {
                    return EbookColocationPlan.Skipped(EbookColocationSkipReason.NotEbook);
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IEbookColocationPlanner).Name}.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_not_append_author_folder_when_root_already_points_to_author_folder()
        {
            var fileNameBuilder = DispatchProxy.Create<IBuildFileNames, BuildFileNamesProxy>();
            ((BuildFileNamesProxy)(object)fileNameBuilder).AuthorFolderFactory = (author, _) => "A. F. Kay";

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFolders.Add("/library/A.F. Kay");

            var resolver = new AuthorFolderPathResolver(fileNameBuilder, diskProvider, LogManager.GetCurrentClassLogger());
            var author = new Author { Name = "A. F. Kay" };

            var result = resolver.GetAuthorPath("/library/A.F. Kay/", author, "audiobook");

            Assert.That(result, Is.EqualTo("/library/A.F. Kay"));
        }

        [Test]
        public void should_reuse_existing_author_folder_with_different_formatting()
        {
            var fileNameBuilder = DispatchProxy.Create<IBuildFileNames, BuildFileNamesProxy>();
            ((BuildFileNamesProxy)(object)fileNameBuilder).AuthorFolderFactory = (author, _) => "A. F. Kay";

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFolders.Add("/library");
            diskProxy.DirectoriesByParent["/library"] = new[] { "/library/A.F. Kay" };

            var resolver = new AuthorFolderPathResolver(fileNameBuilder, diskProvider, LogManager.GetCurrentClassLogger());
            var author = new Author { Name = "A.F. Kay" };

            var result = resolver.GetAuthorPath("/library", author, "audiobook");

            Assert.That(result, Is.EqualTo("/library/A.F. Kay"));
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_continue_move_when_book_context_can_be_rehydrated()
        {
            var fileNameBuilder = DispatchProxy.Create<IBuildFileNames, BuildFileNamesProxy>();
            var fileNameProxy = (BuildFileNamesProxy)(object)fileNameBuilder;
            fileNameProxy.AuthorFolderFactory = (author, _) => author.Name;
            fileNameProxy.BookFileNameFactory = (_, _, _) => "file";

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFolders.Add("/library/Test Author");
            diskProxy.ExistingFiles.Add("/library/Test Author/file.mp3");

            var resolver = new AuthorFolderPathResolver(fileNameBuilder, diskProvider, LogManager.GetCurrentClassLogger());

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).GetEditionsByBookFactory = bookId => new List<Edition>
            {
                new Edition
                {
                    Id = 7,
                    BookId = bookId,
                    Title = "Test Book",
                    Book = new Book
                    {
                        Id = bookId,
                        AuthorId = 1,
                        MediaType = BookMediaType.Audiobook
                    }
                }
            };

            var service = new BookFileMovingService(
                editionService,
                DispatchProxy.Create<IUpdateBookFileService, ThrowingProxy<IUpdateBookFileService>>(),
                fileNameBuilder,
                DispatchProxy.Create<INamingConfigService, ThrowingProxy<INamingConfigService>>(),
                DispatchProxy.Create<IEbookColocationPlanner, NonColocatingPlannerProxy>(),
                DispatchProxy.Create<IDiskTransferService, ThrowingProxy<IDiskTransferService>>(),
                diskProvider,
                DispatchProxy.Create<IRecycleBinProvider, ThrowingProxy<IRecycleBinProvider>>(),
                DispatchProxy.Create<IRootFolderWatchingService, RootFolderWatchingServiceProxy>(),
                DispatchProxy.Create<IMediaFileAttributeService, ThrowingProxy<IMediaFileAttributeService>>(),
                resolver,
                DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                DispatchProxy.Create<IFileMutationSafetyService, ThrowingProxy<IFileMutationSafetyService>>(),
                LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookRootFolderPath = "/library/Test Author/"
            };

            var bookFile = new BookFile
            {
                Path = "/library/Test Author/file.mp3",
                EditionId = 7,
                Edition = new Edition { Id = 7, BookId = 42, Title = "Test Book" },
                Quality = new QualityModel(Quality.MP3),
                MediaType = "audiobook"
            };

            Assert.Throws<SameFilenameException>(() => service.MoveBookFile(bookFile, author));
        }

        [Test]
        public void should_throw_clear_error_when_book_context_cannot_be_rehydrated()
        {
            var fileNameBuilder = DispatchProxy.Create<IBuildFileNames, BuildFileNamesProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var resolver = new AuthorFolderPathResolver(fileNameBuilder, diskProvider, LogManager.GetCurrentClassLogger());

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).GetEditionsByBookFactory = _ => new List<Edition>();

            var service = new BookFileMovingService(
                editionService,
                DispatchProxy.Create<IUpdateBookFileService, ThrowingProxy<IUpdateBookFileService>>(),
                fileNameBuilder,
                DispatchProxy.Create<INamingConfigService, ThrowingProxy<INamingConfigService>>(),
                DispatchProxy.Create<IEbookColocationPlanner, NonColocatingPlannerProxy>(),
                DispatchProxy.Create<IDiskTransferService, ThrowingProxy<IDiskTransferService>>(),
                diskProvider,
                DispatchProxy.Create<IRecycleBinProvider, ThrowingProxy<IRecycleBinProvider>>(),
                DispatchProxy.Create<IRootFolderWatchingService, RootFolderWatchingServiceProxy>(),
                DispatchProxy.Create<IMediaFileAttributeService, ThrowingProxy<IMediaFileAttributeService>>(),
                resolver,
                DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                DispatchProxy.Create<IFileMutationSafetyService, ThrowingProxy<IFileMutationSafetyService>>(),
                LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookRootFolderPath = "/library/Test Author/"
            };

            var bookFile = new BookFile
            {
                Path = "/library/Test Author/file.mp3",
                EditionId = 7,
                Edition = new Edition { Id = 7, BookId = 42, Title = "Test Book" },
                Quality = new QualityModel(Quality.MP3),
                MediaType = "audiobook"
            };

            var exception = Assert.Throws<InvalidOperationException>(() => service.MoveBookFile(bookFile, author));

            Assert.That(exception.Message, Does.Contain("missing book context"));
            Assert.That(exception.Message, Does.Contain("edition '7'"));
        }
    }
}

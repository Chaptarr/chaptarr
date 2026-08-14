using System;
using System.Collections.Generic;
using System.IO;
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
    public class BookFileMovingServiceFixture
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
            public Func<Author, Edition, BookFile, NamingConfig, string> BookFileNameFactory { get; set; }

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
                    return BookFileNameFactory?.Invoke(args?[0] as Author, args?[1] as Edition, args?[2] as BookFile, args?[3] as NamingConfig);
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

        private class AuthorPathBuilderProxy : DispatchProxy
        {
            public Func<Author, Quality, string> PathFactory { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBuildAuthorPaths.BuildPathForQuality))
                {
                    return PathFactory?.Invoke((Author)args[0], (Quality)args[1]);
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IBuildAuthorPaths).Name}.{targetMethod?.Name}");
            }
        }

        private class NamingConfigServiceProxy : DispatchProxy
        {
            public NamingConfig Config { get; set; } = NamingConfig.Default;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(INamingConfigService.GetConfig))
                {
                    return Config;
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(INamingConfigService).Name}.{targetMethod?.Name}");
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

        private class ColocatingPlannerProxy : DispatchProxy
        {
            public EbookColocationPlan PlanResult { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEbookColocationPlanner.Plan))
                {
                    return PlanResult;
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IEbookColocationPlanner).Name}.{targetMethod?.Name}");
            }
        }

        private static BookFileMovingService CreateService(
            IBuildFileNames fileNameBuilder,
            INamingConfigService namingConfigService,
            IDiskProvider diskProvider = null,
            IBuildAuthorPaths authorPathBuilder = null,
            IEditionService editionService = null,
            IEbookColocationPlanner ebookColocationPlanner = null)
        {
            return new BookFileMovingService(
                editionService ?? DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                DispatchProxy.Create<IUpdateBookFileService, ThrowingProxy<IUpdateBookFileService>>(),
                fileNameBuilder,
                authorPathBuilder ?? DispatchProxy.Create<IBuildAuthorPaths, ThrowingProxy<IBuildAuthorPaths>>(),
                namingConfigService,
                ebookColocationPlanner ?? DispatchProxy.Create<IEbookColocationPlanner, NonColocatingPlannerProxy>(),
                DispatchProxy.Create<IDiskTransferService, ThrowingProxy<IDiskTransferService>>(),
                diskProvider ?? DispatchProxy.Create<IDiskProvider, DiskProviderProxy>(),
                DispatchProxy.Create<IRecycleBinProvider, ThrowingProxy<IRecycleBinProvider>>(),
                DispatchProxy.Create<IRootFolderWatchingService, RootFolderWatchingServiceProxy>(),
                DispatchProxy.Create<IMediaFileAttributeService, ThrowingProxy<IMediaFileAttributeService>>(),
                DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                DispatchProxy.Create<IFileMutationSafetyService, ThrowingProxy<IFileMutationSafetyService>>(),
                LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_keep_physical_author_folder_unless_canonical_move_is_requested()
        {
            var rootFolder = @"C:\books".AsOsAgnostic();
            var sourceAuthorFolder = Path.Combine(rootFolder, "George R. R. Martin");
            var canonicalAuthorFolder = Path.Combine(rootFolder, "George R.R. Martin");
            var fileNameBuilder = DispatchProxy.Create<IBuildFileNames, BuildFileNamesProxy>();
            var fileNameProxy = (BuildFileNamesProxy)(object)fileNameBuilder;
            fileNameProxy.AuthorFolderFactory = (_, _) => "George R.R. Martin";
            fileNameProxy.BookFileNameFactory = (_, _, _, _) => Path.Combine("Wild Cards", "file");
            var namingConfigService = DispatchProxy.Create<INamingConfigService, NamingConfigServiceProxy>();
            var service = CreateService(fileNameBuilder, namingConfigService);
            var author = new Author
            {
                Id = 1,
                Name = "George R.R. Martin",
                AudiobookRootFolderPath = rootFolder
            };
            var bookFile = new BookFile
            {
                Path = Path.Combine(sourceAuthorFolder, "Wild Cards", "original.mp3"),
                EditionId = 7,
                Edition = new Edition
                {
                    Id = 7,
                    BookId = 42,
                    Book = new Book { Id = 42, AuthorId = author.Id }
                },
                Quality = new QualityModel(Quality.MP3),
                MediaType = "audiobook"
            };

            var keep = service.GetOrganizeDestination(bookFile, author, false);
            var canonical = service.GetOrganizeDestination(bookFile, author, true);

            Assert.That(keep.CanOrganize, Is.True);
            Assert.That(keep.SourceAuthorFolderPath, Is.EqualTo(sourceAuthorFolder));
            Assert.That(keep.DestinationPath, Is.EqualTo(Path.Combine(sourceAuthorFolder, "Wild Cards", "file.mp3")));
            Assert.That(keep.ShouldUpdateStoredAuthorPath, Is.False);
            Assert.That(canonical.DestinationPath, Is.EqualTo(Path.Combine(canonicalAuthorFolder, "Wild Cards", "file.mp3")));
            Assert.That(canonical.ShouldUpdateStoredAuthorPath, Is.True);
        }

        [Test]
        public void should_let_colocation_override_canonical_ebook_folder_without_updating_stored_path()
        {
            var rootFolder = @"C:\mixed".AsOsAgnostic();
            var sourceAuthorFolder = Path.Combine(rootFolder, "George R. R. Martin");
            var colocatedPath = Path.Combine(sourceAuthorFolder, "Wild Cards", "file.epub");
            var fileNameBuilder = DispatchProxy.Create<IBuildFileNames, BuildFileNamesProxy>();
            var fileNameProxy = (BuildFileNamesProxy)(object)fileNameBuilder;
            fileNameProxy.AuthorFolderFactory = (_, _) => "George R.R. Martin";
            fileNameProxy.BookFileNameFactory = (_, _, _, _) => Path.Combine("Wild Cards", "file");
            var namingConfigService = DispatchProxy.Create<INamingConfigService, NamingConfigServiceProxy>();
            var colocationPlanner = DispatchProxy.Create<IEbookColocationPlanner, ColocatingPlannerProxy>();
            ((ColocatingPlannerProxy)(object)colocationPlanner).PlanResult = new EbookColocationPlan
            {
                Applies = true,
                PrimaryPath = colocatedPath,
                ReplicaPaths = new List<string>()
            };
            var service = CreateService(
                fileNameBuilder,
                namingConfigService,
                ebookColocationPlanner: colocationPlanner);
            var author = new Author
            {
                Id = 1,
                Name = "George R.R. Martin",
                EbookRootFolderPath = rootFolder,
                EbookPath = sourceAuthorFolder
            };
            var bookFile = new BookFile
            {
                Path = Path.Combine(sourceAuthorFolder, "Wild Cards", "original.epub"),
                EditionId = 7,
                Edition = new Edition
                {
                    Id = 7,
                    BookId = 42,
                    Book = new Book { Id = 42, AuthorId = author.Id }
                },
                Quality = new QualityModel(Quality.EPUB),
                MediaType = "ebook"
            };

            var plan = service.GetOrganizeDestination(bookFile, author, true);

            Assert.That(plan.CanOrganize, Is.True);
            Assert.That(plan.DestinationPath, Is.EqualTo(colocatedPath));
            Assert.That(plan.DestinationAuthorFolderPath, Is.EqualTo(sourceAuthorFolder));
            Assert.That(plan.ShouldUpdateStoredAuthorPath, Is.False);
        }

        [Test]
        public void should_skip_organize_when_physical_author_folder_cannot_be_proven()
        {
            var fileNameBuilder = DispatchProxy.Create<IBuildFileNames, BuildFileNamesProxy>();
            var fileNameProxy = (BuildFileNamesProxy)(object)fileNameBuilder;
            fileNameProxy.AuthorFolderFactory = (_, _) => "Joe Abercrombie";
            fileNameProxy.BookFileNameFactory = (_, _, _, _) => "file";
            var namingConfigService = DispatchProxy.Create<INamingConfigService, NamingConfigServiceProxy>();
            var service = CreateService(fileNameBuilder, namingConfigService);
            var author = new Author { Id = 1, Name = "Joe Abercrombie", EbookRootFolderPath = "/ebooks" };
            var bookFile = new BookFile
            {
                Path = "/ebooks/file.epub",
                EditionId = 7,
                Edition = new Edition { Id = 7, BookId = 42, Book = new Book { Id = 42, AuthorId = author.Id } },
                Quality = new QualityModel(Quality.EPUB),
                MediaType = "ebook"
            };

            var plan = service.GetOrganizeDestination(bookFile, author, false);

            Assert.That(plan.CanOrganize, Is.False);
            Assert.That(plan.SkipReason, Does.Contain("current author folder cannot be determined"));
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void should_honor_media_type_rename_settings_in_shared_planner(bool renameAudiobooks, bool renameEbooks)
        {
            var audiobookRoot = @"C:\audiobooks".AsOsAgnostic();
            var ebookRoot = @"C:\ebooks".AsOsAgnostic();
            var fileNameBuilder = DispatchProxy.Create<IBuildFileNames, BuildFileNamesProxy>();
            var fileNameProxy = (BuildFileNamesProxy)(object)fileNameBuilder;
            fileNameProxy.AuthorFolderFactory = (author, _) => author.Name;
            fileNameProxy.BookFileNameFactory = (_, _, file, config) =>
            {
                var effective = config.GetForMediaType(file.MediaType);
                return Path.Combine("Book", effective.RenameBooks ? "renamed" : "original");
            };
            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = renameAudiobooks;
            namingConfig.EbookRenameBooks = renameEbooks;
            var namingConfigService = DispatchProxy.Create<INamingConfigService, NamingConfigServiceProxy>();
            ((NamingConfigServiceProxy)(object)namingConfigService).Config = namingConfig;
            var service = CreateService(fileNameBuilder, namingConfigService);
            var author = new Author
            {
                Id = 1,
                Name = "Joe Abercrombie",
                AudiobookRootFolderPath = audiobookRoot,
                EbookRootFolderPath = ebookRoot
            };
            var book = new Book { Id = 42, AuthorId = author.Id };
            var audiobook = new BookFile
            {
                Path = Path.Combine(audiobookRoot, "Joe", "Book", "original.mp3"),
                EditionId = 7,
                Edition = new Edition { Id = 7, BookId = book.Id, Book = book },
                Quality = new QualityModel(Quality.MP3),
                MediaType = "audiobook"
            };
            var ebook = new BookFile
            {
                Path = Path.Combine(ebookRoot, "Joe", "Book", "original.epub"),
                EditionId = 8,
                Edition = new Edition { Id = 8, BookId = book.Id, Book = book },
                Quality = new QualityModel(Quality.EPUB),
                MediaType = "ebook"
            };

            var audiobookPlan = service.GetOrganizeDestination(audiobook, author, false);
            var ebookPlan = service.GetOrganizeDestination(ebook, author, false);

            Assert.That(audiobookPlan.DestinationPath, Does.EndWith(Path.Combine("Book", renameAudiobooks ? "renamed.mp3" : "original.mp3")));
            Assert.That(ebookPlan.DestinationPath, Does.EndWith(Path.Combine("Book", renameEbooks ? "renamed.epub" : "original.epub")));
        }

        [Test]
        public void should_route_managed_import_to_stored_media_author_path()
        {
            var rootFolder = @"C:\library".AsOsAgnostic();
            var authorFolder = Path.Combine(rootFolder, "A.F. Kay");
            var fileNameBuilder = DispatchProxy.Create<IBuildFileNames, BuildFileNamesProxy>();
            var fileNameProxy = (BuildFileNamesProxy)(object)fileNameBuilder;
            fileNameProxy.BookFileNameFactory = (_, _, _, _) => Path.Combine("Book", "file");
            var namingConfigService = DispatchProxy.Create<INamingConfigService, NamingConfigServiceProxy>();
            var authorPathBuilder = DispatchProxy.Create<IBuildAuthorPaths, AuthorPathBuilderProxy>();
            ((AuthorPathBuilderProxy)(object)authorPathBuilder).PathFactory = (_, _) => authorFolder;
            var service = CreateService(fileNameBuilder, namingConfigService, authorPathBuilder: authorPathBuilder);
            var author = new Author
            {
                Id = 1,
                Name = "A. F. Kay",
                AudiobookRootFolderPath = rootFolder,
                AudiobookPath = authorFolder
            };
            var book = new Book { Id = 42, AuthorId = author.Id, Author = author };
            var edition = new Edition { Id = 7, BookId = book.Id, Book = book };
            var bookFile = new BookFile
            {
                Quality = new QualityModel(Quality.MP3),
                MediaType = "audiobook"
            };
            var localBook = new NzbDrone.Core.Parser.Model.LocalBook
            {
                Path = Path.Combine(@"C:\downloads".AsOsAgnostic(), "file.mp3"),
                Author = author,
                Book = book,
                Edition = edition,
                Quality = bookFile.Quality
            };

            var destination = service.GetImportDestinationPath(bookFile, localBook);

            Assert.That(destination, Is.EqualTo(Path.Combine(authorFolder, "Book", "file.mp3")));
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_continue_move_when_book_context_can_be_rehydrated()
        {
            var fileNameBuilder = DispatchProxy.Create<IBuildFileNames, BuildFileNamesProxy>();
            var fileNameProxy = (BuildFileNamesProxy)(object)fileNameBuilder;
            fileNameProxy.AuthorFolderFactory = (author, _) => author.Name;
            fileNameProxy.BookFileNameFactory = (_, _, _, _) => "file";

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFolders.Add("/library");
            diskProxy.ExistingFolders.Add("/library/Test Author");
            diskProxy.ExistingFiles.Add("/library/Test Author/file.mp3");

            var authorPathBuilder = DispatchProxy.Create<IBuildAuthorPaths, AuthorPathBuilderProxy>();
            ((AuthorPathBuilderProxy)(object)authorPathBuilder).PathFactory = (_, _) => "/library/Test Author";
            var namingConfigService = DispatchProxy.Create<INamingConfigService, NamingConfigServiceProxy>();
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
                authorPathBuilder,
                namingConfigService,
                DispatchProxy.Create<IEbookColocationPlanner, NonColocatingPlannerProxy>(),
                DispatchProxy.Create<IDiskTransferService, ThrowingProxy<IDiskTransferService>>(),
                diskProvider,
                DispatchProxy.Create<IRecycleBinProvider, ThrowingProxy<IRecycleBinProvider>>(),
                DispatchProxy.Create<IRootFolderWatchingService, RootFolderWatchingServiceProxy>(),
                DispatchProxy.Create<IMediaFileAttributeService, ThrowingProxy<IMediaFileAttributeService>>(),
                DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                DispatchProxy.Create<IFileMutationSafetyService, ThrowingProxy<IFileMutationSafetyService>>(),
                LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookRootFolderPath = "/library"
            };

            var bookFile = new BookFile
            {
                Path = "/library/Test Author/file.mp3",
                EditionId = 7,
                Edition = new Edition { Id = 7, BookId = 42, Title = "Test Book" },
                Quality = new QualityModel(Quality.MP3),
                MediaType = "audiobook"
            };

            var plan = service.GetOrganizeDestination(bookFile, author, false);
            Assert.Throws<SameFilenameException>(() => service.MoveBookFile(bookFile, author, plan));
        }

        [Test]
        public void should_throw_clear_error_when_book_context_cannot_be_rehydrated()
        {
            var fileNameBuilder = DispatchProxy.Create<IBuildFileNames, BuildFileNamesProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var authorPathBuilder = DispatchProxy.Create<IBuildAuthorPaths, AuthorPathBuilderProxy>();
            var namingConfigService = DispatchProxy.Create<INamingConfigService, NamingConfigServiceProxy>();
            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editionService).GetEditionsByBookFactory = _ => new List<Edition>();

            var service = new BookFileMovingService(
                editionService,
                DispatchProxy.Create<IUpdateBookFileService, ThrowingProxy<IUpdateBookFileService>>(),
                fileNameBuilder,
                authorPathBuilder,
                namingConfigService,
                DispatchProxy.Create<IEbookColocationPlanner, NonColocatingPlannerProxy>(),
                DispatchProxy.Create<IDiskTransferService, ThrowingProxy<IDiskTransferService>>(),
                diskProvider,
                DispatchProxy.Create<IRecycleBinProvider, ThrowingProxy<IRecycleBinProvider>>(),
                DispatchProxy.Create<IRootFolderWatchingService, RootFolderWatchingServiceProxy>(),
                DispatchProxy.Create<IMediaFileAttributeService, ThrowingProxy<IMediaFileAttributeService>>(),
                DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                DispatchProxy.Create<IFileMutationSafetyService, ThrowingProxy<IFileMutationSafetyService>>(),
                LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookRootFolderPath = "/library"
            };

            var bookFile = new BookFile
            {
                Path = "/library/Test Author/file.mp3",
                EditionId = 7,
                Edition = new Edition { Id = 7, BookId = 42, Title = "Test Book" },
                Quality = new QualityModel(Quality.MP3),
                MediaType = "audiobook"
            };

            var plan = new BookFileMovePlan
            {
                CanOrganize = true,
                DestinationPath = bookFile.Path
            };

            var exception = Assert.Throws<InvalidOperationException>(() => service.MoveBookFile(bookFile, author, plan));

            Assert.That(exception.Message, Does.Contain("missing book context"));
            Assert.That(exception.Message, Does.Contain("edition '7'"));
        }
    }
}

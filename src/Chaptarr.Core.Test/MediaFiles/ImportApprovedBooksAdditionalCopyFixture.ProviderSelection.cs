using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Books;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        [Test]
        public void provider_selection_pins_only_after_real_file_persistence(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(false, true)] bool previouslyPinned,
            [Values(false, true)] bool targetAlreadyLocal,
            [Values(false, true)] bool transferFails)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "provider-selection-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var events = new NoOpEventAggregator();
                    T Optional<T>() where T : class => InterfaceProxy.Proxy<T>((method, _) =>
                        method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
                    var (qualityProfile, author, book, oldEdition) = CreateAudiobookConversionGraph(397);
                    var libraryPath = Path.Combine(root, "library");
                    Directory.CreateDirectory(libraryPath);
                    var extension = mediaType == BookMediaType.Audiobook ? ".m4b" : ".epub";
                    var source = Path.Combine(root, "incoming" + extension);
                    var destination = Path.Combine(libraryPath, "incoming" + extension);
                    var oldPath = Path.Combine(libraryPath, "existing" + extension);
                    File.WriteAllText(source, "incoming recording");
                    File.WriteAllText(oldPath, "existing recording");
                    if (transferFails)
                    {
                        Directory.CreateDirectory(destination);
                    }

                    var profileRows = new MetadataProfileRepository(database, events);
                    var profile = profileRows.Insert(new MetadataProfile
                    {
                        Name = "English", AllowedLanguages = "eng", MinPopularity = 0, Ignored = new List<string>()
                    });
                    author.Id = 0;
                    author.Added = DateTime.UtcNow;
                    author.HardcoverAuthorId = "hc:93001";
                    author.Overview = string.Empty;
                    author.CleanName = "testwriter";
                    author.SortName = "testwriter";
                    author.TitleSlug = "test-writer";
                    author.Path = author.AudiobookPath = author.EbookPath = libraryPath;
                    author.AudiobookRootFolderPath = author.EbookRootFolderPath = libraryPath;
                    author.AudiobookMetadataProfileId = author.EbookMetadataProfileId = profile.Id;
                    author.AudiobookMetadataProfile = author.EbookMetadataProfile = profile;
                    author.EbookQualityProfileId = qualityProfile.Id;
                    author.EbookQualityProfile = qualityProfile;
                    var authors = new AuthorRepository(database, events, logger);
                    authors.Insert(author);
                    var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name switch
                    {
                        nameof(IAuthorService.FindByProviderId) or nameof(IAuthorService.GetAuthor) => author,
                        nameof(IAuthorService.GetExistingAuthors) or nameof(IAuthorService.GetAuthors) or nameof(IAuthorService.GetAllAuthors) => new List<Author> { author },
                        nameof(IAuthorService.UpdateAuthor) => authors.Update((Author)args[0]),
                        _ => throw new AssertionException(method.Name)
                    });
                    var rootFolder = new RootFolder { Id = 1, Path = libraryPath, FolderType = FolderType.Mixed };
                    rootFolder.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = qualityProfile.Id, MetadataProfileId = profile.Id });
                    rootFolder.SetEbookSettings(new MediaTypeSettings { QualityProfileId = qualityProfile.Id, MetadataProfileId = profile.Id });
                    var roots = InterfaceProxy.Proxy<IRootFolderService>((method, args) => method.Name switch
                    {
                        nameof(IRootFolderService.All) => new List<RootFolder> { rootFolder },
                        nameof(IRootFolderService.GetBestRootFolder) => ((string)args[0]).StartsWith(libraryPath, StringComparison.Ordinal) ? rootFolder : null,
                        _ => throw new AssertionException(method.Name)
                    });
                    var bookRows = new BookRepository(database, events);
                    var editionRows = new EditionRepository(database, events);
                    var editions = new EditionService(editionRows, events, logger);
                    var fileRows = new MediaFileRepository(database, events);
                    var media = new MediaFileService(fileRows, events, null, null, logger, roots);
                    var books = new BookService(bookRows, editions, events, authorService, media, roots,
                        new SeriesBookLinkRepository(database, events), Optional<IMultiCopySeriesService>(), logger);
                    book.Id = 0;
                    book.AuthorId = author.Id;
                    book.HardcoverBookId = "hc:93002";
                    book.CleanTitle = "blacksheep";
                    book.Overview = string.Empty;
                    book.Added = DateTime.UtcNow;
                    book.MediaType = mediaType;
                    book.AnyEditionOk = !previouslyPinned;
                    book.AddOptions = new AddBookOptions { AddType = BookAddType.Automatic, SearchForNewBook = true };
                    bookRows.Insert(book);
                    oldEdition.Id = 0;
                    oldEdition.BookId = book.Id;
                    oldEdition.ForeignEditionId = "gr:93003";
                    oldEdition.Language = "eng";
                    oldEdition.Monitored = true;
                    oldEdition.ManualAdd = previouslyPinned;
                    oldEdition.ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : 3;
                    editionRows.Insert(oldEdition);
                    var selected = new Edition
                    {
                        BookId = book.Id, Title = book.Title, ForeignEditionId = "gr:93004",
                        ReadingFormatId = oldEdition.ReadingFormatId, Language = "fra",
                        NarratorNames = new List<string> { "Selected Narrator" }
                    };
                    if (targetAlreadyLocal) editionRows.Insert(selected);
                    var quality = new QualityModel(mediaType == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB);
                    var oldFile = fileRows.Insert(new BookFile
                    {
                        EditionId = oldEdition.Id, Edition = oldEdition, Path = oldPath, Quality = quality,
                        DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, MediaInfo = new MediaInfoModel()
                    });
                    Author Catalog()
                    {
                        var remoteBook = RefreshEntityCopy.CloneBook(book, false);
                        remoteBook.AnyEditionOk = true;
                        remoteBook.Editions = new List<Edition> { RefreshEntityCopy.CloneEdition(oldEdition), RefreshEntityCopy.CloneEdition(selected) };
                        return new Author { Name = author.Name, CleanName = author.CleanName, SortName = author.SortName,
                            TitleSlug = author.TitleSlug, Overview = string.Empty, HardcoverAuthorId = author.HardcoverAuthorId,
                            Books = new List<Book> { remoteBook }, Series = new List<Series>() };
                    }

                    var authorInfo = InterfaceProxy.Proxy<IProvideAuthorInfo>((_, _) => Catalog());
                    var profileService = new MetadataProfileService(profileRows, authorService, books, editions, media, null, roots,
                        new TestTermMatcherService(), events, logger);
                    var filter = new EditionMetadataProfileFilter(new TestTermMatcherService());
                    var selector = new EditionSelector(logger);
                    var queue = Optional<IManageCommandQueue>();
                    var configService = InterfaceProxy.Proxy<IConfigService>((method, _) => method.ReturnType == typeof(string)
                        ? libraryPath : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
                    var exclusions = InterfaceProxy.Proxy<IImportListExclusionService>((_, _) => new List<ImportListExclusion>());
                    var refreshEditions = new RefreshEditionService(editions, Optional<IMetadataTagService>(), null, database, logger);
                    var refreshBooks = new RefreshBookService(books, authorService, roots, editions, authorInfo, null,
                        refreshEditions, media, null, events, null, selector, filter, Optional<IMapCoversToLocal>(), logger, mainDatabase: null);
                    var refresh = new RefreshAuthorService(MetadataHealthGateStub.Ready(), authorInfo, authorService, books, editions, profileService, refreshBooks, null,
                        events, queue, media, new global::NzbDrone.Core.History.HistoryRepository(database, events), roots, null, Optional<IMonitorNewBookService>(), configService,
                        exclusions, null, null, null, logger, editionSelector: selector, mainDatabase: database, bookRepository: new BookRepository(database, events), authorRepository: new AuthorRepository(database, events, logger));
                    var authorLibrary = new AuthorLibraryService(authorService, authorInfo, books, null, editions, null, profileService,
                        null, null, roots, queue, events, null, database, exclusions, filter, null, logger, selector, refresh);
                    var history = new HistoryService(new HistoryRepository(database, events), logger);
                    var importer = new ImportApprovedBooks(media, new StubMetadataTagService(), new StubMediaInfoExtractor(),
                        authorService, books, editions, new StubRecycleBinProvider(), Proxy<IExtraService>(),
                        new StubMoveBookFiles { DestinationPath = destination, TransferFilesOnDisk = true },
                        history, Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), events, queue,
                        new SeriesBookLinkService(new SeriesBookLinkRepository(database, events)), Proxy<ISeriesService>(), null, Proxy<IM4bConversionService>(), logger);
                    var dependencies = new Dictionary<Type, object>
                    {
                        [typeof(Logger)] = logger,
                        [typeof(IConfigService)] = configService,
                        [typeof(IDiskProvider)] = InterfaceProxy.Proxy<IDiskProvider>((_, args) => new System.IO.Abstractions.FileSystem().FileInfo.FromFileName((string)args[0])),
                        [typeof(IRootFolderService)] = roots,
                        [typeof(IRootFolderSettingsResolver)] = new RootFolderSettingsResolver(roots),
                        [typeof(IAuthorService)] = authorService,
                        [typeof(IAuthorLibraryService)] = authorLibrary,
                        [typeof(IBookService)] = books,
                        [typeof(IEditionService)] = editions,
                        [typeof(IMetadataTagService)] = new StubMetadataTagService(),
                        [typeof(ITrackedDownloadService)] = InterfaceProxy.Proxy<ITrackedDownloadService>((_, _) => new List<TrackedDownload>()),
                        [typeof(IImportApprovedBooks)] = importer,
                        [typeof(IEventAggregator)] = events,
                        [typeof(IHistoryService)] = history
                    };
                    var constructor = typeof(ManualImportService).GetConstructors().Single();
                    var subject = (ManualImportService)constructor.Invoke(constructor.GetParameters().Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
                    var command = new ManualImportCommand
                    {
                        ImportMode = ImportMode.Copy, ReplaceExistingFiles = false,
                        Files = new List<ManualImportFile> { new()
                        {
                            Path = source, ForeignAuthorId = author.HardcoverAuthorId, ForeignBookId = book.HardcoverBookId,
                            ForeignEditionId = selected.ForeignEditionId, SelectionSource = ManualImportSelectionSource.UserMetadataSuggestion,
                            Quality = quality
                        } }
                    };
                    if (transferFails)
                    {
                        var error = Assert.Catch(() => subject.Execute(command));
                        Assert.That(error.Message, Does.Contain("Import failed"));
                        Assert.That(books.GetBook(book.Id).AnyEditionOk, Is.EqualTo(!previouslyPinned));
                        Assert.That(books.GetBook(book.Id).IsMonitored(), Is.False);
                        Assert.That(books.GetBook(book.Id).AddOptions.AddType, Is.EqualTo(BookAddType.Automatic));
                        Assert.That(books.GetBook(book.Id).AddOptions.SearchForNewBook, Is.True);
                        Assert.That(editions.GetEdition(oldEdition.Id).Monitored, Is.True);
                        Assert.That(editions.GetEdition(oldEdition.Id).ManualAdd, Is.EqualTo(previouslyPinned));
                        Assert.That(editions.GetEditionsByBook(book.Id).Any(edition => edition.ForeignEditionId == selected.ForeignEditionId && edition.ManualAdd), Is.False);
                        Assert.That(fileRows.GetFilesByBook(book.Id), Has.Count.EqualTo(1));
                    }
                    else
                    {
                        subject.Execute(command);
                        var importedEdition = editions.GetEditionsByBook(book.Id).Single(edition => edition.ForeignEditionId == selected.ForeignEditionId);
                        Assert.That(importedEdition.Monitored, Is.True);
                        Assert.That(importedEdition.ManualAdd, Is.True);
                        Assert.That(books.GetBook(book.Id).AnyEditionOk, Is.False);
                        Assert.That(books.GetBook(book.Id).IsMonitored(), Is.True);
                        Assert.That(books.GetBook(book.Id).AddOptions.AddType, Is.EqualTo(BookAddType.Manual));
                        Assert.That(books.GetBook(book.Id).AddOptions.SearchForNewBook, Is.False);
                        Assert.That(fileRows.GetFilesByBook(book.Id).Single(file => file.Path == destination).EditionId, Is.EqualTo(importedEdition.Id));
                        Assert.That(File.ReadAllText(destination), Is.EqualTo("incoming recording"));
                    }

                    Assert.That(fileRows.Get(oldFile.Id).EditionId, Is.EqualTo(oldEdition.Id));
                    Assert.That(File.ReadAllText(oldPath), Is.EqualTo("existing recording"));
                    Assert.That(File.ReadAllText(source), Is.EqualTo("incoming recording"));
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }
    }
}

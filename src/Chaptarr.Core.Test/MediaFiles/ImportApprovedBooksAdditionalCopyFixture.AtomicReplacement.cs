using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        [TestCase(false, "relink-transfer-failure")]
        [TestCase(true, "relink-transfer-failure")]
        [TestCase(false, "relink-database-failure")]
        [TestCase(true, "relink-database-failure")]
        [TestCase(false, "relocate-transfer-failure")]
        [TestCase(true, "relocate-transfer-failure")]
        [TestCase(false, "relocate-database-failure")]
        [TestCase(true, "relocate-database-failure")]
        [TestCase(false, "relocate-stale-database-failure")]
        [TestCase(true, "relocate-stale-database-failure")]
        [TestCase(false, "relocate-stale-success")]
        [TestCase(true, "relocate-stale-success")]
        [TestCase(false, "relink-edition-failure")]
        [TestCase(true, "relink-edition-failure")]
        [TestCase(false, "relocate-edition-failure")]
        [TestCase(true, "relocate-edition-failure")]
        [TestCase(false, "relocate-book-failure")]
        [TestCase(true, "relocate-book-failure")]
        [TestCase(false, "same-path-success")]
        [TestCase(true, "same-path-success")]
        public void replace_must_commit_or_restore_tracked_and_new_inputs_together(bool postgres, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "atomic-mixed-import-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var published = new List<object>();
                    var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) => { published.Add(args[0]); return null; });
                    var (profile, author, book, edition) = CreateAudiobookConversionGraph(311);
                    profile.ConvertToQualityId = null;
                    profile.UpgradeAllowed = true;
                    var library = Path.Combine(root, "library");
                    Directory.CreateDirectory(library);
                    author.Id = 0;
                    author.Path = library;
                    author.AudiobookPath = library;
                    author.AudiobookRootFolderPath = library;
                    author.CleanName = "testwriter";
                    author.SortName = "testwriter";
                    author.TitleSlug = "test-writer";
                    author.Added = DateTime.UtcNow;
                    new AuthorRepository(database, events, logger).Insert(author);
                    book.Id = 0;
                    book.AuthorId = author.Id;
                    book.CleanTitle = "blacksheep";
                    book.TitleSlug = "target";
                    book.Added = DateTime.UtcNow;
                    book.AnyEditionOk = true;
                    var books = new BookRepository(database, events);
                    books.Insert(book);
                    var editions = new EditionRepository(database, events);
                    edition.Id = 0;
                    edition.BookId = book.Id;
                    edition.Monitored = true;
                    editions.Insert(edition);
                    var selected = editions.Insert(new Edition { BookId = book.Id, Book = book, Title = book.Title,
                        ForeignEditionId = "local:selected", ReadingFormatId = 2 });
                    book.Editions = new() { edition, selected };
                    var trackedPath = Path.Combine(library, "tracked.mp3");
                    var oldPath = Path.Combine(library, "old.mp3");
                    var incoming = Path.Combine(root, "incoming.mp3");
                    var trackedDestination = scenario == "same-path-success" ? trackedPath : Path.Combine(library, "relocated.mp3");
                    var incomingDestination = Path.Combine(library, "new.mp3");
                    File.WriteAllText(trackedPath, "tracked original");
                    File.WriteAllText(oldPath, "old recording");
                    File.WriteAllText(incoming, "new recording");
                    var files = new MediaFileRepository(database, events);
                    BookFile AddFile(string path) => files.Insert(new BookFile { EditionId = edition.Id, Edition = edition,
                        Path = path, Quality = new(Quality.MP3), Part = 1, PartCount = 1,
                        DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, MediaInfo = new MediaInfoModel(),
                        Narrator = "Original narrator", MatchDetails = "original details" });
                    var tracked = AddFile(trackedPath);
                    var old = AddFile(oldPath);
                    var stale = scenario.Contains("stale", StringComparison.Ordinal) ? AddFile(trackedDestination) : null;
                    var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
                    {
                        nameof(IDiskProvider.FileExists) or nameof(IDiskProvider.FileExistsCanonical) => File.Exists((string)args[0]),
                        _ => throw new InvalidOperationException(method.Name)
                    });
                    var media = new MediaFileService(files, events, null, disk, logger);
                    var tags = new StubMetadataTagService();
                    var mover = new StubMoveBookFiles
                    {
                        TransferFilesOnDisk = true,
                        DestinationForFile = local => local.Path == trackedPath ? trackedDestination : incomingDestination,
                        FailSourcePath = scenario.EndsWith("transfer-failure", StringComparison.Ordinal) ? incoming : null
                    };
                    if (scenario.EndsWith("database-failure", StringComparison.Ordinal))
                    {
                        using var connection = database.OpenConnection();
                        connection.Execute(postgres
                            ? "CREATE FUNCTION reject_atomic_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'atomic insert refused'; END $$; CREATE TRIGGER reject_atomic_insert BEFORE INSERT ON \"BookFiles\" FOR EACH ROW EXECUTE FUNCTION reject_atomic_insert();"
                            : "CREATE TRIGGER reject_atomic_insert BEFORE INSERT ON \"BookFiles\" BEGIN SELECT RAISE(ABORT, 'atomic insert refused'); END;");
                    }

                    if (scenario.EndsWith("edition-failure", StringComparison.Ordinal) || scenario.EndsWith("book-failure", StringComparison.Ordinal))
                    {
                        var table = scenario.EndsWith("edition-failure", StringComparison.Ordinal) ? "Editions" : "Books";
                        var column = table == "Editions" ? "Monitored" : "AnyEditionOk";
                        using var connection = database.OpenConnection();
                        connection.Execute(postgres
                            ? $"CREATE FUNCTION reject_atomic_settings() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'atomic settings refused'; END $$; CREATE TRIGGER reject_atomic_settings BEFORE UPDATE OF \"{column}\" ON \"{table}\" FOR EACH ROW EXECUTE FUNCTION reject_atomic_settings();"
                            : $"CREATE TRIGGER reject_atomic_settings BEFORE UPDATE OF \"{column}\" ON \"{table}\" BEGIN SELECT RAISE(ABORT, 'atomic settings refused'); END;");
                    }

                    var editionService = new EditionService(editions, events, logger);
                    var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name == nameof(IAuthorService.GetAuthors) ? new List<Author> { author } : author);
                    var bookService = new BookService(books, editionService, events, authorService, media, Proxy<IRootFolderService>(), null, null, logger);
                    var service = new ImportApprovedBooks(media, tags, new StubMediaInfoExtractor(), authorService,
                        bookService, editionService, new StubRecycleBinProvider(), Proxy<IExtraService>(),
                        mover, Proxy<IHistoryService>(), Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), events,
                        Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(),
                        Proxy<IQualityProfileService>(), Proxy<IM4bConversionService>(), logger);
                    ImportDecision<LocalBook> Decision(string path, bool existing) => new(new LocalBook { Path = path,
                        Book = book, Edition = selected, Author = author, IsManualImport = true, DisableReleaseSwitching = true, ExistingFile = existing,
                        Size = new FileInfo(path).Length, Modified = DateTime.UtcNow, Quality = new(Quality.MP3),
                        Part = 1, PartCount = 2, Narrator = "Selected narrator" });
                    var decisions = new List<ImportDecision<LocalBook>>
                    {
                        Decision(trackedPath, scenario.StartsWith("relink", StringComparison.Ordinal)), Decision(incoming, false)
                    };
                    published.Clear();
                    List<ImportResult> results = null;
                    if (scenario.EndsWith("database-failure", StringComparison.Ordinal) || scenario.EndsWith("edition-failure", StringComparison.Ordinal) || scenario.EndsWith("book-failure", StringComparison.Ordinal))
                        Assert.Catch(() => service.Import(decisions, true, importMode: ImportMode.Copy));
                    else
                        results = service.Import(decisions, true, importMode: ImportMode.Copy);
                    var failed = scenario.EndsWith("failure", StringComparison.Ordinal);
                    Assert.Multiple(() =>
                    {
                        Assert.That(files.Get(tracked.Id).EditionId, Is.EqualTo(failed ? edition.Id : selected.Id));
                        Assert.That(files.Get(tracked.Id).Path, Is.EqualTo(failed || scenario.StartsWith("relink", StringComparison.Ordinal) ? trackedPath : trackedDestination));
                        Assert.That(files.Get(tracked.Id).Narrator, Is.EqualTo(failed ? "Original narrator" : "Selected narrator"));
                        Assert.That(books.Get(book.Id).AnyEditionOk, Is.EqualTo(failed));
                        Assert.That(editions.Get(edition.Id).Monitored, Is.EqualTo(failed));
                        Assert.That(editions.Get(selected.Id).Monitored, Is.EqualTo(!failed));
                        Assert.That(File.ReadAllText(incoming), Is.EqualTo("new recording"));
                        Assert.That(File.Exists(oldPath), Is.EqualTo(failed));
                        Assert.That(File.Exists(incomingDestination), Is.EqualTo(!failed));
                        Assert.That(tags.Writes.Count, Is.EqualTo(failed ? 0 : 2));
                        Assert.That(published.OfType<NzbDrone.Core.Books.Events.BookEditedEvent>().Count(), Is.EqualTo(failed ? 0 : 1));
                        Assert.That(published.OfType<TrackImportedEvent>().Count(), Is.EqualTo(failed ? 0 : 2));
                        Assert.That(published.OfType<BookImportedEvent>().Count(), Is.EqualTo(failed ? 0 : 1));
                        Assert.That(published.OfType<BookFileRenamedEvent>().Count(), Is.EqualTo(!failed && scenario.StartsWith("relocate", StringComparison.Ordinal) ? 1 : 0));
                        if (failed)
                        {
                            Assert.That(File.ReadAllText(trackedPath), Is.EqualTo("tracked original"));
                            Assert.That(File.ReadAllText(oldPath), Is.EqualTo("old recording"));
                            Assert.That(files.Get(old.Id).EditionId, Is.EqualTo(edition.Id));
                            if (stale != null) Assert.That(files.Get(stale.Id).Path, Is.EqualTo(trackedDestination));
                            if (results != null) Assert.That(results.All(result => result.Result != ImportResultType.Imported), Is.True);
                        }
                        else
                        {
                            Assert.That(files.GetFilesByBook(book.Id), Has.Count.EqualTo(2));
                            Assert.That(results.All(result => result.Result == ImportResultType.Imported), Is.True);
                        }
                    });
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }
        [TestCase(false, "unknown")]
        [TestCase(true, "unknown")]
        [TestCase(false, "other-book")]
        [TestCase(true, "other-book")]
        [TestCase(false, "required-audio")]
        [TestCase(true, "required-audio")]
        [TestCase(false, "quality-ebook")]
        [TestCase(true, "quality-ebook")]
        [TestCase(false, "alternative-ebook")]
        [TestCase(true, "alternative-ebook")]
        [TestCase(false, "metadata-existing")]
        [TestCase(true, "metadata-existing")]
        [TestCase(false, "metadata-new")]
        [TestCase(true, "metadata-new")]
        public void replacement_must_only_require_its_selected_recording_members(bool postgres, string rejectedKind)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "required-import-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var events = new NoOpEventAggregator();
                    var (profile, author, book, edition) = CreateAudiobookConversionGraph(312);
                    var ebook = rejectedKind.EndsWith("ebook", StringComparison.Ordinal);
                    var metadata = rejectedKind.StartsWith("metadata", StringComparison.Ordinal);
                    var quality = new QualityModel(ebook ? Quality.EPUB : Quality.MP3, new Revision(2));
                    profile.ConvertToQualityId = null;
                    profile.UpgradeAllowed = true;
                    author.Id = 0;
                    author.Path = root;
                    author.CleanName = "testwriter";
                    author.SortName = "testwriter";
                    author.TitleSlug = "test-writer";
                    author.Added = DateTime.UtcNow;
                    if (ebook)
                    {
                        profile.ProfileType = ProfileType.Ebook;
                        profile.Items = new() { new() { Quality = Quality.MOBI, Allowed = rejectedKind == "alternative-ebook" },
                            new() { Quality = Quality.EPUB, Allowed = true } };
                        author.EbookPath = root;
                        author.EbookQualityProfile = profile;
                        author.EbookQualityProfileId = profile.Id;
                        author.EbookRootFolderPath = root;
                    }
                    new AuthorRepository(database, events, logger).Insert(author);
                    book.Id = 0;
                    book.AuthorId = author.Id;
                    book.CleanTitle = "blacksheep";
                    book.TitleSlug = "target";
                    book.Added = DateTime.UtcNow;
                    book.AnyEditionOk = true;
                    book.MediaType = ebook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                    var books = new BookRepository(database, events);
                    books.Insert(book);
                    var editions = new EditionRepository(database, events);
                    edition.Id = 0;
                    edition.BookId = book.Id;
                    edition.Monitored = true;
                    edition.ReadingFormatId = ebook ? 3 : 2;
                    editions.Insert(edition);
                    book.Editions = new() { edition };
                    var extension = ebook ? ".epub" : ".mp3";
                    var incoming = Path.Combine(root, "incoming" + extension);
                    var old = Path.Combine(root, "old" + extension);
                    var destination = Path.Combine(root, "imported" + extension);
                    File.WriteAllText(incoming, "new recording");
                    var files = new MediaFileRepository(database, events);
                    if (rejectedKind != "metadata-new")
                    {
                        File.WriteAllText(old, "old recording");
                        files.Insert(new BookFile { Path = old, EditionId = edition.Id, Edition = edition,
                            Quality = new QualityModel(quality.Quality), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, MediaInfo = new MediaInfoModel(),
                            MediaType = ebook ? "ebook" : "audiobook" });
                    }
                    var media = new MediaFileService(files, events, null, null, logger);
                    var mover = new StubMoveBookFiles { DestinationPath = destination, TransferFilesOnDisk = true };
                    var tags = new StubMetadataTagService();
                    var editionService = new EditionService(editions, events, logger);
                    var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name == nameof(IAuthorService.GetAuthors) ? new List<Author> { author } : author);
                    var bookService = new BookService(books, editionService, events, authorService, media, Proxy<IRootFolderService>(), null, null, logger);
                    var service = new ImportApprovedBooks(media, tags, new StubMediaInfoExtractor(), authorService,
                        bookService, editionService, new StubRecycleBinProvider(), Proxy<IExtraService>(),
                        mover, Proxy<IHistoryService>(), Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), events,
                        Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(),
                        Proxy<IQualityProfileService>(), Proxy<IM4bConversionService>(), logger);
                    var local = new LocalBook { Path = incoming, Book = book, Edition = edition, Author = author,
                        Quality = quality, Size = new FileInfo(incoming).Length, Modified = DateTime.UtcNow,
                        IsManualImport = metadata, ConfirmedReplacementFiles = metadata ? new List<ManualImportReplacementFile>() : null,
                        MatchProvenance = metadata ? new MatchProvenance { SelectionSource = "user_metadata" } : null };
                    var decisions = new List<ImportDecision<LocalBook>> { new(local) };
                    if (!metadata)
                    {
                        var rejected = new ImportDecision<LocalBook>(new LocalBook { Path = Path.Combine(root, ebook ? "other.mobi" : "missing.mp3"),
                            Book = rejectedKind == "unknown" ? null : rejectedKind == "other-book" ? new Book { Id = book.Id + 100 } : book,
                            Edition = ebook ? edition : null, Author = author, Quality = new(ebook ? Quality.MOBI : Quality.MP3) });
                        if (!ebook)
                            rejected.Reject(new NzbDrone.Core.DecisionEngine.Rejection("Selected file unavailable"));
                        decisions.Add(rejected);
                    }
                    var results = service.Import(decisions, true, importMode: ImportMode.Copy);
                    var blocked = rejectedKind is "required-audio" or "metadata-existing";
                    Assert.That(results.Single(result => ReferenceEquals(result.ImportDecision.Item, local)).Result,
                        Is.EqualTo(blocked ? ImportResultType.Skipped : ImportResultType.Imported));
                    Assert.That(File.Exists(destination), Is.EqualTo(!blocked));
                    Assert.That(File.Exists(old), Is.EqualTo(blocked));
                    Assert.That(mover.CopyCalls, Is.EqualTo(blocked ? 0 : 1));
                    if (rejectedKind == "required-audio")
                    {
                        Assert.That(results.Single(result => ReferenceEquals(result.ImportDecision.Item, local)).Errors.Single(),
                            Does.Contain("a selected file was rejected").And.Contain("Existing files were kept"));
                    }
                    if (rejectedKind == "metadata-existing")
                    {
                        Assert.That(results.Single().Errors.Single(), Does.Contain($"Book ID {book.Id}"));
                        Assert.That(results.Single().Errors.Single(), Does.Contain("Select that local book in Manual Import"));
                    }
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void earlier_book_events_must_be_published_before_later_book_database_failure(bool postgres, bool settingsFailure)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "committed-group-events-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var history = new HistoryService(new HistoryRepository(database, new NoOpEventAggregator()), logger);
                    var published = new List<object>();
                    var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) =>
                    {
                        published.Add(args[0]);
                        if (args[0] is TrackImportedEvent imported) history.Handle(imported);
                        return null;
                    });
                    var (profile, author, first, firstEdition) = CreateAudiobookConversionGraph(313);
                    profile.ConvertToQualityId = null;
                    profile.UpgradeAllowed = true;
                    author.Id = 0;
                    author.Path = root;
                    author.CleanName = "testwriter";
                    author.SortName = "testwriter";
                    author.TitleSlug = "test-writer";
                    author.Added = DateTime.UtcNow;
                    new AuthorRepository(database, events, logger).Insert(author);
                    var books = new BookRepository(database, events);
                    var editions = new EditionRepository(database, events);
                    var files = new MediaFileRepository(database, events);
                    var decisions = new List<ImportDecision<LocalBook>>();
                    var targets = new List<(Book Book, Edition Edition, string Old, string Incoming, string Destination)>();
                    for (var index = 0; index < 3; index++)
                    {
                        var book = books.Insert(new Book { Author = author, AuthorId = author.Id, Title = "Book " + index,
                            CleanTitle = "book" + index, TitleSlug = "book" + index, Added = DateTime.UtcNow,
                            MediaType = BookMediaType.Audiobook, AnyEditionOk = true });
                        var edition = editions.Insert(new Edition { BookId = book.Id, Book = book, Title = book.Title,
                            ForeignEditionId = "local:" + index, ReadingFormatId = 2, Monitored = true });
                        book.Editions = new() { edition };
                        var old = Path.Combine(root, "old-" + index + ".mp3");
                        var incoming = Path.Combine(root, "incoming-" + index + ".mp3");
                        var destination = Path.Combine(root, "new-" + index + ".mp3");
                        File.WriteAllText(old, "old " + index);
                        File.WriteAllText(incoming, "new " + index);
                        files.Insert(new BookFile { Edition = edition, EditionId = edition.Id, Path = old,
                            Quality = new(Quality.MP3), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, MediaInfo = new MediaInfoModel() });
                        decisions.Add(new(new LocalBook { Book = book, Edition = edition, Author = author,
                            Path = incoming, IsManualImport = true, DisableReleaseSwitching = true, Quality = new(Quality.MP3), Size = new FileInfo(incoming).Length, Modified = DateTime.UtcNow }));
                        targets.Add((book, edition, old, incoming, destination));
                    }
                    using (var connection = database.OpenConnection())
                    {
                        var table = settingsFailure ? "Books" : "BookFiles";
                        var operation = settingsFailure ? "UPDATE OF \"AnyEditionOk\"" : "INSERT";
                        var column = settingsFailure ? "Id" : "EditionId";
                        var id = settingsFailure ? targets[1].Book.Id : targets[1].Edition.Id;
                        connection.Execute(postgres
                            ? $"CREATE FUNCTION reject_second_import() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.\"{column}\" = {id} THEN RAISE EXCEPTION 'second book refused'; END IF; RETURN NEW; END $$; CREATE TRIGGER reject_second_import BEFORE {operation} ON \"{table}\" FOR EACH ROW EXECUTE FUNCTION reject_second_import();"
                            : $"CREATE TRIGGER reject_second_import BEFORE {operation} ON \"{table}\" WHEN NEW.\"{column}\" = {id} BEGIN SELECT RAISE(ABORT, 'second book refused'); END;");
                    }
                    var media = new MediaFileService(files, events, null, null, logger);
                    var mover = new StubMoveBookFiles { TransferFilesOnDisk = true,
                        DestinationForFile = local => targets.Single(target => target.Book.Id == local.Book.Id).Destination };
                    var editionService = new EditionService(editions, events, logger);
                    var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name == nameof(IAuthorService.GetAuthors) ? new List<Author> { author } : author);
                    var bookService = new BookService(books, editionService, events, authorService, media, Proxy<IRootFolderService>(), null, null, logger);
                    var service = new ImportApprovedBooks(media, new StubMetadataTagService(), new StubMediaInfoExtractor(), Proxy<IAuthorService>(),
                        bookService, new EditionService(editions, events, logger), new StubRecycleBinProvider(), Proxy<IExtraService>(),
                        mover, history, Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), events,
                        Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(),
                        Proxy<IQualityProfileService>(), Proxy<IM4bConversionService>(), logger);
                    published.Clear();
                    Assert.Catch(() => service.Import(decisions, true, new DownloadClientItem { DownloadId = "independent-books" }, ImportMode.Copy));
                    Assert.That(history.FindByDownloadId("independent-books").Select(row => row.BookId), Is.EqualTo(new[] { targets[0].Book.Id }));
                    Assert.That(published.OfType<TrackImportedEvent>().Count(), Is.EqualTo(1));
                    Assert.That(published.OfType<BookImportedEvent>().Select(message => message.Book.Id), Is.EqualTo(new[] { targets[0].Book.Id }));
                    Assert.That(files.GetFilesByBook(targets[0].Book.Id).Single().Path, Is.EqualTo(targets[0].Destination));
                    Assert.That(books.Get(targets[0].Book.Id).AnyEditionOk, Is.False);
                    foreach (var target in targets.Skip(1))
                    {
                        Assert.That(books.Get(target.Book.Id).AnyEditionOk, Is.True);
                        Assert.That(files.GetFilesByBook(target.Book.Id).Single().Path, Is.EqualTo(target.Old));
                        Assert.That(File.Exists(target.Destination), Is.False);
                        Assert.That(File.ReadAllText(target.Old), Does.StartWith("old"));
                    }
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void replacement_settings_should_load_large_edition_catalog_once_and_write_only_changed_rows(bool postgres, bool changeSelection)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "atomic-catalog-cost-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var events = new NoOpEventAggregator();
                    var (profile, author, book, _) = CreateAudiobookConversionGraph(314);
                    profile.ConvertToQualityId = null;
                    profile.UpgradeAllowed = true;
                    author.Id = 0;
                    author.Path = root;
                    author.AudiobookPath = root;
                    author.CleanName = "testwriter";
                    author.SortName = "testwriter";
                    author.TitleSlug = "test-writer";
                    author.Added = DateTime.UtcNow;
                    new AuthorRepository(database, events, logger).Insert(author);
                    book.Id = 0;
                    book.AuthorId = author.Id;
                    book.CleanTitle = "blacksheep";
                    book.TitleSlug = "target";
                    book.Added = DateTime.UtcNow;
                    book.AnyEditionOk = true;
                    var books = new BookRepository(database, events);
                    books.Insert(book);
                    var editions = new EditionRepository(database, events);
                    var catalog = Enumerable.Range(0, 500).Select(index => new Edition { Book = book, BookId = book.Id,
                        Title = book.Title, ForeignEditionId = "local:cost:" + index, ReadingFormatId = 2,
                        Monitored = index == 0, ManualAdd = index % 13 == 0 }).ToList();
                    editions.InsertMany(catalog);
                    book.Editions = catalog;
                    var selected = catalog[changeSelection ? 499 : 0];
                    var files = new MediaFileRepository(database, events);
                    var old = Path.Combine(root, "old.mp3");
                    var incoming = Path.Combine(root, "incoming.mp3");
                    var destination = Path.Combine(root, "new.mp3");
                    File.WriteAllText(old, "old recording");
                    File.WriteAllText(incoming, "new recording");
                    files.Insert(new BookFile { Path = old, EditionId = catalog[0].Id, Edition = catalog[0],
                        Quality = new(Quality.MP3), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, MediaInfo = new MediaInfoModel() });
                    var media = new MediaFileService(files, events, null, null, logger);
                    var realEditions = new EditionService(editions, events, logger);
                    var catalogLoads = 0;
                    var changedRows = new List<int>();
                    var editionService = InterfaceProxy.Proxy<IEditionService>((method, args) =>
                    {
                        if (method.Name == nameof(IEditionService.GetEditionsByBook)) catalogLoads++;
                        if (method.Name == nameof(IEditionService.UpdateMany))
                            changedRows.AddRange(((List<Edition>)args[0]).Select(edition => edition.Id));
                        return method.Invoke(realEditions, args);
                    });
                    var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name == nameof(IAuthorService.GetAuthors) ? new List<Author> { author } : author);
                    var bookService = new BookService(books, editionService, events, authorService, media, Proxy<IRootFolderService>(), null, null, logger);
                    var mover = new StubMoveBookFiles { DestinationPath = destination, TransferFilesOnDisk = true };
                    var service = new ImportApprovedBooks(media, new StubMetadataTagService(), new StubMediaInfoExtractor(), authorService,
                        bookService, editionService, new StubRecycleBinProvider(), Proxy<IExtraService>(), mover,
                        Proxy<IHistoryService>(), Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), events,
                        Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(),
                        Proxy<IQualityProfileService>(), Proxy<IM4bConversionService>(), logger);
                    var results = service.Import(new() { new(new LocalBook { Book = book, Edition = selected, Author = author,
                        Path = incoming, IsManualImport = true, DisableReleaseSwitching = true, Quality = new(Quality.MP3),
                        Size = new FileInfo(incoming).Length, Modified = DateTime.UtcNow }) }, true, importMode: ImportMode.Copy);
                    Assert.That(results.Single().Result, Is.EqualTo(ImportResultType.Imported));
                    Assert.That(catalogLoads, Is.EqualTo(1), "GetBook already loaded the catalog for the settings operation");
                    Assert.That(changedRows, Is.EquivalentTo(changeSelection ? new[] { catalog[0].Id, selected.Id } : Array.Empty<int>()));
                    Assert.That(editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(selected.Id));
                    Assert.That(editions.Get(catalog[13].Id).ManualAdd, Is.True, "Independent retention is not displaced by another selection");
                    TestContext.Progress.WriteLine($"IMPORT_SETTINGS_COST database={database.DatabaseType} catalog=500 catalogLoads={catalogLoads} changedEditionRows={changedRows.Count}");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }
    }
}

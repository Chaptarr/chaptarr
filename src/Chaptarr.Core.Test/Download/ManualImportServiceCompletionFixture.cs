using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using NLog;
using Chaptarr.Core.Test.Common;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class ManualImportServiceCompletionFixture
    {
        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void ordinary_completion_should_allow_imported_opposite_media_without_waiving_expected_files(BookMediaType media)
        {
            var opposite = media == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
            var ownPath = "/downloads/expected" + (media == BookMediaType.Audiobook ? ".m4b" : ".epub");
            var companionPath = "/downloads/companion" + (opposite == BookMediaType.Audiobook ? ".m4b" : ".epub");
            var tracked = CreateTrackedDownload(new QualityModel(media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB),
                new Book { Id = 10, AuthorId = 7, MediaType = media });
            var imported = CreateImportResultForPath(10, media, ownPath);
            var companion = CreateImportResultForPath(20, opposite, companionPath);
            var previews = new[] { CreatePreviewFile(10, ownPath), CreatePreviewFile(20, companionPath) };
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { imported, companion }, previews).Complete, Is.True);

            var unrelated = CreateImportResultForPath(30, media, "/downloads/unrelated" + (media == BookMediaType.Audiobook ? ".m4b" : ".epub"));
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { imported, companion, unrelated }, previews).Complete, Is.False);
            var failed = CreateImportResultForPath(10, media, ownPath, "Transfer failed");
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { failed, companion }, previews).Complete, Is.False);
            if (media == BookMediaType.Audiobook)
            {
                Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { imported, companion },
                    previews.Append(CreatePreviewFile(10, "/downloads/expected-part2.m4b")).ToList()).Complete, Is.False);
            }
        }

        [Test]
        public void should_complete_when_imported_book_matches_grabbed_release_media_type()
        {
            var trackedDownload = CreateTrackedDownload(
                new QualityModel(Quality.MP3),
                new Book { Id = 10, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook },
                new Book { Id = 11, AuthorId = 7, Title = "Expected Ebook", MediaType = BookMediaType.Ebook });

            var result = TrackedDownloadAlreadyImported.Evaluate(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResult(10, BookMediaType.Audiobook)
                }).Complete;

            Assert.That(result, Is.True);
        }

        [Test]
        public void should_complete_manual_import_without_expected_target_when_all_selected_items_imported()
        {
            var trackedDownload = CreateTrackedDownload(new QualityModel(Quality.UnknownAudio));

            var result = TrackedDownloadAlreadyImported.Evaluate(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResult(10, BookMediaType.Audiobook)
                }).Complete;

            Assert.That(result, Is.True);
        }

        [Test]
        public void should_not_complete_manual_import_without_expected_target_when_any_selected_item_failed()
        {
            var trackedDownload = CreateTrackedDownload(new QualityModel(Quality.UnknownAudio));

            var result = TrackedDownloadAlreadyImported.Evaluate(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResult(10, BookMediaType.Audiobook),
                    CreateImportResult(11, BookMediaType.Audiobook, "Import failed")
                }).Complete;

            Assert.That(result, Is.False);
        }

        [Test]
        public void should_not_complete_when_imported_book_does_not_match_expected_target()
        {
            var trackedDownload = CreateTrackedDownload(
                new QualityModel(Quality.MP3),
                new Book { Id = 10, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook });

            var result = TrackedDownloadAlreadyImported.Evaluate(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResult(99, BookMediaType.Audiobook)
                }).Complete;

            Assert.That(result, Is.False);
        }

        [Test]
        public void should_not_complete_when_preview_has_unimported_file_for_expected_book()
        {
            var trackedDownload = CreateTrackedDownload(
                new QualityModel(Quality.MP3),
                new Book { Id = 10, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook });

            var result = TrackedDownloadAlreadyImported.Evaluate(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResultForPath(10, BookMediaType.Audiobook, "/downloads/Babbitt 01.m4b")
                },
                new List<ManualImportFile>
                {
                    CreatePreviewFile(10, "/downloads/Babbitt 01.m4b"),
                    CreatePreviewFile(10, "/downloads/Babbitt 02.m4b")
                }).Complete;

            Assert.That(result, Is.False);
        }

        [Test]
        public void should_complete_when_preview_extras_are_not_expected_books()
        {
            var trackedDownload = CreateTrackedDownload(
                new QualityModel(Quality.MP3),
                new Book { Id = 10, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook });

            var result = TrackedDownloadAlreadyImported.Evaluate(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResultForPath(10, BookMediaType.Audiobook, "/downloads/Book A.m4b")
                },
                new List<ManualImportFile>
                {
                    CreatePreviewFile(10, "/downloads/Book A.m4b"),
                    CreatePreviewFile(20, "/downloads/Book B.m4b"),
                    CreatePreviewFile(30, "/downloads/Book C.m4b")
                }).Complete;

            Assert.That(result, Is.True);
        }

        [Test]
        public void should_complete_ebook_when_preview_has_unimported_alternate_format_for_same_book()
        {
            var trackedDownload = CreateTrackedDownload(
                new QualityModel(Quality.EPUB),
                new Book { Id = 10, AuthorId = 7, Title = "Expected Ebook", MediaType = BookMediaType.Ebook });

            var result = TrackedDownloadAlreadyImported.Evaluate(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResultForPath(10, BookMediaType.Ebook, "/downloads/Book A.epub")
                },
                new List<ManualImportFile>
                {
                    CreatePreviewFile(10, "/downloads/Book A.epub"),
                    CreatePreviewFile(10, "/downloads/Book A.azw3")
                }).Complete;

            Assert.That(result, Is.True);
        }

        [Test]
        public void should_complete_audiobook_conversion_when_all_preview_sources_were_imported()
        {
            var trackedDownload = CreateTrackedDownload(
                new QualityModel(Quality.MP3),
                new Book { Id = 10, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook });

            var result = TrackedDownloadAlreadyImported.Evaluate(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateGeneratedConversionResult(10, "/downloads/Babbitt 01.mp3", "/downloads/Babbitt 02.mp3")
                },
                new List<ManualImportFile>
                {
                    CreatePreviewFile(10, "/downloads/Babbitt 01.mp3"),
                    CreatePreviewFile(10, "/downloads/Babbitt 02.mp3")
                }).Complete;

            Assert.That(result, Is.True);
        }

        [Test]
        public void folder_import_should_associate_only_the_unique_live_download_with_exact_snapshot_paths()
        {
            var matching = CreatePathTrackedDownload(
                "matching",
                TrackedDownloadState.ImportPending,
                "/downloads/Book/Disc 1.mp3",
                "/downloads/Book/Disc 2.mp3");
            var other = CreatePathTrackedDownload(
                "other",
                TrackedDownloadState.ImportPending,
                "/downloads/Other/Other.mp3");

            var result = ManualImportService.FindUniqueTrackedDownloadBySourcePaths(
                new[] { "/downloads/Book/Disc 1.mp3", "/downloads/Book/Disc 2.mp3" },
                new[] { matching, other },
                out var reason);

            Assert.That(result, Is.SameAs(matching));
            Assert.That(reason, Is.EqualTo("MANUAL_IMPORT_DOWNLOAD_ASSOCIATION_UNIQUE_PATH"));
        }

        [Test]
        public void folder_import_should_not_use_output_folder_when_snapshot_disagrees()
        {
            var tracked = CreatePathTrackedDownload(
                "download-1",
                TrackedDownloadState.ImportPending,
                "/downloads/Book/Disc 1.mp3");
            tracked.DownloadItem.OutputPath = new OsPath("/downloads/Book");

            var result = ManualImportService.FindUniqueTrackedDownloadBySourcePaths(
                new[] { "/downloads/Book/Disc 2.mp3" },
                new[] { tracked },
                out var reason);

            Assert.That(result, Is.Null);
            Assert.That(reason, Is.EqualTo("MANUAL_IMPORT_DOWNLOAD_ASSOCIATION_NOT_FOUND"));
        }

        [Test]
        public void folder_import_should_use_exact_output_folder_only_when_no_file_snapshot_exists()
        {
            var tracked = CreatePathTrackedDownload("download-1", TrackedDownloadState.ImportBlocked);
            tracked.DownloadItem.OutputPath = new OsPath("/downloads/Book");

            var result = ManualImportService.FindUniqueTrackedDownloadBySourcePaths(
                new[] { "/downloads/Book/Disc 1.mp3", "/downloads/Book/Disc 2.mp3" },
                new[] { tracked },
                out _);

            Assert.That(result, Is.SameAs(tracked));
        }

        [Test]
        public void folder_import_should_remain_unassociated_when_two_live_downloads_claim_the_paths()
        {
            var first = CreatePathTrackedDownload("first", TrackedDownloadState.ImportPending, "/downloads/shared.epub");
            var second = CreatePathTrackedDownload("second", TrackedDownloadState.Importing, "/downloads/shared.epub");

            var result = ManualImportService.FindUniqueTrackedDownloadBySourcePaths(
                new[] { "/downloads/shared.epub" },
                new[] { first, second },
                out var reason);

            Assert.That(result, Is.Null);
            Assert.That(reason, Is.EqualTo("MANUAL_IMPORT_DOWNLOAD_ASSOCIATION_AMBIGUOUS"));
        }

        [Test]
        public void folder_import_should_not_reassociate_an_already_imported_download()
        {
            var imported = CreatePathTrackedDownload("done", TrackedDownloadState.Imported, "/downloads/Book.epub");

            var result = ManualImportService.FindUniqueTrackedDownloadBySourcePaths(
                new[] { "/downloads/Book.epub" },
                new[] { imported },
                out _);

            Assert.That(result, Is.Null);
        }

        private static TrackedDownload CreateTrackedDownload(QualityModel quality, params Book[] books)
        {
            return new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-1",
                    Title = "Manual.Import.Test",
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = quality
                    },
                    Books = new List<Book>(books)
                }
            };
        }

        private static TrackedDownload CreatePathTrackedDownload(string downloadId, TrackedDownloadState state, params string[] filePaths)
        {
            return new TrackedDownload
            {
                State = state,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = downloadId,
                    Title = downloadId,
                    FilePaths = filePaths.ToList(),
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                }
            };
        }

        [TestCase(BookMediaType.Audiobook, 10)] [TestCase(BookMediaType.Audiobook, 11)]
        [TestCase(BookMediaType.Ebook, 10)] [TestCase(BookMediaType.Ebook, 11)]
        public void alternative_manual_choice_should_complete_only_original_allowed_rows(BookMediaType media, int selected)
        {
            var tracked = CreateTrackedDownload(new QualityModel(media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB),
                new Book { Id = 10, AuthorId = 7, MediaType = media }, new Book { Id = 11, AuthorId = 7, MediaType = media });
            tracked.RemoteBook.AlternativeCopies = true;
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { CreateImportResult(selected, media) }).Complete, Is.True);
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { CreateImportResult(99, media) }).Complete, Is.False);
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked,
                new[] { CreateImportResult(10, media), CreateImportResult(11, media, "Import failed") }).Complete, Is.False);
            tracked.RemoteBook.AlternativeCopies = false;
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { CreateImportResult(selected, media) }).Complete, Is.False);
        }

        [Test]
        public void alternative_manual_completion_should_keep_unknown_unit_and_conversion_source_checks()
        {
            var tracked = CreateTrackedDownload(new QualityModel(Quality.MP3), new Book { Id = 10, AuthorId = 7, MediaType = BookMediaType.Audiobook },
                new Book { Id = 11, AuthorId = 7, MediaType = BookMediaType.Audiobook });
            tracked.RemoteBook.AlternativeCopies = true;
            var previews = new[] { CreatePreviewFile(10, "/downloads/one.mp3"), CreatePreviewFile(0, "/downloads/two.mp3") };
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked,
                new[] { CreateImportResultForPath(10, BookMediaType.Audiobook, "/downloads/one.mp3") }, previews).Complete, Is.False);
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked,
                new[] { CreateGeneratedConversionResult(10, "/downloads/one.mp3", "/downloads/two.mp3") }, previews).Complete, Is.True);
        }

        [TestCase(false, BookMediaType.Audiobook)] [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)] [TestCase(true, BookMediaType.Ebook)]
        public void alternative_manual_execute_should_refuse_outside_copy_before_native_settings_file_and_history_writes(bool postgres, BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method,args)=>null);
                var author = new AuthorRepository(database,events,logger).Insert(new Author { Name="Test Writer",CleanName="testwriter",SortName="testwriter",TitleSlug="test-writer",Path="/library",Added=DateTime.UtcNow });
                var books = new BookRepository(database,events);
                Book Seed(string slug) => books.Insert(new Book { AuthorId=author.Id,Title="Paper Lanterns",CleanTitle="paperlanterns",TitleSlug=slug,Added=DateTime.UtcNow,MediaType=media,AnyEditionOk=true });
                var a=Seed("original-a"); var b=Seed("original-b"); var c=Seed("outside-c");
                var editions=new EditionRepository(database,events);
                var edition=editions.Insert(new Edition { BookId=c.Id,Title=c.Title,ForeignEditionId="hc:edition:outside",Monitored=true,ReadingFormatId=media==BookMediaType.Audiobook?2:3 });
                c.Author=author;c.Editions=new List<Edition>{edition};edition.Book=c;
                var histories=new HistoryRepository(database,events);var history=new HistoryService(histories,logger);
                var files=new MediaFileRepository(database,events);
                var extension=media==BookMediaType.Audiobook?".m4b":".epub";var path="/downloads/choice"+extension;
                var quality=new QualityModel(media==BookMediaType.Audiobook?Quality.M4B:Quality.EPUB);
                var tracked=CreateTrackedDownload(quality,a,b);tracked.RemoteBook.AlternativeCopies=true;tracked.RemoteBook.AlternativeCopyGrabDate=DateTime.UtcNow;
                var updates=0;var importWrites=0;
                var bookService=Proxy<IBookService>((method,args)=>
                {
                    if(method.Name==nameof(IBookService.GetBook))return c;
                    if(method.Name==nameof(IBookService.UpdateBook)){updates++;books.Update((Book)args[0]);return args[0];}
                    throw new InvalidOperationException(method.Name);
                });
                var info=Proxy<IFileInfo>((method,args)=>method.Name switch {
                    "get_Exists"=>true,"get_FullName"=>path,"get_Name"=>"choice"+extension,"get_Extension"=>extension,"get_Length"=>1L,"get_LastWriteTimeUtc"=>DateTime.UtcNow,_=>throw new InvalidOperationException(method.Name)});
                var disk=Proxy<IDiskProvider>((method,args)=>method.Name==nameof(IDiskProvider.GetFileInfo)?info:throw new InvalidOperationException(method.Name));
                var root=Proxy<IRootFolderService>((method,args)=>new RootFolder{Id=1,Path="/library"});
                var authorService=Proxy<IAuthorService>((method,args)=>author);
                var editionService=Proxy<IEditionService>((method,args)=>edition);
                var tags=Proxy<IMetadataTagService>((method,args)=>(new Dictionary<string,List<string>>(),(int?)1));
                var tracker=Proxy<ITrackedDownloadService>((method,args)=>tracked);
                var importer=Proxy<IImportApprovedBooks>((method,args)=>
                {
                    var decisions=(List<ImportDecision<LocalBook>>)args[0];
                    foreach(var decision in decisions.Where(decision=>decision.Approved))
                    {
                        importWrites++;
                        var file=new BookFile{EditionId=edition.Id,Edition=edition,Author=author,Path=path,Quality=quality,Size=1};
                        files.ReplaceMany(new List<BookFile>{file},new List<BookFile>());
                        history.Handle(new TrackImportedEvent(decision.Item,file,new List<BookFile>(),true,tracked.DownloadItem));
                    }
                    return decisions.Select(decision=>new ImportResult(decision,decision.Approved?Array.Empty<string>():new[]{"Outside original target"})).ToList();
                });
                var modes=Proxy<IDownloadImportModeResolver>((method,args)=>ImportMode.Copy);
                var constructor=typeof(ManualImportService).GetConstructors().Single();
                var dependencies=new Dictionary<Type,object>{[typeof(NzbDrone.Core.Download.TrackedDownloads.ITrackedDownloadAlreadyImported)] = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger()), [typeof(Logger)]=logger,[typeof(IDiskProvider)]=disk,[typeof(IRootFolderService)]=root,
                    [typeof(IAuthorService)]=authorService,[typeof(IBookService)]=bookService,[typeof(IEditionService)]=editionService,
                    [typeof(IMetadataTagService)]=tags,[typeof(ITrackedDownloadService)]=tracker,[typeof(IImportApprovedBooks)]=importer,
                    [typeof(IDownloadClientFileSnapshotService)]=new DownloadClientFileSnapshotService(new DownloadClientFileSnapshotRepository(database,events),disk,logger),
                    [typeof(IDownloadImportModeResolver)]=modes,[typeof(IEventAggregator)]=events,[typeof(IHistoryService)]=history};
                var subject=(ManualImportService)constructor.Invoke(constructor.GetParameters().Select(parameter=>dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
                var failure = Assert.Throws<NzbDrone.Core.Messaging.Commands.CommandFailedException>(() => subject.Execute(new ManualImportCommand {Files=new List<ManualImportFile>{new(){Path=path,AuthorId=author.Id,BookId=c.Id,EditionId=edition.Id,
                    DownloadId=tracked.DownloadItem.DownloadId,Quality=quality,DisableReleaseSwitching=true}},ImportMode=ImportMode.Copy}));
                Assert.That(failure.Message, Does.Contain("Imported 0 files").And.Contain(path).And.Contain("Outside original target"));
                Assert.Multiple(()=>{Assert.That(updates,Is.Zero);Assert.That(importWrites,Is.Zero);Assert.That(books.Get(c.Id).AnyEditionOk,Is.True);
                    Assert.That(files.All(),Is.Empty);Assert.That(histories.All(),Is.Empty);});
                TestContext.Progress.WriteLine($"C20_MANUAL_OUTSIDE database={database.DatabaseType} media={media} outsideBook={c.Id} settingsWrites={updates} fileHistoryWrites={importWrites}");
            });
        }

        [Test]
        public void alternative_ebook_completion_should_keep_same_proven_edition_duplicate_format_parity()
        {
            var tracked=CreateTrackedDownload(new QualityModel(Quality.EPUB),new Book {Id=10,AuthorId=7,MediaType=BookMediaType.Ebook},
                new Book {Id=11,AuthorId=7,MediaType=BookMediaType.Ebook});
            tracked.RemoteBook.AlternativeCopies=true;
            var imported=CreateImportResultForPath(10,BookMediaType.Ebook,"/downloads/one.epub");
            var duplicate=CreateImportResultForPath(10,BookMediaType.Ebook,"/downloads/one.mobi","Skipped duplicate ebook format");
            imported.ImportDecision.Item.Edition = new Edition { Id = 100, BookId = 10 };
            duplicate.ImportDecision.Item.Edition = new Edition { Id = 100, BookId = 10 };
            duplicate.ImportDecision.Reject(new NzbDrone.Core.DecisionEngine.Rejection("Skipped duplicate ebook format") { Category = "EbookAlternative" });
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked,new[]{imported,duplicate}).Complete,Is.True);
            var previews = new[] { CreatePreviewFile(10, imported.ImportDecision.Item.Path), CreatePreviewFile(0, duplicate.ImportDecision.Item.Path) };
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { imported }, previews).Complete, Is.False);
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { imported, duplicate }, previews).Complete, Is.True);
            duplicate.ImportDecision.Item.Edition.Id = 101;
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { imported, duplicate }, previews).Complete, Is.False);
            var other=CreateImportResultForPath(11,BookMediaType.Ebook,"/downloads/other.mobi","Import failed");
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked,new[]{imported,other}).Complete,Is.False);
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void alternative_completion_should_ignore_companions_without_waiving_book_file_failures(BookMediaType media)
        {
            var tracked = CreateTrackedDownload(new QualityModel(media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB),
                new Book { Id = 10, AuthorId = 7, MediaType = media }, new Book { Id = 11, AuthorId = 7, MediaType = media });
            tracked.RemoteBook.AlternativeCopies = true;
            var ownPath = "/downloads/book" + (media == BookMediaType.Audiobook ? ".m4b" : ".epub");
            var oppositePath = "/downloads/book" + (media == BookMediaType.Audiobook ? ".epub" : ".m4b");
            var imported = CreateImportResultForPath(10, media, ownPath);
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { imported },
                new[] { CreatePreviewFile(10, ownPath), CreatePreviewFile(0, oppositePath), CreatePreviewFile(0, "/downloads/cover.jpg") }).Complete, Is.True);
            var failed = CreateImportResultForPath(10, media, ownPath + (media == BookMediaType.Audiobook ? ".mp3" : ".mobi"), "Transfer failed");
            Assert.That(TrackedDownloadAlreadyImported.Evaluate(tracked, new[] { imported, failed }).Complete, Is.False);
        }

        [TestCase("author-failure")]
        [TestCase("book-failure")]
        [TestCase("multi-book")]
        [TestCase("parsed-multi-book")]
        [TestCase("parsed-single-book")]
        public void tracked_manual_preview_should_resolve_author_and_book_independently_without_collapsing_multi_book_context(string scenario)
        {
            var author = new Author { Id = 7, Name = "Test Writer" };
            var book = new Book { Id = 10, AuthorId = author.Id, Author = author, Title = "Paper Lanterns", MediaType = BookMediaType.Audiobook };
            var tracked = CreateTrackedDownload(new QualityModel(Quality.M4B), book);
            tracked.ImportItem = new DownloadClientItem { OutputPath = new OsPath("/downloads/payload") };
            tracked.RemoteBook.Author = scenario == "author-failure" ? new Author { Name = author.Name } : author;
            tracked.RemoteBook.ParsedBookInfo.BookTitle = book.Title;
            if (scenario == "multi-book") tracked.RemoteBook.Books.Add(new Book { Id = 11, AuthorId = author.Id, MediaType = BookMediaType.Audiobook });
            if (scenario.StartsWith("parsed-", StringComparison.Ordinal)) tracked.RemoteBook.Books.Clear();
            var lookups = 0;
            var parseLookups = 0;
            NzbDrone.Core.MediaFiles.BookImport.Identification.IdentificationOverrides observed = null;
            var dependencies = new Dictionary<Type, object>
            {
                [typeof(IDiskProvider)] = Proxy<IDiskProvider>((method, args) => method.Name == nameof(IDiskProvider.FolderExists) ? true : null),
                [typeof(IDiskScanService)] = Proxy<IDiskScanService>((method, args) => Array.Empty<IFileInfo>()),
                [typeof(IAuthorService)] = Proxy<IAuthorService>((method, args) => throw new InvalidOperationException("Author lookup unavailable")),
                [typeof(IBookService)] = Proxy<IBookService>((method, args) =>
                {
                    lookups++;
                    if (scenario == "book-failure") throw new InvalidOperationException("Book lookup unavailable");
                    return book;
                }),
                [typeof(NzbDrone.Core.Parser.IParsingService)] = Proxy<NzbDrone.Core.Parser.IParsingService>((method, args) =>
                {
                    if (method.Name != nameof(NzbDrone.Core.Parser.IParsingService.GetBooks)) return null;
                    parseLookups++;
                    return scenario == "parsed-single-book" ? new List<Book> { book } : new List<Book> { book, new() { Id = 11, AuthorId = author.Id } };
                }),
                [typeof(ITrackedDownloadService)] = Proxy<ITrackedDownloadService>((method, args) => tracked),
                [typeof(NzbDrone.Core.MediaFiles.BookImport.Identification.IMakeImportDecision)] = Proxy<NzbDrone.Core.MediaFiles.BookImport.Identification.IMakeImportDecision>((method, args) =>
                {
                    observed = (NzbDrone.Core.MediaFiles.BookImport.Identification.IdentificationOverrides)args[1];
                    return new List<ImportDecision<LocalBook>>();
                }),
                [typeof(NzbDrone.Core.Download.TrackedDownloads.ITrackedDownloadAlreadyImported)] = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger()), [typeof(Logger)] = LogManager.GetCurrentClassLogger()
            };
            var constructor = typeof(ManualImportService).GetConstructors().Single();
            var subject = (ManualImportService)constructor.Invoke(constructor.GetParameters().Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
            Assert.That(subject.GetMediaFiles("ignored", "download-1", null, FilterFilesType.None, false), Is.Empty);
            Assert.That(observed.Author?.Id, Is.EqualTo(author.Id));
            Assert.That(observed.Book?.Id, Is.EqualTo(scenario is "author-failure" or "parsed-single-book" ? (int?)book.Id : null));
            Assert.That(lookups, Is.EqualTo(scenario is "author-failure" or "book-failure" ? 1 : 0));
            Assert.That(parseLookups, Is.EqualTo(scenario.StartsWith("parsed-", StringComparison.Ordinal) ? 1 : 0));
        }

        private static IEnumerable<TestCaseData> ManualReleaseGroupCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var existing in new[] { false, true })
            foreach (var value in new[] { "named", "empty", "null", "omitted" })
                yield return new TestCaseData(postgres, media, existing, value);
        }

        [TestCaseSource(nameof(ManualReleaseGroupCases))]
        public void manual_command_should_persist_resolved_release_group_without_clearing_an_existing_group(
            bool postgres, BookMediaType media, bool existing, string value)
        {
            AssertManualReleaseGroup(postgres, media, existing, value, false);
        }

        [TestCase(false, BookMediaType.Audiobook, false)]
        [TestCase(false, BookMediaType.Audiobook, true)]
        [TestCase(true, BookMediaType.Audiobook, false)]
        [TestCase(true, BookMediaType.Audiobook, true)]
        [TestCase(false, BookMediaType.Ebook, false)]
        [TestCase(false, BookMediaType.Ebook, true)]
        [TestCase(true, BookMediaType.Ebook, false)]
        [TestCase(true, BookMediaType.Ebook, true)]
        public void canonical_manual_writer_should_persist_a_resolved_group_for_new_and_existing_rows(
            bool postgres, BookMediaType media, bool existing)
        {
            AssertManualReleaseGroup(postgres, media, existing, "named", true);
        }

        private static void AssertManualReleaseGroup(bool postgres, BookMediaType media, bool existing, string value, bool directWriter)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "manual-release-group-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(folder);
                try
                {
                    var logger = LogManager.GetCurrentClassLogger();
                    var noop = Proxy<IEventAggregator>((method, args) => null);
                    var author = new AuthorRepository(database, noop, logger).Insert(new Author
                    {
                        Name = "Test Writer", CleanName = "testwriter", SortName = "testwriter", TitleSlug = "test-writer",
                        Path = folder, Added = DateTime.UtcNow
                    });
                    var quality = new QualityModel(media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB);
                    var profile = new NzbDrone.Core.Profiles.Qualities.QualityProfile
                    {
                        Id = 1, Name = "Native import", UpgradeAllowed = true,
                        ProfileType = media == BookMediaType.Audiobook ? NzbDrone.Core.Profiles.Qualities.ProfileType.Audiobook : NzbDrone.Core.Profiles.Qualities.ProfileType.Ebook,
                        Items = new() { new() { Allowed = true, Quality = quality.Quality } }
                    };
                    if (media == BookMediaType.Audiobook) { author.AudiobookQualityProfileId = 1; author.AudiobookQualityProfile = profile; }
                    else { author.EbookQualityProfileId = 1; author.EbookQualityProfile = profile; }
                    var books = new BookRepository(database, noop);
                    var book = books.Insert(new Book { AuthorId = author.Id, Author = author, Title = "Paper Lanterns", CleanTitle = "paperlanterns",
                        TitleSlug = "paper-lanterns", Added = DateTime.UtcNow, MediaType = media, AnyEditionOk = true });
                    var editions = new EditionRepository(database, noop);
                    var edition = editions.Insert(new Edition { BookId = book.Id, Book = book, Title = book.Title, ForeignEditionId = "local:manual-group",
                        Monitored = true, ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3 });
                    book.Editions = new() { edition };
                    var files = new MediaFileRepository(database, noop);
                    var path = System.IO.Path.Combine(folder, "selected" + (media == BookMediaType.Audiobook ? ".m4b" : ".epub"));
                    System.IO.File.WriteAllText(path, "selected source");
                    BookFile original = null;
                    if (existing)
                    {
                        original = files.Insert(new BookFile { Path = path, EditionId = edition.Id, Edition = edition, Author = author,
                            DateAdded = DateTime.UtcNow.AddDays(-1), Modified = DateTime.UtcNow, Quality = quality, Size = 15,
                            MediaInfo = new MediaInfoModel(), ReleaseGroup = "Stored Group",
                            SceneName = "Stored Scene", IndexerFlags = (IndexerFlags)3 });
                    }
                    var historyRows = new HistoryRepository(database, noop);
                    var history = new HistoryService(historyRows, logger);
                    var events = Proxy<IEventAggregator>((method, args) =>
                    {
                        if (args[0] is TrackImportedEvent imported) history.Handle(imported);
                        return null;
                    });
                    var mediaWrites = 0;
                    var mediaFiles = Proxy<IMediaFileService>((method, args) =>
                    {
                        if (method.Name == nameof(IMediaFileService.ValidateReplacementFiles)) { Assert.That((List<BookFile>)args[0], Is.Empty); return null; }
                        if (method.Name == nameof(IMediaFileService.ReplaceMany)) { mediaWrites++; files.ReplaceMany((List<BookFile>)args[0], (List<BookFile>)args[1]); return null; }
                        if (method.Name == nameof(IMediaFileService.Update)) { mediaWrites++; return files.Update((BookFile)args[0]); }
                        if (method.Name == nameof(IMediaFileService.GetFilesByBook)) return files.GetFilesByBook((int)args[0]);
                        if (method.Name == nameof(IMediaFileService.GetFilesByEdition)) return files.GetFilesByEdition((int)args[0]);
                        if (method.Name == nameof(IMediaFileService.GetFileWithPath)) return args[0] is List<string> paths ? files.GetFileWithPath(paths) : files.GetFileWithPath((string)args[0]);
                        throw new InvalidOperationException("Unexpected media operation " + method.Name);
                    });
                    var fileSystem = new System.IO.Abstractions.FileSystem();
                    var disk = Proxy<IDiskProvider>((method, args) => method.Name switch
                    {
                        nameof(IDiskProvider.GetFileInfo) => fileSystem.FileInfo.FromFileName((string)args[0]),
                        nameof(IDiskProvider.FileExists) or nameof(IDiskProvider.FileExistsCanonical) => System.IO.File.Exists((string)args[0]),
                        nameof(IDiskProvider.FolderExists) => System.IO.Directory.Exists((string)args[0]),
                        _ => null
                    });
                    var tags = Proxy<IMetadataTagService>((method, args) => method.Name == nameof(IMetadataTagService.ReadAllTagsAndDuration)
                        ? (new Dictionary<string, List<string>>(), (int?)1) : null);
                    var bookService = Proxy<IBookService>((method, args) => method.Name switch
                    {
                        nameof(IBookService.GetBook) => book,
                        nameof(IBookService.GetExistingBooks) => new List<Book> { book },
                        nameof(IBookService.UpdateBook) => books.Update((Book)args[0]),
                        _ => throw new InvalidOperationException("Unexpected book operation " + method.Name)
                    });
                    var authorService = Proxy<IAuthorService>((method, args) => author);
                    var editionService = new EditionService(editions, events, logger);
                    var dependencies = new Dictionary<Type, object>
                    {
                        [typeof(NzbDrone.Core.Download.TrackedDownloads.ITrackedDownloadAlreadyImported)] = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger()), [typeof(Logger)] = logger, [typeof(IEventAggregator)] = events, [typeof(IMediaFileService)] = mediaFiles,
                        [typeof(IDiskProvider)] = disk, [typeof(IMetadataTagService)] = tags, [typeof(IBookService)] = bookService,
                        [typeof(IAuthorService)] = authorService, [typeof(IEditionService)] = editionService,
                        [typeof(IHistoryService)] = history
                    };
                    var writerConstructor = typeof(ImportApprovedBooks).GetConstructors().Single();
                    var writer = (ImportApprovedBooks)writerConstructor.Invoke(writerConstructor.GetParameters().Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
                    var group = value == "named" ? "Selected Audit Group" : value == "empty" ? string.Empty : null;
                    if (directWriter)
                    {
                        var local = new LocalBook { IsManualImport = true, ExistingFile = true, Path = path, Author = author, Book = book, Edition = edition,
                            Quality = quality, DurationSeconds = 1, Part = 1, PartCount = 1, Size = 15, Modified = DateTime.UtcNow, ReleaseGroup = group, IndexerFlags = (IndexerFlags)4 };
                        var results = writer.Import(new() { new(local) }, true, null, ImportMode.Move);
                        Assert.That(results.Single().Result, Is.EqualTo(ImportResultType.Imported), string.Join(";", results.Single().Errors));
                    }
                    else
                    {
                        dependencies[typeof(IImportApprovedBooks)] = writer;
                        dependencies[typeof(IRootFolderService)] = Proxy<IRootFolderService>((method, args) => new RootFolder { Id = 1, Path = folder });
                        dependencies[typeof(ITrackedDownloadService)] = Proxy<ITrackedDownloadService>((method, args) => method.Name == nameof(ITrackedDownloadService.GetTrackedDownloads)
                            ? new List<TrackedDownload>() : null);
                        var constructor = typeof(ManualImportService).GetConstructors().Single();
                        var subject = (ManualImportService)constructor.Invoke(constructor.GetParameters().Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
                        var command = new ManualImportCommand { Files = new() { new() { Path = path, AuthorId = author.Id, BookId = book.Id,
                            EditionId = edition.Id, Quality = quality, IndexerFlags = 4 } }, ReplaceExistingFiles = true, ConfirmedReplacementFiles = new(), ImportMode = ImportMode.Move };
                        var body = System.Text.Json.Nodes.JsonNode.Parse(NzbDrone.Common.Serializer.STJson.ToJson(command));
                        var file = body["files"][0].AsObject();
                        file.Remove("releaseGroup");
                        if (value != "omitted") file["releaseGroup"] = group;
                        subject.Execute((ManualImportCommand)NzbDrone.Common.Serializer.STJson.Deserialize(body.ToJsonString(), typeof(ManualImportCommand)));
                    }
                    var saved = files.All().Single();
                    Assert.Multiple(() =>
                    {
                        Assert.That(saved.ReleaseGroup, Is.EqualTo(existing && string.IsNullOrWhiteSpace(group) ? "Stored Group" : group));
                        Assert.That(saved.EditionId, Is.EqualTo(edition.Id));
                        Assert.That(saved.Quality.Quality, Is.EqualTo(quality.Quality));
                        Assert.That(saved.Path, Is.EqualTo(path));
                        Assert.That(saved.Id, existing ? Is.EqualTo(original.Id) : Is.GreaterThan(0));
                        Assert.That(saved.SceneName, Is.EqualTo(existing ? "Stored Scene" : null));
                        Assert.That(saved.IndexerFlags, Is.EqualTo((IndexerFlags)(existing ? 3 : 4)));
                        Assert.That(book.AnyEditionOk, Is.True);
                        Assert.That(System.IO.File.ReadAllText(path), Is.EqualTo("selected source"));
                        Assert.That(mediaWrites, Is.EqualTo(1));
                        Assert.That(historyRows.All().Single().BookId, Is.EqualTo(book.Id));
                    });
                    TestContext.Progress.WriteLine($"MANUAL_GROUP database={database.DatabaseType} media={media} existing={existing} input={value} directWriter={directWriter} group={saved.ReleaseGroup ?? "<null>"} fileWrites={mediaWrites}");
                }
                finally
                {
                    System.IO.Directory.Delete(folder, true);
                }
            });
        }

        [TestCase("Detected Group")]
        [TestCase(null)]
        public void initial_manual_preview_should_preserve_the_detected_release_group(string group)
        {
            var file = Proxy<IFileInfo>((method, args) => method.Name switch
            {
                "get_FullName" => "/downloads/preview.epub", "get_Exists" => true, _ => null
            });
            var dependencies = new Dictionary<Type, object>
            {
                [typeof(NzbDrone.Core.Download.TrackedDownloads.ITrackedDownloadAlreadyImported)] = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger()), [typeof(Logger)] = LogManager.GetCurrentClassLogger(),
                [typeof(IDiskProvider)] = Proxy<IDiskProvider>((method, args) => file)
            };
            var constructor = typeof(ManualImportService).GetConstructors().Single();
            var subject = (ManualImportService)constructor.Invoke(constructor.GetParameters().Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
            var method = typeof(ManualImportService).GetMethod("MapItem", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var item = (ManualImportItem)method.Invoke(subject, new object[] { new ImportDecision<LocalBook>(new LocalBook
            {
                Path = "/downloads/preview.epub", ReleaseGroup = group, Quality = new QualityModel(Quality.EPUB)
            }), null, false, false, new Dictionary<LocalBook, List<NzbDrone.Core.CustomFormats.CustomFormat>>() });
            Assert.That(item.ReleaseGroup, Is.EqualTo(group));
        }

        [TestCase("Selected Audit Group", "Selected Audit Group", false)]
        [TestCase("Selected Audit Group", "Selected Audit Group", true)]
        [TestCase("", "Detected Group", false)]
        [TestCase("", "Detected Group", true)]
        [TestCase(null, "Detected Group", false)]
        [TestCase(null, "Detected Group", true)]
        [TestCase("   ", "Detected Group", false)]
        [TestCase("   ", "Detected Group", true)]
        public void manual_preview_update_should_keep_its_existing_group_fallback(string input, string expected, bool unknownQuality)
        {
            var decision = new ImportDecision<LocalBook>(new LocalBook
            {
                Path = "/downloads/preview.epub", ReleaseGroup = "Detected Group", Quality = new QualityModel(Quality.EPUB)
            });
            var dependencies = new Dictionary<Type, object>
            {
                [typeof(NzbDrone.Core.Download.TrackedDownloads.ITrackedDownloadAlreadyImported)] = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger()), [typeof(Logger)] = LogManager.GetCurrentClassLogger(),
                [typeof(IDiskProvider)] = Proxy<IDiskProvider>((method, args) => Proxy<IFileInfo>((property, values) => null)),
                [typeof(IMakeImportDecision)] = Proxy<IMakeImportDecision>((method, args) => new List<ImportDecision<LocalBook>> { decision })
            };
            var constructor = typeof(ManualImportService).GetConstructors().Single();
            var subject = (ManualImportService)constructor.Invoke(constructor.GetParameters().Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
            var item = new ManualImportItem { Path = decision.Item.Path, ReleaseGroup = input, Quality = new QualityModel(unknownQuality ? Quality.Unknown : Quality.EPUB),
                IndexerFlags = 4, DisableReleaseSwitching = true, ReplaceExistingFiles = true };
            var saved = subject.UpdateItems(new() { item }).Single();
            Assert.Multiple(() =>
            {
                Assert.That(saved, Is.SameAs(item));
                Assert.That(saved.ReleaseGroup, Is.EqualTo(expected));
                Assert.That(saved.IndexerFlags, Is.EqualTo(4));
                Assert.That(saved.DisableReleaseSwitching, Is.True);
                Assert.That(saved.ReplaceExistingFiles, Is.True);
                Assert.That(saved.Quality.Quality, Is.EqualTo(Quality.EPUB));
            });
        }

        private static IEnumerable<TestCaseData> MixedMediaPreviewCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var singleFile in new[] { false, true })
            foreach (var independentMatch in new[] { false, true })
                yield return new TestCaseData(postgres, media, singleFile, independentMatch);
        }

        [TestCaseSource(nameof(MixedMediaPreviewCases))]
        public void tracked_manual_preview_should_show_all_media_and_resolve_each_side_independently(
            bool postgres, BookMediaType media, bool singleFile, bool independentMatch)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "manual-preview-media-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(temp);
                try
                {
                    using var logFactory = new LogFactory();
                    var logTarget = new NLog.Targets.MemoryTarget();
                    var logConfig = new NLog.Config.LoggingConfiguration();
                    logConfig.AddRuleForAllLevels(logTarget);
                    logFactory.Configuration = logConfig;
                    var logger = logFactory.GetLogger("ManualMediaPreview");
                    var events = Proxy<IEventAggregator>((method, args) => null);
                    var author = new AuthorRepository(database, events, logger).Insert(new Author { Name = "Test Writer", CleanName = "testwriter",
                        SortName = "testwriter", TitleSlug = "test-writer", Path = temp, Added = DateTime.UtcNow });
                    var books = new BookRepository(database, events);
                    var editions = new EditionRepository(database, events);
                    var aliases = new ProviderAliasService(new ProviderAliasRepository(database, events));
                    Book Seed(string slug, BookMediaType type, string title)
                    {
                        var book = books.Insert(new Book { AuthorId = author.Id, Author = author, Title = title, CleanTitle = title,
                            TitleSlug = slug, MediaType = type, Added = DateTime.UtcNow });
                        var edition = editions.Insert(new Edition { BookId = book.Id, Book = book, Title = title, ForeignEditionId = "local:" + slug,
                            ReadingFormatId = type == BookMediaType.Audiobook ? 2 : 3, Monitored = true,
                            Narrator = "First Reader", NarratorNames = new() { "First Reader" } });
                        book.Editions = new() { edition };
                        return book;
                    }
                    var own = Seed("grabbed", media, "Paper Lanterns");
                    var opposite = Seed("companion", media == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook, "Companion Notes");
                    var authorService = new AuthorService(new AuthorRepository(database, events, logger), events, null, null, null,
                        new NzbDrone.Common.Cache.CacheManager(), books, null, logger, null, null, null, database, aliases);
                    var editionService = new EditionService(editions, events, logger);
                    var bookFiles = new MediaFileRepository(database, events);
                    var mediaFiles = Proxy<IMediaFileService>((method, args) => method.Name switch
                    {
                        nameof(IMediaFileService.GetFilesByBooks) => bookFiles.GetFilesByBooks((List<int>)args[0]),
                        nameof(IMediaFileService.GetFileWithPath) => bookFiles.GetFileWithPath((List<string>)args[0]),
                        _ => throw new NotImplementedException(method.Name)
                    });
                    var bookService = new BookService(books, editionService, events, authorService, mediaFiles, null,
                        new SeriesBookLinkRepository(database, events), null, logger, providerAliasService: aliases);
                    Assert.That(bookService.GetBook(own.Id).Id, Is.EqualTo(own.Id));
                    Assert.That(bookService.GetBook(opposite.Id).Id, Is.EqualTo(opposite.Id));
                    var fts = new EditionFtsRepository(database, logger);
                    fts.RebuildIndex();
                    var actualMatcher = new FileMatchingService(Proxy<IMatchingUploadLogger>((method, args) => null), null,
                        new NzbDrone.Core.MediaFiles.BookImport.Services.ContainmentValidator(new NzbDrone.Core.MediaFiles.BookImport.Services.TagNormalizer(), logger),
                        null, null, null, null, ConfigServiceTestProxy.Create(), authorService, events, null, fts, bookService, editionService, editions, null, logger);
                    var matchingBatches = 0;
                    var matching = Proxy<IFileMatchingService>((method, args) =>
                    {
                        matchingBatches++;
                        return method.Invoke(actualMatcher, args);
                    });
                    var ownPath = System.IO.Path.Combine(temp, "own" + (media == BookMediaType.Audiobook ? ".m4b" : ".epub"));
                    var oppositePath = System.IO.Path.Combine(temp, "bonus" + (media == BookMediaType.Audiobook ? ".epub" : ".m4b"));
                    System.IO.File.WriteAllText(ownPath, "book"); System.IO.File.WriteAllText(oppositePath, "companion");
                    var fs = new FileSystem();
                    var disk = Proxy<IDiskProvider>((method, args) => method.Name switch
                    {
                        nameof(IDiskProvider.FolderExists) => System.IO.Directory.Exists((string)args[0]),
                        nameof(IDiskProvider.FileExists) => System.IO.File.Exists((string)args[0]),
                        nameof(IDiskProvider.GetFileInfo) => fs.FileInfo.FromFileName((string)args[0]),
                        _ => null
                    });
                    var tags = Proxy<IMetadataTagService>((method, args) =>
                    {
                        var ownFile = ((IFileInfo)args[0]).FullName == ownPath;
                        var values = ownFile || independentMatch
                            ? new Dictionary<string, List<string>> { ["TITLE"] = new() { ownFile ? own.Title : opposite.Title },
                                ["AUTHOR"] = new() { author.Name }, ["NARRATOR"] = new() { "First Reader" } }
                            : new Dictionary<string, List<string>>();
                        return (values, (int?)null);
                    });
                    var decisionMaker = new SimpleImportDecisionMaker(tags, matching, authorService, bookService, editionService, mediaFiles, logger);
                    var tracked = CreateTrackedDownload(new QualityModel(media == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB), own);
                    tracked.RemoteBook.Author = author;
                    tracked.ImportItem = new DownloadClientItem { OutputPath = new OsPath(singleFile ? oppositePath : temp) };
                    var dependencies = new Dictionary<Type, object>
                    {
                        [typeof(IDiskProvider)] = disk,
                        [typeof(IDiskScanService)] = Proxy<IDiskScanService>((method, args) => new[] { ownPath, oppositePath }.Select(path => fs.FileInfo.FromFileName(path)).ToArray()),
                        [typeof(IBookService)] = bookService,
                        [typeof(IHistoryService)] = new HistoryService(new HistoryRepository(database, events), logger,
                            new NzbDrone.Core.Download.History.DownloadHistoryRepository(database, events)),
                        [typeof(ITrackedDownloadService)] = Proxy<ITrackedDownloadService>((method, args) => tracked),
                        [typeof(IMakeImportDecision)] = decisionMaker,
                        [typeof(NzbDrone.Core.CustomFormats.ICustomFormatCalculationService)] = Proxy<NzbDrone.Core.CustomFormats.ICustomFormatCalculationService>((method, args) => new List<NzbDrone.Core.CustomFormats.CustomFormat>()),
                        [typeof(NzbDrone.Core.Download.TrackedDownloads.ITrackedDownloadAlreadyImported)] = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger()), [typeof(Logger)] = logger
                    };
                    var constructor = typeof(ManualImportService).GetConstructors().Single();
                    var subject = (ManualImportService)constructor.Invoke(constructor.GetParameters().Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
                    var rows = subject.GetMediaFiles("ignored", "download-1", null, FilterFilesType.None, false);
                    TestContext.Progress.WriteLine($"MANUAL_MEDIA_PREVIEW database={database.DatabaseType} media={media} single={singleFile} independent={independentMatch} rows={string.Join(',', rows.Select(row => row.Name + ':' + row.Book?.Id))} matchingBatches={matchingBatches}");
                    if (rows.Count != (singleFile ? 1 : 2) || (independentMatch && !rows.Any(row => row.Book?.Id == opposite.Id)))
                        TestContext.Progress.WriteLine(string.Join(Environment.NewLine, logTarget.Logs));
                    Assert.That(rows, Has.Count.EqualTo(singleFile ? 1 : 2));
                    var companion = rows.Single(row => row.Path == oppositePath);
                    Assert.That(companion.Book?.Id, Is.EqualTo(independentMatch ? (int?)opposite.Id : null));
                    Assert.That(companion.Edition?.Id, Is.EqualTo(independentMatch ? (int?)opposite.Editions[0].Id : null));
                    Assert.That(companion.Book?.Id, Is.Not.EqualTo(own.Id));
                    Assert.That(matchingBatches, Is.EqualTo(1));
                    if (!singleFile) Assert.That(rows.Single(row => row.Path == ownPath).Book?.Id, Is.EqualTo(own.Id));
                }
                finally { System.IO.Directory.Delete(temp, true); }
            });
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void explicit_manual_update_selections_should_remain_under_user_control(BookMediaType media)
        {
            var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + (media == BookMediaType.Audiobook ? ".epub" : ".m4b"));
            System.IO.File.WriteAllText(temp, "manual");
            try
            {
                var book = new Book { Id = 10, MediaType = media };
                var edition = new Edition { Id = 20, BookId = book.Id };
                var mediaFiles = Proxy<IMediaFileService>((method, args) => method.Name == nameof(IMediaFileService.GetFileWithPath)
                    ? new List<BookFile>() : throw new NotImplementedException(method.Name));
                var decisionMaker = new SimpleImportDecisionMaker(null, null, null, null, null, mediaFiles, LogManager.GetCurrentClassLogger());
                var decisions = decisionMaker.GetImportDecisions(new() { new FileSystem().FileInfo.FromFileName(temp) },
                    new NzbDrone.Core.MediaFiles.BookImport.Identification.IdentificationOverrides { Book = book, Edition = edition }, null,
                    new NzbDrone.Core.MediaFiles.BookImport.Identification.ImportDecisionMakerConfig { NewDownload = true, Filter = FilterFilesType.None });
                Assert.That(decisions.Single().Item.Book, Is.SameAs(book));
                Assert.That(decisions.Single().Item.Edition, Is.SameAs(edition));
            }
            finally { System.IO.File.Delete(temp); }
        }

        private static ImportResult CreateImportResult(int bookId, BookMediaType mediaType, params string[] errors)
        {
            return CreateImportResultForPath(bookId, mediaType, "/downloads/book-" + bookId + (mediaType == BookMediaType.Audiobook ? ".m4b" : ".epub"), errors);
        }

        private static ImportResult CreateImportResultForPath(int bookId, BookMediaType mediaType, string path, params string[] errors)
        {
            var author = new Author { Id = 7, Name = "Test Author" };
            var book = new Book
            {
                Id = bookId,
                AuthorId = author.Id,
                Author = author,
                Title = $"Book {bookId}",
                MediaType = mediaType
            };

            var localBook = new LocalBook
            {
                Author = author,
                Book = book,
                Path = path
            };

            return new ImportResult(new ImportDecision<LocalBook>(localBook), errors);
        }

        private static ManualImportFile CreatePreviewFile(int bookId, string path)
        {
            return new ManualImportFile
            {
                BookId = bookId,
                Path = path,
                DownloadId = "download-1"
            };
        }

        private static ImportResult CreateGeneratedConversionResult(int bookId, params string[] sourcePaths)
        {
            var result = CreateImportResultForPath(bookId, BookMediaType.Audiobook, "/downloads/Babbitt.m4b");
            result.ImportDecision.Item.GeneratedConversionSourcePaths = sourcePaths.ToList();
            return result;
        }
    }
}

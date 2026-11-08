using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Chaptarr.Core.Test.Parser;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ManualImportTitleLookupFixture
    {
        private sealed class Observed
        {
            public IdentificationOverrides Overrides { get; set; }
            public ImportDecisionMakerInfo Info { get; set; }
            public int TrackedReads { get; set; }
            public DownloadClientItem DownloadItem { get; set; }
            public int PreviewBookReads { get; set; }
            public int PreviewHistoryReads { get; set; }
        }

        private static (ManualImportService Service, Observed Observed, string File) Service(
            ParsingServiceTitleLookupFixture.Harness harness, ParsedBookInfo parsed, bool folder, Book identified = null, List<Book> identifiedBooks = null, bool alternativeCopies = false, bool trackedAvailable = true, bool noDecisions = false)
        {
            var directory = @"C:\Downloads\Test Writer\Paper Lanterns".AsOsAgnostic();
            var file = Path.Combine(directory, "Paper Lanterns.m4b");
            var observed = new Observed();
            var item = new DownloadClientItem { DownloadId = "tracked-1", OutputPath = new OsPath(folder ? directory : file) };
            observed.DownloadItem = item;
            var tracked = new TrackedDownload
            {
                DownloadItem = item, ImportItem = item,
                RemoteBook = new RemoteBook
                {
                    Author = harness.Author, ParsedBookInfo = parsed, AlternativeCopies = alternativeCopies,
                    Books = identifiedBooks ?? (identified == null ? new List<Book>() : new List<Book> { identified })
                }
            };
            var downloads = ParsingServiceTitleLookupFixture.Proxy<ITrackedDownloadService>((method, args) =>
            {
                observed.TrackedReads++;
                Assert.That(method.Name, Is.EqualTo(nameof(ITrackedDownloadService.Find)));
                return trackedAvailable ? tracked : null;
            });
            var disk = ParsingServiceTitleLookupFixture.Proxy<IDiskProvider>((method, args) => method.Name switch
            {
                nameof(IDiskProvider.FolderExists) => folder,
                nameof(IDiskProvider.FileExists) => true,
                nameof(IDiskProvider.GetFileInfo) => ParsingServiceTitleLookupFixture.Proxy<IFileInfo>((property, _) => property.Name switch
                {
                    "get_FullName" => (string)args[0], "get_Exists" => true, "get_Extension" => ".m4b", "get_Length" => 1024L,
                    _ => throw new NotImplementedException(property.Name)
                }),
                _ => throw new NotImplementedException(method.Name)
            });
            var decisions = ParsingServiceTitleLookupFixture.Proxy<IMakeImportDecision>((method, args) =>
            {
                observed.Overrides = (IdentificationOverrides)args[1];
                observed.Info = (ImportDecisionMakerInfo)args[2];
                if (noDecisions) return new List<ImportDecision<LocalBook>>();
                return ((List<IFileInfo>)args[0]).Select(info => new ImportDecision<LocalBook>(new LocalBook
                {
                    Path = info.FullName, Quality = parsed.Quality,
                    Book = observed.Overrides?.Book, Edition = observed.Overrides?.Book?.Editions.FirstOrDefault()
                })).ToList();
            });
            var calculator = ParsingServiceTitleLookupFixture.Proxy<ICustomFormatCalculationService>((method, args) => new List<CustomFormat>());
            var history = ParsingServiceTitleLookupFixture.Proxy<IHistoryService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IHistoryService.FindByDownloadIds)));
                Assert.That((IEnumerable<string>)args[0], Is.EqualTo(new[] { "tracked-1" }));
                Assert.That(args[1], Is.EqualTo(EntityHistoryEventType.BookFileImported));
                observed.PreviewHistoryReads++;
                return new List<EntityHistory>();
            });
            var previewBooks = ParsingServiceTitleLookupFixture.Proxy<IBookService>((method, args) =>
            {
                if (method.Name == nameof(IBookService.GetBooksForLookup))
                {
                    observed.PreviewBookReads++;
                    var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                    return harness.Catalog.Where(book => ids.Contains(book.Id)).ToList();
                }
                return method.Invoke(harness.Books, args);
            });
            var service = new ManualImportService(disk, harness.Parser, null, null, decisions, null, previewBooks,
                null, null, null, null, null, null, null, null, calculator, downloads, null, history, null, null, null,
                LogManager.GetCurrentClassLogger(), new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(NLog.LogManager.GetCurrentClassLogger()));
            return (service, observed, file);
        }

        [Test]
        public void unavailable_tracked_download_returns_no_preview_without_reading_history()
        {
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book>());
            var (service, observed, file) = Service(harness, ParsingServiceTitleLookupFixture.Parsed("Unknown"), true, trackedAvailable: false);
            var results = service.GetMediaFiles("ignored", "tracked-1", null, FilterFilesType.None, false, exactPaths: new[] { file });
            Assert.That(results, Is.Empty);
            Assert.That(observed.TrackedReads, Is.EqualTo(1));
            Assert.That(observed.PreviewHistoryReads, Is.Zero);
            Assert.That(observed.PreviewBookReads, Is.Zero);
        }

        [Test]
        public void empty_folder_decisions_return_no_preview_without_requesting_policy_history()
        {
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book>());
            var (service, observed, file) = Service(harness, ParsingServiceTitleLookupFixture.Parsed("Unknown"), true, noDecisions: true);
            var results = service.GetMediaFiles("ignored", "tracked-1", harness.Author, FilterFilesType.None, false, exactPaths: new[] { file });
            Assert.That(results, Is.Empty);
            Assert.That(observed.TrackedReads, Is.EqualTo(1));
            Assert.That(observed.PreviewHistoryReads, Is.Zero);
            Assert.That(observed.PreviewBookReads, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void tracked_title_should_use_shared_articles_and_load_the_catalog_once(bool folder)
        {
            var audio = ParsingServiceTitleLookupFixture.Make(501, "The Paper Lanterns");
            var ebook = ParsingServiceTitleLookupFixture.Make(502, "Paper Lanterns", BookMediaType.Ebook);
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { audio, ebook });
            var (service, observed, file) = Service(harness, ParsingServiceTitleLookupFixture.Parsed("Paper Lanterns"), folder);

            var results = service.GetMediaFiles("ignored", "tracked-1", harness.Author, FilterFilesType.None, false,
                exactPaths: new[] { file });

            Assert.That(observed.Info.TargetBookIds, Is.Empty);
            Assert.That(observed.Overrides.Book, Is.SameAs(audio));
            Assert.That(results.Single().Book, Is.SameAs(audio));
            Assert.That(harness.BookReads, Is.EqualTo(1));
            Assert.That(observed.TrackedReads, Is.EqualTo(1), "Policy preview reuses the tracked download already resolved for this request");
            Assert.That(observed.PreviewHistoryReads, Is.EqualTo(1));
        }

        [Test]
        public void unresolved_internal_word_omission_should_keep_the_author_without_a_book_override()
        {
            var book = ParsingServiceTitleLookupFixture.Make(501, "I Married a Naga");
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { book });
            var (service, observed, file) = Service(harness, ParsingServiceTitleLookupFixture.Parsed("I Married Naga"), true);

            service.GetMediaFiles("ignored", "tracked-1", harness.Author, FilterFilesType.None, false, exactPaths: new[] { file });

            Assert.That(observed.Overrides.Author, Is.SameAs(harness.Author));
            Assert.That(observed.Overrides.Book, Is.Null);
            Assert.That(harness.BookReads, Is.EqualTo(1));
            Assert.That(observed.TrackedReads, Is.EqualTo(1), "Policy preview reuses the tracked download already resolved for this request");
            Assert.That(observed.PreviewHistoryReads, Is.EqualTo(1));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void an_explicit_tracked_book_and_its_edition_should_precede_title_lookup(bool folder, bool differentCallerAuthor)
        {
            var book = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns");
            var edition = book.Editions.Single();
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { book });
            var (service, observed, file) = Service(harness, ParsingServiceTitleLookupFixture.Parsed("Different title"), folder, book);

            var callerAuthor = differentCallerAuthor ? new Author { Id = 39, Name = "Another Writer" } : harness.Author;
            var results = service.GetMediaFiles("ignored", "tracked-1", callerAuthor, FilterFilesType.None, false, exactPaths: new[] { file });

            Assert.That(observed.Overrides.Author.Id, Is.EqualTo(book.AuthorId));
            Assert.That(observed.Info.TargetBookIds, Is.EqualTo(new[] { book.Id }));
            Assert.That(observed.Overrides.Book, Is.SameAs(book));
            Assert.That(results.Single().Edition, Is.SameAs(edition));
            Assert.That(harness.BookReads, Is.Zero);
            Assert.That(harness.BookIdReads, Is.EqualTo(1));
            Assert.That(observed.TrackedReads, Is.EqualTo(1), "Policy preview reuses the tracked download already resolved for this request");
            Assert.That(observed.PreviewHistoryReads, Is.EqualTo(1));
        }

        [Test]
        public void alternative_copy_preview_must_not_use_ordinary_single_grab_context()
        {
            var book = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns");
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { book });
            var (service, observed, file) = Service(harness, ParsingServiceTitleLookupFixture.Parsed(book.Title), true,
                book, alternativeCopies: true);
            service.GetMediaFiles("ignored", "tracked-1", harness.Author, FilterFilesType.None, false, exactPaths: new[] { file });
            Assert.That(observed.Info.TargetBookIds, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void a_tracked_collection_should_keep_author_and_download_context_without_selecting_one_book(bool folder)
        {
            var first = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns");
            var second = ParsingServiceTitleLookupFixture.Make(502, "Glass Lanterns");
            first.ReleaseDate = second.ReleaseDate = new DateTime(2020, 1, 1);
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { first, second });
            var parsed = ParsingServiceTitleLookupFixture.Parsed("Collected works");
            parsed.Discography = true;
            var (service, observed, file) = Service(harness, parsed, folder);

            service.GetMediaFiles("ignored", "tracked-1", null, FilterFilesType.None, false, exactPaths: new[] { file });

            Assert.Multiple(() =>
            {
                Assert.That(observed.Overrides?.Author, Is.SameAs(harness.Author));
                Assert.That(observed.Overrides?.Book, Is.Null);
                Assert.That(observed.Info?.DownloadClientItem, Is.SameAs(observed.DownloadItem));
                Assert.That(harness.BookReads, Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_tracked_title_resolution_should_keep_author_and_download_context(bool folder)
        {
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book>());
            var parsed = ParsingServiceTitleLookupFixture.Parsed("Collected works");
            parsed.Discography = true;
            parsed.DiscographyStart = parsed.DiscographyEnd = 10000;
            var (service, observed, file) = Service(harness, parsed, folder);

            service.GetMediaFiles("ignored", "tracked-1", null, FilterFilesType.None, false, exactPaths: new[] { file });

            Assert.Multiple(() =>
            {
                Assert.That(observed.Overrides?.Author, Is.SameAs(harness.Author));
                Assert.That(observed.Overrides?.Book, Is.Null);
                Assert.That(observed.Info?.DownloadClientItem, Is.SameAs(observed.DownloadItem));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void multiple_tracked_target_ids_should_keep_collection_context_without_title_lookup(bool folder)
        {
            var first = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns");
            var second = ParsingServiceTitleLookupFixture.Make(502, "Glass Lanterns");
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { first, second });
            var (service, observed, file) = Service(harness, ParsingServiceTitleLookupFixture.Parsed(first.Title), folder,
                identifiedBooks: new() { first, second });

            service.GetMediaFiles("ignored", "tracked-1", null, FilterFilesType.None, false, exactPaths: new[] { file });

            Assert.Multiple(() =>
            {
                Assert.That(observed.Overrides?.Author, Is.SameAs(harness.Author));
                Assert.That(observed.Overrides?.Book, Is.Null);
                Assert.That(observed.Info?.DownloadClientItem, Is.SameAs(observed.DownloadItem));
                Assert.That(harness.BookReads + harness.BookIdReads, Is.Zero);
                Assert.That(observed.Info.TargetBookIds, Is.EqualTo(new[] { first.Id, second.Id }));
            });
        }
    }
}

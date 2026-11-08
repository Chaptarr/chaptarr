using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Parser.Model;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        private static IEnumerable<TestCaseData> MissingWorkAuthorMonitoringCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var monitorBooks in new[] { false, true })
            foreach (var monitorAuthors in new[] { false, true })
            {
                yield return new TestCaseData(postgres, side, monitorBooks, monitorAuthors);
            }
        }

        [TestCaseSource(nameof(MissingWorkAuthorMonitoringCases))]
        public void missing_list_work_should_apply_author_monitoring_independently_of_book_monitoring(
            bool postgres, BookMediaType side, bool monitorBooks, bool monitorAuthors)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new StoredListLibrary(database);
                var foreignSide = side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var foreign = CatalogBook(foreignSide, "Requested Work", "123");
                library.Save(foreign);
                var unrelated = CatalogBook(side, "Unrequested Work", "125");
                library.Save(unrelated);
                var requested = CatalogBook(side, "Requested Work", "123");
                var reports = new List<ImportListItemInfo> { library.Report(requested, 1) };
                var monitoringWrites = 0;
                var authors = Proxy<IAuthorService>((method, args) =>
                {
                    if (method.Name == nameof(IAuthorService.SetMediaTypeMonitoring)) monitoringWrites++;
                    return method.Invoke(library.AuthorService, args);
                });
                var excludedOtherSide = new List<ImportListExclusion>
                {
                    new() { ForeignId = library.Author.GoodreadsAuthorId, MediaType = foreignSide },
                    new() { ForeignId = requested.GoodreadsWorkId, MediaType = foreignSide }
                };
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(library.Author, foreign, side, false,
                    authors, library.Books, library.Editions, library.Pending, library.Roots, library.Queue,
                    reports: reports, keep: monitorBooks, exclusions: excludedOtherSide, search: false,
                    configureList: list => list.MonitoringOptions.ResumePausedAuthors = monitorAuthors,
                    addMissingAuthors: false).Execute(new ImportListSyncCommand(1));

                var saved = library.Authors.Get(library.Author.Id);
                var pending = library.Pending.GetByProviderId(library.Author.GoodreadsAuthorId);
                Assert.Multiple(() =>
                {
                    Assert.That(side == BookMediaType.Audiobook ? saved.AudiobookMonitored : saved.EbookMonitored, Is.EqualTo(monitorAuthors));
                    Assert.That(side == BookMediaType.Audiobook ? saved.EbookMonitored : saved.AudiobookMonitored, Is.False);
                    Assert.That(monitoringWrites, Is.EqualTo(monitorAuthors ? 1 : 0));
                    Assert.That(library.Books.GetBook(foreign.Id).IsMonitored(), Is.False);
                    Assert.That(library.Books.GetBook(unrelated.Id).IsMonitored(), Is.False);
                    Assert.That(saved.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    Assert.That(saved.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    Assert.That(saved.AudiobookQualityProfileId, Is.EqualTo(9));
                    Assert.That(saved.EbookQualityProfileId, Is.EqualTo(9));
                    Assert.That(saved.AudiobookMetadataProfileId, Is.EqualTo(8));
                    Assert.That(saved.EbookMetadataProfileId, Is.EqualTo(8));
                    Assert.That(saved.AudiobookRootFolderPath, Is.EqualTo(library.Author.AudiobookRootFolderPath));
                    Assert.That(saved.EbookRootFolderPath, Is.EqualTo(library.Author.EbookRootFolderPath));
                    Assert.That(pending != null, Is.EqualTo(monitorBooks));
                    Assert.That(library.Commands.OfType<ProcessPendingImportsCommand>().Count(), Is.EqualTo(monitorBooks ? 1 : 0));
                });
                if (monitorBooks)
                {
                    Assert.That(side == BookMediaType.Audiobook ? pending.AudiobookMonitored : pending.EbookMonitored, Is.EqualTo(monitorAuthors));
                    library.Process(pending, new Author
                    {
                        Name = library.Author.Name, GoodreadsAuthorId = library.Author.GoodreadsAuthorId,
                        Books = new List<Book> { requested, CatalogBook(side, unrelated.Title, "125"), CatalogBook(foreignSide, foreign.Title, "123") },
                        Series = new List<Series>()
                    });
                    var remaining = library.Pending.GetByProviderId(library.Author.GoodreadsAuthorId);
                    Assert.That(remaining, Is.Null, remaining?.LastError);
                    var hydrated = library.Books.GetBooksByAuthor(library.Author.Id);
                    Assert.That(hydrated.Single(book => book.MediaType == side && book.GoodreadsWorkId == "gr:123").IsMonitored(), Is.True);
                    Assert.That(hydrated.Single(book => book.Id == unrelated.Id).IsMonitored(), Is.False);
                    saved = library.Authors.Get(library.Author.Id);
                    Assert.That(side == BookMediaType.Audiobook ? saved.AudiobookMonitored : saved.EbookMonitored, Is.EqualTo(monitorAuthors));
                }
                TestContext.WriteLine($"{database.DatabaseType}/{side}: absent listed work, book control {monitorBooks}, author control {monitorAuthors}; author monitoring actions {monitoringWrites}, bounded hydration {monitorBooks}.");
            });
        }

        private static IEnumerable<TestCaseData> RejectedMissingWorkAuthorMonitoringCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var monitorBooks in new[] { false, true })
            foreach (var rejection in new[] { "work exclusion", "author exclusion", "unconfigured side" })
            {
                yield return new TestCaseData(postgres, side, monitorBooks, rejection);
            }
        }

        [TestCaseSource(nameof(RejectedMissingWorkAuthorMonitoringCases))]
        public void rejected_or_unconfigured_missing_list_side_should_not_enable_author_monitoring(
            bool postgres, BookMediaType side, bool monitorBooks, string rejection)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new StoredListLibrary(database);
                var foreignSide = side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var foreign = CatalogBook(foreignSide, "Requested Work", "123");
                library.Save(foreign);
                if (rejection == "unconfigured side")
                {
                    if (side == BookMediaType.Audiobook)
                    {
                        library.Author.AudiobookQualityProfileId = null;
                        library.Author.AudiobookMetadataProfileId = null;
                        library.Author.AudiobookRootFolderPath = null;
                        library.Author.AudiobookMonitored = null;
                        library.Author.AudiobookMonitorNewItems = null;
                    }
                    else
                    {
                        library.Author.EbookQualityProfileId = null;
                        library.Author.EbookMetadataProfileId = null;
                        library.Author.EbookRootFolderPath = null;
                        library.Author.EbookMonitored = null;
                        library.Author.EbookMonitorNewItems = null;
                    }
                    library.Authors.Update(library.Author);
                }
                var requested = CatalogBook(side, foreign.Title, "123");
                var exclusions = rejection == "unconfigured side" ? new List<ImportListExclusion>() : new()
                {
                    new() { ForeignId = rejection == "author exclusion" ? library.Author.GoodreadsAuthorId : requested.GoodreadsWorkId, MediaType = side }
                };
                var monitoringWrites = 0;
                var authors = Proxy<IAuthorService>((method, args) =>
                {
                    if (method.Name == nameof(IAuthorService.SetMediaTypeMonitoring)) monitoringWrites++;
                    return method.Invoke(library.AuthorService, args);
                });
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(library.Author, foreign, side, false,
                    authors, library.Books, library.Editions, library.Pending, library.Roots, library.Queue,
                    reports: new() { library.Report(requested, 1) }, keep: monitorBooks, exclusions: exclusions, search: false,
                    configureList: list => list.MonitoringOptions.ResumePausedAuthors = true,
                    addMissingAuthors: false).Execute(new ImportListSyncCommand(1));
                var saved = library.Authors.Get(library.Author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(side == BookMediaType.Audiobook ? saved.AudiobookMonitored : saved.EbookMonitored,
                        rejection == "unconfigured side" ? Is.Null : Is.False);
                    Assert.That(side == BookMediaType.Audiobook ? saved.EbookMonitored : saved.AudiobookMonitored, Is.False);
                    Assert.That(monitoringWrites, Is.Zero);
                    Assert.That(library.Pending.GetByProviderId(library.Author.GoodreadsAuthorId), Is.Null);
                    Assert.That(library.Commands.OfType<ProcessPendingImportsCommand>(), Is.Empty);
                    Assert.That(library.Books.GetBook(foreign.Id).IsMonitored(), Is.False);
                });
            });
        }

        [TestCase(false, BookMediaType.Audiobook, false)]
        [TestCase(true, BookMediaType.Audiobook, false)]
        [TestCase(false, BookMediaType.Ebook, false)]
        [TestCase(true, BookMediaType.Ebook, false)]
        [TestCase(false, BookMediaType.Audiobook, true)]
        [TestCase(true, BookMediaType.Audiobook, true)]
        [TestCase(false, BookMediaType.Ebook, true)]
        [TestCase(true, BookMediaType.Ebook, true)]
        public void missing_work_batch_should_enable_author_side_once_and_reuse_existing_batch_lookups(
            bool postgres, BookMediaType side, bool monitorBooks)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new StoredListLibrary(database);
                var foreign = CatalogBook(side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook,
                    "Requested Work 1", "1");
                library.Save(foreign);
                var reports = Enumerable.Range(1, 25).Select(index => library.Report(CatalogBook(side,
                    "Requested Work " + index, index.ToString()), 1)).ToList();
                var monitoringWrites = 0;
                var authors = Proxy<IAuthorService>((method, args) =>
                {
                    if (method.Name == nameof(IAuthorService.SetMediaTypeMonitoring)) monitoringWrites++;
                    return method.Invoke(library.AuthorService, args);
                });
                var readersBefore = library.ReaderStatements.Count;
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(library.Author, foreign, side, false,
                    authors, library.Books, library.Editions, library.Pending, library.Roots, library.Queue,
                    reports: reports, keep: monitorBooks, search: false,
                    configureList: list => list.MonitoringOptions.ResumePausedAuthors = true,
                    addMissingAuthors: false).Execute(new ImportListSyncCommand(1));
                Assert.Multiple(() =>
                {
                    Assert.That(monitoringWrites, Is.EqualTo(1));
                    Assert.That(library.Calls.GetValueOrDefault(nameof(IBookService.GetImportListBookIdentities)), Is.EqualTo(1));
                    Assert.That(library.Calls.GetValueOrDefault(nameof(IBookService.GetExistingBooks)), Is.EqualTo(1));
                    Assert.That(library.Calls.GetValueOrDefault(nameof(IBookService.FindBookIdsByProviderIds)), Is.EqualTo(1));
                    Assert.That(library.Commands.OfType<ProcessPendingImportsCommand>().Count(), Is.EqualTo(monitorBooks ? 1 : 0));
                    Assert.That(library.AddOptionsBatches, Is.Empty);
                });
                TestContext.WriteLine($"{database.DatabaseType}/{side}: 25 absent listed works, book control {monitorBooks}; 1 author monitoring action, 1 catalog/provider/hydration read batch; reader statements {library.ReaderStatements.Count - readersBefore}; elapsed {elapsed.ElapsedMilliseconds} ms.");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void deferred_narration_for_work_excluded_by_another_provider_id_should_not_enable_author_monitoring(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var library = new StoredListLibrary(database);
                var present = CatalogBook(BookMediaType.Audiobook, "Excluded Work", "123");
                library.Save(present);
                var report = library.Report(present, 0);
                report.EditionProviderId = "gr:edition:999";
                report.EditionHasNarration = true;
                report.EditionNarratorNames = new() { "Requested Narrator" };
                var monitoringWrites = 0;
                var authors = Proxy<IAuthorService>((method, args) =>
                {
                    if (method.Name == nameof(IAuthorService.SetMediaTypeMonitoring)) monitoringWrites++;
                    return method.Invoke(library.AuthorService, args);
                });
                ImportListSyncServiceSearchOnAddFixture.BuildMissingMediaSideService(library.Author, present, BookMediaType.Audiobook, false,
                    authors, library.Books, library.Editions, library.Pending, library.Roots, library.Queue,
                    reports: new() { report }, exclusions: new() { new() { ForeignId = present.HardcoverBookId, MediaType = BookMediaType.Audiobook } },
                    search: false, configureList: list => list.MonitoringOptions.ResumePausedAuthors = true,
                    addMissingAuthors: false).Execute(new ImportListSyncCommand(1));
                var saved = library.Authors.Get(library.Author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(saved.AudiobookMonitored, Is.False);
                    Assert.That(saved.EbookMonitored, Is.False);
                    Assert.That(monitoringWrites, Is.Zero);
                    Assert.That(library.Books.GetBook(present.Id).IsMonitored(), Is.False);
                });
            });
        }
    }
}

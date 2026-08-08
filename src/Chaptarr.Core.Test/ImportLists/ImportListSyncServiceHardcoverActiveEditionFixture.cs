using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using Chaptarr.Core.Test.Books;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class ImportListSyncServiceHardcoverActiveEditionFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private static ImportListSyncService BuildService()
        {
            return new ImportListSyncService(
                importListFactory: DispatchProxy.Create<IImportListFactory, ThrowingProxy<IImportListFactory>>(),
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ThrowingProxy<IImportListExclusionService>>(),
                listFetcherAndParser: DispatchProxy.Create<IFetchAndParseImportList, ThrowingProxy<IFetchAndParseImportList>>(),
                bookInfoProxy: DispatchProxy.Create<IProvideBookInfo, ThrowingProxy<IProvideBookInfo>>(),
                authorService: DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                bookService: DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                bookRepository: DispatchProxy.Create<IBookRepository, ThrowingProxy<IBookRepository>>(),
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()),
                authorLibraryService: DispatchProxy.Create<IAuthorLibraryService, ThrowingProxy<IAuthorLibraryService>>(),
                pendingAuthorImportService: DispatchProxy.Create<IPendingAuthorImportService, ThrowingProxy<IPendingAuthorImportService>>(),
                rootFolderService: DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                rootFolderSettingsResolver: DispatchProxy.Create<IRootFolderSettingsResolver, ThrowingProxy<IRootFolderSettingsResolver>>(),
                eventAggregator: DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                commandQueueManager: DispatchProxy.Create<IManageCommandQueue, ThrowingProxy<IManageCommandQueue>>(),
                logger: LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: DispatchProxy.Create<IImportListBookIdentityCacheRepository, ThrowingProxy<IImportListBookIdentityCacheRepository>>());
        }

        private static Book InvokeFindBookAlreadyTargetingHardcoverEdition(ImportListSyncService service, IEnumerable<Book> candidateBooks, string editionProviderId, string editionRawId)
        {
            var method = typeof(ImportListSyncService).GetMethod(
                "FindBookAlreadyTargetingHardcoverEdition",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (method == null)
            {
                throw new InvalidOperationException("Could not find ImportListSyncService.FindBookAlreadyTargetingHardcoverEdition via reflection");
            }

            return (Book)method.Invoke(service, new object[] { candidateBooks, editionProviderId, editionRawId });
        }

        private static Book InvokeFindReusableHardcoverTargetBook(IEnumerable<Book> candidateBooks, ISet<int> reservedIds, ISet<int> bookIdsWithFiles)
        {
            var method = typeof(ImportListSyncService).GetMethod(
                "FindReusableHardcoverTargetBook",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
            {
                throw new InvalidOperationException("Could not find ImportListSyncService.FindReusableHardcoverTargetBook via reflection");
            }

            return (Book)method.Invoke(null, new object[] { candidateBooks, reservedIds, bookIdsWithFiles });
        }

        [Test]
        public void should_match_existing_book_by_monitored_edition()
        {
            var service = BuildService();
            var target = new Book
            {
                Id = 10,
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { Id = 20, Title = "Second", HardcoverEditionId = "hc:other" },
                    new Edition { Id = 10, Title = "First", HardcoverEditionId = "hc:123", Monitored = true }
                }
            };

            var other = new Book
            {
                Id = 11,
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { Id = 30, Title = "Other", HardcoverEditionId = "hc:999" }
                }
            };

            var matched = InvokeFindBookAlreadyTargetingHardcoverEdition(service, new[] { target, other }, "hc:123", "123");

            Assert.That(matched, Is.SameAs(target));
        }

        [Test]
        public void should_choose_lowest_id_when_multiple_books_already_target_same_hardcover_edition()
        {
            var service = BuildService();
            var higherId = new Book
            {
                Id = 20,
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { Id = 40, Title = "Higher", HardcoverEditionId = "hc:123", Monitored = true }
                }
            };

            var lowerId = new Book
            {
                Id = 10,
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { Id = 30, Title = "Lower", HardcoverEditionId = "hc:123", Monitored = true }
                }
            };

            var matched = InvokeFindBookAlreadyTargetingHardcoverEdition(service, new[] { higherId, lowerId }, "hc:123", "123");

            Assert.That(matched, Is.SameAs(lowerId));
        }

        [Test]
        public void should_choose_lowest_id_reusable_book_deterministically()
        {
            var higherId = new Book
            {
                Id = 20,
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { Id = 40, Title = "Higher" }
                }
            };

            var lowerId = new Book
            {
                Id = 10,
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition { Id = 30, Title = "Lower" }
                }
            };

            var matched = InvokeFindReusableHardcoverTargetBook(
                new[] { higherId, lowerId },
                new HashSet<int>(),
                new HashSet<int>());

            Assert.That(matched, Is.SameAs(lowerId));
        }

        [Test]
        public void should_not_retarget_gui_or_manual_pins_for_automatic_hardcover_selection()
        {
            var guiPinned = new Book
            {
                Id = 10,
                AnyEditionOk = false,
                Editions = new List<Edition> { new Edition { Id = 30, BookId = 10, Monitored = true } }
            };
            var manuallyPreserved = new Book
            {
                Id = 11,
                AnyEditionOk = true,
                Editions = new List<Edition> { new Edition { Id = 31, BookId = 11, Monitored = true, ManualAdd = true } }
            };
            var automatic = new Book
            {
                Id = 12,
                AnyEditionOk = true,
                Editions = new List<Edition> { new Edition { Id = 32, BookId = 12, Monitored = true } }
            };

            var matched = InvokeFindReusableHardcoverTargetBook(
                new[] { guiPinned, manuallyPreserved, automatic },
                new HashSet<int>(),
                new HashSet<int>());

            Assert.That(matched, Is.SameAs(automatic));
        }
    }
}

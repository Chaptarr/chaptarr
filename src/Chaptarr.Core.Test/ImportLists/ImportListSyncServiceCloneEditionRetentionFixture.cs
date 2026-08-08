using System;
using System.Collections.Generic;
using System.Linq;
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
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class ImportListSyncServiceCloneEditionRetentionFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class BookInsertProxy : DispatchProxy
        {
            public int NextId { get; set; } = 1000;
            public List<Book> InsertedBooks { get; } = new List<Book>();
            public List<Book> RefreshedAliases { get; } = new List<Book>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.InsertMany) &&
                    args?.Length >= 1 &&
                    args[0] is List<Book> books)
                {
                    foreach (var b in books.Where(b => b != null))
                    {
                        if (b.Id <= 0)
                        {
                            b.Id = NextId++;
                        }

                        InsertedBooks.Add(b);
                    }

                    return null;
                }

                if (targetMethod?.Name == nameof(IBookService.RefreshProviderAliases) &&
                    args?.Length == 1 &&
                    args[0] is Book book)
                {
                    RefreshedAliases.Add(book);
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
            }
        }

        private class EditionInsertProxy : DispatchProxy
        {
            public int NextId { get; set; } = 2000;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.InsertMany) &&
                    args?.Length >= 1 &&
                    args[0] is List<Edition> editions)
                {
                    foreach (var e in editions.Where(e => e != null))
                    {
                        if (e.Id <= 0)
                        {
                            e.Id = NextId++;
                        }
                    }

                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IEditionService.{targetMethod?.Name}");
            }
        }

        private static ImportListSyncService BuildService(IBookService bookService, IEditionService editionService, IEditionSelector editionSelector)
        {
            return new ImportListSyncService(
                importListFactory: DispatchProxy.Create<IImportListFactory, ThrowingProxy<IImportListFactory>>(),
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ThrowingProxy<IImportListExclusionService>>(),
                listFetcherAndParser: DispatchProxy.Create<IFetchAndParseImportList, ThrowingProxy<IFetchAndParseImportList>>(),
                bookInfoProxy: DispatchProxy.Create<IProvideBookInfo, ThrowingProxy<IProvideBookInfo>>(),
                authorService: DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                bookService: bookService,
                bookRepository: DispatchProxy.Create<IBookRepository, ThrowingProxy<IBookRepository>>(),
                editionService: editionService,
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                editionSelector: editionSelector,
                authorLibraryService: DispatchProxy.Create<NzbDrone.Core.Books.Services.IAuthorLibraryService, ThrowingProxy<NzbDrone.Core.Books.Services.IAuthorLibraryService>>(),
                pendingAuthorImportService: DispatchProxy.Create<NzbDrone.Core.Books.Services.IPendingAuthorImportService, ThrowingProxy<NzbDrone.Core.Books.Services.IPendingAuthorImportService>>(),
                rootFolderService: DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                rootFolderSettingsResolver: DispatchProxy.Create<IRootFolderSettingsResolver, ThrowingProxy<IRootFolderSettingsResolver>>(),
                eventAggregator: DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                commandQueueManager: DispatchProxy.Create<IManageCommandQueue, ThrowingProxy<IManageCommandQueue>>(),
                logger: LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: DispatchProxy.Create<IImportListBookIdentityCacheRepository, ThrowingProxy<IImportListBookIdentityCacheRepository>>());
        }

        private static Book CloneBookWithEditions(ImportListSyncService service, Book canonicalBook, string requiredEditionProviderId, string requiredEditionRawId)
        {
            var method = typeof(ImportListSyncService).GetMethod(
                "CloneBookWithEditions",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (method == null)
            {
                throw new InvalidOperationException("Could not find ImportListSyncService.CloneBookWithEditions via reflection");
            }

            return (Book)method.Invoke(service, new object[] { canonicalBook, requiredEditionProviderId, requiredEditionRawId });
        }

        private static Edition MakeEdition(string foreignEditionId, int readingFormatId, string language, string hardcoverEditionId = null)
        {
            return new Edition
            {
                ForeignEditionId = foreignEditionId,
                ReadingFormatId = readingFormatId,
                Language = language,
                HardcoverEditionId = hardcoverEditionId,
                Title = foreignEditionId ?? hardcoverEditionId ?? "Untitled Edition"
            };
        }

        private static IBookService CreateBookService(out BookInsertProxy proxy)
        {
            var service = DispatchProxy.Create<IBookService, BookInsertProxy>();
            proxy = (BookInsertProxy)(object)service;
            return service;
        }

        [Test]
        public void should_prune_cloned_book_editions_to_retained_set_for_author_profile()
        {
            var bookService = DispatchProxy.Create<IBookService, BookInsertProxy>();
            var editionService = DispatchProxy.Create<IEditionService, EditionInsertProxy>();
            var editionSelector = new EditionSelector(LogManager.GetCurrentClassLogger());

            var service = BuildService(bookService, editionService, editionSelector);

            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookMetadataProfile = new MetadataProfile { AllowedLanguages = "eng" }
            };

            var canonicalBook = new Book
            {
                Id = 10,
                Author = author,
                AuthorId = author.Id,
                MediaType = BookMediaType.Audiobook,
                Title = "Dune",
                Editions = new List<Edition>
                {
                    MakeEdition("eng-audio-1", 2, "eng"),
                    MakeEdition("eng-audio-2", 2, "eng"),
                    MakeEdition("eng-ebook", 3, "eng"),
                    MakeEdition("eng-print", 1, "eng"),
                    MakeEdition("fra-audio", 2, "fra"),
                    MakeEdition("fra-ebook", 3, "fra")
                }
            };

            var clone = CloneBookWithEditions(service, canonicalBook, requiredEditionProviderId: null, requiredEditionRawId: null);

            Assert.That(clone, Is.Not.Null);
            Assert.That(clone.Editions, Is.Not.Null);

            // Audiobook instance: keep all native audio editions plus one ebook/print safety-net companion for the allowed language.
            Assert.That(clone.Editions.Select(e => e.ForeignEditionId),
                Is.EquivalentTo(new[] { "eng-audio-1", "eng-audio-2", "eng-ebook" }));
        }

        [Test]
        public void should_clone_remote_provider_ids_for_import_list_book()
        {
            var bookService = CreateBookService(out _);
            var editionService = DispatchProxy.Create<IEditionService, EditionInsertProxy>();
            var editionSelector = new EditionSelector(LogManager.GetCurrentClassLogger());

            var service = BuildService(bookService, editionService, editionSelector);

            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookMetadataProfile = new MetadataProfile { AllowedLanguages = "eng" }
            };

            var canonicalBook = new Book
            {
                Id = 10,
                Author = author,
                AuthorId = author.Id,
                MediaType = BookMediaType.Audiobook,
                Title = "Harry Potter and the Goblet of Fire",
                RemoteProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "gr:231260754",
                    "gr:3046572",
                    "hc:383236"
                },
                Editions = new List<Edition>
                {
                    MakeEdition("eng-audio-1", 2, "eng")
                }
            };

            var clone = CloneBookWithEditions(service, canonicalBook, requiredEditionProviderId: null, requiredEditionRawId: null);
            clone.RemoteProviderIds.Add("gr:57992582");

            Assert.Multiple(() =>
            {
                Assert.That(clone.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:231260754", "gr:3046572", "hc:383236", "gr:57992582" }));
                Assert.That(canonicalBook.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:231260754", "gr:3046572", "hc:383236" }));
                Assert.That(clone.RemoteProviderIds, Is.Not.SameAs(canonicalBook.RemoteProviderIds));
            });
        }

        [Test]
        public void automatic_import_list_clone_does_not_inherit_a_user_edition_pin()
        {
            var bookService = CreateBookService(out _);
            var editionService = DispatchProxy.Create<IEditionService, EditionInsertProxy>();
            var service = BuildService(bookService, editionService, new EditionSelector(LogManager.GetCurrentClassLogger()));
            var canonical = new Book
            {
                Id = 10,
                Author = new Author
                {
                    Id = 1,
                    Name = "Test Author",
                    AudiobookMetadataProfile = new MetadataProfile { AllowedLanguages = "eng" }
                },
                AuthorId = 1,
                MediaType = BookMediaType.Audiobook,
                Title = "Dune",
                AnyEditionOk = false,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 20,
                        ForeignEditionId = "eng-audio",
                        Title = "Dune",
                        ReadingFormatId = 2,
                        Language = "eng",
                        Monitored = true,
                        ManualAdd = true
                    }
                }
            };

            var clone = CloneBookWithEditions(service, canonical, null, null);

            Assert.Multiple(() =>
            {
                Assert.That(clone.AnyEditionOk, Is.True);
                Assert.That(clone.Editions, Has.All.Matches<Edition>(edition => !edition.ManualAdd));
            });
        }

        [Test]
        public void should_refresh_provider_aliases_after_cloned_editions_are_attached()
        {
            var bookService = CreateBookService(out var bookProxy);
            var editionService = DispatchProxy.Create<IEditionService, EditionInsertProxy>();
            var editionSelector = new EditionSelector(LogManager.GetCurrentClassLogger());

            var service = BuildService(bookService, editionService, editionSelector);

            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookMetadataProfile = new MetadataProfile { AllowedLanguages = "eng" }
            };

            var canonicalBook = new Book
            {
                Id = 10,
                Author = author,
                AuthorId = author.Id,
                MediaType = BookMediaType.Audiobook,
                Title = "Harry Potter and the Goblet of Fire",
                RemoteProviderIds = new HashSet<string> { "gr:3046572", "hc:383236" },
                Editions = new List<Edition>
                {
                    MakeEdition("eng-audio-1", 2, "eng", hardcoverEditionId: "hc:edition:123")
                }
            };

            var clone = CloneBookWithEditions(service, canonicalBook, requiredEditionProviderId: null, requiredEditionRawId: null);

            Assert.Multiple(() =>
            {
                Assert.That(bookProxy.RefreshedAliases, Has.Count.EqualTo(1));
                Assert.That(bookProxy.RefreshedAliases[0], Is.SameAs(clone));
                Assert.That(bookProxy.RefreshedAliases[0].Editions, Has.Count.EqualTo(1));
                Assert.That(bookProxy.RefreshedAliases[0].Editions[0].HardcoverEditionId, Is.EqualTo("hc:edition:123"));
            });
        }

        [Test]
        public void should_preserve_required_hardcover_edition_even_when_out_of_profile_language()
        {
            var bookService = DispatchProxy.Create<IBookService, BookInsertProxy>();
            var editionService = DispatchProxy.Create<IEditionService, EditionInsertProxy>();
            var editionSelector = new EditionSelector(LogManager.GetCurrentClassLogger());

            var service = BuildService(bookService, editionService, editionSelector);

            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookMetadataProfile = new MetadataProfile { AllowedLanguages = "eng" }
            };

            var requiredHcProviderId = "hc:999";
            var requiredRawId = "999";

            var canonicalBook = new Book
            {
                Id = 10,
                Author = author,
                AuthorId = author.Id,
                MediaType = BookMediaType.Audiobook,
                Title = "Dune",
                Editions = new List<Edition>
                {
                    MakeEdition("eng-audio-1", 2, "eng"),
                    MakeEdition("eng-ebook", 3, "eng"),
                    MakeEdition("fra-required-audio", 2, "fra", hardcoverEditionId: requiredHcProviderId),
                    MakeEdition("fra-other-audio", 2, "fra", hardcoverEditionId: "hc:998"),
                    MakeEdition("fra-other-ebook", 3, "fra")
                }
            };

            var clone = CloneBookWithEditions(service, canonicalBook, requiredHcProviderId, requiredRawId);

            Assert.That(clone, Is.Not.Null);
            Assert.That(clone.Editions, Is.Not.Null);

            // Required Hardcover edition survives even though fra is out-of-profile.
            Assert.That(clone.Editions.Any(e => e.ForeignEditionId == "fra-required-audio"), Is.True);
            Assert.That(clone.Editions.Any(e => e.ForeignEditionId == "fra-other-audio"), Is.False);

            // Still keeps retained native English set plus the audiobook safety-net companion.
            Assert.That(clone.Editions.Any(e => e.ForeignEditionId == "eng-audio-1"), Is.True);
            Assert.That(clone.Editions.Any(e => e.ForeignEditionId == "eng-ebook"), Is.True);
        }

        [Test]
        public void should_preserve_required_hardcover_edition_when_foreign_edition_id_is_missing()
        {
            var bookService = DispatchProxy.Create<IBookService, BookInsertProxy>();
            var editionService = DispatchProxy.Create<IEditionService, EditionInsertProxy>();
            var editionSelector = new EditionSelector(LogManager.GetCurrentClassLogger());

            var service = BuildService(bookService, editionService, editionSelector);

            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                AudiobookMetadataProfile = new MetadataProfile { AllowedLanguages = "eng" }
            };

            var requiredHcProviderId = "hc:999";
            var requiredRawId = "999";

            var canonicalBook = new Book
            {
                Id = 10,
                Author = author,
                AuthorId = author.Id,
                MediaType = BookMediaType.Audiobook,
                Title = "Dune",
                Editions = new List<Edition>
                {
                    MakeEdition("eng-audio-1", 2, "eng"),
                    MakeEdition("eng-ebook", 3, "eng"),
                    MakeEdition(null, 2, "fra", hardcoverEditionId: requiredHcProviderId),
                    MakeEdition("fra-other-audio", 2, "fra", hardcoverEditionId: "hc:998"),
                    MakeEdition("fra-other-ebook", 3, "fra")
                }
            };

            var clone = CloneBookWithEditions(service, canonicalBook, requiredHcProviderId, requiredRawId);

            Assert.That(clone, Is.Not.Null);
            Assert.That(clone.Editions, Is.Not.Null);
            Assert.That(clone.Editions.Count(e => e.HardcoverEditionId == requiredHcProviderId), Is.EqualTo(1));
            Assert.That(clone.Editions.Any(e => e.ForeignEditionId == "fra-other-ebook"), Is.False);
        }

        [Test]
        public void should_skip_clone_when_no_editions_survive_media_type_retention()
        {
            var bookService = CreateBookService(out var bookProxy);
            var editionService = DispatchProxy.Create<IEditionService, EditionInsertProxy>();
            var editionSelector = new EditionSelector(LogManager.GetCurrentClassLogger());

            var service = BuildService(bookService, editionService, editionSelector);

            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                EbookMetadataProfile = new MetadataProfile { AllowedLanguages = "eng" }
            };

            var canonicalBook = new Book
            {
                Id = 10,
                Author = author,
                AuthorId = author.Id,
                MediaType = BookMediaType.Ebook,
                Title = "Audio Only",
                Editions = new List<Edition>
                {
                    MakeEdition("eng-audio-only", 2, "eng")
                }
            };

            var clone = CloneBookWithEditions(service, canonicalBook, requiredEditionProviderId: null, requiredEditionRawId: null);

            Assert.Multiple(() =>
            {
                Assert.That(clone, Is.Null);
                Assert.That(bookProxy.InsertedBooks, Is.Empty);
            });
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ImportLists.Exclusions;

namespace Chaptarr.Core.Test.Books
{
    public partial class BookServiceWantedNarratorSeriesLinksFixture
    {
        private sealed class CountingSeriesLinks : LazyLoaded<List<SeriesBookLink>>
        {
            public int Reads { get; private set; }
            public CountingSeriesLinks(bool loaded)
            {
                IsLoaded = loaded;
                _value = new List<SeriesBookLink> { new() { SeriesId = 1 } };
            }
            public override void LazyLoad() { Reads++; IsLoaded = true; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void instance_metadata_copy_must_skip_unused_series_links_and_preserve_default_copy_behavior(bool loaded)
        {
            var links = new CountingSeriesLinks(loaded);
            var source = new Book { Title = "Requested Work", LazySeriesLinks = links };
            var instance = RefreshEntityCopy.CloneBook(source, includeEditions: false, includeSeriesLinks: false);
            Assert.That(links.Reads, Is.Zero);
            Assert.That(instance.SeriesLinks, Is.Null);
            Assert.That(links.IsLoaded, Is.EqualTo(loaded));
            var normal = RefreshEntityCopy.CloneBook(source, includeEditions: false);
            Assert.That(normal.SeriesLinks.Single().SeriesId, Is.EqualTo(1));
            Assert.That(links.Reads, Is.EqualTo(1));
        }

        private static void WithWantedWriter(bool postgres, Action<BookService, BookRepository, EditionRepository, AuthorRepository, StubMediaFileService> test, Func<IEditionService, IEditionService> wrap = null, Func<IProviderAliasService, IProviderAliasService> wrapAliases = null, Func<IAuthorService, IAuthorService> wrapAuthors = null, Func<IBookRepository, IBookRepository> wrapBooks = null, bool persistedFiles = false)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var authors = new AuthorRepository(database, events, logger);
                var author = authors.Insert(new Author
                {
                    Name = "Requested Author", CleanName = "requestedauthor", TitleSlug = "requested-author",
                    Path = @"C:\Audio\Requested Author".AsOsAgnostic(),
                    AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic(), Added = DateTime.UtcNow
                });
                var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAuthor) => authors.Get((int)args[0]),
                    nameof(IAuthorService.GetExistingAuthors) => authors.Get((IEnumerable<int>)args[0]).ToList(),
                    nameof(IAuthorService.GetAuthors) => authors.Get((IEnumerable<int>)args[0]).ToList(),
                    _ => throw new NotImplementedException(method.Name)
                });
                var roots = InterfaceProxy.Proxy<IRootFolderService>((method, _) => method.Name == nameof(IRootFolderService.All)
                    ? new List<RootFolder> { new() { Path = @"C:\Audio".AsOsAgnostic(), FolderType = FolderType.Audiobook } }
                    : throw new NotImplementedException(method.Name));
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var files = new StubMediaFileService();
                if (persistedFiles) files.Repository = new NzbDrone.Core.MediaFiles.MediaFileRepository(database, events);
                IEditionService editionService = new EditionService(editions, events, logger);
                var service = new BookService(wrapBooks?.Invoke(books) ?? books, wrap?.Invoke(editionService) ?? editionService, events, wrapAuthors?.Invoke(authorService) ?? authorService,
                    files, roots, new SeriesBookLinkRepository(database, events), new StubMultiCopySeriesService(), logger,
                    providerAliasService: wrapAliases?.Invoke(new ProviderAliasService(new ProviderAliasRepository(database, events))));
                test(service, books, editions, authors, files);
            });
        }

        [TestCase(false, 1)]
        [TestCase(false, 50)]
        [TestCase(true, 1)]
        [TestCase(true, 50)]
        public void requested_monitoring_must_load_authors_once_for_the_whole_batch(bool postgres, int count)
        {
            var singles = 0;
            var batches = 0;
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var authorId = authors.All().Single().Id;
                var targets = Enumerable.Range(1000, count).Select(work => SeedWantedBook(books, editions, authorId,
                    work.ToString(), "Jim Dale", false)).ToList();
                targets.ForEach(book => book.AudiobookMonitored = false);
                books.SetFields(targets, book => book.AudiobookMonitored);
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                service.MonitorRequestedBooks(targets.Select(book => book.Id), "audiobook");
                Assert.That(books.Get(targets.Select(book => book.Id)).All(book => book.AudiobookMonitored), Is.True);
                Assert.That(singles, Is.Zero);
                Assert.That(batches, Is.EqualTo(1));
                TestContext.WriteLine($"Requested monitoring: {count} books; {elapsed.ElapsedMilliseconds}ms; author batches={batches}, singles={singles}");
            }, wrapAuthors: inner => InterfaceProxy.Proxy<IAuthorService>((method, args) =>
            {
                if (method.Name == nameof(IAuthorService.GetAuthor)) singles++;
                if (method.Name == nameof(IAuthorService.GetAuthors)) batches++;
                return method.Invoke(inner, args);
            }));
        }

        private static Book SeedWantedBook(BookRepository books, EditionRepository editions, int authorId, string work, string selected, bool pinned = true)
        {
            var book = books.Insert(new Book
            {
                AuthorId = authorId, Title = "Requested Work", CleanTitle = "requestedwork", TitleSlug = "work-" + work,
                HardcoverBookId = "hc:" + work, MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true, AnyEditionOk = !pinned, Added = DateTime.UtcNow
            });
            book.Editions = new[] { "Jim Dale", "Stephen Fry" }.Select((name, index) => editions.Insert(new Edition
            {
                BookId = book.Id, Title = book.Title, HardcoverEditionId = "hc:edition:" + work + index,
                ForeignEditionId = "hc:edition:" + work + index, ReadingFormatId = 2, Narrator = name,
                Monitored = name == selected, ManualAdd = pinned && name == selected
            })).ToList();
            return book;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void wanted_copy_must_persist_secondary_edition_aliases_for_provider_lookup(bool postgres)
        {
            IProviderAliasService aliases = null;
            WithWantedWriter(postgres, (writer, books, editions, authors, _) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale");
                var requested = source.Editions[1];
                requested.RemoteProviderIds = new HashSet<string> { "gr:7701", "gb:recording-7701" };
                editions.Update(requested);
                var wanted = writer.AddWantedEditionCopy(source.Id, requested.Id).Book;
                var persisted = editions.FindByBook(new[] { wanted.Id }).Single(edition => edition.Monitored);
                Assert.Multiple(() =>
                {
                    Assert.That(wanted.Id, Is.Not.EqualTo(source.Id));
                    Assert.That(persisted.Id, Is.Not.EqualTo(requested.Id));
                    Assert.That(persisted.ManualAdd, Is.True);
                    Assert.That(aliases.GetAliases("Edition", persisted.Id, "edition")
                        .Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}"),
                        Is.SupersetOf(new[] { "gr:7701", "gb:recording-7701" }));
                    Assert.That(editions.FindAllByProviderIds("gr", new[] { "7701" })["7701"].Select(edition => edition.Id),
                        Is.EquivalentTo(new[] { requested.Id, persisted.Id }));
                    Assert.That(editions.FindAllByProviderIds("gb", new[] { "recording-7701" })["recording-7701"].Select(edition => edition.Id),
                        Is.EquivalentTo(new[] { requested.Id, persisted.Id }));
                });
                Assert.That(writer.AddWantedEditionCopy(source.Id, requested.Id).Book.Id, Is.EqualTo(wanted.Id));
                Assert.That(books.GetBooksByAuthorId(source.AuthorId), Has.Count.EqualTo(2));
                TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: inserted wanted local book {wanted.Id}, edition {persisted.Id}; both secondary provider lookups return source and copy after reload.");
            }, wrapAliases: inner => aliases = inner);
        }

        [Test]
        public void wanted_ties_must_choose_the_lowest_local_book_independent_of_input_order(
            [Values(false, true)] bool postgres, [Values(false, true)] bool reusable, [Values(false, true)] bool reverse)
        {
            WithWantedWriter(postgres, (writer, books, editions, authors, _) =>
            {
                var authorId = authors.All().Single().Id;
                var first = SeedWantedBook(books, editions, authorId, "1001", reusable ? "Jim Dale" : "Stephen Fry", !reusable);
                var second = SeedWantedBook(books, editions, authorId, "1002", reusable ? "Jim Dale" : "Stephen Fry", !reusable);
                var source = SeedWantedBook(books, editions, authorId, "1003", "Jim Dale");
                var requested = source.Editions[1];
                foreach (var book in new[] { first, second })
                {
                    book.HardcoverBookId = source.HardcoverBookId;
                    book.Editions[1].ForeignEditionId = requested.ForeignEditionId;
                    book.Editions[1].HardcoverEditionId = requested.HardcoverEditionId;
                }
                books.UpdateMany(new[] { first, second });
                editions.UpdateMany(first.Editions.Concat(second.Editions).ToList());
                var requests = new List<Edition> { requested, source.Editions[0] };
                if (reverse) requests.Reverse();
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    var result = writer.AddWantedEditions(requests);
                    Assert.That(result[requested.Id].Id, Is.EqualTo(first.Id));
                    Assert.That(result[source.Editions[0].Id].Id, Is.EqualTo(source.Id));
                }
                Assert.Multiple(() =>
                {
                    Assert.That(books.GetBooksByAuthorId(authorId), Has.Count.EqualTo(3));
                    Assert.That(editions.FindByBook(new[] { first.Id }).Single(edition => edition.Monitored && edition.ManualAdd).Id,
                        Is.EqualTo(first.Editions[1].Id));
                    Assert.That(editions.FindByBook(new[] { second.Id }).Single(edition => edition.Monitored).Id,
                        Is.EqualTo(second.Editions[reusable ? 0 : 1].Id));
                    Assert.That(editions.FindByBook(new[] { source.Id }).Single(edition => edition.Monitored && edition.ManualAdd).Id,
                        Is.EqualTo(source.Editions[0].Id));
                });
                TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: {(reusable ? "reusable" : "selected")} tied copies, reversed input {reverse}; winner local book {first.Id}; 2 repeated batches, 3 stored rows.");
            }, wrapBooks: inner => InterfaceProxy.Proxy<IBookRepository>((method, args) =>
            {
                var result = method.Invoke(inner, args);
                return reverse && method.Name == nameof(IBookRepository.GetBooksByAuthorId)
                    ? ((List<Book>)result).AsEnumerable().Reverse().ToList() : result;
            }));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void explicit_readd_must_reuse_a_requested_narration_stored_on_another_compatible_copy(bool postgres, bool secondaryWorkAliases)
        {
            WithWantedWriter(postgres, (writer, books, editions, authors, _) =>
            {
                var author = authors.All().Single();
                author.HardcoverAuthorId = "hc:777";
                authors.SetFields(author, candidate => candidate.HardcoverAuthorId);
                var source = SeedWantedBook(books, editions, author.Id, "1001", "Jim Dale");
                editions.DeleteMany(source.Editions.Where(edition => !edition.Monitored).ToList());
                var wanted = SeedWantedBook(books, editions, author.Id, "1002", "Stephen Fry");
                if (secondaryWorkAliases)
                {
                    source.RemoteProviderIds = new HashSet<string> { "gr:7001" };
                    wanted.RemoteProviderIds = new HashSet<string> { "gr:7001" };
                    books.SetFields(source, row => row.ProviderIds);
                    books.SetFields(wanted, row => row.ProviderIds);
                }
                else
                {
                    wanted.HardcoverBookId = source.HardcoverBookId;
                    books.SetFields(wanted, book => book.HardcoverBookId);
                }
                var narration = wanted.Editions.Single(edition => edition.Monitored);
                var wrongWork = SeedWantedBook(books, editions, author.Id, "1003", "Stephen Fry");
                var ebook = SeedWantedBook(books, editions, author.Id, "1004", "Stephen Fry");
                ebook.MediaType = BookMediaType.Ebook;
                ebook.HardcoverBookId = source.HardcoverBookId;
                books.SetFields(ebook, book => book.MediaType, book => book.HardcoverBookId);
                var otherAuthor = authors.Insert(new Author
                {
                    Name = "Other Author", CleanName = "otherauthor", TitleSlug = "other-author",
                    Path = @"C:\Audio\Other Author".AsOsAgnostic(), Added = DateTime.UtcNow
                });
                var wrongAuthor = SeedWantedBook(books, editions, otherAuthor.Id, "1005", "Stephen Fry");
                wrongAuthor.HardcoverBookId = source.HardcoverBookId;
                books.SetFields(wrongAuthor, book => book.HardcoverBookId);
                var unrelated = new[] { wrongWork, ebook, wrongAuthor };
                foreach (var book in unrelated)
                {
                    var edition = book.Editions.Single(candidate => candidate.Monitored);
                    edition.ForeignEditionId = narration.ForeignEditionId;
                    edition.HardcoverEditionId = narration.HardcoverEditionId;
                    editions.UpdateMany(new List<Edition> { edition });
                }
                var exclusions = InterfaceProxy.Proxy<IImportListExclusionService>((method, _) => method.Name == nameof(IImportListExclusionService.FindByForeignId)
                    ? new List<ImportListExclusion>() : throw new NotImplementedException(method.Name));
                var authorLibrary = InterfaceProxy.Proxy<IAuthorLibraryService>((_, _) => throw new InvalidOperationException("Existing local narration must not trigger author import."));
                var service = new AddBookService(new StubAuthorService(author), authorLibrary, writer, null, null, exclusions,
                    null, null, null, null, null, new EditionService(editions, new StubEventAggregator(), LogManager.GetCurrentClassLogger()),
                    null, null, null, LogManager.GetCurrentClassLogger(), null);
                var requestEdition = RefreshEntityCopy.CloneEdition(narration);
                requestEdition.Monitored = requestEdition.ManualAdd = true;
                var request = new Book
                {
                    HardcoverBookId = source.HardcoverBookId, MediaType = BookMediaType.Audiobook, Author = author,
                    Editions = new List<Edition> { requestEdition }, AddOptions = new AddBookOptions()
                };
                var result = service.AddBook(request).GetAwaiter().GetResult();
                Assert.That(result.Id, Is.EqualTo(wanted.Id));
                Assert.That(books.GetBooksByAuthorId(author.Id), Has.Count.EqualTo(4));
                Assert.That(editions.FindByBook(new[] { source.Id }).Single().Narrator, Is.EqualTo("Jim Dale"));
                Assert.That(editions.FindByBook(new[] { source.Id }).Single().Monitored, Is.True);
                Assert.That(editions.FindByBook(new[] { wanted.Id }).Single(edition => edition.Monitored).ManualAdd, Is.True);
                Assert.That(unrelated.All(book => editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).ManualAdd), Is.True);
            }, wrapAliases: inner => inner);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void book_alias_refresh_must_preserve_absent_secondary_work_ids_in_one_batch(bool postgres)
        {
            var reads = 0;
            IProviderAliasService aliasService = null;
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var targets = Enumerable.Range(1000, 25).Select(work => SeedWantedBook(books, editions, authors.All().Single().Id,
                    work.ToString(), "Jim Dale", false)).ToList();
                foreach (var book in targets)
                {
                    book.RemoteProviderIds = new HashSet<string> { "gr:" + (9000 + book.Id) };
                    books.SetFields(book, row => row.ProviderIds);
                    book.RemoteProviderIds = null;
                }
                reads = 0;
                service.UpdateMany(targets);
                Assert.That(reads, Is.Zero, "the persisted snapshot eliminates alias hydration queries");
                foreach (var book in targets)
                {
                    Assert.That(aliasService.FindBookIds("work", new[] { "gr:" + (9000 + book.Id) }), Does.Contain(book.Id));
                }
                targets.ForEach(book => book.RemoteProviderIds = new HashSet<string>());
                service.UpdateMany(targets);
                Assert.That(aliasService.FindBookIds("work", targets.Select(book => "gr:" + (9000 + book.Id))), Is.Empty);
            }, wrapAliases: inner =>
            {
                aliasService = inner;
                return InterfaceProxy.Proxy<IProviderAliasService>((method, args) =>
                {
                    if (method.Name == nameof(IProviderAliasService.GetAliases) && args[1] is IEnumerable<int> ids && ids.Any()) reads++;
                    return method.Invoke(inner, args);
                });
            });
        }

        [Test]
        public void refreshed_work_aliases_must_withdraw_explicit_empty_and_preserve_unknown_sets(
            [Values(false, true)] bool postgres, [Values(false, true)] bool changed,
            [Values("empty", "unknown", "poison", "mixed", "blank")] string providerSet)
        {
            IProviderAliasService aliases = null;
            WithWantedWriter(postgres, (writer, books, editions, authors, _) =>
            {
                var author = authors.All().Single();
                var source = SeedWantedBook(books, editions, author.Id, "1001", "Jim Dale", false);
                source.RemoteProviderIds = new HashSet<string> { "gr:7001" };
                books.SetFields(source, row => row.ProviderIds);
                var local = books.Get(source.Id);
                local.Author = author;
                var remote = RefreshEntityCopy.CloneBook(local, includeEditions: false, includeSeriesLinks: false);
                remote.Author = null;
                const string poison = "System.Collections.Generic.List`1[System.String]";
                remote.RemoteProviderIds = providerSet switch
                {
                    "unknown" => null,
                    "poison" => new HashSet<string> { poison },
                    "blank" => new HashSet<string> { " ", "\t" },
                    "mixed" => new HashSet<string> { " gr:8001 ", poison },
                    _ => new HashSet<string>()
                };
                if (changed) remote.Title = "Changed metadata title";
                var refresh = new RefreshBookService(writer, null, null, null, null, null, null, null, null, null, null,
                    null, null, null, LogManager.GetCurrentClassLogger(), mainDatabase: null);
                var result = typeof(RefreshBookService).GetMethod("UpdateEntity", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(refresh, new object[] { local, remote });
                Assert.That(result.ToString(), Is.EqualTo(changed ? "UpdateTags" : "None"));
                var copiedIds = local.RemoteProviderIds?.ToList();
                var acceptedFreshSnapshot = local.ProviderIdsChanged;
                writer.UpdateMany(new List<Book> { local });
                Assert.That(aliases.FindBookIds("work", new[] { "gr:7001" }).Contains(source.Id), Is.EqualTo(providerSet is "unknown" or "poison" or "blank"));
                Assert.That(aliases.FindBookIds("work", new[] { "gr:8001" }).Contains(source.Id), Is.EqualTo(providerSet == "mixed"));
                Assert.That(aliases.FindBookIds("work", new[] { "hc:1001" }), Does.Contain(source.Id));
                Assert.That(books.Get(source.Id).Title, Is.EqualTo(remote.Title));
                if (providerSet is "unknown" or "poison" or "blank")
                {
                    Assert.That(acceptedFreshSnapshot, Is.False, "unavailable metadata must not become an empty snapshot");
                    Assert.That(copiedIds, Is.EquivalentTo(new[] { "gr:7001", "hc:1001" }), "the last stored snapshot remains readable");
                }
                else if (providerSet == "empty") Assert.That(copiedIds, Is.Not.Null.And.Empty);
                else Assert.That(copiedIds, Is.EquivalentTo(new[] { "gr:8001" }));
            }, wrapAliases: inner => aliases = inner);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void wanted_batch_must_load_raw_editions_once_and_save_only_selection_fields(bool postgres)
        {
            var rawReads = 0;
            var joinedReads = 0;
            var selectionWrites = 0;
            var fullWrites = 0;
            WithWantedWriter(postgres, (service, books, editions, authors, files) =>
            {
                var authorId = authors.All().Single().Id;
                var targets = Enumerable.Range(1000, 25).Select(work => SeedWantedBook(books, editions, authorId, work.ToString(), "Jim Dale", false)).ToList();
                files.BeforeBatchRead = () =>
                {
                    var refreshed = editions.FindByBook(targets.Select(book => book.Id));
                    refreshed.ForEach(edition => edition.Title = "Refreshed metadata");
                    editions.UpdateMany(refreshed);
                };
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                var selected = service.AddWantedEditions(targets.Select(book => book.Editions[1]).ToList());
                Assert.That(selected, Has.Count.EqualTo(25));
                Assert.That(editions.FindByBook(targets.Select(book => book.Id)).Select(edition => edition.Title), Has.All.EqualTo("Refreshed metadata"));
                Assert.That(rawReads, Is.EqualTo(1));
                Assert.That(joinedReads, Is.Zero);
                Assert.That(selectionWrites, Is.EqualTo(1));
                Assert.That(fullWrites, Is.Zero);
                TestContext.WriteLine($"Wanted batch: {25} requests / {25} books / {50} editions; {elapsed.ElapsedMilliseconds}ms; raw reads={rawReads}, selection writes={selectionWrites}, full metadata writes={fullWrites}");
            }, inner => InterfaceProxy.Proxy<IEditionService>((method, args) =>
            {
                if (method.Name == "GetEditionRowsByBook") rawReads++;
                if (method.Name == nameof(IEditionService.GetEditionsByBook)) joinedReads++;
                if (method.Name == "UpdateSelection") selectionWrites++;
                if (method.Name == nameof(IEditionService.UpdateMany)) fullWrites++;
                return method.Invoke(inner, args);
            }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void microphone_copies_the_requested_edition_without_crossing_captured_work_pockets(bool postgres)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var author = authors.All().Single();
                author.Name = "George R.R. Martin";
                authors.Update(author);
                var other = SeedWantedBook(books, editions, author.Id, "1001", "Jim Dale", false);
                var source = SeedWantedBook(books, editions, author.Id, "1002", "Jim Dale", false);
                other.Title = "Le Trône de Fer l'intégrale 2";
                other.HardcoverBookId = null;
                other.GoodreadsWorkId = "gr:178649170";
                source.Title = "A Clash of Kings";
                source.HardcoverBookId = "hc:427374";
                source.GoodreadsWorkId = "gr:224297699";
                books.UpdateMany(new[] { other, source });
                var sharedAsins = new List<string> { "B0001MC01Y", "B002UZKIBO", "B004I0O0CW", "B0093CVJOC", "B01B5X59KG",
                    "B071S45CFY", "B079TMW6NF", "B07F7Y2H2P", "B0CBL9YX8C" };
                foreach (var selected in new[] { other.Editions[0], source.Editions[0] })
                {
                    selected.ForeignEditionId = "gr:13500182-audiobook";
                    selected.HardcoverEditionId = null;
                    selected.Narrator = "Roy Dotrice";
                    selected.Asin = "B0001MC01Y";
                    selected.AudibleASIN = "B002UZKIBO";
                    selected.Asins = sharedAsins.ToList();
                }
                var requested = source.Editions[1];
                requested.ForeignEditionId = "hc:edition:32053017-audiobook";
                requested.HardcoverEditionId = "32053017";
                requested.Narrator = "Roy Dotrice";
                requested.Asin = "1415902003";
                requested.AudibleASIN = "B002UZKIBO";
                requested.Asins = sharedAsins.Concat(new[] { "1415902003", "B079TKXXH4", "B079TLH1GN" }).ToList();
                editions.UpdateMany(other.Editions.Concat(source.Editions).ToList());
                var result = service.AddWantedEditionCopy(source.Id, requested.Id).Book;
                Assert.That(result.Id, Is.Not.EqualTo(source.Id).And.Not.EqualTo(other.Id));
                Assert.That(result.GoodreadsWorkId, Is.EqualTo("gr:224297699"));
                Assert.That(result.Editions.Single(edition => edition.Monitored).HardcoverEditionId, Is.EqualTo(requested.HardcoverEditionId));
                Assert.That(books.GetBooksByAuthorId(author.Id), Has.Count.EqualTo(3));
                Assert.That(books.Get(source.Id).AnyEditionOk, Is.True);
                Assert.That(editions.FindByBook(new[] { source.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(source.Editions[0].Id));
                Assert.That(books.Get(other.Id).AnyEditionOk, Is.True);
                Assert.That(editions.FindByBook(new[] { other.Id }).Single(edition => edition.Monitored).ManualAdd, Is.False);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void captured_distinct_work_pockets_with_shared_asins_must_keep_the_requested_work(bool postgres)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var author = authors.All().Single();
                author.Name = "George R.R. Martin";
                authors.Update(author);
                var other = SeedWantedBook(books, editions, author.Id, "1001", "Jim Dale", false);
                var source = SeedWantedBook(books, editions, author.Id, "1002", "Jim Dale", false);
                other.Title = "Le Trône de Fer l'intégrale 2";
                other.HardcoverBookId = null;
                other.GoodreadsWorkId = "gr:178649170";
                source.Title = "A Clash of Kings";
                source.HardcoverBookId = "hc:427374";
                source.GoodreadsWorkId = "gr:224297699";
                books.UpdateMany(new[] { other, source });
                var sharedAsins = new List<string> { "B0001MC01Y", "B002UZKIBO", "B004I0O0CW", "B0093CVJOC", "B01B5X59KG",
                    "B071S45CFY", "B079TMW6NF", "B07F7Y2H2P", "B0CBL9YX8C" };
                foreach (var selected in new[] { other.Editions[0], source.Editions[0] })
                {
                    selected.ForeignEditionId = "gr:13500182-audiobook";
                    selected.HardcoverEditionId = null;
                    selected.Narrator = "Roy Dotrice";
                    selected.Asin = "B0001MC01Y";
                    selected.AudibleASIN = "B002UZKIBO";
                    selected.Asins = sharedAsins.ToList();
                }
                var requested = source.Editions[1];
                requested.ForeignEditionId = "hc:edition:32053017-audiobook";
                requested.HardcoverEditionId = "32053017";
                requested.Narrator = "Roy Dotrice";
                requested.Asin = "1415902003";
                requested.AudibleASIN = "B002UZKIBO";
                requested.Asins = sharedAsins.Concat(new[] { "1415902003", "B079TKXXH4", "B079TLH1GN" }).ToList();
                editions.UpdateMany(other.Editions.Concat(source.Editions).ToList());
                var result = service.AddWantedEditions(new List<Edition> { requested })[requested.Id];
                Assert.That(result.Id, Is.EqualTo(source.Id));
                Assert.That(result.GoodreadsWorkId, Is.EqualTo("gr:224297699"));
                Assert.That(books.Get(other.Id).AnyEditionOk, Is.True);
                Assert.That(editions.FindByBook(new[] { other.Id }).Single(edition => edition.Monitored).ManualAdd, Is.False);
            });
        }

        [TestCase(false, true)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(true, false)]
        public void shared_asin_must_not_join_conflicting_work_pockets(bool postgres, bool stableEditionIds)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var authorId = authors.All().Single().Id;
                var other = SeedWantedBook(books, editions, authorId, "1001", "Stephen Fry");
                var source = SeedWantedBook(books, editions, authorId, "1002", "Jim Dale");
                var requested = source.Editions[1];
                var selected = other.Editions[1];
                requested.Asin = selected.Asin = "B002UZKIBO";
                if (!stableEditionIds)
                {
                    requested.HardcoverEditionId = selected.HardcoverEditionId = null;
                    requested.ForeignEditionId = selected.ForeignEditionId = "az:B002UZKIBO-audiobook";
                }
                editions.UpdateMany(new[] { requested, selected });
                var result = service.AddWantedEditionCopy(source.Id, requested.Id).Book;
                Assert.That(result.Id, Is.Not.EqualTo(other.Id));
                Assert.That(result.HardcoverBookId, Is.EqualTo(source.HardcoverBookId));
                Assert.That(editions.FindByBook(new[] { other.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(selected.Id));
                Assert.That(books.GetBooksByAuthorId(authorId), Has.Count.EqualTo(3));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void deleted_wanted_owner_must_not_abort_surviving_request(bool postgres)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale", false);
                var deleted = new Edition { Id = 99999, BookId = 99999, ForeignEditionId = "hc:edition:99999" };
                var failures = new List<int>();
                var result = service.AddWantedEditions(new List<Edition> { deleted, source.Editions[1] }, (edition, _) => failures.Add(edition.Id));
                Assert.That(failures, Is.EqualTo(new[] { deleted.Id }));
                Assert.That(result.Keys, Is.EqualTo(new[] { source.Editions[1].Id }));
                Assert.That(editions.FindByBook(new[] { source.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(source.Editions[1].Id));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_root_must_fail_only_its_request_without_persisting_a_pin(bool postgres)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var author = authors.All().Single();
                author.AudiobookRootFolderPath = null;
                authors.Update(author);
                var source = SeedWantedBook(books, editions, author.Id, "1001", "Stephen Fry", false);
                source.AudiobookMonitored = false;
                books.Update(source);
                var validAuthor = authors.Insert(new Author { Name = "Other Author", CleanName = "otherauthor", TitleSlug = "other-author",
                    Path = @"C:\Audio\Other Author".AsOsAgnostic(), AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic(), Added = DateTime.UtcNow });
                var valid = SeedWantedBook(books, editions, validAuthor.Id, "1002", "Stephen Fry", false);
                var failures = new List<int>();
                var result = service.AddWantedEditions(new List<Edition> { source.Editions[1], valid.Editions[1] }, (edition, _) => failures.Add(edition.Id));
                Assert.That(failures, Is.EqualTo(new[] { source.Editions[1].Id }));
                Assert.That(result.Keys, Is.EqualTo(new[] { valid.Editions[1].Id }));
                Assert.That(books.Get(source.Id).AnyEditionOk, Is.True);
                Assert.That(editions.FindByBook(new[] { source.Id }).Any(edition => edition.ManualAdd), Is.False);
                Assert.That(editions.FindByBook(new[] { valid.Id }).Single(edition => edition.Monitored).ManualAdd, Is.True);
            });
        }

        [Test]
        public void stale_unselected_manual_marker_must_not_switch_a_filed_selection([Values(false, true)] bool postgres, [Values(false, true)] bool copy)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, files) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale", false);
                source.Editions[1].ManualAdd = true;
                editions.Update(source.Editions[1]);
                files.FilesByBookId[source.Id] = new List<NzbDrone.Core.MediaFiles.BookFile> { new() { Edition = source.Editions[0] } };
                var result = copy ? service.AddWantedEditionCopy(source.Id, source.Editions[1].Id).Book
                    : service.AddWantedEditions(new List<Edition> { source.Editions[1] })[source.Editions[1].Id];
                Assert.That(result.Id, Is.Not.EqualTo(source.Id));
                Assert.That(editions.FindByBook(new[] { source.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(source.Editions[0].Id));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void stale_request_row_id_must_resolve_the_current_edition_before_cloning(bool postgres)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale");
                var requested = RefreshEntityCopy.CloneEdition(source.Editions[1]);
                requested.Id = 99999;
                requested.BookId = source.Id;
                var result = service.AddWantedEditions(new List<Edition> { requested });
                Assert.That(result.Keys, Is.EqualTo(new[] { requested.Id }));
                var saved = editions.FindByBook(new[] { result[requested.Id].Id }).Single(edition => edition.Monitored);
                Assert.That(saved.ForeignEditionId, Is.EqualTo(source.Editions[1].ForeignEditionId));
                Assert.That(saved.ManualAdd, Is.True);
            });
        }
    }
}

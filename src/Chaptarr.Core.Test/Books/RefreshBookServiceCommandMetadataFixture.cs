using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshBookServiceCommandMetadataFixture
    {
        private sealed class CaptureRefresh : RefreshBookService
        {
            public List<string> SeenTitles { get; } = new();

            public CaptureRefresh(IBookService books, IProvideAuthorInfo authors, IProvideBookInfo metadata)
                : base(books, null, null, null, authors, metadata, null,
                    InterfaceProxy.Proxy<IMediaFileService>((_, _) => new List<BookFile>()),
                    null, null, null, new EditionSelector(LogManager.GetCurrentClassLogger()),
                    null, null, LogManager.GetCurrentClassLogger(), mainDatabase: null)
            {
            }

            protected override RemoteData GetRemoteData(Book local, List<Book> remote, Author author, RefreshContext context)
            {
                var result = base.GetRemoteData(local, remote, author, context);
                SeenTitles.Add(result.Entity?.Title);
                // Stop before persistence: this audit exercises actual command lookup and selection.
                return new RemoteData();
            }

            protected override bool ShouldDelete(Book local) => false;
        }

        private static (CaptureRefresh Service, Action<int> Version, Func<int> Calls, Author ProviderAuthor, Func<Book> ProviderBook) Build(BookMediaType media, string returnedWorkId = "hc:1001")
        {
            var version = 1;
            var calls = 0;
            Book providerBook = null;
            var providerAuthor = new Author
            {
                Id = 1, Name = "Audit Author", HardcoverAuthorId = "hc:9001",
                Books = new List<Book> { new() { Title = "Catalog work 1" }, new() { Title = "Catalog work 2" } }
            };
            var author = new Author { Id = 1, Name = "Audit Author", HardcoverAuthorId = "hc:9001" };
            var books = Enumerable.Range(1, 2).Select(id => new Book
            {
                Id = id, Title = "Local work", HardcoverBookId = "hc:1001", MediaType = media,
                Author = author, AuthorId = 1, Editions = new List<Edition>(), BookFiles = new List<BookFile>()
            }).ToList();
            var bookService = InterfaceProxy.Proxy<IBookService>((method, args) => method.Name switch
            {
                nameof(IBookService.GetBook) => books.Single(book => book.Id == (int)args[0]),
                nameof(IBookService.GetBooks) => books,
                nameof(IBookService.GetExistingBooks) => books.Where(book => ((IEnumerable<int>)args[0]).Contains(book.Id)).ToList(),
                _ => throw new AssertionException(method.Name)
            });
            var authorInfo = InterfaceProxy.Proxy<IProvideAuthorInfo>((method, _) =>
                method.Name == nameof(IProvideAuthorInfo.GetAuthorInfo)
                    ? providerAuthor
                    : throw new AssertionException(method.Name));
            var bookInfo = InterfaceProxy.Proxy<IProvideBookInfo>((method, _) =>
            {
                Assert.That(method.Name, Is.AnyOf(nameof(IProvideBookInfo.GetWorkInfo), nameof(IProvideBookInfo.GetBookInfo)));
                calls++;
                providerBook = new Book
                {
                    Title = "Metadata version " + version, HardcoverBookId = returnedWorkId, MediaType = media,
                    Editions = new List<Edition>
                    {
                        new() { ForeignEditionId = "hc-ed:2001", ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3 }
                    }
                };
                return Tuple.Create("hc:9001", providerBook, new List<Author>());
            });
            return (new CaptureRefresh(bookService, authorInfo, bookInfo), value => version = value, () => calls, providerAuthor, () => providerBook);
        }


        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void direct_refresh_must_keep_the_existing_work_identity_gate(BookMediaType media)
        {
            var graph = Build(media, "hc:9999");
            graph.Service.Execute(new RefreshBookCommand { BookId = 1 });
            Assert.That(graph.Service.SeenTitles, Is.EqualTo(new string[] { null }));
        }

        [Test]
        public void bulk_refresh_must_keep_audio_and_ebook_lookups_independent()
        {
            var calls = 0;
            var author = new Author { Id = 1, Name = "Audit Author", HardcoverAuthorId = "hc:9001" };
            var books = new[] { BookMediaType.Audiobook, BookMediaType.Audiobook, BookMediaType.Ebook, BookMediaType.Ebook }
                .Select((media, index) => new Book
                {
                    Id = index + 1, Title = "Local work", HardcoverBookId = "hc:1001", MediaType = media,
                    Author = author, AuthorId = 1, Editions = new List<Edition>(), BookFiles = new List<BookFile>()
                }).ToList();
            var bookService = InterfaceProxy.Proxy<IBookService>((method, args) =>
                method.Name is nameof(IBookService.GetBooks) or nameof(IBookService.GetExistingBooks) ? books : throw new AssertionException(method.Name));
            var authorInfo = InterfaceProxy.Proxy<IProvideAuthorInfo>((method, args) =>
                method.Name == nameof(IProvideAuthorInfo.GetAuthorInfo) ? author : throw new AssertionException(method.Name));
            var metadata = InterfaceProxy.Proxy<IProvideBookInfo>((method, args) =>
            {
                calls++;
                var media = (BookMediaType)args[1];
                return Tuple.Create("hc:9001", new Book
                {
                    Title = media.ToString(), HardcoverBookId = "hc:1001", MediaType = media,
                    Editions = new List<Edition> { new() { ForeignEditionId = "hc-ed:2001", ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3 } }
                }, new List<Author>());
            });
            var service = new CaptureRefresh(bookService, authorInfo, metadata);
            service.Execute(new BulkRefreshBookCommand { BookIds = new List<int> { 1, 2, 3, 4 } });
            Assert.That(service.SeenTitles, Is.EqualTo(new[] { "Audiobook", "Audiobook", "Ebook", "Ebook" }));
            Assert.That(calls, Is.EqualTo(2));
        }

        [TestCase(1, 1)]
        [TestCase(1, 20)]
        [TestCase(20, 2)]
        public void bulk_refresh_should_reload_once_per_author_without_metadata_calls_under_selection_lock(int authorCount, int copies)
        {
            var authors = Enumerable.Range(1, authorCount).Select(id => new Author
            {
                Id = id, Name = "Audit Author " + id, HardcoverAuthorId = "hc:" + (9000 + id)
            }).ToList();
            var books = authors.SelectMany(author => Enumerable.Range(1, copies).Select(copy => new Book
            {
                Id = (author.Id - 1) * copies + copy, Title = "Local work", HardcoverBookId = "hc:" + (1000 + author.Id),
                MediaType = BookMediaType.Audiobook, AuthorId = author.Id, Author = author,
                Editions = new List<Edition>(), BookFiles = new List<BookFile>()
            })).ToList();
            var initialReads = 0;
            var singleReads = 0;
            var batchReads = new List<List<int>>();
            var bookService = InterfaceProxy.Proxy<IBookService>((method, args) =>
            {
                if (method.Name == nameof(IBookService.GetBooks))
                {
                    initialReads++;
                    return books;
                }

                if (method.Name == nameof(IBookService.GetExistingBooks))
                {
                    var ids = ((IEnumerable<int>)args[0]).ToList();
                    batchReads.Add(ids);
                    return books.Where(book => ids.Contains(book.Id)).ToList();
                }

                if (method.Name == nameof(IBookService.GetBook))
                {
                    singleReads++;
                    return books.Single(book => book.Id == (int)args[0]);
                }

                throw new AssertionException(method.Name);
            });
            var authorInfo = InterfaceProxy.Proxy<IProvideAuthorInfo>((_, args) =>
                authors.Single(author => author.HardcoverAuthorId == (string)args[0]));
            var metadataCalls = 0;
            var metadata = InterfaceProxy.Proxy<IProvideBookInfo>((_, args) =>
            {
                metadataCalls++;
                var workId = (string)args[0];
                var owner = books.First(book => book.HardcoverBookId == workId).Author;
                Assert.That(Monitor.IsEntered(BookService.GetWantedEditionLock(owner.Id)), Is.False);
                return Tuple.Create(owner.HardcoverAuthorId, new Book
                {
                    Title = "Metadata work", HardcoverBookId = workId, MediaType = BookMediaType.Audiobook,
                    Editions = new List<Edition> { new() { ForeignEditionId = "hc-ed:" + owner.Id, ReadingFormatId = 2 } }
                }, new List<Author>());
            });
            var service = new CaptureRefresh(bookService, authorInfo, metadata);
            service.Execute(new BulkRefreshBookCommand { BookIds = books.Select(book => book.Id).ToList() });

            TestContext.WriteLine($"Authors={authorCount}, copies={copies}: initial service reads={initialReads}, batch reloads={batchReads.Count}, single reloads={singleReads}, metadata calls={metadataCalls}");
            Assert.That(initialReads, Is.EqualTo(1));
            Assert.That(batchReads.Count, Is.EqualTo(authorCount));
            Assert.That(batchReads.All(ids => ids.Count == copies), Is.True);
            Assert.That(singleReads, Is.Zero);
            Assert.That(metadataCalls, Is.EqualTo(authorCount));
            Assert.That(service.SeenTitles.Count, Is.EqualTo(books.Count));
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void direct_refresh_should_leave_provider_author_catalog_unchanged(BookMediaType media)
        {
            var graph = Build(media);
            var catalog = graph.ProviderAuthor.Books;
            graph.Service.Execute(new RefreshBookCommand { BookId = 1 });
            Assert.That(graph.ProviderAuthor.Books, Is.SameAs(catalog));
            Assert.That(graph.ProviderAuthor.Books.Select(book => book.Title), Is.EqualTo(new[] { "Catalog work 1", "Catalog work 2" }));
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void direct_refresh_should_not_decorate_provider_work_with_author_state(BookMediaType media)
        {
            var graph = Build(media);
            graph.Service.Execute(new RefreshBookCommand { BookId = 1 });
            Assert.That(graph.ProviderBook().Author.Id, Is.Zero);
            Assert.That(graph.ProviderBook().Author.HardcoverAuthorId, Is.Null);
            Assert.That(graph.ProviderBook().AuthorId, Is.Zero);
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void separate_single_book_commands_should_use_current_metadata(BookMediaType media)
        {
            var graph = Build(media);
            graph.Service.Execute(new RefreshBookCommand { BookId = 1 });
            graph.Version(2);
            graph.Service.Execute(new RefreshBookCommand { BookId = 1 });
            TestContext.WriteLine($"Actual command inputs: {string.Join(",", graph.Service.SeenTitles)}; metadata calls={graph.Calls()}");
            Assert.That(graph.Service.SeenTitles, Is.EqualTo(new[] { "Metadata version 1", "Metadata version 2" }));
            Assert.That(graph.Calls(), Is.EqualTo(2));
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void same_work_copies_within_bulk_should_reuse_one_lookup(BookMediaType media)
        {
            var graph = Build(media);
            graph.Service.Execute(new BulkRefreshBookCommand { BookIds = new List<int> { 1, 2 } });
            Assert.That(graph.Service.SeenTitles, Is.EqualTo(new[] { "Metadata version 1", "Metadata version 1" }));
            Assert.That(graph.Calls(), Is.EqualTo(1));
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void separate_bulk_commands_should_use_current_metadata_and_reuse_each_work(BookMediaType media)
        {
            var graph = Build(media);
            graph.Service.Execute(new BulkRefreshBookCommand { BookIds = new List<int> { 1, 2 } });
            graph.Version(2);
            graph.Service.Execute(new BulkRefreshBookCommand { BookIds = new List<int> { 1, 2 } });
            TestContext.WriteLine($"Actual command inputs: {string.Join(",", graph.Service.SeenTitles)}; metadata calls={graph.Calls()}");
            Assert.That(graph.Service.SeenTitles, Is.EqualTo(new[] { "Metadata version 1", "Metadata version 1", "Metadata version 2", "Metadata version 2" }));
            Assert.That(graph.Calls(), Is.EqualTo(2));
        }
    }
}

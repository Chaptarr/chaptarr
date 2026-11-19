using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class PersistedAliasPendingLifecycleFixture
    {
        private sealed class ErrorContractRefresh : RefreshBookService
        {
            public readonly List<string> AcceptedRemoteWorkIds = new();

            public ErrorContractRefresh(Harness h, IProvideAuthorInfo authors, IProvideBookInfo metadata)
                : base(h.BookService, null, null, null, authors, metadata, null,
                    Proxy<IMediaFileService>((_, _) => new List<BookFile>()), null, null, null,
                    new EditionSelector(LogManager.GetCurrentClassLogger()), null, null, LogManager.GetCurrentClassLogger(), mainDatabase: null)
            {
            }

            protected override RemoteData GetRemoteData(Book local, List<Book> remote, Author author, RefreshContext context)
            {
                var result = base.GetRemoteData(local, remote, author, context);
                if (result.Entity != null) AcceptedRemoteWorkIds.Add(result.Entity.HardcoverBookId);
                return new RemoteData();
            }

            protected override bool ShouldDelete(Book local) => false;
        }

        [Test]
        public void direct_refresh_unavailable_work_or_author_skips_without_changing_persisted_book(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool authorUnavailable)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(media, "hc:7000", "hc:edition:7000");
                var metadataCalls = 0;
                var authorCalls = 0;
                var metadata = Proxy<IProvideBookInfo>((_, _) =>
                {
                    metadataCalls++;
                    if (!authorUnavailable) throw new BookInfoException("Metadata is being prepared");
                    return Tuple.Create(h.Author.HardcoverAuthorId, new Book { HardcoverBookId = "hc:7000", MediaType = media,
                        Editions = new() { new() { ForeignEditionId = "hc:edition:7000", ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3 } } }, new List<Author> { h.Author });
                });
                var authors = Proxy<IProvideAuthorInfo>((_, _) => { authorCalls++; throw new BookInfoException("Author metadata is unavailable"); });
                var refresh = new ErrorContractRefresh(h, authors, metadata);
                Assert.DoesNotThrow(() => refresh.Execute(new RefreshBookCommand { BookId = book.Id }));
                Assert.Multiple(() =>
                {
                    Assert.That(metadataCalls, Is.EqualTo(1));
                    Assert.That(authorCalls, Is.EqualTo(authorUnavailable ? 1 : 0));
                    Assert.That(refresh.AcceptedRemoteWorkIds, Is.Empty);
                    Assert.That(h.BookService.GetBook(book.Id).Title, Is.EqualTo(book.Title));
                    Assert.That(h.BookService.GetBook(book.Id).AnyEditionOk, Is.True);
                    Assert.That(h.BookService.GetBook(book.Id).IsMonitored(), Is.False);
                    Assert.That(h.Editions.FindRowsByBook(new[] { book.Id }).Single().ManualAdd, Is.False);
                    Assert.That(h.Requests.All(), Is.Empty);
                });
            });
        }

        [Test]
        public void bulk_refresh_unavailable_work_reuses_one_lookup_per_media_and_continues_healthy_work(
            [Values(false, true)] bool postgres, [Values(1, 20)] int copies,
            [Values("temporary", "missing", "bad-request")] string failure)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var books = new[] { BookMediaType.Audiobook, BookMediaType.Ebook }.SelectMany(media => Enumerable.Range(0, copies)
                    .Select(index => h.AddBook(media, "hc:7000", "hc:edition:" + (7000 + index)))).ToList();
                var healthy = h.AddBook(work: "hc:7001", edition: "hc:edition:7999");
                books.Add(healthy);
                var lookups = new List<(string Work, BookMediaType Media)>();
                var authorCalls = 0;
                var metadata = Proxy<IProvideBookInfo>((_, args) =>
                {
                    var work = (string)args[0];
                    var media = (BookMediaType)args[1];
                    lookups.Add((work, media));
                    if (work == "hc:7000")
                    {
                        if (failure == "temporary") throw new BookInfoException("Metadata is being prepared");
                        if (failure == "missing") throw new BookNotFoundException(work);
                        throw new BadRequestException(work);
                    }
                    return Tuple.Create(h.Author.HardcoverAuthorId, new Book { HardcoverBookId = work, Title = healthy.Title, MediaType = media,
                        Editions = new() { new() { ForeignEditionId = "hc:edition:7999", ReadingFormatId = 2 } } }, new List<Author> { h.Author });
                });
                var authors = Proxy<IProvideAuthorInfo>((_, _) => { authorCalls++; return h.Author; });
                var refresh = new ErrorContractRefresh(h, authors, metadata);
                refresh.Execute(new BulkRefreshBookCommand { BookIds = books.Select(book => book.Id).ToList() });
                Assert.Multiple(() =>
                {
                    Assert.That(lookups, Is.EquivalentTo(new[] { ("hc:7000", BookMediaType.Audiobook), ("hc:7000", BookMediaType.Ebook), ("hc:7001", BookMediaType.Audiobook) }));
                    Assert.That(authorCalls, Is.EqualTo(1));
                    Assert.That(refresh.AcceptedRemoteWorkIds, Is.EqualTo(new[] { healthy.HardcoverBookId }));
                    Assert.That(h.Books.All(), Has.Count.EqualTo(2 * copies + 1));
                    Assert.That(h.Editions.All().All(edition => !edition.ManualAdd), Is.True);
                });
                TestContext.Progress.WriteLine($"Metadata errors ({(postgres ? "PostgreSQL" : "SQLite")}): {failure}, {copies} copies per media, {lookups.Count} work/media lookups, {authorCalls} healthy author lookup");
            });
        }
    }
}

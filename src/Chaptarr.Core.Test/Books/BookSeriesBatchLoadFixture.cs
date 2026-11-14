using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public class BookSeriesBatchLoadFixture
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void rename_and_retag_preview_load_multipart_series_in_one_batch(bool postgres, bool retag)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var author = new AuthorRepository(database, events, LogManager.GetCurrentClassLogger()).Insert(new Author
                {
                    Name = "Series Writer", CleanName = "serieswriter", Path = "/series-test", Added = DateTime.UtcNow
                });
                var book = new BasicRepository<Book>(database, events).Insert(new Book
                {
                    AuthorId = author.Id, Title = "Book", CleanTitle = "book", Added = DateTime.UtcNow
                });
                var edition = new EditionRepository(database, events).Insert(new Edition { BookId = book.Id, Title = "Book" });
                var series = new BasicRepository<Series>(database, events).Insert(new Series { Title = "Series", GoodreadsSeriesId = "gr:123" });
                new SeriesBookLinkRepository(database, events).Insert(new SeriesBookLink { BookId = book.Id, SeriesId = series.Id, Position = "2" });
                var files = new MediaFileRepository(database, events);
                files.InsertMany(Enumerable.Range(1, 100).Select(part => new BookFile
                {
                    EditionId = edition.Id, Path = $"/series-test/Book/{part}.mp3", Quality = new QualityModel(Quality.MP3),
                    MediaInfo = new MediaInfoModel(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow
                }).ToList());
                var secondBook = new BasicRepository<Book>(database, events).Insert(new Book { AuthorId = author.Id, Title = "Another book", CleanTitle = "anotherbook", Added = DateTime.UtcNow });
                var secondEdition = new EditionRepository(database, events).Insert(new Edition { BookId = secondBook.Id, Title = "Another book" });
                new SeriesBookLinkRepository(database, events).Insert(new SeriesBookLink { BookId = secondBook.Id, SeriesId = series.Id, Position = "2" });
                files.Insert(new BookFile { EditionId = secondEdition.Id, Path = "/series-test/Other.mp3", Quality = new QualityModel(Quality.MP3), MediaInfo = new MediaInfoModel(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow });
                var reads = 0;
                var observed = InterfaceProxy.Proxy<IMainDatabase>((method, args) => method.Name == nameof(IDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), _ => reads++) : method.Invoke(database, args));
                var loaded = new MediaFileRepository(observed, events).GetFilesByAuthor(author.Id);
                var books = loaded.Select(file => file.Edition.Book).ToList();
                reads = 0;
                var repository = new SeriesBookLinkRepository(observed, events);
                var authors = InterfaceProxy.Proxy<IAuthorService>((method, _) => method.Name == nameof(IAuthorService.GetAuthor)
                    ? author : throw new AssertionException(method.Name));
                var media = InterfaceProxy.Proxy<IMediaFileService>((method, _) => method.Name == nameof(IMediaFileService.GetFilesByAuthor)
                    ? loaded : throw new AssertionException(method.Name));
                var mover = InterfaceProxy.Proxy<IMoveBookFiles>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IMoveBookFiles.GetOrganizeDestination)));
                    var file = (BookFile)args[0];
                    return new BookFileMovePlan
                    {
                        CanOrganize = true,
                        DestinationPath = "/series-test/" + BookSeries.Label(BookSeries.Select(file.Edition.Book)) + "/" + Path.GetFileName(file.Path)
                    };
                });
                if (retag)
                {
                    foreach (var file in loaded) file.Path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Audio", "silence.mp3");
                    var config = InterfaceProxy.Proxy<IConfigService>((method, _) => method.Name == "get_ScrubAudioTags" ? false : throw new AssertionException(method.Name));
                    var audio = new AudioTagService(config, media, null, null, null, null, null, LogManager.GetCurrentClassLogger(), null, new SeriesBookLinkService(repository));
                    var previews = audio.GetRetagPreviewsByAuthor(author.Id);
                    Assert.That(previews, Has.Count.EqualTo(101));
                    Assert.That(previews.Select(p => p.Changes["SERIES"].Item2), Is.All.EqualTo("Series"));
                    Assert.That(previews.Select(p => p.Changes["SERIES-PART"].Item2), Is.All.EqualTo("2"));
                }
                else
                {
                    var rename = new RenameBookFileService(authors, media, mover, events, null, LogManager.GetCurrentClassLogger(),
                        new SeriesBookLinkService(repository));
                    Assert.That(rename.GetRenamePreviews(author.Id), Has.Count.EqualTo(101));
                }
                Assert.That(books.Select(BookSeries.Select).Select(BookSeries.Label), Is.All.EqualTo("Series #2"));
                Assert.That(reads, Is.EqualTo(2), "One membership query and one series query for the batch");
                BookSeries.LoadLinks(books, _ => throw new AssertionException("Already-loaded metadata must be reused"));
                Assert.That(reads, Is.EqualTo(2));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void large_author_memberships_are_batched_and_reused(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var author = new AuthorRepository(database, events, LogManager.GetCurrentClassLogger()).Insert(new Author
                {
                    Name = "Large author", CleanName = "largeauthor", Path = "/large-series", Added = DateTime.UtcNow
                });
                var books = Enumerable.Range(1, 3015).Select(id => new Book { AuthorId = author.Id, Title = "Book " + id, CleanTitle = "book" + id,
                    Added = DateTime.UtcNow, LazySeriesLinks = new LazyLoaded<List<SeriesBookLink>>() }).ToList();
                new BasicRepository<Book>(database, events).InsertMany(books);
                var series = new BasicRepository<Series>(database, events).Insert(new Series { Title = "Series", GoodreadsSeriesId = "gr:1" });
                new SeriesBookLinkRepository(database, events).InsertMany(books.Select(book => new SeriesBookLink { BookId = book.Id, SeriesId = series.Id, Position = "1" }).ToList());
                var reads = 0;
                var observed = InterfaceProxy.Proxy<IMainDatabase>((method, args) => method.Name == nameof(IDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), _ => reads++) : method.Invoke(database, args));
                BookSeries.LoadLinks(books, new SeriesBookLinkRepository(observed, events).GetLinksByBook);
                Assert.That(reads, Is.EqualTo(postgres ? 2 : 5), "Membership pages plus one shared-series read, never one query per book");
                Assert.That(books.Select(BookSeries.AudioTagPair), Is.All.EqualTo(("Series", "1")));
                BookSeries.LoadLinks(books, _ => throw new AssertionException("Already hydrated author must not be reloaded"));
            });
        }

        [Test]
        public void failed_audio_preload_does_not_mark_books_loaded_empty()
        {
            var pending = new Book { Id = 1, LazySeriesLinks = new LazyLoaded<List<SeriesBookLink>>() };
            var known = new Book { Id = 2, SeriesLinks = new List<SeriesBookLink> { new() { Position = "4" } } };
            var existing = known.SeriesLinks;
            var calls = 0;
            var service = InterfaceProxy.Proxy<ISeriesBookLinkService>((_, _) =>
            {
                if (++calls == 1) throw new InvalidOperationException("Database unavailable");
                return new List<SeriesBookLink> { new() { BookId = 1, Position = "7" } };
            });
            Assert.That(() => AudioTagService.LoadSeries(new[] { pending, known }, service, LogManager.GetCurrentClassLogger()),
                Throws.InvalidOperationException.With.Message.EqualTo("Database unavailable"));
            Assert.That(pending.LazySeriesLinks.IsLoaded, Is.False);
            Assert.That(known.SeriesLinks, Is.SameAs(existing));
            AudioTagService.LoadSeries(new[] { pending }, service, LogManager.GetCurrentClassLogger());
            Assert.That(pending.SeriesLinks.Single().Position, Is.EqualTo("7"));
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void a_failed_bulk_read_must_not_clear_loaded_metadata_or_partially_assign_links()
        {
            var pending = new Book { Id = 1, LazySeriesLinks = new LazyLoaded<List<SeriesBookLink>>() };
            var known = new Book { Id = 2, SeriesLinks = new List<SeriesBookLink> { new() { Position = "4" } } };
            var existing = known.SeriesLinks;
            Assert.Throws<InvalidOperationException>(() => BookSeries.LoadLinks(new[] { pending, known }, ids =>
            {
                Assert.That(ids, Is.EqualTo(new[] { 1 }));
                throw new InvalidOperationException("Database unavailable");
            }));
            Assert.That(pending.LazySeriesLinks.IsLoaded, Is.False);
            Assert.That(known.SeriesLinks, Is.SameAs(existing));
        }
    }
}

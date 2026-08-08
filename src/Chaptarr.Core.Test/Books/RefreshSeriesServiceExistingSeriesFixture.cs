using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshSeriesServiceExistingSeriesFixture
    {
        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new List<Book>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthor) &&
                    args?.Length == 1 &&
                    args[0] is int authorId)
                {
                    return Books.Where(b => b.AuthorId == authorId).ToList();
                }

                if (targetMethod?.Name == nameof(IBookService.FindByProviderId) &&
                    args?.Length == 3 &&
                    args[0] is string provider &&
                    args[1] is string providerId &&
                    args[2] is BookMediaType mediaType)
                {
                    return Books.SingleOrDefault(b =>
                        b.MediaType == mediaType &&
                        MatchesProviderId(b, provider, providerId));
                }

                if (targetMethod?.Name == nameof(IBookService.FindAllByProviderId) &&
                    args?.Length == 3 &&
                    args[0] is string allProvider &&
                    args[1] is string allProviderId &&
                    args[2] is BookMediaType allMediaType)
                {
                    return Books
                        .Where(b => b != null &&
                                    b.MediaType == allMediaType &&
                                    MatchesProviderId(b, allProvider, allProviderId))
                        .ToList();
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
            }

            private static bool MatchesProviderId(Book book, string provider, string providerId)
            {
                if (book == null || string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerId))
                {
                    return false;
                }

                provider = provider.Trim().ToLowerInvariant();
                providerId = providerId.Trim();

                // Accept either canonical "<prefix>:<id>" or raw id portion.
                string id = providerId;
                if (providerId.Contains(":"))
                {
                    var idx = providerId.IndexOf(':');
                    provider = providerId.Substring(0, idx).Trim().ToLowerInvariant();
                    id = providerId.Substring(idx + 1).Trim();
                }

                provider = provider switch
                {
                    "hardcover" => "hc",
                    "goodreads" => "gr",
                    "openlibrary" => "ol",
                    "googlebooks" => "gb",
                    "amazon" => "az",
                    _ => provider
                };

                return provider switch
                {
                    "hc" => book.HardcoverBookId == $"hc:{id}",
                    "gr" => book.GoodreadsBookId == $"gr:{id}" || book.GoodreadsWorkId == $"gr:{id}",
                    "ol" => book.OpenLibraryWorkId == $"ol:{id}",
                    "gb" => book.GoogleBooksId == $"gb:{id}",
                    "az" => book.BaseBookId == $"az:{id}" || book.ASIN == id || book.AudibleASIN == id,
                    _ => false
                };
            }
        }

        private sealed class StubSeriesService : ISeriesService
        {
            private int _nextId = 1000;

            public bool ReturnGetByAuthorIdResults { get; set; } = true;

            public List<Series> SeriesByAuthor { get; } = new List<Series>();
            public List<Series> Inserted { get; } = new List<Series>();
            public List<Series> Updated { get; } = new List<Series>();
            public List<int> DeletedIds { get; } = new List<int>();

            public Series GetSeries(int seriesId) => SeriesByAuthor.SingleOrDefault(s => s.Id == seriesId);

            public Series FindById(string foreignSeriesId)
            {
                return SeriesByAuthor.SingleOrDefault(s =>
                    s.HardcoverSeriesId == foreignSeriesId ||
                    s.GoodreadsSeriesId == foreignSeriesId ||
                    s.OpenLibrarySeriesId == foreignSeriesId ||
                    s.AmazonSeriesAsin == foreignSeriesId);
            }

            public Series FindById(string foreignSeriesId, BookMediaType mediaType)
            {
                return SeriesByAuthor.SingleOrDefault(s =>
                    s.MediaType == mediaType &&
                    (s.HardcoverSeriesId == foreignSeriesId ||
                     s.GoodreadsSeriesId == foreignSeriesId ||
                     s.OpenLibrarySeriesId == foreignSeriesId ||
                     s.AmazonSeriesAsin == foreignSeriesId));
            }

            public List<Series> FindById(List<string> foreignSeriesId)
            {
                return SeriesByAuthor
                    .Where(s => foreignSeriesId.Contains(s.HardcoverSeriesId) ||
                                foreignSeriesId.Contains(s.GoodreadsSeriesId) ||
                                foreignSeriesId.Contains(s.OpenLibrarySeriesId) ||
                                foreignSeriesId.Contains(s.AmazonSeriesAsin))
                    .ToList();
            }

            public List<Series> FindById(List<string> foreignSeriesId, BookMediaType mediaType)
            {
                return SeriesByAuthor
                    .Where(s => s.MediaType == mediaType &&
                                (foreignSeriesId.Contains(s.HardcoverSeriesId) ||
                                 foreignSeriesId.Contains(s.GoodreadsSeriesId) ||
                                 foreignSeriesId.Contains(s.OpenLibrarySeriesId) ||
                                 foreignSeriesId.Contains(s.AmazonSeriesAsin)))
                    .ToList();
            }

            public List<Series> GetByAuthorId(int authorId)
            {
                return ReturnGetByAuthorIdResults ? SeriesByAuthor.ToList() : new List<Series>();
            }

            public List<Series> GetAllSeries()
            {
                return SeriesByAuthor.ToList();
            }

            public Series AddSeries(Series series)
            {
                if (series.Id == 0)
                {
                    series.Id = _nextId++;
                }

                SeriesByAuthor.Add(series);
                return series;
            }

            public void Delete(int seriesId)
            {
                DeletedIds.Add(seriesId);
                SeriesByAuthor.RemoveAll(s => s.Id == seriesId);
            }

            public void InsertMany(IList<Series> series)
            {
                foreach (var item in series)
                {
                    if (item.Id == 0)
                    {
                        item.Id = _nextId++;
                    }

                    Inserted.Add(item);
                    SeriesByAuthor.Add(item);
                }
            }

            public void InsertMany(IList<Series> series, IDbConnection connection, IDbTransaction transaction)
            {
                InsertMany(series);
            }

            public void UpdateMany(IList<Series> series)
            {
                Updated.AddRange(series);
            }
        }

        private sealed class StubSeriesBookLinkService : ISeriesBookLinkService
        {
            private readonly Dictionary<int, List<SeriesBookLink>> _linksBySeriesId = new Dictionary<int, List<SeriesBookLink>>();

            public List<SeriesBookLink> Inserted { get; } = new List<SeriesBookLink>();
            public List<SeriesBookLink> Updated { get; } = new List<SeriesBookLink>();
            public List<SeriesBookLink> Deleted { get; } = new List<SeriesBookLink>();
            public HashSet<int> ClaimedBookIds { get; set; } = new HashSet<int>();

            public void SetLinks(int seriesId, params SeriesBookLink[] links)
            {
                _linksBySeriesId[seriesId] = new List<SeriesBookLink>(links ?? Array.Empty<SeriesBookLink>());
            }

            public List<SeriesBookLink> GetLinksBySeries(int seriesId)
            {
                return _linksBySeriesId.TryGetValue(seriesId, out var links)
                    ? links.Select(CloneLink).ToList()
                    : new List<SeriesBookLink>();
            }

            public List<SeriesBookLink> GetLinksBySeriesAndAuthor(int seriesId, string foreignAuthorId)
            {
                throw new NotImplementedException();
            }

            public List<SeriesBookLink> GetLinksByBook(List<int> bookIds)
            {
                return _linksBySeriesId.Values
                    .SelectMany(x => x)
                    .Where(x => bookIds.Contains(x.BookId))
                    .Select(CloneLink)
                    .ToList();
            }

            public HashSet<int> GetClaimedBookIdsForSeriesIdentity(BookMediaType mediaType, string goodreadsSeriesId)
            {
                return new HashSet<int>(ClaimedBookIds);
            }

            public void InsertMany(List<SeriesBookLink> model)
            {
                Inserted.AddRange(model.Select(CloneLink));

                foreach (var link in model)
                {
                    if (!_linksBySeriesId.TryGetValue(link.SeriesId, out var existing))
                    {
                        existing = new List<SeriesBookLink>();
                        _linksBySeriesId[link.SeriesId] = existing;
                    }

                    existing.Add(CloneLink(link));
                }
            }

            public void InsertMany(List<SeriesBookLink> model, IDbConnection connection, IDbTransaction transaction)
            {
                InsertMany(model);
            }

            public void UpdateMany(List<SeriesBookLink> model)
            {
                Updated.AddRange(model.Select(CloneLink));
            }

            public void DeleteMany(List<SeriesBookLink> model)
            {
                Deleted.AddRange(model.Select(CloneLink));

                foreach (var link in model)
                {
                    if (_linksBySeriesId.TryGetValue(link.SeriesId, out var existing))
                    {
                        existing.RemoveAll(x => x.BookId == link.BookId);
                    }
                }
            }

            public void AddLink(SeriesBookLink link)
            {
                InsertMany(new List<SeriesBookLink> { link });
            }

            private static SeriesBookLink CloneLink(SeriesBookLink link)
            {
                return new SeriesBookLink
                {
                    Id = link.Id,
                    SeriesId = link.SeriesId,
                    BookId = link.BookId,
                    Position = link.Position,
                    SeriesPosition = link.SeriesPosition,
                    IsPrimary = link.IsPrimary,
                    SeriesInstanceType = link.SeriesInstanceType,
                    IsInheritedLink = link.IsInheritedLink,
                    Book = link.Book?.IsLoaded == true ? new LazyLoaded<Book>(link.Book.Value) : null,
                    Series = link.Series?.IsLoaded == true ? new LazyLoaded<Series>(link.Series.Value) : null
                };
            }
        }

        private sealed class TestableRefreshSeriesService : RefreshSeriesService
        {
            public int DeleteCalls { get; private set; }

            public TestableRefreshSeriesService(IBookService bookService, ISeriesService seriesService, ISeriesBookLinkService linkService, IRefreshSeriesBookLinkService refreshLinkService, Logger logger)
                : base(bookService, seriesService, linkService, refreshLinkService, logger)
            {
            }

            protected override void DeleteEntity(Series local, bool deleteFiles)
            {
                DeleteCalls++;
                base.DeleteEntity(local, deleteFiles);
            }
        }

        [Test]
        public void should_backfill_links_for_existing_matched_original_series()
        {
            var ebookBook = new Book
            {
                Id = 201,
                AuthorId = 29,
                Title = "The Housemaid",
                MediaType = BookMediaType.Ebook,
                GoodreadsWorkId = "gr:203559547"
            };

            var existingSeries = new Series
            {
                Id = 873,
                Title = "The Housemaid",
                GoodreadsSeriesId = "gr:353739",
                MediaType = BookMediaType.Ebook
            };

            var remoteSeries = CreateRemoteSeries(
                title: "The Housemaid",
                goodreadsSeriesId: "gr:353739",
                mediaType: BookMediaType.Ebook,
                books: new[] { ebookBook });

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Books = new List<Book> { ebookBook };

            var seriesService = new StubSeriesService();
            seriesService.SeriesByAuthor.Add(existingSeries);

            var linkService = new StubSeriesBookLinkService();
            linkService.SetLinks(existingSeries.Id);

            var sut = new TestableRefreshSeriesService(
                bookService,
                seriesService,
                linkService,
                new RefreshSeriesBookLinkService(linkService, LogManager.GetCurrentClassLogger()),
                LogManager.GetCurrentClassLogger());

            var updated = sut.RefreshSeriesInfo(29, new List<Series> { remoteSeries }, new Author { Id = 29 }, false, false, null);

            Assert.Multiple(() =>
            {
                Assert.That(updated, Is.True);
                Assert.That(linkService.Inserted.Select(x => x.BookId), Is.EqualTo(new[] { ebookBook.Id }));
                Assert.That(linkService.GetLinksBySeries(existingSeries.Id).Select(x => x.BookId), Is.EqualTo(new[] { ebookBook.Id }));
                Assert.That(seriesService.Updated.Select(x => x.Id), Contains.Item(existingSeries.Id));
            });
        }

        [Test]
        public void should_backfill_links_for_existing_matched_original_series_using_v5_seriesbooks()
        {
            var ebookBook = new Book
            {
                Id = 4028,
                AuthorId = 29,
                Title = "The Boyfriend",
                MediaType = BookMediaType.Ebook,
                GoodreadsWorkId = "gr:214628030",
                HardcoverBookId = "hc:1325795"
            };

            var existingSeries = new Series
            {
                Id = 1001,
                Title = "Crime, Thriller & Mystery in French",
                GoodreadsSeriesId = "gr:french-series",
                MediaType = BookMediaType.Ebook
            };

            var remoteSeries = CreateRemoteSeriesWithSeriesBooks(
                title: "Crime, Thriller & Mystery in French",
                goodreadsSeriesId: "gr:french-series",
                mediaType: BookMediaType.Ebook,
                books: new[]
                {
                    new SeriesBook
                    {
                        Title = ebookBook.Title,
                        BookId = "gr:214628030",
                        Position = "26"
                    }
                });

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Books = new List<Book> { ebookBook };

            var seriesService = new StubSeriesService();
            seriesService.SeriesByAuthor.Add(existingSeries);

            var linkService = new StubSeriesBookLinkService();
            linkService.SetLinks(existingSeries.Id);

            var sut = new TestableRefreshSeriesService(
                bookService,
                seriesService,
                linkService,
                new RefreshSeriesBookLinkService(linkService, LogManager.GetCurrentClassLogger()),
                LogManager.GetCurrentClassLogger());

            var updated = sut.RefreshSeriesInfo(29, new List<Series> { remoteSeries }, new Author { Id = 29 }, false, false, null);

            Assert.Multiple(() =>
            {
                Assert.That(updated, Is.True);
                Assert.That(linkService.Inserted.Select(x => x.BookId), Is.EqualTo(new[] { ebookBook.Id }));
                Assert.That(linkService.GetLinksBySeries(existingSeries.Id).Select(x => x.BookId), Is.EqualTo(new[] { ebookBook.Id }));
                Assert.That(seriesService.Updated.Select(x => x.Id), Contains.Item(existingSeries.Id));
            });
        }

        [Test]
        public void should_delete_amazon_only_series_and_insert_goodreads_series_links()
        {
            var ebookBook = new Book
            {
                Id = 612,
                AuthorId = 29,
                Title = "Example Book",
                MediaType = BookMediaType.Ebook,
                GoodreadsWorkId = "gr:999999"
            };

            var existingSeries = new Series
            {
                Id = 2001,
                Title = "Example Series",
                GoodreadsSeriesId = null,
                AmazonSeriesAsin = "az:B012345678",
                MediaType = BookMediaType.Ebook
            };

            var remoteSeries = new Series
            {
                Title = "Example Series",
                GoodreadsSeriesId = "gr:series-123",
                AmazonSeriesAsin = "az:B012345678",
                MediaType = BookMediaType.Ebook,
                SeriesBooks = new List<SeriesBook>
                {
                    new SeriesBook
                    {
                        Title = ebookBook.Title,
                        BookId = "gr:999999",
                        Position = "1"
                    }
                }
            };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Books = new List<Book> { ebookBook };

            var seriesService = new StubSeriesService
            { };
            seriesService.SeriesByAuthor.Add(existingSeries);

            var linkService = new StubSeriesBookLinkService();
            linkService.SetLinks(existingSeries.Id);

            var sut = new TestableRefreshSeriesService(
                bookService,
                seriesService,
                linkService,
                new RefreshSeriesBookLinkService(linkService, LogManager.GetCurrentClassLogger()),
                LogManager.GetCurrentClassLogger());

            var updated = sut.RefreshSeriesInfo(29, new List<Series> { remoteSeries }, new Author { Id = 29 }, false, false, null);

            Assert.Multiple(() =>
            {
                Assert.That(updated, Is.True);
                Assert.That(seriesService.DeletedIds, Contains.Item(existingSeries.Id));

                var insertedSeries = seriesService.Inserted.Single(x => x.GoodreadsSeriesId == remoteSeries.GoodreadsSeriesId);
                Assert.That(linkService.Inserted.Select(x => x.BookId).ToArray(), Is.EqualTo(new[] { ebookBook.Id }));
                Assert.That(linkService.GetLinksBySeries(insertedSeries.Id).Select(x => x.BookId).ToArray(), Is.EqualTo(new[] { ebookBook.Id }));
            });
        }

        [Test]
        public void should_backfill_missing_links_for_partially_linked_existing_original_series()
        {
            var firstBook = new Book
            {
                Id = 501,
                AuthorId = 29,
                Title = "Alibis Collection #1",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:alibis-1"
            };

            var secondBook = new Book
            {
                Id = 502,
                AuthorId = 29,
                Title = "Alibis Collection #2",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:alibis-2"
            };

            var existingSeries = new Series
            {
                Id = 990,
                Title = "Alibis Collection",
                GoodreadsSeriesId = "gr:alibis",
                MediaType = BookMediaType.Audiobook
            };

            var remoteSeries = CreateRemoteSeries(
                title: "Alibis Collection",
                goodreadsSeriesId: "gr:alibis",
                mediaType: BookMediaType.Audiobook,
                books: new[] { firstBook, secondBook });

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Books = new List<Book> { firstBook, secondBook };

            var seriesService = new StubSeriesService();
            seriesService.SeriesByAuthor.Add(existingSeries);

            var linkService = new StubSeriesBookLinkService();
            linkService.SetLinks(existingSeries.Id, new SeriesBookLink
            {
                SeriesId = existingSeries.Id,
                BookId = firstBook.Id,
                Position = "1",
                SeriesPosition = 1,
                IsPrimary = true,
                SeriesInstanceType = "original",
                Book = new LazyLoaded<Book>(firstBook)
            });

            var sut = new TestableRefreshSeriesService(
                bookService,
                seriesService,
                linkService,
                new RefreshSeriesBookLinkService(linkService, LogManager.GetCurrentClassLogger()),
                LogManager.GetCurrentClassLogger());

            var updated = sut.RefreshSeriesInfo(29, new List<Series> { remoteSeries }, new Author { Id = 29 }, false, false, null);

            Assert.Multiple(() =>
            {
                Assert.That(updated, Is.True);
                Assert.That(linkService.Inserted.Select(x => x.BookId), Is.EqualTo(new[] { secondBook.Id }));
                Assert.That(linkService.GetLinksBySeries(existingSeries.Id).Select(x => x.BookId).OrderBy(x => x), Is.EqualTo(new[] { firstBook.Id, secondBook.Id }));
                Assert.That(seriesService.Updated.Select(x => x.Id), Contains.Item(existingSeries.Id));
            });
        }

        [Test]
        public void should_not_delete_unmatched_narrator_variants()
        {
            var variant = new Series
            {
                Id = 777,
                Title = "The Housemaid",
                GoodreadsSeriesId = "gr:353739",
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorId = 123,
                Narrator = "Jim Dale"
            };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var seriesService = new StubSeriesService();
            seriesService.SeriesByAuthor.Add(variant);

            var linkService = new StubSeriesBookLinkService();
            var sut = new TestableRefreshSeriesService(
                bookService,
                seriesService,
                linkService,
                new RefreshSeriesBookLinkService(linkService, LogManager.GetCurrentClassLogger()),
                LogManager.GetCurrentClassLogger());

            var updated = sut.RefreshEntityInfo(variant, new List<Series>(), new Author { Id = 29 }, false, false, null);

            Assert.Multiple(() =>
            {
                Assert.That(updated, Is.False);
                Assert.That(sut.DeleteCalls, Is.EqualTo(0));
                Assert.That(seriesService.DeletedIds, Is.Empty);
            });
        }

        [Test]
        public void should_not_delete_series_from_other_media_types_during_partial_refresh()
        {
            var audiobookSeries = new Series
            {
                Id = 100,
                Title = "Audio Only",
                GoodreadsSeriesId = "gr:audio-series",
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorId = null
            };

            var ebookSeries = new Series
            {
                Id = 101,
                Title = "Ebook Only",
                GoodreadsSeriesId = "gr:ebook-series",
                MediaType = BookMediaType.Ebook,
                PreferredNarratorId = null
            };

            var remoteAudiobookSeries = CreateRemoteSeries(
                title: audiobookSeries.Title,
                goodreadsSeriesId: audiobookSeries.GoodreadsSeriesId,
                mediaType: BookMediaType.Audiobook);

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var seriesService = new StubSeriesService();
            seriesService.SeriesByAuthor.Add(audiobookSeries);
            seriesService.SeriesByAuthor.Add(ebookSeries);

            var linkService = new StubSeriesBookLinkService();
            var sut = new TestableRefreshSeriesService(
                bookService,
                seriesService,
                linkService,
                new RefreshSeriesBookLinkService(linkService, LogManager.GetCurrentClassLogger()),
                LogManager.GetCurrentClassLogger());

            sut.RefreshSeriesInfo(29, new List<Series> { remoteAudiobookSeries }, new Author { Id = 29 }, false, false, null);

            Assert.Multiple(() =>
            {
                Assert.That(seriesService.DeletedIds, Is.Empty);
                Assert.That(seriesService.SeriesByAuthor.Select(s => s.Id), Contains.Item(ebookSeries.Id));
            });
        }

        [Test]
        public void should_refresh_shared_provider_series_using_the_matching_media_type()
        {
            var audiobookBook = new Book
            {
                Id = 301,
                AuthorId = 29,
                Title = "Shared Provider Audio",
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:work-audio"
            };

            var audiobookSeries = new Series
            {
                Id = 1100,
                Title = "Shared Provider",
                GoodreadsSeriesId = "gr:shared-series",
                MediaType = BookMediaType.Audiobook
            };

            var ebookSeries = new Series
            {
                Id = 1101,
                Title = "Shared Provider",
                GoodreadsSeriesId = "gr:shared-series",
                MediaType = BookMediaType.Ebook
            };

            var remoteAudiobookSeries = CreateRemoteSeries(
                title: audiobookSeries.Title,
                goodreadsSeriesId: audiobookSeries.GoodreadsSeriesId,
                mediaType: BookMediaType.Audiobook,
                books: new[] { audiobookBook });

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Books = new List<Book> { audiobookBook };

            var seriesService = new StubSeriesService
            {
                ReturnGetByAuthorIdResults = false
            };
            seriesService.SeriesByAuthor.Add(audiobookSeries);
            seriesService.SeriesByAuthor.Add(ebookSeries);

            var linkService = new StubSeriesBookLinkService();
            linkService.SetLinks(audiobookSeries.Id);

            var sut = new TestableRefreshSeriesService(
                bookService,
                seriesService,
                linkService,
                new RefreshSeriesBookLinkService(linkService, LogManager.GetCurrentClassLogger()),
                LogManager.GetCurrentClassLogger());

            var updated = sut.RefreshSeriesInfo(29, new List<Series> { remoteAudiobookSeries }, new Author { Id = 29 }, false, false, null);

            Assert.Multiple(() =>
            {
                Assert.That(updated, Is.True);
                Assert.That(seriesService.Inserted, Is.Empty);
                Assert.That(seriesService.Updated.Select(s => s.Id), Does.Not.Contain(ebookSeries.Id));
                Assert.That(seriesService.DeletedIds, Is.Empty);
                Assert.That(linkService.Inserted.Select(l => l.BookId), Is.EqualTo(new[] { audiobookBook.Id }));
                Assert.That(linkService.GetLinksBySeries(audiobookSeries.Id).Select(l => l.BookId), Is.EqualTo(new[] { audiobookBook.Id }));
            });
        }

        [Test]
        public void should_not_prune_existing_series_when_remote_has_no_goodreads_series()
        {
            var existingSeries = new Series
            {
                Id = 222,
                Title = "Existing",
                GoodreadsSeriesId = "gr:existing-series",
                MediaType = BookMediaType.Audiobook
            };

            var remoteAmazonOnly = new Series
            {
                Title = "Amazon Only",
                GoodreadsSeriesId = null,
                AmazonSeriesAsin = "az:B00TEST",
                MediaType = BookMediaType.Audiobook
            };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var seriesService = new StubSeriesService();
            seriesService.SeriesByAuthor.Add(existingSeries);

            var linkService = new StubSeriesBookLinkService();
            var sut = new TestableRefreshSeriesService(
                bookService,
                seriesService,
                linkService,
                new RefreshSeriesBookLinkService(linkService, LogManager.GetCurrentClassLogger()),
                LogManager.GetCurrentClassLogger());

            var updated = sut.RefreshSeriesInfo(29, new List<Series> { remoteAmazonOnly }, new Author { Id = 29 }, false, false, null);

            Assert.Multiple(() =>
            {
                Assert.That(updated, Is.False);
                Assert.That(seriesService.DeletedIds, Is.Empty);
                Assert.That(seriesService.SeriesByAuthor.Select(s => s.Id), Contains.Item(existingSeries.Id));
                Assert.That(seriesService.Inserted, Is.Empty);
            });
        }

        [Test]
        public void should_add_links_for_all_matching_local_book_copies()
        {
	            var firstCopy = new Book
	            {
	                Id = 601,
	                AuthorId = 29,
	                Title = "Copy A",
	                MediaType = BookMediaType.Audiobook,
	                GoodreadsWorkId = "gr:work-1"
	            };

	            var secondCopy = new Book
	            {
	                Id = 602,
	                AuthorId = 29,
	                Title = "Copy B",
	                MediaType = BookMediaType.Audiobook,
	                GoodreadsWorkId = "gr:work-1"
	            };

            var existingSeries = new Series
            {
                Id = 333,
                Title = "Test Series",
                GoodreadsSeriesId = "gr:series-1",
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorId = null
            };

            var remoteSeries = CreateRemoteSeriesWithSeriesBooks(
                title: existingSeries.Title,
                goodreadsSeriesId: existingSeries.GoodreadsSeriesId,
                mediaType: BookMediaType.Audiobook,
                books: new[]
                {
                    new SeriesBook
                    {
                        Title = "Work 1",
                        BookId = "gr:work-1",
                        Position = "1"
                    }
                });

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Books = new List<Book> { firstCopy, secondCopy };

            var seriesService = new StubSeriesService();
            seriesService.SeriesByAuthor.Add(existingSeries);

            var linkService = new StubSeriesBookLinkService();
            linkService.SetLinks(existingSeries.Id);

            var sut = new TestableRefreshSeriesService(
                bookService,
                seriesService,
                linkService,
                new RefreshSeriesBookLinkService(linkService, LogManager.GetCurrentClassLogger()),
                LogManager.GetCurrentClassLogger());

            var updated = sut.RefreshSeriesInfo(29, new List<Series> { remoteSeries }, new Author { Id = 29 }, false, false, null);

            Assert.Multiple(() =>
            {
                Assert.That(updated, Is.True);
                Assert.That(linkService.Inserted.Select(x => x.BookId).OrderBy(x => x), Is.EqualTo(new[] { firstCopy.Id, secondCopy.Id }));
                Assert.That(linkService.GetLinksBySeries(existingSeries.Id).Select(x => x.BookId).OrderBy(x => x), Is.EqualTo(new[] { firstCopy.Id, secondCopy.Id }));
            });
        }

        [Test]
        public void should_remove_claimed_book_links_from_original_series_during_refresh()
        {
	            var keptCopy = new Book
	            {
	                Id = 701,
	                AuthorId = 29,
	                Title = "Kept Copy",
	                MediaType = BookMediaType.Audiobook,
	                GoodreadsWorkId = "gr:work-1"
	            };

	            var claimedCopy = new Book
	            {
	                Id = 702,
	                AuthorId = 29,
	                Title = "Claimed Copy",
	                MediaType = BookMediaType.Audiobook,
	                GoodreadsWorkId = "gr:work-1"
	            };

            var existingSeries = new Series
            {
                Id = 444,
                Title = "Test Series",
                GoodreadsSeriesId = "gr:series-1",
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorId = null
            };

            var remoteSeries = CreateRemoteSeriesWithSeriesBooks(
                title: existingSeries.Title,
                goodreadsSeriesId: existingSeries.GoodreadsSeriesId,
                mediaType: BookMediaType.Audiobook,
                books: new[]
                {
                    new SeriesBook { Title = "Work 1", BookId = "gr:work-1", Position = "1" }
                });

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Books = new List<Book> { keptCopy, claimedCopy };

            var seriesService = new StubSeriesService();
            seriesService.SeriesByAuthor.Add(existingSeries);

            var linkService = new StubSeriesBookLinkService
            {
                ClaimedBookIds = new HashSet<int> { claimedCopy.Id }
            };

            // Existing series initially has links for both copies.
            linkService.SetLinks(existingSeries.Id,
                new SeriesBookLink
                {
                    SeriesId = existingSeries.Id,
                    BookId = keptCopy.Id,
                    Position = "1",
                    SeriesPosition = 1,
                    IsPrimary = true,
                    SeriesInstanceType = "original",
                    Book = new LazyLoaded<Book>(keptCopy)
                },
                new SeriesBookLink
                {
                    SeriesId = existingSeries.Id,
                    BookId = claimedCopy.Id,
                    Position = "1",
                    SeriesPosition = 1,
                    IsPrimary = true,
                    SeriesInstanceType = "original",
                    Book = new LazyLoaded<Book>(claimedCopy)
                });

            var sut = new TestableRefreshSeriesService(
                bookService,
                seriesService,
                linkService,
                new RefreshSeriesBookLinkService(linkService, LogManager.GetCurrentClassLogger()),
                LogManager.GetCurrentClassLogger());

            var updated = sut.RefreshSeriesInfo(29, new List<Series> { remoteSeries }, new Author { Id = 29 }, false, false, null);

            Assert.Multiple(() =>
            {
                Assert.That(updated, Is.True);
                Assert.That(linkService.Deleted.Select(x => x.BookId), Contains.Item(claimedCopy.Id));
                Assert.That(linkService.GetLinksBySeries(existingSeries.Id).Select(x => x.BookId), Is.EqualTo(new[] { keptCopy.Id }));
            });
        }

        private static Series CreateRemoteSeries(string title, string goodreadsSeriesId, BookMediaType mediaType, params Book[] books)
        {
            var series = new Series
            {
                Title = title,
                GoodreadsSeriesId = goodreadsSeriesId,
                MediaType = mediaType
            };

            series.LinkItems = books.Select((book, index) => new SeriesBookLink
            {
                Position = (index + 1).ToString(),
                SeriesPosition = index + 1,
                BookId = book.Id,
                IsPrimary = true,
                SeriesInstanceType = "original",
                Book = new LazyLoaded<Book>(book)
            }).ToList();

            return series;
        }

        private static Series CreateRemoteSeriesWithSeriesBooks(string title, string goodreadsSeriesId, BookMediaType mediaType, params SeriesBook[] books)
        {
            return new Series
            {
                Title = title,
                GoodreadsSeriesId = goodreadsSeriesId,
                MediaType = mediaType,
                SeriesBooks = books.ToList()
            };
        }
    }
}

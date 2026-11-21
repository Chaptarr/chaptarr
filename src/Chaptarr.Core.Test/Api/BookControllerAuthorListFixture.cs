using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookControllerAuthorListFixture
    {
        private sealed class UnloadedFileList : LazyLoaded<List<BookFile>>
        {
            public override void LazyLoad()
            {
                throw new AssertionException("A library response must reuse batched statistics instead of lazily reading files per book.");
            }
        }

        private class ServiceProxy<T> : DispatchProxy where T : class
        {
            public Dictionary<string, Func<MethodInfo, object[], object>> Handlers { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (Handlers.TryGetValue(targetMethod.Name, out var handler))
                {
                    return handler(targetMethod, args);
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod.Name}");
            }
        }

        private static T Proxy<T>(Dictionary<string, Func<MethodInfo, object[], object>> handlers) where T : class
        {
            var proxy = DispatchProxy.Create<T, ServiceProxy<T>>();
            ((ServiceProxy<T>)(object)proxy).Handlers = handlers;
            return proxy;
        }

        private static T ThrowingProxy<T>() where T : class
        {
            return Proxy<T>(new Dictionary<string, Func<MethodInfo, object[], object>>());
        }

        private static BookController CreateController(
            Dictionary<string, Func<MethodInfo, object[], object>> bookHandlers = null,
            Dictionary<string, Func<MethodInfo, object[], object>> authorHandlers = null,
            Func<MethodInfo, object[], object> authorStatisticsHandler = null,
            Func<MethodInfo, object[], object> seriesLinksHandler = null)
        {
            bookHandlers ??= new Dictionary<string, Func<MethodInfo, object[], object>>();
            authorHandlers ??= new Dictionary<string, Func<MethodInfo, object[], object>>();
            authorStatisticsHandler ??= (method, _) =>
                method.ReturnType == typeof(AuthorStatistics)
                    ? new AuthorStatistics()
                    : new List<AuthorStatistics>();
            seriesLinksHandler ??= (_, _) => new List<SeriesBookLink>();

            if (!authorHandlers.ContainsKey(nameof(IAuthorService.GetAuthor)))
            {
                authorHandlers[nameof(IAuthorService.GetAuthor)] = (_, args) => new Author
                {
                    Id = (int)args[0],
                    Name = "Test Author",
                    SortNameLastFirst = "Author, Test"
                };
            }

            if (!authorHandlers.ContainsKey(nameof(IAuthorService.GetAllAuthors)))
            {
                authorHandlers[nameof(IAuthorService.GetAllAuthors)] = (_, _) => new List<Author>();
            }

            return new BookController(
                authorService: Proxy<IAuthorService>(authorHandlers),
                bookService: Proxy<IBookService>(bookHandlers),
                addBookService: null,
                editionService: Proxy<IEditionService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IEditionService.GetEditionsByAuthor)] = (_, _) => new List<Edition>(),
                    [nameof(IEditionService.GetEditionsByBook)] = (_, _) => new List<Edition>()
                }),
                editionSelector: null,
                seriesBookLinkService: Proxy<ISeriesBookLinkService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(ISeriesBookLinkService.GetLinksByBook)] = seriesLinksHandler
                }),
                authorStatisticsService: Proxy<IAuthorStatisticsService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IAuthorStatisticsService.AuthorStatistics)] = authorStatisticsHandler
                }),
                mediaFileService: null,
                coverMapper: Proxy<IMapCoversToLocal>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IMapCoversToLocal.ConvertToLocalUrls)] = (_, _) => null,
                    [nameof(IMapCoversToLocal.ConvertBookCoversToLocalUrls)] = (_, _) => null
                }),
                upgradableSpecification: ThrowingProxy<IUpgradableSpecification>(),
                signalRBroadcaster: null,
                commandQueueManager: ThrowingProxy<IManageCommandQueue>(),
                eventAggregator: ThrowingProxy<IEventAggregator>(),
                rootFolderService: ThrowingProxy<IRootFolderService>(),
                qualityProfileExistsValidator: new QualityProfileExistsValidator(new TestQualityProfileService()),
                metadataProfileExistsValidator: new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                logger: LogManager.GetCurrentClassLogger());
        }

        [TestCase("audiobook")]
        [TestCase("ebook")]
        public void library_filter_should_reach_pages_buckets_and_ids_with_the_selected_media(string mediaType)
        {
            var calls = new List<string>();
            object Handle(MethodInfo method, object[] args)
            {
                var parameters = method.GetParameters();
                Assert.That(args[Array.FindIndex(parameters, parameter => parameter.Name == "mediaType")], Is.EqualTo(mediaType));
                Assert.That(args[Array.FindIndex(parameters, parameter => parameter.Name == "library")], Is.True);
                calls.Add(method.Name);
                if (method.Name == nameof(IBookService.GetBooksPaged))
                {
                    return new PagedBookResource { Records = new List<Book>() };
                }

                return method.Name == nameof(IBookService.GetBookIds)
                    ? new List<int>()
                    : new BookBucketResource();
            }

            var controller = CreateController(bookHandlers: new Dictionary<string, Func<MethodInfo, object[], object>>
            {
                [nameof(IBookService.GetBooksPaged)] = Handle,
                [nameof(IBookService.GetBookBuckets)] = Handle,
                [nameof(IBookService.GetBookIds)] = Handle
            });
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            Assert.That(controller.GetBooksPaged(mediaType: mediaType, library: true).Result, Is.TypeOf<OkObjectResult>());
            Assert.That(controller.GetBookBuckets(mediaType: mediaType, library: true).Result, Is.TypeOf<OkObjectResult>());
            Assert.That(controller.GetBookIds(mediaType: mediaType, library: true).Result, Is.TypeOf<OkObjectResult>());
            Assert.That(calls, Has.Count.EqualTo(3));
        }

        [Test]
        public void author_book_list_should_not_reapply_metadata_profile_filter_to_local_rows()
        {
            var author = new Author
            {
                Id = 42,
                Name = "Author Name",
                SortNameLastFirst = "Name, Author",
                EbookMetadataProfileId = 7
            };

            var books = new List<Book>
            {
                new Book
                {
                    Id = 100,
                    AuthorId = author.Id,
                    Title = "Visible Book One",
                    TitleSlug = "visible-book-one",
                    MediaType = BookMediaType.Ebook,
                    EbookMonitored = true,
                    AnyEditionOk = true,
                    Ratings = new Ratings()
                },
                new Book
                {
                    Id = 101,
                    AuthorId = author.Id,
                    Title = "Visible Book Two",
                    TitleSlug = "visible-book-two",
                    MediaType = BookMediaType.Ebook,
                    EbookMonitored = false,
                    AnyEditionOk = true,
                    Ratings = new Ratings()
                }
            };

            var controller = new BookController(
                authorService: Proxy<IAuthorService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IAuthorService.GetAuthor)] = (_, _) => author
                }),
                bookService: Proxy<IBookService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IBookService.GetBooksForDisplay)] = (_, args) =>
                    {
                        Assert.That(args[0], Is.EqualTo(author.Id));
                        Assert.That(args[1], Is.EqualTo("ebook"));
                        return books;
                    }
                }),
                addBookService: null,
                editionService: Proxy<IEditionService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IEditionService.GetEditionsByAuthor)] = (_, _) => new List<Edition>()
                }),
                editionSelector: null,
                seriesBookLinkService: Proxy<ISeriesBookLinkService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(ISeriesBookLinkService.GetLinksByBook)] = (_, _) => new List<SeriesBookLink>()
                }),
                authorStatisticsService: Proxy<IAuthorStatisticsService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IAuthorStatisticsService.AuthorStatistics)] = (method, args) =>
                    {
                        Assert.That(method.ReturnType, Is.EqualTo(typeof(AuthorStatistics)));
                        Assert.That(args[0], Is.EqualTo(author.Id));
                        return new AuthorStatistics
                        {
                            AuthorId = author.Id
                        };
                    }
                }),
                mediaFileService: null,
                coverMapper: Proxy<IMapCoversToLocal>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IMapCoversToLocal.ConvertToLocalUrls)] = (_, _) => null,
                    [nameof(IMapCoversToLocal.ConvertBookCoversToLocalUrls)] = (_, _) => null
                }),
                upgradableSpecification: ThrowingProxy<IUpgradableSpecification>(),
                signalRBroadcaster: null,
                commandQueueManager: ThrowingProxy<IManageCommandQueue>(),
                eventAggregator: ThrowingProxy<IEventAggregator>(),
                rootFolderService: ThrowingProxy<IRootFolderService>(),
                qualityProfileExistsValidator: new QualityProfileExistsValidator(new TestQualityProfileService()),
                metadataProfileExistsValidator: new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                logger: LogManager.GetCurrentClassLogger());

            var resources = controller.GetBooks(
                authorId: author.Id,
                bookIds: new List<int>(),
                bookId: null,
                mediaType: "ebook");

            Assert.That(resources.Select(x => x.Id), Is.EquivalentTo(new[] { 100, 101 }));
        }

        [Test]
        public void book_put_should_monitor_requested_row_without_enabling_its_author_side(
            [Values("audiobook", "ebook")] string mediaType,
            [Values(false, true)] bool initialScan,
            [Values(false, true)] bool alreadySelected,
            [Values(false, true)] bool facade)
        {
            var author = new Author
            {
                Id = 42,
                Name = "Susanna Clarke",
                AudiobookMonitored = false,
                EbookMonitored = false,
                SyncMonitoredAcrossFormats = true,
                AudiobookRootFolderPath = "/audiobooks",
                EbookRootFolderPath = "/ebooks",
                AddOptions = initialScan ? new AddAuthorOptions() : null
            };
            var book = new Book
            {
                Id = 100,
                AuthorId = author.Id,
                Author = author,
                Title = "Piranesi",
                TitleSlug = "piranesi",
                CleanTitle = "piranesi",
                MediaType = mediaType == "audiobook" ? BookMediaType.Audiobook : BookMediaType.Ebook,
                AudiobookMonitored = false,
                EbookMonitored = false,
                AnyEditionOk = true,
                Ratings = new Ratings(),
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 501,
                        BookId = 100,
                        Title = "Piranesi",
                        ForeignEditionId = "hc:edition:501-audiobook",
                        Monitored = true
                    }
                }
            };

            book.SetMonitored(alreadySelected);

            var authorMutationCount = 0;
            var scopedBookUpdateCount = 0;
            var genericBookUpdateCount = 0;
            var bookService = Proxy<IBookService>(new Dictionary<string, Func<MethodInfo, object[], object>>
            {
                [nameof(IBookService.GetBook)] = (_, _) => book,
                [nameof(IBookService.MonitorRequestedBooks)] = (_, args) =>
                {
                    Assert.That((IEnumerable<int>)args[0], Is.EquivalentTo(new[] { book.Id }));
                    Assert.That(args[1], Is.EqualTo(mediaType));
                    book.SetMonitoredForMediaType(mediaType, true);
                    scopedBookUpdateCount++;
                    return null;
                },
                [nameof(IBookService.UpdateBook)] = (_, args) =>
                {
                    genericBookUpdateCount++;
                    if (facade)
                    {
                        Assert.That(((Book)args[0]).AddOptions.AddType, Is.EqualTo(BookAddType.Manual));
                        Assert.That(((Book)args[0]).IsMonitored(), Is.EqualTo(alreadySelected));
                        Assert.That(args[2], Is.EqualTo(mediaType));
                        book.SetMonitoredForMediaType(mediaType, true);
                        scopedBookUpdateCount++;
                    }
                    return args[0];
                }
            });
            var controller = new BookController(
                authorService: Proxy<IAuthorService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IAuthorService.GetAuthor)] = (_, _) => author,
                    [nameof(IAuthorService.UpdateAuthor)] = (_, args) =>
                    {
                        authorMutationCount++;
                        return args[0];
                    },
                    [nameof(IAuthorService.SetMediaTypeMonitoring)] = (_, _) =>
                    {
                        authorMutationCount++;
                        return null;
                    }
                }),
                bookService: bookService,
                addBookService: null,
                editionService: Proxy<IEditionService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IEditionService.UpdateMany)] = (_, _) => null
                }),
                editionSelector: Proxy<IEditionSelector>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IEditionSelector.EnsureSingleMonitoredEdition)] = (_, _) => false
                }),
                seriesBookLinkService: ThrowingProxy<ISeriesBookLinkService>(),
                authorStatisticsService: Proxy<IAuthorStatisticsService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IAuthorStatisticsService.AuthorStatistics)] = (method, _) =>
                        method.ReturnType == typeof(AuthorStatistics)
                            ? new AuthorStatistics()
                            : new List<AuthorStatistics>()
                }),
                mediaFileService: null,
                coverMapper: Proxy<IMapCoversToLocal>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IMapCoversToLocal.ConvertToLocalUrls)] = (_, _) => null,
                    [nameof(IMapCoversToLocal.ConvertBookCoversToLocalUrls)] = (_, _) => null
                }),
                upgradableSpecification: ThrowingProxy<IUpgradableSpecification>(),
                signalRBroadcaster: Proxy<IBroadcastSignalRMessage>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    ["get_IsConnected"] = (_, _) => false
                }),
                commandQueueManager: ThrowingProxy<IManageCommandQueue>(),
                eventAggregator: ThrowingProxy<IEventAggregator>(),
                rootFolderService: ThrowingProxy<IRootFolderService>(),
                qualityProfileExistsValidator: new QualityProfileExistsValidator(new TestQualityProfileService()),
                metadataProfileExistsValidator: new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                logger: LogManager.GetCurrentClassLogger());
            var httpContext = new DefaultHttpContext();
            if (facade)
            {
                httpContext.Items[ReadarrFacadeContext.ItemKey] =
                    new ReadarrFacadeContext("hc", mediaType, "/" + mediaType);
            }

            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var result = controller.UpdateBookByBody(new BookResource
            {
                Id = book.Id,
                Monitored = true,
                AudiobookMonitored = mediaType == "audiobook" ? true : null,
                EbookMonitored = mediaType == "ebook" ? true : null,
                AnyEditionOk = true,
                TitleSlug = book.TitleSlug,
                Editions = new List<EditionResource>
                {
                    new EditionResource
                    {
                        Id = 501,
                        BookId = book.Id,
                        Title = "Piranesi",
                        ForeignEditionId = facade ? "501" : "hc:501",
                        Monitored = true
                    }
                }
            });

            var accepted = result.Result as AcceptedAtActionResult;
            var resource = accepted?.Value as BookResource;
            Assert.Multiple(() =>
            {
                Assert.That(authorMutationCount, Is.Zero);
                Assert.That(scopedBookUpdateCount, Is.EqualTo(facade ? 1 : 0));
                Assert.That(genericBookUpdateCount, Is.EqualTo(1));
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(author.EbookMonitored, Is.False);
                Assert.That(author.SyncMonitoredAcrossFormats, Is.True);
                Assert.That(book.IsMonitoredForMediaType(mediaType), Is.True);
                Assert.That(book.IsMonitoredForMediaType(mediaType == "audiobook" ? "ebook" : "audiobook"), Is.False);
                Assert.That(resource?.Monitored, Is.True);
            });
        }

        [Test]
        public void single_book_query_should_use_scoped_statistics_and_preserve_resource_shape()
        {
            const int authorId = 42;
            const int bookId = 49184;
            var author = new Author
            {
                Id = authorId,
                Name = "Shirtaloon",
                SortNameLastFirst = "Shirtaloon"
            };
            var series = new Series
            {
                Id = 7,
                Title = "He Who Fights with Monsters",
                MediaType = BookMediaType.Audiobook,
                GoodreadsSeriesId = "gr:7000000"
            };
            var book = new Book
            {
                Id = bookId,
                AuthorId = authorId,
                Author = author,
                Title = "He Who Fights with Monsters 1",
                TitleSlug = "he-who-fights-with-monsters-1",
                HardcoverBookId = "hc:2371526",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true,
                Ratings = new Ratings(),
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 700,
                        BookId = bookId,
                        Monitored = true,
                        Title = "He Who Fights with Monsters: A LitRPG Adventure",
                        NarratorNames = new List<string> { "Heath Miller" }
                    }
                }
            };
            var statistics = new AuthorStatistics
            {
                AuthorId = authorId,
                BookStatistics = new List<BookStatistics>
                {
                    new BookStatistics
                    {
                        AuthorId = authorId,
                        BookId = bookId,
                        BookFileCount = 2,
                        BookCount = 1,
                        AvailableBookCount = 1,
                        TotalBookCount = 1,
                        SizeOnDisk = 2048
                    }
                }
            };
            var globalCalls = 0;
            var scopedCalls = 0;
            var controller = CreateController(
                bookHandlers: new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IBookService.GetBook)] = (_, _) => book
                },
                authorHandlers: new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IAuthorService.GetAuthor)] = (_, _) => author
                },
                authorStatisticsHandler: (method, args) =>
                {
                    if (method.ReturnType == typeof(AuthorStatistics))
                    {
                        scopedCalls++;
                        Assert.That(args[0], Is.EqualTo(authorId));
                        return statistics;
                    }

                    globalCalls++;
                    return new List<AuthorStatistics>();
                },
                seriesLinksHandler: (_, _) => new List<SeriesBookLink>
                {
                    new SeriesBookLink
                    {
                        BookId = bookId,
                        SeriesId = series.Id,
                        Position = "1",
                        Series = series
                    }
                });
            var httpContext = new DefaultHttpContext();
            httpContext.Items[ReadarrFacadeContext.ItemKey] =
                new ReadarrFacadeContext("hc", "audiobook", "/audiobook");
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var resource = controller.GetBooks(
                authorId: null,
                bookIds: new List<int>(),
                bookId: bookId.ToString()).Single();

            Assert.Multiple(() =>
            {
                Assert.That(scopedCalls, Is.EqualTo(1));
                Assert.That(globalCalls, Is.Zero);
                Assert.That(resource.Id, Is.EqualTo(bookId));
                Assert.That(resource.ForeignBookId, Is.EqualTo("2371526"));
                Assert.That(resource.Author, Is.Null);
                Assert.That(resource.MediaType, Is.EqualTo("audiobook"));
                Assert.That(resource.Title, Is.EqualTo("He Who Fights with Monsters: A LitRPG Adventure"));
                Assert.That(resource.SeriesTitle, Is.EqualTo("He Who Fights with Monsters #1"));
                Assert.That(resource.Narrator, Is.EqualTo("Heath Miller"));
                Assert.That(resource.Statistics.BookFileCount, Is.EqualTo(2));
                Assert.That(resource.HasFiles, Is.True);
            });
        }

        [Test]
        public void mixed_author_book_list_should_use_one_batched_statistics_lookup()
        {
            var books = new List<Book>
            {
                new Book { Id = 10, AuthorId = 42, Title = "One", MediaType = BookMediaType.Ebook, AnyEditionOk = true },
                new Book { Id = 20, AuthorId = 43, Title = "Two", MediaType = BookMediaType.Ebook, AnyEditionOk = true }
            };
            var globalCalls = 0;
            var batchCalls = 0;
            var scopedCalls = 0;
            var controller = CreateController(
                bookHandlers: new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IBookService.GetBooks)] = (_, _) => books
                },
                authorStatisticsHandler: (method, args) =>
                {
                    if (method.ReturnType == typeof(AuthorStatistics))
                    {
                        scopedCalls++;
                        return new AuthorStatistics();
                    }

                    if (args.Length == 0)
                    {
                        globalCalls++;
                    }
                    else
                    {
                        batchCalls++;
                        Assert.That((IEnumerable<int>)args[0], Is.EquivalentTo(new[] { 42, 43 }));
                    }
                    return new List<AuthorStatistics>
                    {
                        new AuthorStatistics
                        {
                            AuthorId = 42,
                            BookStatistics = new List<BookStatistics>
                            {
                                new BookStatistics { AuthorId = 42, BookId = 10, BookCount = 1, TotalBookCount = 1 }
                            }
                        },
                        new AuthorStatistics
                        {
                            AuthorId = 43,
                            BookStatistics = new List<BookStatistics>
                            {
                                new BookStatistics { AuthorId = 43, BookId = 20, BookCount = 1, TotalBookCount = 1 }
                            }
                        }
                    };
                });

            var resources = controller.GetBooks(
                authorId: null,
                bookIds: new List<int> { 10, 20 },
                bookId: null);

            Assert.Multiple(() =>
            {
                Assert.That(globalCalls, Is.Zero);
                Assert.That(batchCalls, Is.EqualTo(1));
                Assert.That(scopedCalls, Is.Zero);
                Assert.That(resources.Select(resource => resource.Statistics?.TotalBookCount), Is.EqualTo(new[] { 1, 1 }));
            });
        }

        [Test]
        public void provided_statistics_should_not_trigger_an_additional_lookup()
        {
            const int authorId = 42;
            const int bookId = 10;
            var author = new Author { Id = authorId, Name = "Author", SortNameLastFirst = "Author" };
            var book = new Book
            {
                Id = bookId,
                AuthorId = authorId,
                Author = author,
                Title = "One",
                MediaType = BookMediaType.Ebook,
                AnyEditionOk = true
            };
            var statisticsCalls = 0;
            var controller = CreateController(
                bookHandlers: new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IBookService.GetBooksForDisplay)] = (_, _) => new List<Book> { book }
                },
                authorHandlers: new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IAuthorService.GetAllAuthors)] = (_, _) => new List<Author> { author }
                },
                authorStatisticsHandler: (method, _) =>
                {
                    Assert.That(method.ReturnType, Is.EqualTo(typeof(List<AuthorStatistics>)));
                    statisticsCalls++;
                    return new List<AuthorStatistics>
                    {
                        new AuthorStatistics
                        {
                            AuthorId = authorId,
                            BookStatistics = new List<BookStatistics>
                            {
                                new BookStatistics { AuthorId = authorId, BookId = bookId, BookCount = 1, TotalBookCount = 1 }
                            }
                        }
                    };
                });

            var resources = controller.GetBooks(
                authorId: null,
                bookIds: new List<int>(),
                bookId: null);

            Assert.Multiple(() =>
            {
                Assert.That(statisticsCalls, Is.EqualTo(1));
                Assert.That(resources.Single().Statistics.TotalBookCount, Is.EqualTo(1));
            });
        }

        [TestCase(false, null)]
        [TestCase(false, "ebook")]
        [TestCase(false, "audiobook")]
        [TestCase(true, null)]
        [TestCase(true, "ebook")]
        [TestCase(true, "audiobook")]
        public void library_list_should_preserve_owned_lean_rows_with_one_statistics_lookup(bool authorScope, string mediaType)
        {
            var author = new Author { Id = 42, Name = "Author", SortNameLastFirst = "Author" };
            var audiobook = mediaType == "audiobook";
            var books = Enumerable.Range(1, 4).Select(id => new Book
            {
                Id = id,
                AuthorId = author.Id,
                Title = $"Book {id}",
                MediaType = audiobook ? BookMediaType.Audiobook : BookMediaType.Ebook,
                AudiobookMonitored = audiobook ? id == 2 : id == 1,
                EbookMonitored = audiobook ? id == 1 : id == 2,
                AnyEditionOk = true,
                LazyBookFiles = new UnloadedFileList()
            }).ToList();
            var statisticsCalls = 0;
            var statistics = new AuthorStatistics
            {
                AuthorId = author.Id,
                BookStatistics = books.Select(book => new BookStatistics
                {
                    AuthorId = author.Id,
                    BookId = book.Id,
                    BookFileCount = book.Id == 3 ? 2 : 0,
                    TotalBookCount = 1
                }).ToList()
            };
            var controller = CreateController(
                bookHandlers: new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IBookService.GetBooksForDisplay)] = (_, _) => books
                },
                authorHandlers: new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IAuthorService.GetAuthor)] = (_, _) => author,
                    [nameof(IAuthorService.GetAllAuthors)] = (_, _) => new List<Author> { author }
                },
                authorStatisticsHandler: (method, _) =>
                {
                    statisticsCalls++;
                    return method.ReturnType == typeof(AuthorStatistics) ? statistics : new List<AuthorStatistics> { statistics };
                });
            var resources = controller.GetBooks(authorScope ? author.Id : null, new List<int>(), null, mediaType: mediaType, monitored: true);
            Assert.That(resources.Select(resource => resource.Id), Is.EqualTo(new[] { 2, 3 }));
            Assert.That(resources.Single(resource => resource.Id == 3).HasFiles, Is.True);
            Assert.That(statisticsCalls, Is.EqualTo(1), "Filtering and mapping must reuse one complete statistics batch.");
        }

        [TestCase("ids")]
        [TestCase("single")]
        [TestCase("author")]
        [TestCase("slug")]
        public void explicit_library_queries_should_reuse_one_statistics_batch_without_lazy_file_reads(string route)
        {
            var books = Enumerable.Range(1, 4).Select(id => new Book
            {
                Id = id,
                AuthorId = 42,
                Title = $"Book {id}",
                MediaType = BookMediaType.Ebook,
                EbookMonitored = id == 2,
                AudiobookMonitored = id == 1,
                AnyEditionOk = true,
                LazyBookFiles = new UnloadedFileList()
            }).ToList();
            var statisticsCalls = 0;
            var controller = CreateController(
                bookHandlers: new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IBookService.GetBook)] = (_, _) => books[2],
                    [nameof(IBookService.GetBooks)] = (_, _) => books,
                    [nameof(IBookService.GetBooksByAuthor)] = (_, _) => books
                },
                authorStatisticsHandler: (method, _) =>
                {
                    statisticsCalls++;
                    Assert.That(method.ReturnType, Is.EqualTo(typeof(AuthorStatistics)));
                    return new AuthorStatistics
                    {
                        AuthorId = 42,
                        BookStatistics = books.Select(book => new BookStatistics
                        {
                            AuthorId = 42,
                            BookId = book.Id,
                            BookFileCount = book.Id == 3 ? 2 : 0
                        }).ToList()
                    };
                });
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            var resources = controller.GetBooks(null, route == "ids" ? new List<int> { 1, 2, 3, 4 } : new List<int>(),
                route == "single" || route == "author" ? "3" : null,
                titleSlug: route == "slug" ? "3" : null, includeAllAuthorBooks: route == "author", monitored: true);
            Assert.That(resources.Select(resource => resource.Id), Is.EqualTo(route == "ids" || route == "author" ? new[] { 2, 3 } : new[] { 3 }));
            Assert.That(statisticsCalls, Is.EqualTo(1));
            Assert.That(resources.Single(resource => resource.Id == 3).HasFiles, Is.True);
        }

        [Test]
        public void empty_author_book_list_should_not_lookup_statistics()
        {
            var controller = CreateController(
                bookHandlers: new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IBookService.GetBooksForDisplay)] = (_, _) => new List<Book>()
                },
                authorStatisticsHandler: (_, _) => throw new AssertionException("An empty author list must not query statistics."));
            Assert.That(controller.GetBooks(42, new List<int>(), null, monitored: true), Is.Empty);
        }

        [Test]
        public void empty_book_batch_should_not_lookup_statistics()
        {
            var statisticsCalls = 0;
            var controller = CreateController(
                bookHandlers: new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IBookService.GetBook)] = (_, _) => null
                },
                authorStatisticsHandler: (method, _) =>
                {
                    statisticsCalls++;
                    return method.ReturnType == typeof(AuthorStatistics)
                        ? new AuthorStatistics()
                        : new List<AuthorStatistics>();
                },
                seriesLinksHandler: (_, _) => throw new AssertionException("empty mapping must not load series links"));

            var resources = controller.GetBooks(
                authorId: null,
                bookIds: new List<int>(),
                bookId: "49184");

            Assert.Multiple(() =>
            {
                Assert.That(resources, Is.Empty);
                Assert.That(statisticsCalls, Is.Zero);
            });
        }
    }
}

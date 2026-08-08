using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ParsingServiceMapFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class DelegateProxy<T> : DispatchProxy where T : class
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (Handler != null)
                {
                    return Handler(targetMethod, args);
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private static T CreateProxy<T>(Func<MethodInfo, object[], object> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, DelegateProxy<T>>();
            ((DelegateProxy<T>)(object)proxy).Handler = handler;
            return proxy;
        }

        [Test]
        public void should_not_throw_when_mapping_with_invalid_author_and_book_ids()
        {
            var service = new ParsingService(
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                DispatchProxy.Create<IAudioTagService, ThrowingProxy<IAudioTagService>>(),
                LogManager.GetCurrentClassLogger());

            var parsedBookInfo = new ParsedBookInfo
            {
                AuthorName = "Travis Beacham",
                BookTitle = "Impact Winter"
            };

            var remoteBook = service.Map(parsedBookInfo, authorId: 0, bookIds: new[] { 0, -1 });

            Assert.That(remoteBook, Is.Not.Null);
            Assert.That(remoteBook.Author, Is.Null);
            Assert.That(remoteBook.Books, Is.Not.Null);
            Assert.That(remoteBook.Books, Is.Empty);
        }


        [Test]
        public void should_use_search_criteria_author_when_parsed_author_contains_requested_author_among_coauthors()
        {
            var service = new ParsingService(
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                DispatchProxy.Create<IAudioTagService, ThrowingProxy<IAudioTagService>>(),
                LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Id = 38,
                Name = "Brian Herbert",
                CleanName = "brianherbert"
            };

            var book = new Book
            {
                Id = 501,
                AuthorId = author.Id,
                Title = "House Harkonnen",
                CleanTitle = "househarkonnen"
            };

            var parsedBookInfo = new ParsedBookInfo
            {
                AuthorName = "Kevin J Anderson, Brian Herbert",
                BookTitle = "House Harkonnen"
            };

            var criteria = new NzbDrone.Core.IndexerSearch.Definitions.BookSearchCriteria
            {
                Author = author,
                Books = new System.Collections.Generic.List<Book> { book }
            };

            var remoteBook = service.Map(parsedBookInfo, criteria);

            Assert.That(remoteBook.Author, Is.SameAs(author));
            Assert.That(remoteBook.Books, Has.Count.EqualTo(1));
            Assert.That(remoteBook.Books[0], Is.SameAs(book));
        }

        [Test]
        public void should_prefer_ebook_book_when_exact_title_match_initially_returns_audiobook()
        {
            var author = new Author
            {
                Id = 1235,
                Name = "Che Yeun",
                CleanName = "cheyeun"
            };

            var audiobookBook = new Book
            {
                Id = 10,
                AuthorId = author.Id,
                Title = "Tailbone",
                CleanTitle = "tailbone",
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true,
                EbookMonitored = false
            };

            var ebookBook = new Book
            {
                Id = 11,
                AuthorId = author.Id,
                Title = "Tailbone",
                CleanTitle = "tailbone",
                MediaType = BookMediaType.Ebook,
                AudiobookMonitored = false,
                EbookMonitored = true
            };

            var authorService = CreateProxy<IAuthorService>((method, args) =>
            {
                if (method.Name == nameof(IAuthorService.FindByName) && (string)args[0] == "Che Yeun")
                {
                    return author;
                }

                if (method.Name == nameof(IAuthorService.FindByNameInexact))
                {
                    return null;
                }

                throw new NotImplementedException($"Unhandled {nameof(IAuthorService)}.{method.Name}");
            });

            var bookService = CreateProxy<IBookService>((method, args) =>
            {
                if (method.Name == nameof(IBookService.FindByTitle))
                {
                    var title = (string)args[1];

                    if (title == "Tailbone")
                    {
                        return audiobookBook;
                    }

                    return null;
                }

                if (method.Name == nameof(IBookService.GetBooksByAuthorId))
                {
                    return new List<Book> { audiobookBook, ebookBook };
                }

                if (method.Name == nameof(IBookService.FindByTitleInexact))
                {
                    return null;
                }

                if (method.Name == nameof(IBookService.GetCandidates))
                {
                    return new List<Book>();
                }

                throw new NotImplementedException($"Unhandled {nameof(IBookService)}.{method.Name}");
            });

            var editionService = CreateProxy<IEditionService>((method, args) =>
            {
                if (method.Name == nameof(IEditionService.FindByTitle) ||
                    method.Name == nameof(IEditionService.FindByTitleInexact))
                {
                    return null;
                }

                if (method.Name == nameof(IEditionService.GetCandidates) ||
                    method.Name == nameof(IEditionService.GetEditionsByAuthor))
                {
                    return new List<Edition>();
                }

                throw new NotImplementedException($"Unhandled {nameof(IEditionService)}.{method.Name}");
            });

            var service = new ParsingService(
                authorService,
                bookService,
                editionService,
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                DispatchProxy.Create<IAudioTagService, ThrowingProxy<IAudioTagService>>(),
                LogManager.GetCurrentClassLogger());

            var parsedBookInfo = new ParsedBookInfo
            {
                AuthorName = "Che Yeun",
                BookTitle = "Tailbone: A Novel",
                Quality = new QualityModel(Quality.EPUB, new Revision(1))
            };

            var remoteBook = service.Map(parsedBookInfo);

            Assert.That(remoteBook.Author, Is.SameAs(author));
            Assert.That(remoteBook.Books, Has.Count.EqualTo(1));
            Assert.That(remoteBook.Books[0], Is.SameAs(ebookBook));
            Assert.That(remoteBook.Books[0].EbookMonitored, Is.True);
        }
    }
}

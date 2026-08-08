using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.Books
{
    /// <summary>
    /// Author.Id is a local database key. It shares a numeric space with every provider's author
    /// ids, so sending it upstream asks the metadata server for whichever author happens to carry
    /// the same digits. An author with no provider identity has nothing to look up.
    /// </summary>
    [TestFixture]
    public class SeriesImportProviderIdentityFixture
    {
        private AuthorInfoProxy _authorInfo;
        private Author _author;

        [SetUp]
        public void SetUp()
        {
            _author = new Author
            {
                Id = 2514,
                Name = "Suzanne Forster"
            };
        }

        [Test]
        public void should_not_request_author_info_for_an_author_without_provider_identity()
        {
            var subject = BuildSubject();

            subject.ProcessSeriesForAuthor(_author.Id);

            Assert.That(_authorInfo.RequestedIds, Is.Empty,
                "an author with no provider identity has nothing to look up upstream");
        }

        [Test]
        public void should_request_author_info_by_provider_id_when_one_is_present()
        {
            _author.GoodreadsAuthorId = "gr:2514";
            var subject = BuildSubject();

            subject.ProcessSeriesForAuthor(_author.Id);

            Assert.That(_authorInfo.RequestedIds, Is.EqualTo(new[] { "gr:2514" }));
        }

        private SeriesImportService BuildSubject()
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Authors = new List<Author> { _author };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Books = new List<Book>
            {
                new Book { Id = 1, AuthorId = _author.Id, Title = "Shameless" }
            };

            var authorInfo = DispatchProxy.Create<IProvideAuthorInfo, AuthorInfoProxy>();
            _authorInfo = (AuthorInfoProxy)(object)authorInfo;

            return new SeriesImportService(
                authorService,
                DispatchProxy.Create<ISeriesService, SeriesServiceProxy>(),
                bookService,
                DispatchProxy.Create<IRefreshSeriesService, ThrowingProxy<IRefreshSeriesService>>(),
                authorInfo,
                DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                LogManager.GetLogger("SeriesImportProviderIdentityFixture"));
        }

        public class AuthorServiceProxy : DispatchProxy
        {
            public List<Author> Authors { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAllAuthors))
                {
                    return Authors;
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }
        }

        public class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    return Books;
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
            }
        }

        public class SeriesServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(ISeriesService.GetByAuthorId))
                {
                    return new List<Series>();
                }

                throw new NotImplementedException($"Test proxy does not implement ISeriesService.{targetMethod?.Name}");
            }
        }

        public class AuthorInfoProxy : DispatchProxy
        {
            public List<string> RequestedIds { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IProvideAuthorInfo.GetAuthorInfo))
                {
                    RequestedIds.Add((string)args[0]);
                    return new Author { Series = new List<Series>() };
                }

                throw new NotImplementedException($"Test proxy does not implement IProvideAuthorInfo.{targetMethod?.Name}");
            }
        }

        public class ThrowingProxy<T> : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ParsingServiceGrabHistoryFixture
    {
        private class AuthorServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthor))
                {
                    return new Author { Id = (int)args[0], Name = "Example Author" };
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<int> RequestedBookIds { get; private set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetExistingBooks))
                {
                    RequestedBookIds = ((IEnumerable<int>)args[0]).ToList();
                    return RequestedBookIds
                        .Where(bookId => bookId != 20)
                        .Select(bookId => new Book
                        {
                            Id = bookId,
                            AuthorId = 5,
                            Title = "Still Existing"
                        })
                        .ToList();
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_drop_stale_grabbed_book_ids_when_mapping_history_context()
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var subject = new ParsingService(authorService, bookService, null, null, null, LogManager.GetCurrentClassLogger());

            var remoteBook = subject.Map(new ParsedBookInfo { BookTitle = "Still Existing" }, 5, new[] { 10, 20 });

            Assert.That(remoteBook.Author.Id, Is.EqualTo(5));
            Assert.That(remoteBook.Books.Select(b => b.Id), Is.EqualTo(new[] { 10 }));
        }
    }
}

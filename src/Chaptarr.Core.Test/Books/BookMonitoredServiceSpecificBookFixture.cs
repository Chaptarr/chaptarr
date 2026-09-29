using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookMonitoredServiceSpecificBookFixture
    {
        [Test]
        public void specific_book_mode_should_leave_the_seeded_rows_unchanged()
        {
            var author = new Author { Id = 99, Name = "Shelf Author" };
            var requested = new Book { Id = 1, AuthorId = 99, MediaType = BookMediaType.Audiobook, AudiobookMonitored = true };
            var sibling = new Book { Id = 2, AuthorId = 99, MediaType = BookMediaType.Audiobook, AudiobookMonitored = false };
            var siblingEbook = new Book { Id = 3, AuthorId = 99, MediaType = BookMediaType.Ebook, EbookMonitored = false };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Books = new List<Book> { requested, sibling, siblingEbook };
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();

            var subject = new BookMonitoredService(authorService, bookService, LogManager.GetCurrentClassLogger());

            subject.SetBookMonitoredStatus(author, new AddAuthorOptions
            {
                Monitor = MonitorTypes.SpecificBook,
                SearchForMissingBooks = true
            });

            Assert.Multiple(() =>
            {
                Assert.That(requested.AudiobookMonitored, Is.True);
                Assert.That(sibling.AudiobookMonitored, Is.False);
                Assert.That(siblingEbook.EbookMonitored, Is.False);
                Assert.That(((AuthorServiceProxy)(object)authorService).Updated, Is.SameAs(author));
            });
        }

        public class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IBookService.GetBooksByAuthor):
                        return Books.ToList();
                    case nameof(IBookService.GetAuthorBooksWithFiles):
                        return new List<Book>();
                    case nameof(IBookService.UpdateManyWithLifecycle):
                        return targetMethod.ReturnType == typeof(void) ? null : Activator.CreateInstance(targetMethod.ReturnType);
                    default:
                        throw new NotImplementedException(targetMethod?.Name);
                }
            }
        }

        public class AuthorServiceProxy : DispatchProxy
        {
            public Author Updated { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.UpdateAuthor))
                {
                    Updated = (Author)args[0];
                    return args[0];
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }
    }
}

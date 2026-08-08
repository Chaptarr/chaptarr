using System;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class ShouldRefreshNullLastInfoSyncFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_refresh_author_when_last_info_sync_is_null()
        {
            var bookService = DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>();
            var subject = new ShouldRefreshAuthor(bookService, LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                LastInfoSync = null
            };

            Assert.That(subject.ShouldRefresh(author), Is.True);
        }

        [Test]
        public void should_refresh_book_when_last_info_sync_is_null()
        {
            var subject = new ShouldRefreshBook(LogManager.GetCurrentClassLogger());

            var book = new Book
            {
                Id = 1,
                Title = "Test Book",
                LastInfoSync = null
            };

            Assert.That(subject.ShouldRefresh(book), Is.True);
        }
    }
}


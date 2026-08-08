using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.IndexerSearch
{
    [TestFixture]
    public class ReleaseSearchServiceCatalogReuseFixture
    {
        [Test]
        public void book_search_should_reuse_the_provided_author_catalog()
        {
            var author = new Author
            {
                Id = 10,
                Name = "Catalog Author",
                AudiobookQualityProfileId = 1
            };
            var book = new Book
            {
                Id = 101,
                Title = "Target Book",
                Author = author,
                MediaType = BookMediaType.Audiobook
            };
            var authorCatalog = new List<Book>
            {
                book,
                new()
                {
                    Id = 102,
                    Title = "Sibling Book",
                    Author = author,
                    MediaType = BookMediaType.Audiobook
                }
            };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var decisionMaker = DispatchProxy.Create<IMakeDownloadDecision, DecisionMakerProxy>();
            var subject = new ReleaseSearchService(
                DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>(),
                bookService,
                authorService,
                null,
                decisionMaker,
                LogManager.GetLogger("ReleaseSearchServiceCatalogReuseFixture"));

            subject.BookSearch(book, authorCatalog, false, true, false).GetAwaiter().GetResult();

            Assert.That(((BookServiceProxy)(object)bookService).CatalogLoads, Is.Zero);
            Assert.That(((AuthorServiceProxy)(object)authorService).AuthorLoads, Is.Zero);
            Assert.That(((DecisionMakerProxy)(object)decisionMaker).AuthorCatalogCount, Is.EqualTo(2));
            Assert.That(author.Books, Is.Null);
        }

        private class BookServiceProxy : DispatchProxy
        {
            public int CatalogLoads { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    CatalogLoads++;
                    throw new AssertionException("The provided author catalog should have been reused");
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public int AuthorLoads { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IAuthorService.GetAuthor))
                {
                    AuthorLoads++;
                    throw new AssertionException("The attached author should have been reused");
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class IndexerFactoryProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IIndexerFactory.AutomaticSearchEnabled) ||
                    targetMethod.Name == nameof(IIndexerFactory.InteractiveSearchEnabled))
                {
                    return new List<IIndexer>();
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class DecisionMakerProxy : DispatchProxy
        {
            public int AuthorCatalogCount { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IMakeDownloadDecision.GetSearchDecision))
                {
                    AuthorCatalogCount = ((SearchCriteriaBase)args[1]).AuthorCatalog?.Count ?? 0;
                    return new List<DownloadDecision>();
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }
    }
}

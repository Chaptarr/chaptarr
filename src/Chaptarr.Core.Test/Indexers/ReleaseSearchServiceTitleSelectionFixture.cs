using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class ReleaseSearchServiceTitleSelectionFixture
    {
        [Test]
        public void should_use_selected_edition_title_when_any_edition_ok()
        {
            var englishEdition = new Edition { Title = "Harry Potter and the Philosopher's Stone", Language = "eng" };
            var frenchEdition = new Edition { Title = "l'Epreuve", Language = "fra" };

            var book = new Book
            {
                AnyEditionOk = true,
                Title = "Harry Potter and the Sorcerer's Stone, Book 1",
                Editions = new List<Edition> { frenchEdition, englishEdition }
            };

            var selected = englishEdition;
            var title = ReleaseSearchService.GetSearchBookTitle(book, selected);

            Assert.That(title, Is.EqualTo("Harry Potter and the Philosopher's Stone"));
        }

        [Test]
        public void should_fallback_to_book_title_when_selected_edition_is_null()
        {
            var book = new Book
            {
                Title = "Alanna: The First Adventure"
            };

            var title = ReleaseSearchService.GetSearchBookTitle(book, null);

            Assert.That(title, Is.EqualTo("Alanna: The First Adventure"));
        }

        [Test]
        public void should_fallback_to_book_title_when_selected_edition_title_is_blank()
        {
            var blankEdition = new Edition { Title = "   " };
            var book = new Book
            {
                Title = "Alanna: The First Adventure",
                Editions = new List<Edition> { blankEdition }
            };

            var title = ReleaseSearchService.GetSearchBookTitle(book, blankEdition);

            Assert.That(title, Is.EqualTo("Alanna: The First Adventure"));
        }

        [Test]
        public void book_query_should_use_main_title_section()
        {
            var criteria = new BookSearchCriteria
            {
                Author = new Author { Name = "Mitch Albom" },
                BookTitle = "Tuesdays with Morrie: An Old Man, a Young Man, and Life's Greatest Lesson"
            };

            Assert.That(criteria.BookQuery, Is.EqualTo("Tuesdays+with+Morrie"));
        }

        [Test]
        public void book_query_should_remove_leading_author_prefix_before_splitting()
        {
            var criteria = new BookSearchCriteria
            {
                Author = new Author { Name = "Mitch Albom" },
                BookTitle = "Mitch Albom: Tuesdays with Morrie: An Old Man, a Young Man, and Life's Greatest Lesson"
            };

            Assert.That(criteria.BookQuery, Is.EqualTo("Tuesdays+with+Morrie"));
        }

        [Test]
        public void book_query_should_strip_marketing_subtitle_from_selected_edition_title()
        {
            var criteria = new BookSearchCriteria
            {
                Author = new Author { Name = "A.F. Kay" },
                BookTitle = "Shade's First Rule: A Fantasy LitRPG Adventure"
            };

            Assert.That(criteria.BookQuery, Is.EqualTo("Shade's+First+Rule"));
        }

        [Test]
        public void book_query_should_strip_parenthetical_production_title()
        {
            var criteria = new BookSearchCriteria
            {
                Author = new Author { Name = "Jim Butcher" },
                BookTitle = "Storm Front (Dramatized Adaptation)"
            };

            Assert.That(criteria.BookQuery, Is.EqualTo("Storm+Front"));
        }

        [TestCase("9780439554930", null, ExpectedResult = "9780439554930")]
        [TestCase(null, "0439554934", ExpectedResult = "0439554934")]
        public async Task<string> book_search_should_propagate_monitored_ebook_edition_isbn(string isbn13, string isbn10)
        {
            var author = new Author
            {
                Id = 18,
                Name = "J.K. Rowling",
                EbookQualityProfileId = 2,
                Books = new List<Book>()
            };
            var monitoredEdition = new Edition
            {
                Id = 7,
                Monitored = true,
                Title = "Harry Potter and the Sorcerer's Stone",
                Isbn13 = isbn13,
                Isbn10 = isbn10
            };
            var book = new Book
            {
                Id = 1625,
                Author = author,
                AuthorId = author.Id,
                Title = "Harry Potter and the Sorcerer's Stone, Book 1",
                MediaType = BookMediaType.Ebook,
                Editions = new List<Edition> { monitoredEdition }
            };
            author.Books.Add(book);

            var indexer = new RecordingIndexer();
            var subject = new ReleaseSearchService(
                new SingleIndexerFactory(indexer),
                DispatchProxy.Create<NzbDrone.Core.Books.IBookService, UnusedBookServiceProxy>(),
                DispatchProxy.Create<NzbDrone.Core.Books.IAuthorService, UnusedAuthorServiceProxy>(),
                null,
                DispatchProxy.Create<IMakeDownloadDecision, EmptyDecisionMakerProxy>(),
                LogManager.GetLogger("ReleaseSearchServiceTitleSelectionFixture"));

            await subject.BookSearch(book, false, true, false);

            return indexer.LastBookSearchCriteria?.BookIsbn;
        }

        [Test]
        public async Task book_search_should_fallback_to_first_suitable_ebook_edition_isbn_when_monitored_edition_lacks_one()
        {
            var author = new Author
            {
                Id = 18,
                Name = "J.K. Rowling",
                EbookQualityProfileId = 2,
                Books = new List<Book>()
            };
            var book = new Book
            {
                Id = 1625,
                Author = author,
                AuthorId = author.Id,
                Title = "Harry Potter and the Philosopher's Stone",
                MediaType = BookMediaType.Ebook,
                Editions = new List<Edition>
                {
                    new()
                    {
                        Id = 1,
                        Monitored = true,
                        Title = "Harry Potter and the Philosopher's Stone",
                        ReadingFormatId = 3
                    },
                    new()
                    {
                        Id = 2,
                        Title = "Physical Edition",
                        ReadingFormatId = 1,
                        Isbn13 = "9780000000001"
                    },
                    new()
                    {
                        Id = 30,
                        Title = "Harry Potter and the Philosopher's Stone",
                        ReadingFormatId = 3,
                        Isbn13 = "9781408865279"
                    },
                    new()
                    {
                        Id = 4,
                        Title = "Audio Edition",
                        ReadingFormatId = 2,
                        Isbn13 = "9789999999999"
                    },
                    new()
                    {
                        Id = 5,
                        Title = "German Ebook",
                        ReadingFormatId = 3,
                        Isbn13 = "9781781100554"
                    }
                }
            };
            author.Books.Add(book);

            var indexer = new RecordingIndexer();
            var subject = new ReleaseSearchService(
                new SingleIndexerFactory(indexer),
                DispatchProxy.Create<NzbDrone.Core.Books.IBookService, UnusedBookServiceProxy>(),
                DispatchProxy.Create<NzbDrone.Core.Books.IAuthorService, UnusedAuthorServiceProxy>(),
                null,
                DispatchProxy.Create<IMakeDownloadDecision, EmptyDecisionMakerProxy>(),
                LogManager.GetLogger("ReleaseSearchServiceTitleSelectionFixture"));

            await subject.BookSearch(book, false, true, false);

            Assert.That(indexer.LastBookSearchCriteria?.BookIsbn, Is.EqualTo("9781408865279"));
        }

        [Test]
        public void get_search_book_isbn_should_match_curly_and_straight_apostrophe_titles_when_monitored_edition_is_unavailable()
        {
            var book = new Book
            {
                Title = "Harry Potter and the Philosopher’s Stone",
                MediaType = BookMediaType.Ebook,
                Editions = new List<Edition>
                {
                    new()
                    {
                        Id = 2,
                        Title = "Harry Potter und der Stein der Weisen",
                        ReadingFormatId = 3,
                        Isbn13 = "9781781100554"
                    },
                    new()
                    {
                        Id = 3,
                        Title = "Harry Potter and the Philosopher's Stone",
                        ReadingFormatId = 3,
                        Isbn13 = "9781781105771"
                    }
                }
            };

            var isbn = ReleaseSearchService.GetSearchBookIsbn(book, null);

            Assert.That(isbn, Is.EqualTo("9781781105771"));
        }

        private class RecordingIndexer : IIndexer
        {
            public BookSearchCriteria LastBookSearchCriteria { get; private set; }

            public bool SupportsRss => false;
            public bool SupportsSearch => true;
            public DownloadProtocol Protocol => DownloadProtocol.Direct;
            public string Name => "Recording Indexer";
            public Type ConfigContract => typeof(object);
            public NzbDrone.Core.ThingiProvider.ProviderMessage Message => null;
            public IEnumerable<NzbDrone.Core.ThingiProvider.ProviderDefinition> DefaultDefinitions => new List<NzbDrone.Core.ThingiProvider.ProviderDefinition>();
            public NzbDrone.Core.ThingiProvider.ProviderDefinition Definition { get; set; } = new IndexerDefinition { Name = "Recording Indexer" };

            public object RequestAction(string action, IDictionary<string, string> query) => null;
            public ValidationResult Test() => new();
            public Task<IList<ReleaseInfo>> FetchRecent() => Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>());

            public Task<IList<ReleaseInfo>> Fetch(BookSearchCriteria searchCriteria)
            {
                LastBookSearchCriteria = searchCriteria;
                return Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>());
            }

            public Task<IList<ReleaseInfo>> Fetch(AuthorSearchCriteria searchCriteria) => Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>());
            public NzbDrone.Common.Http.HttpRequest GetDownloadRequest(string link) => throw new System.NotImplementedException();
            public Task<NzbDrone.Common.Http.HttpResponse> ExecuteDownloadRequestAsync(NzbDrone.Common.Http.HttpRequest request) => throw new System.NotImplementedException();
        }

        private class SingleIndexerFactory : IIndexerFactory
        {
            private readonly IIndexer _indexer;

            public SingleIndexerFactory(IIndexer indexer)
            {
                _indexer = indexer;
            }

            public List<IIndexer> AutomaticSearchEnabled(bool filterBlockedIndexers = true) => new() { _indexer };
            public List<IIndexer> InteractiveSearchEnabled(bool filterBlockedIndexers = true) => new() { _indexer };
            public List<IIndexer> RssEnabled(bool filterBlockedIndexers = true) => new();
            public List<IndexerDefinition> All() => new();
            public List<IIndexer> GetAvailableProviders() => new() { _indexer };
            public bool Exists(int id) => false;
            public IndexerDefinition Find(int id) => null;
            public IndexerDefinition Get(int id) => null;
            public IEnumerable<IndexerDefinition> Get(IEnumerable<int> ids) => new List<IndexerDefinition>();
            public IndexerDefinition Create(IndexerDefinition definition) => throw new System.NotImplementedException();
            public void Update(IndexerDefinition definition) => throw new System.NotImplementedException();
            public IEnumerable<IndexerDefinition> Update(IEnumerable<IndexerDefinition> definitions) => throw new System.NotImplementedException();
            public void Delete(int id) => throw new System.NotImplementedException();
            public void Delete(IEnumerable<int> ids) => throw new System.NotImplementedException();
            public IEnumerable<IndexerDefinition> GetDefaultDefinitions() => new List<IndexerDefinition>();
            public IEnumerable<IndexerDefinition> GetPresetDefinitions(IndexerDefinition providerDefinition) => new List<IndexerDefinition>();
            public void SetProviderCharacteristics(IndexerDefinition definition) => throw new System.NotImplementedException();
            public void SetProviderCharacteristics(IIndexer provider, IndexerDefinition definition) => throw new System.NotImplementedException();
            public IIndexer GetInstance(IndexerDefinition definition) => _indexer;
            public FluentValidation.Results.ValidationResult Test(IndexerDefinition definition) => new();
            public object RequestAction(IndexerDefinition definition, string action, IDictionary<string, string> query) => null;
            public List<IndexerDefinition> AllForTag(int tagId) => new();
        }

        private class EmptyDecisionMakerProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IMakeDownloadDecision.GetSearchDecision))
                {
                    return new List<DownloadDecision>();
                }

                throw new System.NotImplementedException(targetMethod.Name);
            }
        }

        private class UnusedBookServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(NzbDrone.Core.Books.IBookService.UpdateLastSearchTime))
                {
                    return null;
                }

                throw new AssertionException($"Unexpected IBookService call: {targetMethod.Name}");
            }
        }

        private class UnusedAuthorServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new AssertionException($"Unexpected IAuthorService call: {targetMethod.Name}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class NearExactHolyGrailMatchFixture
    {
        private sealed class StubEditionFtsRepository : IEditionFtsRepository
        {
            private readonly List<EditionFtsMatch> _results;

            public StubEditionFtsRepository(IEnumerable<EditionFtsMatch> results)
            {
                _results = results.ToList();
            }

            public bool FtsTableExists() => true;
            public void RebuildIndex() { }
            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20) => _results.Take(limit).ToList();
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthor))
                {
                    return Author;
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public Dictionary<int, Book> Books { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBook))
                {
                    return Books.TryGetValue((int)args[0], out var book) ? book : null;
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
            }
        }

        private static FileMatchingService CreateService(IEnumerable<EditionFtsMatch> candidates, IEnumerable<Book> books)
        {
            var logger = LogManager.GetCurrentClassLogger();
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = new Author { Id = 41, Name = "J.K. Rowling" };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Books = books.ToDictionary(b => b.Id);

            return new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: authorService,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new StubEditionFtsRepository(candidates),
                bookService: bookService,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);
        }

        private static EditionFtsMatch Candidate(int editionId, int bookId, string title, int durationSeconds, double score = 100)
        {
            return new EditionFtsMatch
            {
                EditionId = editionId,
                BookId = bookId,
                EditionTitle = title,
                BookTitle = title,
                AuthorId = 41,
                AuthorName = "J.K. Rowling",
                DurationSeconds = durationSeconds,
                ReadingFormatId = 2,
                MatchScore = score
            };
        }

        private static Book Book(int bookId, string title)
        {
            return new Book
            {
                Id = bookId,
                AuthorId = 41,
                Title = title,
                MediaType = BookMediaType.Audiobook
            };
        }

        private static DiscoveredFileWithMetadata File(string tagTitle, int durationSeconds)
        {
            return new DiscoveredFileWithMetadata
            {
                Path = $"/downloads/incomplete/{tagTitle}/{tagTitle}.mp3",
                DurationSeconds = durationSeconds,
                AllTags = new Dictionary<string, List<string>>
                {
                    { "ALBUM", new List<string> { tagTitle } },
                    { "ARTIST", new List<string> { "J.K. Rowling" } }
                }
            };
        }

        [Test]
        public void should_match_fantastic_beast_tag_to_fantastic_beasts_book_when_no_better_book_exists()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new StubEditionFtsRepository(new[]
            {
                new EditionFtsMatch
                {
                    EditionId = 4782,
                    BookId = 1931,
                    EditionTitle = "Fantastic Beasts and Where to Find Them",
                    BookTitle = "Fantastic Beasts and Where to Find Them",
                    AuthorId = 41,
                    AuthorName = "J.K. Rowling"
                },
                new EditionFtsMatch
                {
                    EditionId = 5000,
                    BookId = 5000,
                    EditionTitle = "Quidditch Through the Ages",
                    BookTitle = "Quidditch Through the Ages",
                    AuthorId = 41,
                    AuthorName = "J.K. Rowling"
                }
            });

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = new Author { Id = 41, Name = "J.K. Rowling" };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Books = new Dictionary<int, Book>
            {
                { 1931, new Book { Id = 1931, AuthorId = 41, Title = "Fantastic Beasts and Where to Find Them", MediaType = BookMediaType.Audiobook } },
                { 5000, new Book { Id = 5000, AuthorId = 41, Title = "Quidditch Through the Ages", MediaType = BookMediaType.Audiobook } }
            };

            var svc = new FileMatchingService(
                matchingLogger: null,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: authorService,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: bookService,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var match = svc.HolyGrailMatchFile(
                new DiscoveredFileWithMetadata
                {
                    Path = "/downloads/incomplete/Fantastic Beast and Where to Find Them/Fantastic Beast and Where to Find Them.mp3",
                    AllTags = new Dictionary<string, List<string>>
                    {
                        { "ALBUM", new List<string> { "Fantastic Beast and Where to Find Them" } },
                        { "ARTIST", new List<string> { "J.K. Rowling , Newt Scamander" } }
                    }
                },
                BookMediaType.Audiobook,
                restrictToAuthorId: 41);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(1931));
            Assert.That(match.EditionId, Is.EqualTo(4782));
        }

        [Test]
        public void should_allow_transposition_when_audiobook_duration_corroborates_and_no_better_book_exists()
        {
            var svc = CreateService(
                new[]
                {
                    Candidate(1, 1, "Salt to the Sea", durationSeconds: 3600)
                },
                new[]
                {
                    Book(1, "Salt to the Sea")
                });

            var match = svc.HolyGrailMatchFile(File("Slat to the Sea", durationSeconds: 3610), BookMediaType.Audiobook, restrictToAuthorId: 41);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(1));
        }

        [Test]
        public void should_reject_transposition_when_audiobook_duration_does_not_corroborate()
        {
            var svc = CreateService(
                new[]
                {
                    Candidate(1, 1, "Salt to the Sea", durationSeconds: 3600)
                },
                new[]
                {
                    Book(1, "Salt to the Sea")
                });

            var match = svc.HolyGrailMatchFile(File("Slat to the Sea", durationSeconds: 7200), BookMediaType.Audiobook, restrictToAuthorId: 41);

            Assert.That(match, Is.Null);
        }

        [Test]
        public void should_prefer_exact_title_candidate_over_duration_gated_near_candidate()
        {
            var svc = CreateService(
                new[]
                {
                    Candidate(1, 1, "Salt to the Sea", durationSeconds: 3600, score: 100),
                    Candidate(2, 2, "Slat to the Sea", durationSeconds: 7200, score: 1)
                },
                new[]
                {
                    Book(1, "Salt to the Sea"),
                    Book(2, "Slat to the Sea")
                });

            var match = svc.HolyGrailMatchFile(File("Slat to the Sea", durationSeconds: 3610), BookMediaType.Audiobook, restrictToAuthorId: 41);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(2));
        }

        [Test]
        public void should_reject_when_multiple_near_exact_books_explain_the_same_title()
        {
            var svc = CreateService(
                new[]
                {
                    Candidate(1, 1, "Salt to the Sea", durationSeconds: 3600, score: 100),
                    Candidate(2, 2, "Slat to the Seas", durationSeconds: 3600, score: 90)
                },
                new[]
                {
                    Book(1, "Salt to the Sea"),
                    Book(2, "Slat to the Seas")
                });

            var match = svc.HolyGrailMatchFile(File("Slat to the Sea", durationSeconds: 3610), BookMediaType.Audiobook, restrictToAuthorId: 41);

            Assert.That(match, Is.Null);
        }
    }
}

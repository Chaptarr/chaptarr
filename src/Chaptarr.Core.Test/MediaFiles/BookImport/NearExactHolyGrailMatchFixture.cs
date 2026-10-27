using Chaptarr.Core.Test.Common;
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
        private sealed class StubEditionFtsRepository : StagedEditionFtsStub
        {
            private readonly List<EditionFtsMatch> _results;

            public StubEditionFtsRepository(IEnumerable<EditionFtsMatch> results)
            {
                _results = results.ToList();
            }

            public override bool FtsTableExists() => true;
            public override void RebuildIndex() { }
            protected override List<EditionFtsMatch> SelectEditions(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit) => _results.Take(limit).ToList();
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
            public int GetBooksByAuthorCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBook))
                {
                    return Books.TryGetValue((int)args[0], out var book) ? book : null;
                }

                if (targetMethod?.Name == nameof(IBookService.GetBooks))
                {
                    var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                    return Books.Values.Where(book => ids.Contains(book.Id)).ToList();
                }

                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    GetBooksByAuthorCalls++;
                    return Books.Values.Where(book => book.AuthorId == (int)args[0]).ToList();
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
            }
        }

        private static FileMatchingService CreateService(IEnumerable<EditionFtsMatch> candidates, IEnumerable<Book> books)
        {
            return CreateService(candidates, books, out _);
        }

        private static FileMatchingService CreateService(
            IEnumerable<EditionFtsMatch> candidates,
            IEnumerable<Book> books,
            out BookServiceProxy bookServiceProxy)
        {
            var logger = LogManager.GetCurrentClassLogger();
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = new Author { Id = 41, Name = "J.K. Rowling" };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            bookServiceProxy = (BookServiceProxy)(object)bookService;
            bookServiceProxy.Books = books.ToDictionary(b => b.Id);

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
                MatchingTitle = title,
                BookTitle = title,
                Lang = "eng",
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
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = bookId,
                        BookId = bookId,
                        Title = title,
                        Language = "eng",
                        Monitored = true
                    }
                }
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
        public void should_match_roman_numeral_alias_to_english_edition_and_preserve_proof_span()
        {
            var svc = CreateService(
                new[] { Candidate(1, 1, "Ready Player 2", durationSeconds: 3600) },
                new[] { Book(1, "Ready Player 2") },
                out var bookService);

            var match = svc.HolyGrailMatchFile(File("Ready Player Ⅱ", durationSeconds: 3600), BookMediaType.Audiobook, restrictToAuthorId: 41);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(1));
            Assert.That(bookService.GetBooksByAuthorCalls, Is.EqualTo(1));
            Assert.That(match.IdentityProof.Values, Has.Some.Matches<MatchIdentityProofValue>(value =>
                value.Role == MatchIdentityRole.Title && value.Observed == "Ready Player Ⅱ" && value.Expected == "Ready Player 2"));
            var titleEvidence = match.Provenance.EvidenceValues.Single(value => value.Value == "Ready Player Ⅱ");
            Assert.That(titleEvidence.Ranges, Has.Some.Matches<MatchEvidenceRange>(range =>
                range.Type == "title" && range.Disposition == "supporting" &&
                titleEvidence.Value.Substring(range.Start, range.End - range.Start) == "Ready Player Ⅱ"));
        }

        [TestCase("I, Robot", "1 Robot")]
        [TestCase("The Mix", "The 1009")]
        [TestCase("Vi", "6")]
        [TestCase("Ready Player II", "Ready Player 2")]
        [TestCase("Wild Cards VII: Dead Man's Hand", "Wild Cards 7: Dead Man's Hand")]
        [TestCase("Wild Cards 7: Dead Man's Hand", "Wild Cards VII: Dead Man's Hand")]
        public void roman_title_fallback_preserves_literal_titles_and_original_evidence(string title, string observed)
        {
            var svc = CreateService(
                new[] { Candidate(1, 1, title, durationSeconds: 3600) },
                new[] { Book(1, title) });

            var file = File(observed, durationSeconds: 3600);
            var match = svc.HolyGrailMatchFile(file, BookMediaType.Audiobook, restrictToAuthorId: 41);
            Assert.That(match, Is.Not.Null);
            Assert.That(match.EditionId, Is.EqualTo(1));
            Assert.That(file.AllTags["ALBUM"], Is.EqualTo(new[] { observed }));
            Assert.That(match.IdentityProof.Values, Has.Some.Matches<MatchIdentityProofValue>(value =>
                value.Role == MatchIdentityRole.Title && value.Observed == observed && value.Expected == title));
            Assert.That(svc.HolyGrailMatchFile(File(title, durationSeconds: 3600), BookMediaType.Audiobook, restrictToAuthorId: 41), Is.Not.Null);
        }

        [TestCase("Wild Cards VII", "Wild Cards 6")]
        [TestCase("Wild Cards VI", "Wild Cards 7")]
        [TestCase("Wild Cards VII: Dead Man's Hand", "Wild Cards 7")]
        [TestCase("Vi", "Violet")]
        public void numeric_fallback_requires_the_same_number_and_complete_title(string title, string observed)
        {
            var svc = CreateService(
                new[] { Candidate(1, 1, title, durationSeconds: 3600) },
                new[] { Book(1, title) });

            Assert.That(svc.HolyGrailMatchFile(File(observed, durationSeconds: 3600), BookMediaType.Audiobook, restrictToAuthorId: 41), Is.Null);
        }

        [TestCase("Collected Stories Volume II", "Collected Stories Volume 2")]
        [TestCase("Collected Stories Part II", "Collected Stories Part Two")]
        public void marked_roman_title_keeps_numeric_import_proof_and_highlight(string title, string observed)
        {
            var svc = CreateService(
                new[] { Candidate(1, 1, title, durationSeconds: 3600) },
                new[] { Book(1, title) });

            var match = svc.HolyGrailMatchFile(File(observed, durationSeconds: 3600), BookMediaType.Audiobook, restrictToAuthorId: 41);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.IdentityProof.Values, Has.Some.Matches<MatchIdentityProofValue>(value =>
                value.Role == MatchIdentityRole.Title && value.Observed == observed && value.Expected == title));
            Assert.That(match.Provenance.EvidenceValues, Has.Some.Matches<MatchEvidenceValue>(value =>
                value.Value == observed && value.Ranges.Any(range => range.Type == "title" && range.Disposition == "supporting")));
        }

        [Test]
        public void literal_title_match_does_not_load_the_full_author_catalog()
        {
            var svc = CreateService(
                new[] { Candidate(1, 1, "Ready Player 2", durationSeconds: 3600) },
                new[] { Book(1, "Ready Player 2") },
                out var bookService);

            var match = svc.HolyGrailMatchFile(File("Ready Player 2", durationSeconds: 3600), BookMediaType.Audiobook, restrictToAuthorId: 41);

            Assert.That(match, Is.Not.Null);
            Assert.That(bookService.GetBooksByAuthorCalls, Is.Zero);
        }

        [Test]
        public void should_reject_numeric_alias_when_a_distinct_same_media_selected_title_competes()
        {
            var svc = CreateService(
                new[] { Candidate(1, 1, "Ready Player 2", durationSeconds: 3600) },
                new[] { Book(1, "Ready Player 2"), Book(2, "Ready Player Two") });

            var match = svc.HolyGrailMatchFile(File("Ready Player Ⅱ", durationSeconds: 3600), BookMediaType.Audiobook, restrictToAuthorId: 41);

            Assert.That(match, Is.Null);
        }

        [Test]
        public void unmarked_roman_sibling_still_blocks_another_books_numeric_import_alias()
        {
            var svc = CreateService(
                new[] { Candidate(1, 1, "Ready Player Two", durationSeconds: 3600) },
                new[] { Book(1, "Ready Player Two"), Book(2, "Ready Player II") });

            var match = svc.HolyGrailMatchFile(File("Ready Player 2", durationSeconds: 3600), BookMediaType.Audiobook, restrictToAuthorId: 41);

            Assert.That(match, Is.Null);
        }

        [Test]
        public void provider_work_clones_do_not_create_numeric_title_collision()
        {
            var target = Book(1, "Ready Player 2");
            var providerClone = Book(2, "Ready Player Two");
            target.GoodreadsWorkId = "gr:12345";
            providerClone.GoodreadsWorkId = "gr:12345";
            var svc = CreateService(
                new[] { Candidate(1, 1, "Ready Player 2", durationSeconds: 3600) },
                new[] { target, providerClone });

            var match = svc.HolyGrailMatchFile(File("Ready Player Ⅱ", durationSeconds: 3600), BookMediaType.Audiobook, restrictToAuthorId: 41);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(1));
        }

        [Test]
        public void identical_full_selected_titles_do_not_create_numeric_title_collision()
        {
            var svc = CreateService(
                new[] { Candidate(1, 1, "Ready Player Two", durationSeconds: 3600) },
                new[] { Book(1, "Ready Player Two"), Book(2, "Ready Player Two") });

            var match = svc.HolyGrailMatchFile(File("Ready Player 2", durationSeconds: 3600), BookMediaType.Audiobook, restrictToAuthorId: 41);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(1));
        }

        [Test]
        public void should_prefer_the_exact_numeric_spelling_over_a_distinct_alias_candidate()
        {
            var exactBook = Book(1, "Ready Player 2");
            var aliasBook = Book(2, "Ready Player Two");
            var svc = CreateService(
                new[]
                {
                    Candidate(1, 1, "Ready Player 2", durationSeconds: 3600),
                    Candidate(2, 2, "Ready Player Two", durationSeconds: 3600)
                },
                new[] { exactBook, aliasBook });

            var match = svc.HolyGrailMatchFile(File("Ready Player 2", durationSeconds: 3600), BookMediaType.Audiobook, restrictToAuthorId: 41);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(1));
        }

        [TestCase(null)]
        [TestCase("und")]
        public void unsupported_target_language_still_detects_an_english_numeric_sibling_but_keeps_literal_proof(string targetLanguage)
        {
            var target = Book(1, "Ready Player 2");
            target.Editions.Single().Language = targetLanguage;
            var candidate = Candidate(1, 1, "Ready Player 2", durationSeconds: 3600);
            candidate.Lang = targetLanguage;
            var sibling = Book(2, "Ready Player Two");

            var aliasService = CreateService(new[] { candidate }, new[] { target, sibling });
            var aliasMatch = aliasService.HolyGrailMatchFile(
                File("Ready Player II", durationSeconds: 3600),
                BookMediaType.Audiobook,
                restrictToAuthorId: 41);

            Assert.That(aliasMatch, Is.Null);

            var literalService = CreateService(new[] { candidate }, new[] { target, sibling });
            var literalMatch = literalService.HolyGrailMatchFile(
                File("Ready Player 2", durationSeconds: 3600),
                BookMediaType.Audiobook,
                restrictToAuthorId: 41);

            Assert.That(literalMatch, Is.Not.Null);
            Assert.That(literalMatch.BookId, Is.EqualTo(1));
        }

        [Test]
        public void different_media_type_numeric_sibling_does_not_veto_import_title_proof()
        {
            var audioBook = Book(1, "Ready Player 2");
            var ebookSibling = Book(2, "Ready Player Two");
            ebookSibling.MediaType = BookMediaType.Ebook;
            var svc = CreateService(
                new[] { Candidate(1, 1, "Ready Player 2", durationSeconds: 3600) },
                new[] { audioBook, ebookSibling });

            var match = svc.HolyGrailMatchFile(File("Ready Player Ⅱ", durationSeconds: 3600), BookMediaType.Audiobook, restrictToAuthorId: 41);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.BookId, Is.EqualTo(1));
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

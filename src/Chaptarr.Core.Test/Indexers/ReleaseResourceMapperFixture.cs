using System;
using Chaptarr.Api.V1.Indexers;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class ReleaseResourceMapperFixture
    {
        [Test]
        public void should_not_throw_when_parsed_book_info_is_missing()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "The Housemaid",
                    Indexer = "MyAnonaMouse",
                    PublishDate = DateTime.UtcNow
                }
            };

            var decision = new DownloadDecision(remoteBook);

            Assert.DoesNotThrow(() => decision.ToResource());

            var resource = decision.ToResource();
            Assert.That(resource.Quality, Is.Not.Null);
            Assert.That(resource.Quality.Quality, Is.EqualTo(Quality.Unknown));
        }

        [Test]
        public void should_include_resolved_author_and_book_ids_for_single_book_results()
        {
            var author = new Author
            {
                Id = 11,
                Name = "Freida McFadden"
            };

            var book = new Book
            {
                Id = 1493,
                AuthorId = author.Id,
                Title = "Dead Med"
            };

            var remoteBook = new RemoteBook
            {
                Author = author,
                Books = new System.Collections.Generic.List<Book> { book },
                Release = new ReleaseInfo
                {
                    Title = "Dead Med",
                    Indexer = "MyAnonaMouse",
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = author.Name,
                    BookTitle = book.Title,
                    Quality = new QualityModel(Quality.M4B)
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.AuthorId, Is.EqualTo(author.Id));
            Assert.That(resource.BookId, Is.EqualTo(book.Id));
        }

        [Test]
        public void should_fallback_to_book_author_id_when_remote_author_is_missing()
        {
            var book = new Book
            {
                Id = 1493,
                AuthorId = 11,
                Title = "Dead Med"
            };

            var remoteBook = new RemoteBook
            {
                Books = new System.Collections.Generic.List<Book> { book },
                Release = new ReleaseInfo
                {
                    Title = "Dead Med",
                    Indexer = "MyAnonaMouse",
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Freida McFadden",
                    BookTitle = book.Title,
                    Quality = new QualityModel(Quality.M4B)
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.AuthorId, Is.EqualTo(book.AuthorId));
            Assert.That(resource.BookId, Is.EqualTo(book.Id));
        }

        [Test]
        public void should_clean_only_metadata_displayed_in_other_release_columns()
        {
            var remoteBook = new RemoteBook
            {
                Release = new TorrentInfo
                {
                    Title = "Pierce Brown - Red Rising [Tim Gerard Reynolds] [MP3] [Freeleech]",
                    Indexer = "MyAnonaMouse",
                    PublishDate = DateTime.UtcNow,
                    Narrator = "Tim Gerard Reynolds",
                    IndexerFlags = IndexerFlags.Freeleech
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Pierce Brown",
                    BookTitle = "Red Rising",
                    Quality = new QualityModel(Quality.MP3)
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.Title, Is.EqualTo("Pierce Brown - Red Rising [Tim Gerard Reynolds] [MP3] [Freeleech]"));
            Assert.That(resource.DisplayTitle, Is.EqualTo("Pierce Brown - Red Rising"));
        }

        [Test]
        public void should_not_clean_author_or_unshown_edition_text_from_release_title()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "Pierce Brown - Red Rising (Unabridged) Part 1, Chapter 08",
                    Indexer = "MyAnonaMouse",
                    PublishDate = DateTime.UtcNow,
                    Narrator = "Pierce Brown"
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Pierce Brown",
                    BookTitle = "Red Rising",
                    Quality = new QualityModel(Quality.MP3)
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.DisplayTitle, Is.EqualTo("Pierce Brown - Red Rising (Unabridged) Part 1, Chapter 08"));
        }

        [Test]
        public void should_only_set_matched_title_for_search_criteria_match()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "Test Author - Bury Me",
                    Indexer = "TestIndexer",
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Test Author",
                    BookTitle = "Bury Me",
                    Quality = new QualityModel(Quality.M4B)
                },
                SearchCriteriaMatch = new TitleMatchResult
                {
                    IsMatch = true,
                    PrimaryTitle = "Bury Me",
                    MatchedVariant = "Bury Me",
                    MatchedStart = 2,
                    MatchedEnd = 3
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.BookTitle, Is.EqualTo("Bury Me"));
            Assert.That(resource.MatchedTitle, Is.EqualTo("Bury Me"));
            Assert.That(resource.MatchedTitleCharStart, Is.EqualTo(14));
            Assert.That(resource.MatchedTitleCharEnd, Is.EqualTo(21));
        }

        [Test]
        public void should_set_matched_title_char_span_for_symbol_normalized_title()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "First Word & Other Words",
                    Indexer = "TestIndexer",
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Test Author",
                    BookTitle = "First Word and Other Words",
                    Quality = new QualityModel(Quality.M4B)
                },
                SearchCriteriaMatch = new TitleMatchResult
                {
                    IsMatch = true,
                    PrimaryTitle = "First Word and Other Words",
                    MatchedVariant = "First Word and Other Words",
                    MatchedStart = 0,
                    MatchedEnd = 4
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.MatchedTitle, Is.EqualTo("First Word and Other Words"));
            Assert.That(resource.MatchedTitleCharStart, Is.EqualTo(0));
            Assert.That(resource.MatchedTitleCharEnd, Is.EqualTo(24));
        }

        [Test]
        public void should_use_stored_span_occurrence_for_repeated_title_highlight()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "Bury Me - Bury Me",
                    Indexer = "TestIndexer",
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Test Author",
                    BookTitle = "Bury Me",
                    Quality = new QualityModel(Quality.M4B)
                },
                SearchCriteriaMatch = new TitleMatchResult
                {
                    IsMatch = true,
                    PrimaryTitle = "Bury Me",
                    MatchedVariant = "Bury Me",
                    MatchedStart = 2,
                    MatchedEnd = 3
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.MatchedTitleCharStart, Is.EqualTo(10));
            Assert.That(resource.MatchedTitleCharEnd, Is.EqualTo(17));
        }

        [Test]
        public void should_not_use_parser_book_title_as_matched_title_without_search_match()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "Jim Hendricks-Home On The Range(1996)) - 14 The Colorado Trail_Bury Me Not On",
                    Indexer = "DrunkenSlug (Prowlarr)",
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Jim Hendricks",
                    BookTitle = "Home On The Range",
                    Quality = new QualityModel(Quality.MP3)
                },
                SearchCriteriaMatch = null
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.BookTitle, Is.EqualTo("Home On The Range"));
            Assert.That(resource.MatchedTitle, Is.Null);
        }

    }
}

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Utilities;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleaseTitleMatchScorerFixture
    {
        [Test]
        public void should_return_source_spans_for_backend_title_tokens()
        {
            var tokens = ReleaseTitleMatchScorer.TokenizeWithSpans("HANDMAID'S & Café 1.5");

            Assert.That(tokens.Select(token => token.Value), Is.EqualTo(new[] { "handmaids", "and", "cafe", "1.5" }));
            Assert.That(tokens.Select(token => (token.Start, token.End)), Is.EqualTo(new[] { (0, 10), (11, 12), (13, 17), (18, 21) }));
        }

        [Test]
        public void should_keep_span_tokenizer_unicode_output_in_parity_with_shared_normalizer()
        {
            var title = "한글 제목 Café";
            var expectedTokens = UnicodeComparisonNormalizer.NormalizeWords(title).Split(' ');

            Assert.That(ReleaseTitleMatchScorer.Tokenize(title), Is.EqualTo(expectedTokens));
        }

        [Test]
        public void should_match_exact_monitored_title_with_author_series_and_noise()
        {
            var author = new Author { Name = "Brandon Sanderson" };
            var book = new Book
            {
                Title = "Mistborn: The Final Empire",
                Author = author,
                SeriesName = "Mistborn",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "The Final Empire", Monitored = true },
                    new Edition { Id = 2, Title = "Mistborn: The Final Empire" }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Brandon Sanderson - [Mistborn 01] - The Final Empire (epub)",
                "Brandon Sanderson",
                new[] { book },
                "Brandon Sanderson",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.PrimaryTitle, Is.EqualTo("The Final Empire"));
            Assert.That(result.MatchedVariant, Is.EqualTo("The Final Empire"));
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_match_exact_title_with_bracketed_series_counter_metadata()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var book = new Book
            {
                Title = "Harry Potter and the Philosopher's Stone",
                Author = author,
                SeriesName = "Harry Potter",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Philosopher's Stone", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "J K Rowling - [Harry Potter 01] - Harry Potter and the Philosopher's Stone (retail) (epub)",
                "J. K. Rowling",
                new[] { book },
                "J K Rowling",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo("Harry Potter and the Philosopher's Stone"));
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_match_exact_title_with_compact_prefix_code()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var book = new Book
            {
                Title = "Harry Potter and the Philosopher's Stone",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Philosopher's Stone", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "PB1 - Harry Potter and the Philosopher's Stone - J.K. Rowling",
                "J. K. Rowling",
                new[] { book },
                null,
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo("Harry Potter and the Philosopher's Stone"));
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_still_reject_authorless_exact_title_even_when_no_contradictions_exist()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var book = new Book
            {
                Title = "Harry Potter and the Philosopher's Stone",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Philosopher's Stone", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Harry Potter and the Philosopher's Stone (retail) (epub)",
                "J. K. Rowling",
                new[] { book },
                null,
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.False);
            Assert.That(result.MatchedVariant, Is.EqualTo("Harry Potter and the Philosopher's Stone"));
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_reject_superstring_when_other_tracked_title_contradicts()
        {
            var author = new Author { Name = "Freida McFadden" };
            var targetBook = new Book
            {
                Id = 1,
                Title = "The Housemaid",
                Author = author,
                SeriesName = "The Housemaid Series"
            };
            var contradictoryBook = new Book
            {
                Id = 2,
                Title = "The Housemaid Is Watching",
                Author = author
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Freida McFadden - The Housemaid Is Watching - The Housemaid Series [M4B]",
                "Freida McFadden",
                new[] { targetBook },
                "Freida McFadden",
                new[] { targetBook, contradictoryBook });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.False);
            Assert.That(result.MatchedVariant, Is.EqualTo("The Housemaid"));
            Assert.That(result.MeaningfulLeftovers, Does.Contain("The Housemaid Is Watching"));
        }

        [Test]
        public void should_reject_short_title_false_positive_even_with_polluted_author_hint()
        {
            var book = new Book
            {
                Title = "Voyeur",
                Author = new Author { Name = "Fiona Cole" }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Moongarden - Voyeur (2014) MP3",
                "Fiona Cole",
                new[] { book },
                "Fiona Cole",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.False);
            Assert.That(result.MatchedVariant, Is.EqualTo("Voyeur"));
            Assert.That(result.MeaningfulLeftovers, Is.EquivalentTo(new[] { "moongarden" }));
        }

        [Test]
        public void should_allow_author_hint_for_exact_title_only_release()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var book = new Book
            {
                Title = "Harry Potter and the Philosopher's Stone",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Sorcerer's Stone", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Harry Potter and the Sorcerer's Stone",
                "J. K. Rowling",
                new[] { book },
                "J K Rowling",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.PrimaryTitle, Is.EqualTo("Harry Potter and the Sorcerer's Stone"));
            Assert.That(result.MatchedVariant, Is.EqualTo("Harry Potter and the Sorcerer's Stone"));
        }

        [TestCase("Tuesdays with Morrie")]
        [TestCase("Mitch Albom - Tuesdays with Morrie (2007) MP3")]
        [TestCase("Tuesdays With Morrie - Mitch Albom audiobook")]
        public void should_not_shorten_the_monitored_edition_title_for_release_identity(string releaseTitle)
        {
            var author = new Author { Name = "Mitch Albom" };
            var book = new Book
            {
                Title = "Tuesdays with Morrie",
                Subtitle = "An Old Man, a Young Man, and Life's Greatest Lesson",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Tuesdays with Morrie: An Old Man, a Young Man, and Life's Greatest Lesson",
                        Subtitle = "An Old Man, a Young Man, and Life's Greatest Lesson",
                        Monitored = true
                    }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                releaseTitle,
                "Mitch Albom",
                new[] { book },
                "Mitch Albom",
                new[] { book });

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_not_drop_an_unmapped_marketing_subtitle_from_release_identity()
        {
            var author = new Author { Name = "A.F. Kay" };
            var book = new Book
            {
                Title = "Shade's First Rule",
                Author = author,
                SeriesName = "Divine Apostasy",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Shade's First Rule: A Fantasy LitRPG Adventure",
                        Subtitle = "Divine Apostasy, Book 1",
                        Monitored = true
                    }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "A F Kay - Shade's First Rule",
                "A.F. Kay",
                new[] { book },
                "A F Kay",
                new[] { book });

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_prefer_full_monitored_title_over_split_base_when_both_appear()
        {
            var author = new Author { Name = "A.F. Kay" };
            var book = new Book
            {
                Title = "Shade's First Rule",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Shade's First Rule: A Fantasy LitRPG Adventure",
                        Subtitle = "Divine Apostasy, Book 1",
                        Monitored = true
                    }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "A F Kay - Shade's First Rule: A Fantasy LitRPG Adventure aka Shade's First Rule",
                "A.F. Kay",
                new[] { book },
                "A F Kay",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo("Shade's First Rule: A Fantasy LitRPG Adventure"));
        }

        [TestCase("Jason Anspach - King's League: An Epic Lit RPG Adventure")]
        [TestCase("Jason Anspach - King's League: An Epic LitRPG Adventure")]
        public void should_match_compact_litrpg_and_split_lit_rpg_as_same_title_tokens(string releaseTitle)
        {
            var author = new Author { Name = "Jason Anspach" };
            var book = new Book
            {
                Title = "King's League: An Epic LitRPG Adventure",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "King's League: An Epic LitRPG Adventure",
                        Monitored = true
                    }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                releaseTitle,
                "Jason Anspach",
                new[] { book },
                "Jason Anspach",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True, $"ProblemCode={result.ProblemCode}; Leftovers={string.Join(", ", result.MeaningfulLeftovers)}");
            Assert.That(result.MatchedVariant, Is.EqualTo("King's League: An Epic LitRPG Adventure"));
        }

        [Test]
        public void should_not_accept_split_prefix_when_it_is_not_the_book_title()
        {
            var author = new Author { Name = "Arthur Conan Doyle" };
            var book = new Book
            {
                Title = "The Speckled Band",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Sherlock Holmes: The Speckled Band", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Arthur Conan Doyle - Sherlock Holmes",
                "Arthur Conan Doyle",
                new[] { book },
                "Arthur Conan Doyle",
                new[] { book });

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_not_accept_bare_split_prefix_for_pocket_potters()
        {
            var author = new Author { Name = "J.K. Rowling" };
            var book = new Book
            {
                Title = "Pocket Potters: Harry Potter: Little Guides to the Harry Potter Stories",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Pocket Potters: Harry Potter: Little Guides to the Harry Potter Stories",
                        Monitored = true
                    }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "J K Rowling - Pocket Potters",
                "J.K. Rowling",
                new[] { book },
                "J K Rowling",
                new[] { book });

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_not_match_short_title_when_release_contains_known_sibling_title()
        {
            var author = new Author { Name = "Alan Dean Foster" };
            var book = new Book
            {
                Title = "Aliens",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Aliens", Monitored = true }
                }
            };
            var siblingBook = new Book
            {
                Id = 2,
                Title = "Aliens: Resurrection",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 2, Title = "Aliens: Resurrection", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Alan Dean Foster - Aliens: Resurrection MP3",
                "Alan Dean Foster",
                new[] { book },
                "Alan Dean Foster",
                new[] { book, siblingBook });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.False);
            Assert.That(result.MeaningfulLeftovers, Does.Contain("Aliens: Resurrection"));
        }

        [Test]
        public void should_reject_adjacent_bare_series_number_when_it_does_not_match_requested_book_position()
        {
            var author = new Author { Name = "Pierce Brown" };
            var book = new Book
            {
                Title = "Red Rising",
                Author = author,
                SeriesName = "Red Rising",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Red Rising", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Pierce Brown - Red Rising 3 - Tag der Entscheidung",
                "Pierce Brown",
                new[] { book },
                "Pierce Brown",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.False);
            Assert.That(result.MatchedVariant, Is.EqualTo("Red Rising"));
            Assert.That(result.MeaningfulLeftovers, Does.Contain("3"));
        }

        [Test]
        public void should_accept_adjacent_bare_series_number_when_it_matches_requested_book_position()
        {
            var author = new Author { Name = "Pierce Brown" };
            var book = new Book
            {
                Title = "Red Rising",
                Author = author,
                SeriesName = "Red Rising",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Red Rising", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Pierce Brown - Red Rising 1",
                "Pierce Brown",
                new[] { book },
                "Pierce Brown",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True, $"ProblemCode={result.ProblemCode}; Leftovers={string.Join(", ", result.MeaningfulLeftovers)}");
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [TestCase("Pierce Brown - Red Rising 01")]
        [TestCase("Pierce Brown - Red Rising 001")]
        public void should_accept_adjacent_zero_padded_series_number_when_it_matches_requested_book_position(string releaseTitle)
        {
            var author = new Author { Name = "Pierce Brown" };
            var book = new Book
            {
                Title = "Red Rising",
                Author = author,
                SeriesName = "Red Rising",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Red Rising", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                releaseTitle,
                "Pierce Brown",
                new[] { book },
                "Pierce Brown",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True, $"ProblemCode={result.ProblemCode}; Leftovers={string.Join(", ", result.MeaningfulLeftovers)}");
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_accept_adjacent_zero_padded_series_number_when_position_is_prose()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var book = new Book
            {
                Title = "Harry Potter and the Half-Blood Prince",
                Author = author,
                SeriesName = "Harry Potter Persian/Farsi Split-Volume Edition",
                SeriesPosition = "#6, part 1 of 2",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Half-Blood Prince", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Harry Potter and the Half-Blood Prince (006) by J. K. Rowling M4B",
                "J. K. Rowling",
                new[] { book },
                "J. K. Rowling",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True, $"ProblemCode={result.ProblemCode}; Leftovers={string.Join(", ", result.MeaningfulLeftovers)}");
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_not_bind_a_part_number_to_an_unrelated_series_label()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var book = new Book
            {
                Title = "Harry Potter and the Half-Blood Prince",
                Author = author,
                SeriesName = "Harry Potter Persian/Farsi Split-Volume Edition",
                SeriesPosition = "#6, part 1 of 2",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Half-Blood Prince", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Harry Potter and the Half-Blood Prince 2 by J. K. Rowling M4B",
                "J. K. Rowling",
                new[] { book },
                "J. K. Rowling",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.False);
            Assert.That(result.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SuspiciousAdjacentNumber));
        }

        [Test]
        public void should_compare_decimal_series_positions_as_decimal_numbers()
        {
            var author = new Author { Name = "Pierce Brown" };
            var book = new Book
            {
                Title = "Red Rising",
                Author = author,
                SeriesName = "Red Rising",
                SeriesPosition = "6.2",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Red Rising", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Pierce Brown - Red Rising 06.2 - Zeitalter des Lichts",
                "Pierce Brown",
                new[] { book },
                "Pierce Brown",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True, $"ProblemCode={result.ProblemCode}; Leftovers={string.Join(", ", result.MeaningfulLeftovers)}");
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_not_treat_decimal_series_position_as_whole_number_match()
        {
            var author = new Author { Name = "Pierce Brown" };
            var book = new Book
            {
                Title = "Red Rising",
                Author = author,
                SeriesName = "Red Rising",
                SeriesPosition = "6.2",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Red Rising", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Pierce Brown - Red Rising 06 - Light Bringer",
                "Pierce Brown",
                new[] { book },
                "Pierce Brown",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.False);
            Assert.That(result.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SeriesPositionMismatch));
        }

        [TestCase("Pierce Brown - Red Rising Part 2")]
        [TestCase("Pierce Brown - Red Rising (2 of 2)")]
        public void should_accept_adjacent_number_when_it_is_a_part_marker(string releaseTitle)
        {
            var author = new Author { Name = "Pierce Brown" };
            var book = new Book
            {
                Title = "Red Rising",
                Author = author,
                SeriesName = "Red Rising",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Red Rising", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                releaseTitle,
                "Pierce Brown",
                new[] { book },
                "Pierce Brown",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_not_treat_adjacent_bitrate_as_series_number()
        {
            var author = new Author { Name = "Pierce Brown" };
            var book = new Book
            {
                Title = "Red Rising",
                Author = author,
                SeriesName = "Red Rising",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Red Rising", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Pierce Brown - Red Rising 64 kbps",
                "Pierce Brown",
                new[] { book },
                "Pierce Brown",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_accept_series_name_and_position_when_exact_book_title_is_present()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var book = new Book
            {
                Title = "Harry Potter and the Goblet of Fire",
                Author = author,
                SeriesName = "Harry Potter",
                SeriesPosition = "4",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Goblet of Fire", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Harry Potter 4 - Harry Potter and the Goblet of Fire",
                "J. K. Rowling",
                new[] { book },
                "J K Rowling",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo("Harry Potter and the Goblet of Fire"));
        }

        [Test]
        public void should_not_match_series_name_and_position_without_exact_book_title()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var book = new Book
            {
                Title = "Harry Potter and the Goblet of Fire",
                Author = author,
                SeriesName = "Harry Potter",
                SeriesPosition = "4",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Goblet of Fire", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Harry Potter 4",
                "J. K. Rowling",
                new[] { book },
                "J K Rowling",
                new[] { book });

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_not_reject_sibling_title_when_it_is_not_adjacent_to_matched_title()
        {
            var author = new Author { Name = "Freida McFadden" };
            var targetBook = new Book
            {
                Id = 1,
                Title = "The Housemaid",
                Author = author
            };
            var siblingBook = new Book
            {
                Id = 2,
                Title = "The Housemaid Is Watching",
                Author = author
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "The Housemaid - Freida McFadden - uploader - The Housemaid Is Watching sample",
                "Freida McFadden",
                new[] { targetBook },
                "Freida McFadden",
                new[] { targetBook, siblingBook });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo("The Housemaid"));
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_match_dramatized_title_when_release_uses_graphicaudio_badge()
        {
            var author = new Author { Name = "Jim Butcher" };
            var book = new Book
            {
                Title = "Storm Front (Dramatized Adaptation)",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Storm Front (Dramatized Adaptation)", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Storm Front [GraphicAudio] [8h 00m] M4B",
                "Jim Butcher",
                new[] { book },
                "Jim Butcher",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.PrimaryTitle, Is.EqualTo("Storm Front (Dramatized Adaptation)"));
            Assert.That(result.MatchedVariant, Is.EqualTo("Storm Front"));
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_match_dramatized_title_when_production_label_precedes_series_subtitle()
        {
            var author = new Author { Name = "Jim Butcher" };
            var book = new Book
            {
                Title = "Grave Peril (Dramatized Adaptation): Dresden Files, Book 3",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Grave Peril (Dramatized Adaptation): Dresden Files, Book 3", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Jim Butcher - Grave Peril [GraphicAudio]",
                "Jim Butcher",
                new[] { book },
                null,
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo("Grave Peril"));
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_not_drop_part_parenthetical_when_it_identifies_the_requested_book()
        {
            var author = new Author { Name = "Rebecca Yarros" };
            var book = new Book
            {
                Title = "Iron Flame (Part 2 of 2) (Dramatized Adaptation)",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Iron Flame (Part 2 of 2) (Dramatized Adaptation)", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Rebecca Yarros - Iron Flame [GraphicAudio]",
                "Rebecca Yarros",
                new[] { book },
                null,
                new[] { book });

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_allow_same_book_alias_tokens_when_monitored_title_is_present()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var book = new Book
            {
                Title = "Harry Potter and the Philosopher's Stone",
                AnyEditionOk = false,
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Sorcerer's Stone", Monitored = true },
                    new Edition { Id = 2, Title = "Harry Potter and the Philosopher's Stone" }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Harry Potter and the Philosopher's Stone (aka Harry Potter and the Sorcerer's Stone)",
                "J. K. Rowling",
                new[] { book },
                "J K Rowling",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.PrimaryTitle, Is.EqualTo("Harry Potter and the Sorcerer's Stone"));
            Assert.That(result.MatchedVariant, Is.EqualTo("Harry Potter and the Sorcerer's Stone"));
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_not_use_sibling_edition_title_for_release_identity_when_any_edition_ok_is_enabled()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var book = new Book
            {
                Title = "Harry Potter and the Philosopher's Stone",
                AnyEditionOk = true,
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Sorcerer's Stone", Monitored = true },
                    new Edition { Id = 2, Title = "Harry Potter and the Philosopher's Stone" }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Harry Potter and the Philosopher's Stone",
                "J. K. Rowling",
                new[] { book },
                "J K Rowling",
                new[] { book });

            Assert.That(result, Is.Null,
                "AnyEditionOk permits a locally proven edition switch after download; it must not broaden release identity");
        }

        [Test]
        public void should_not_let_a_poisoned_sibling_edition_hide_different_book_evidence()
        {
            var author = new Author { Name = "Frank Herbert" };
            var book = new Book
            {
                Title = "Dune",
                AnyEditionOk = true,
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Dune", Monitored = true },
                    new Edition { Id = 2, Title = "Dune Messiah" }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Frank Herbert - Dune Messiah",
                "Frank Herbert",
                new[] { book },
                "Frank Herbert",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.False);
            Assert.That(result.MatchedVariant, Is.EqualTo("Dune"));
            Assert.That(result.MeaningfulLeftovers, Is.Not.Empty);
        }

        [Test]
        public void should_use_a_retitled_sibling_work_title_as_contradiction_evidence()
        {
            var author = new Author { Name = "Pierce Brown" };
            var target = new Book
            {
                Id = 1,
                Title = "Red Rising",
                HardcoverBookId = "hc:target",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Red Rising", Monitored = true }
                }
            };
            var sibling = new Book
            {
                Id = 2,
                Title = "Red Rising: Sons of Ares",
                HardcoverBookId = "hc:sibling",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 2, Title = "Sons of Ares", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Pierce Brown - Red Rising: Sons of Ares",
                "Pierce Brown",
                new[] { target },
                "Pierce Brown",
                new[] { target, sibling });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.False);
            Assert.That(result.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SiblingTitleContradiction));
            Assert.That(result.MeaningfulLeftovers, Does.Contain("Red Rising: Sons of Ares"));
        }

        [Test]
        public void should_not_treat_exact_or_provider_proven_same_work_copies_as_sibling_contradictions()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var target = new Book
            {
                Id = 1,
                Title = "Harry Potter and the Goblet of Fire",
                HardcoverBookId = "hc:goblet",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Goblet of Fire", Monitored = true }
                }
            };
            var exactCopyWithoutProviderIds = new Book
            {
                Id = 3,
                Title = "Harry Potter and the Goblet of Fire",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 3, Title = "Harry Potter and the Goblet of Fire", Monitored = true }
                }
            };

            var jimDaleCopy = new Book
            {
                Id = 2,
                Title = "Harry Potter and the Goblet of Fire: Jim Dale Edition",
                HardcoverBookId = "hc:goblet",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 2, Title = "Harry Potter and the Goblet of Fire: Jim Dale Edition", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "J. K. Rowling - Harry Potter and the Goblet of Fire: Jim Dale Edition",
                "J. K. Rowling",
                new[] { target },
                "J. K. Rowling",
                new[] { target, exactCopyWithoutProviderIds, jimDaleCopy });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.Problems, Is.Empty);
        }

        [Test]
        public void should_prefer_primary_match_when_primary_and_sibling_titles_both_appear()
        {
            var author = new Author { Name = "J. K. Rowling" };
            var book = new Book
            {
                Title = "Harry Potter and the Philosopher's Stone",
                AnyEditionOk = true,
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Sorcerer's Stone", Monitored = true },
                    new Edition { Id = 2, Title = "Harry Potter and the Philosopher's Stone" }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Harry Potter and the Sorcerer's Stone (aka Harry Potter and the Philosopher's Stone)",
                "J. K. Rowling",
                new[] { book },
                "J K Rowling",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo("Harry Potter and the Sorcerer's Stone"));
        }

        [Test]
        public void should_not_fallback_to_split_main_title_when_primary_title_is_absent()
        {
            var book = new Book
            {
                Title = "Heretics of Dune: Dune Chronicles",
                AnyEditionOk = true,
                Author = new Author { Name = "Frank Herbert" }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Heretics of Dune",
                "Frank Herbert",
                new[] { book },
                "Frank Herbert",
                new[] { book });

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_ignore_shorter_tracked_title_that_is_fully_inside_matched_title_span()
        {
            var author = new Author { Name = "Frank Herbert" };
            var targetBook = new Book
            {
                Id = 1,
                Title = "Dune Messiah",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 10, Title = "Dune Messiah", Monitored = true }
                }
            };
            var otherBook = new Book
            {
                Id = 2,
                Title = "Dune",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 20, Title = "Dune", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Frank Herbert - Dune Messiah (m4b)",
                "Frank Herbert",
                new[] { targetBook },
                "Frank Herbert",
                new[] { targetBook, otherBook });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_accept_possessive_author_with_series_metadata_before_title()
        {
            var author = new Author { Name = "Frank Herbert" };
            var targetBook = new Book
            {
                Id = 1,
                Title = "Dune Messiah",
                Author = author,
                SeriesName = "Dune",
                SeriesPosition = "2",
                Editions = new List<Edition>
                {
                    new Edition { Id = 10, Title = "Dune Messiah", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Frank Herbert's 'Dune', Bk 2 - Dune Messiah (NMR 56 kbps) \"Dune Messiah.vol01+02.PAR2\" 03/86",
                "Frank Herbert",
                new[] { targetBook },
                null,
                new[] { targetBook });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_match_usenet_subject_after_cleanup_when_monitored_title_is_present()
        {
            var author = new Author { Name = "Brandon Sanderson" };
            var book = new Book
            {
                Title = "Mistborn: The Final Empire",
                Author = author,
                SeriesName = "Mistborn",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "The Final Empire", Monitored = true },
                    new Edition { Id = 2, Title = "Mistborn: The Final Empire" }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Brandon Sanderson - 'Mistborn', Bk 1 - The Final Empire  (NMR 64 kbps) [27/31] - \"Brandon Sanderson - Mistborn - The Final Empire 18 of 22.mp3\" yEnc",
                "Brandon Sanderson",
                new[] { book },
                "Brandon Sanderson",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.PrimaryTitle, Is.EqualTo("The Final Empire"));
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_match_titles_when_release_omits_diacritics()
        {
            var author = new Author { Name = "Renée Ahdieh" };
            var book = new Book
            {
                Title = "The Wrath & the Dawn",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "The Wrath & the Dawn", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Renee Ahdieh - The Wrath and the Dawn (epub)",
                "Renée Ahdieh",
                new[] { book },
                "Renee Ahdieh",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo("The Wrath & the Dawn"));
        }

        [Test]
        public void should_match_swedish_title_when_release_uses_scandinavian_transliteration()
        {
            var author = new Author { Name = "Jonna Björnstjerna" };
            var book = new Book
            {
                Title = "Sagan om den underbara familjen Kanin och spöktåget",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Sagan om den underbara familjen Kanin och spöktåget", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Jonna.Bjornstjerna.Sagan.om.den.underbara.familjen.Kanin.och.Spoktaaget.2019",
                "Jonna Björnstjerna",
                new[] { book },
                "Jonna Bjornstjerna",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
        }

        [Test]
        public void should_match_germanic_title_transliteration()
        {
            var author = new Author { Name = "Example Author" };
            var book = new Book
            {
                Title = "Fußball für Anfänger",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Fußball für Anfänger", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Example Author - Fussball fuer Anfaenger",
                "Example Author",
                new[] { book },
                "Example Author",
                new[] { book });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True);
        }
    }
}

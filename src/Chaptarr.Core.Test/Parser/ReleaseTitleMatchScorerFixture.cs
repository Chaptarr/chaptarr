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
        [TestCase("Radio Theatre")]
        [TestCase("Radio Theater")]
        [TestCase("Dramatization")]
        [TestCase("Dramatisation")]
        [TestCase("Dramatic Reading")]
        public void edition_classification_terms_do_not_make_title_qualifiers_optional(string qualifier)
        {
            var author = new Author { Name = "Fixture Author" };
            var book = new Book
            {
                Id = 1, Author = author, Title = "Northern Lights",
                Editions = new List<Edition> { new Edition { Title = $"Northern Lights ({qualifier})", Monitored = true } }
            };
            var result = ReleaseTitleMatchScorer.FindBestMatch("Fixture Author - Northern Lights", author.Name, new[] { book });
            Assert.That(result, Is.Null, "Edition-only classification must not create a shorter matching title");
            var complete = ReleaseTitleMatchScorer.FindBestMatch($"Fixture Author - Northern Lights ({qualifier})", author.Name, new[] { book });
            Assert.That(complete?.IsMatch, Is.True);

            book.Editions[0].Title = "Northern Lights";
            var decoratedRelease = ReleaseTitleMatchScorer.FindBestMatch($"Fixture Author - Northern Lights ({qualifier})", author.Name, new[] { book });
            Assert.That(decoratedRelease?.IsMatch, Is.True, "The existing author-backed title match may carry additional release text");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t ")]
        public void blank_searched_author_should_not_supply_author_evidence(string authorName)
        {
            var book = new Book
            {
                Id = 1,
                Author = new Author { Name = authorName },
                Title = "Dune",
                Editions = new List<Edition> { new Edition { Title = "Dune", Monitored = true } }
            };

            foreach (var authorHint in new[] { null, "Frank Herbert" })
            {
                var result = ReleaseTitleMatchScorer.FindBestMatch("Dune M4B", authorName, new[] { book }, authorHint);

                Assert.That(result, Is.Not.Null);
                Assert.That(result.IsMatch, Is.False, "A title match cannot establish an unspecified author's identity.");
            }
        }

        [TestCase("Frank Herbert Dune (Unabridged) - Dune Messiah MP3")]
        [TestCase("Frank Herbert Dune (Unabridged) - Dune 4 MP3")]
        public void optional_qualifier_variants_should_not_hide_a_contradicted_base_title_occurrence(string release)
        {
            var author = new Author { Name = "Frank Herbert" };
            var target = new Book
            {
                Id = 1, Author = author, Title = "Dune", SeriesName = "Dune", SeriesPosition = "1",
                Editions = new List<Edition> { new Edition { Title = "Dune (Unabridged)", Monitored = true } }
            };
            var sibling = new Book
            {
                Id = 2, Author = author, Title = "Dune Messiah",
                Editions = new List<Edition> { new Edition { Title = "Dune Messiah", Monitored = true } }
            };
            Assert.That(ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, new[] { target }, author.Name, new[] { target, sibling })?.IsMatch, Is.False);
        }

        [TestCase("Here IT Comes - Stephen King", true)]
        [TestCase("Stephen King - If It Bleeds", false)]
        [TestCase("Stephen King - You Like It Darker", false)]
        public void a_shared_word_only_contradicts_when_the_complete_sibling_title_is_present(string release, bool expected)
        {
            var author = new Author { Name = "Stephen King" };
            var titles = new[] { "It", "If It Bleeds", "You Like It Darker" };
            var catalog = titles.Select((title, index) => new Book
            {
                Id = index + 1, Title = title, Author = author,
                Editions = new List<Edition> { new Edition { Id = index + 1, Title = title, Monitored = true } }
            }).ToList();
            var result = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, new[] { catalog[0] }, null, catalog);

            Assert.That(result?.IsMatch, Is.EqualTo(expected));
            Assert.That(result.ProblemCode, Is.EqualTo(expected ? TitleMatchProblemCode.None : TitleMatchProblemCode.SiblingTitleContradiction));
        }

        [TestCase("Frank.Herbert.Dune.Repack.m4b.Scott.Brick.Euan.Morton.Simon.vance.320kps", true)]
        [TestCase("Frank.Herbert.Dune.320kbps.Repack.m4b.Scott.Brick.Euan.Morton.Simon.vance.", true)]
        [TestCase("Frank.Herbert.Dune.Messiah.Repack.m4b.Scott.Brick.Euan.Morton.Simon.vance.320kps", false)]
        [TestCase("Frank Herbert Dune series du02 Dune Messiah MP3", false)]
        [TestCase("Frank Herbert Dune Neuauflage MP3", true)]
        [TestCase("Frank Herbert Dune réédition M4B", true)]
        [TestCase("Frank Herbert Dune 再版 M4B", true)]
        [TestCase("[1/3] - \"Frank Herbert - Dune.mp3\"", true)]
        [TestCase("Frank Herbert - \"Dune.mp3\"", true)]
        [TestCase("[1/3] - \"Frank Herbert - Dune Messiah.mp3\"", false)]
        [TestCase("Frank Herbert Dune - \"Frank Herbert - Dune Messiah.mp3\" yEnc", false)]
        public void release_identity_should_follow_displayed_sibling_titles_not_extra_words(string release, bool expected)
        {
            var author = new Author { Name = "Frank Herbert" };
            var target = new Book
            {
                Id = 1, Author = author, Title = "Dune", HardcoverBookId = "hc:312460",
                Editions = new List<Edition> { new Edition { Id = 1, Title = "Dune", Monitored = true } }
            };
            var sibling = new Book
            {
                Id = 2, Author = author, Title = "Dune Messiah", HardcoverBookId = "hc:427393", AudiobookMonitored = false,
                Editions = new List<Edition> { new Edition { Id = 2, Title = "Dune Messiah", Monitored = true } }
            };
            var creditCollision = new Book
            {
                Id = 3, Author = author, Title = "Frank Herbert: Unpublished Stories", HardcoverBookId = "hc:1842196",
                Editions = new List<Edition> { new Edition { Id = 3, Title = "Frank Herbert", Subtitle = "Unpublished Stories", Monitored = true } }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, new[] { target }, null, new[] { target, sibling, creditCollision });
            Assert.That(result?.IsMatch, Is.EqualTo(expected));
        }

        [TestCase("(Narrated by Stephen Fry)", true)]
        [TestCase("(Gelesen von Stephen Fry)", true)]
        [TestCase("[Lu par Stephen Fry]", true)]
        [TestCase("（Stephen Fry, Unabridged）", true)]
        [TestCase("［Stephen Fry, arbitrary extra words］", true)]
        [TestCase("【Stephen Fry】", true)]
        [TestCase("［Unabridged］", true)]
        [TestCase("【Unabridged】", true)]
        [TestCase("(Narrated by Jim Dale)", false)]
        [TestCase("(Reprint)", false)]
        [TestCase("(réédition)", false)]
        [TestCase("(Neuauflage)", false)]
        [TestCase("（再版）", false)]
        public void only_known_metadata_parentheticals_may_be_omitted(string qualifier, bool canOmit)
        {
            var author = new Author { Name = "J.K. Rowling" };
            var book = new Book
            {
                Author = author, AnyEditionOk = true,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = "Harry Potter and the Chamber of Secrets " + qualifier, Narrator = "Stephen Fry", Monitored = true }
                }
            };
            var result = ReleaseTitleMatchScorer.FindBestMatch("J K Rowling Harry Potter and the Chamber of Secrets M4B", author.Name, new[] { book });
            Assert.That(result?.IsMatch == true, Is.EqualTo(canOmit));
            Assert.That(ReleaseTitleMatchScorer.FindBestMatch($"J K Rowling {book.Editions[0].Title} M4B", author.Name, new[] { book })?.IsMatch, Is.True);
        }

        [TestCase("Seven and the Swift (International Edition)")]
        [TestCase("Seven and the Swift: An Epic Romantic Fantasy")]
        [TestCase("Seven and the Swift: An Epic Romantic Fantasy (Shield of Sparrows Book 3)")]
        public void seven_and_the_swift_releases_never_go_to_the_dramatized_shield_of_sparrows(string editionTitle)
        {
            var author = new Author { Id = 77, Name = "Devney Perry" };
            Book CreateBook(int id, string title, string seriesName, string seriesPosition, string selectedEditionTitle) => new Book
            {
                Id = id, AuthorId = author.Id, Author = author, Title = title, MediaType = BookMediaType.Audiobook,
                SeriesName = seriesName, SeriesPosition = seriesPosition,
                Editions = new List<Edition> { new Edition { Id = id, Title = selectedEditionTitle, Monitored = true } }
            };
            var seven = CreateBook(1, "Seven and the Swift", "Shield of Sparrows", "3", editionTitle);
            var generic = CreateBook(2, "Shield of Sparrows", "Shield of Sparrows", "1", "Shield of Sparrows");
            var dramatized = CreateBook(3, "Shield of Sparrows (Part 1 of 2) [Dramatized Adaptation]", "Shield of Sparrows [Dramatized Adaptation]", "1, Part 1",
                "Shield of Sparrows (Part 1 of 2) [Dramatized Adaptation]");
            var catalog = new[] { seven, generic, dramatized };
            foreach (var release in new[] { "Devney Perry - Seven and the Swift", "Devney Perry - Seven and the Swift (Shield of Sparrows Book 3)" })
            {
                var best = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, catalog, null, catalog);
                Assert.That(best?.IsMatch == true ? best.Book : seven, Is.SameAs(seven), release);
            }

            var complete = ReleaseTitleMatchScorer.FindBestMatch("Devney Perry - " + editionTitle, author.Name, catalog, null, catalog);
            Assert.That(complete?.IsMatch == true ? complete.Book : null, Is.SameAs(seven));
        }

        [Test]
        public void unselected_sibling_editions_should_not_supply_competing_titles()
        {
            var author = new Author { Name = "Frank Herbert" };
            var target = new Book { Id = 1, Author = author, Title = "Dune", HardcoverBookId = "hc:312460" };
            var sibling = new Book
            {
                Id = 2, Author = author, Title = "Dune Messiah", HardcoverBookId = "hc:427393", AnyEditionOk = true,
                Editions = new List<Edition>
                {
                    new Edition { Id = 2, Title = "Der Herr des Wüstenplaneten", Monitored = true },
                    new Edition { Id = 3, Title = "Dune Repack", Monitored = false }
                }
            };
            var result = ReleaseTitleMatchScorer.FindBestMatch("Frank Herbert Dune Repack M4B", author.Name, new[] { target }, null, new[] { target, sibling });
            Assert.That(result?.IsMatch, Is.True);
        }

        [Test]
        public void should_return_source_spans_for_backend_title_tokens()
        {
            var tokens = ReleaseTitleMatchScorer.TokenizeWithSpans("HANDMAID'S & Café 1.5");

            Assert.That(tokens.Select(token => token.Value), Is.EqualTo(new[] { "handmaids", "and", "cafe", "1.5" }));
            Assert.That(tokens.Select(token => (token.Start, token.End)), Is.EqualTo(new[] { (0, 10), (11, 12), (13, 17), (18, 21) }));
        }

        [TestCase("한글 제목 Café")]
        [TestCase("Astorias𠀀Smut")]
        public void should_keep_span_tokenizer_unicode_output_in_parity_with_shared_normalizer(string title)
        {
            var expectedTokens = UnicodeComparisonNormalizer.NormalizeWords(title).Split(' ');

            Assert.That(ReleaseTitleMatchScorer.Tokenize(title), Is.EqualTo(expectedTokens));
        }

        [Test]
        public void should_preserve_supplementary_letters_with_token_boundaries_and_source_offsets()
        {
            var tokens = ReleaseTitleMatchScorer.TokenizeWithSpans("Astorias𠀀Smut 🌶️ 1.5");

            Assert.That(tokens.Select(token => token.Value), Is.EqualTo(new[] { "astorias𠀀smut", "1.5" }));
            Assert.That(tokens.Select(token => (token.Start, token.End)), Is.EqualTo(new[] { (0, 14), (19, 22) }));
        }

        [Test]
        public void should_not_throw_on_release_titles_containing_supplementary_plane_characters()
        {
            var tokens = ReleaseTitleMatchScorer.Tokenize("Astorias Smutty Smut 🌶️");

            Assert.That(tokens, Is.EqualTo(new[] { "astorias", "smutty", "smut" }));
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
                Editions = new List<Edition> { new Edition { Title = "The Housemaid Is Watching", Monitored = true } },
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
        public void should_treat_extra_words_as_neutral_with_structured_author_evidence()
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
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo("Voyeur"));
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
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
        public void should_shorten_the_edition_title_when_the_release_omits_the_subtitle(string releaseTitle)
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

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo(book.Title));
        }

        [Test]
        public void should_shorten_the_edition_title_without_classifying_marketing_words()
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

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo(book.Title));
        }

        [TestCase("Psychotherapie für den Laien")]
        [TestCase("La psicoterapia al alcance de todos")]
        [TestCase(null)]
        public void subtitle_fallback_should_use_only_the_selected_editions_language(string bookTitle)
        {
            var author = new Author { Name = "Viktor E. Frankl" };
            var book = new Book
            {
                Title = bookTitle, Author = author, HardcoverBookId = "hc:2574029",
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Title = "La psicoterapia al alcance de todos: Conferencias radiofónicas sobre terapéutica psíquica",
                        Subtitle = "Conferencias radiofónicas sobre terapéutica psíquica",
                        ForeignEditionId = "gr:33411216-audiobook", Monitored = true
                    }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch("Viktor E. Frankl - La psicoterapia al alcance de todos", author.Name, new[] { book });
            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo("La psicoterapia al alcance de todos"));
            Assert.That(ReleaseTitleMatchScorer.FindBestMatch("Viktor E. Frankl - Psychotherapie für den Laien", author.Name, new[] { book }), Is.Null);
        }

        [TestCase("M4B")]
        [TestCase("MP3")]
        public void same_pocket_clones_should_not_veto_edition_subtitle_fallback(string format)
        {
            var author = new Author { Name = "J.K. Rowling" };
            var title = "Harry Potter et l’Ordre du Phénix";
            var target = new Book
            {
                Id = 1, Author = author, Title = "Harry Potter and the Order of the Phoenix", HardcoverBookId = "hc:428465",
                Editions = new List<Edition> { new Edition { Title = title + ": édition illustrée", Monitored = true } }
            };
            var clone = new Book
            {
                Id = 2, Author = author, Title = target.Title, HardcoverBookId = target.HardcoverBookId,
                Editions = new List<Edition> { new Edition { Title = title, Monitored = true } }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch($"{author.Name} - {title} {format}", author.Name, new[] { target }, null, new[] { target, clone });
            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.Book, Is.SameAs(target));
            Assert.That(result.MatchedVariant, Is.EqualTo(title));
        }

        [Test]
        public void two_distinct_pockets_should_not_share_an_edition_subtitle_fallback()
        {
            var author = new Author { Name = "Brian Herbert" };
            Book Make(string title, string id) => new Book
            {
                Title = title, Author = author, HardcoverBookId = id,
                Editions = new List<Edition> { new Edition { Title = title, Monitored = true } }
            };
            var books = new[]
            {
                Make("Dune: House Atreides, Volume One", "hc:766181"),
                Make("Dune: A Whisper of Caladan Seas", "hc:2050942")
            };

            foreach (var book in books)
            {
                Assert.That(ReleaseTitleMatchScorer.FindBestMatch("Brian Herbert - Dune", author.Name, new[] { book }, null, books), Is.Null);
            }
        }

        [Test]
        public void series_prefixed_edition_should_match_its_work_title_tail()
        {
            var author = new Author { Name = "Philip Pullman" };
            var book = new Book
            {
                Title = "The Golden Compass",
                Author = author,
                SeriesName = "His Dark Materials",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Title = "His Dark Materials: The Golden Compass (Book 1)", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Philip Pullman - The Golden Compass",
                author.Name,
                new[] { book },
                author.Name,
                new[] { book });

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo("The Golden Compass"));
        }

        [Test]
        public void series_prefixed_localized_edition_should_not_use_another_language_book_title()
        {
            var author = new Author { Name = "Philip Pullman" };
            var book = new Book
            {
                Title = "The Golden Compass",
                Author = author,
                SeriesName = "His Dark Materials",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Title = "His Dark Materials: La brújula dorada", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Philip Pullman - The Golden Compass",
                author.Name,
                new[] { book },
                author.Name,
                new[] { book });

            Assert.That(result, Is.Null);
        }

        [Test]
        public void series_prefixed_edition_should_not_create_a_tail_without_its_own_series_prefix()
        {
            var author = new Author { Name = "Brian Herbert" };
            var book = new Book
            {
                Title = "Dune: The Duke of Caladan",
                Author = author,
                SeriesName = "The Caladan Trilogy",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Dune: The Duke of Caladan", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Brian Herbert - The Duke of Caladan",
                author.Name,
                new[] { book },
                author.Name,
                new[] { book });

            Assert.That(result, Is.Null);
        }

        [TestCase("Shirtaloon", "He Who Fights with Monsters", "He Who Fights with Monsters: A LitRPG Adventure")]
        [TestCase("Shirtaloon", "He Who Fights with Monsters 11", "He Who Fights with Monsters 11: A LitRPG Adventure")]
        [TestCase("Neil Gaiman", "Good Omens", "Good Omens: The Nice and Accurate Prophecies of Agnes Nutter, Witch—A Humorous Fantasy About Armageddon")]
        [TestCase("Tad Williams", "Le trône du dragon", "Le trône du dragon: édition illustrée")]
        [TestCase("Frank Herbert", "Dune: Messiah", "Dune: Messiah: édition illustrée")]
        public void should_try_full_edition_title_before_an_edition_derived_base(string authorName, string title, string editionTitle)
        {
            var author = new Author { Name = authorName };
            var book = new Book
            {
                Title = title, Author = author,
                Editions = new List<Edition> { new Edition { Title = editionTitle, Monitored = true } }
            };

            var full = ReleaseTitleMatchScorer.FindBestMatch($"{authorName} - {editionTitle}", authorName, new[] { book });
            var shortened = ReleaseTitleMatchScorer.FindBestMatch($"{authorName} - {title}", authorName, new[] { book });

            Assert.Multiple(() =>
            {
                Assert.That(full?.IsMatch, Is.True);
                Assert.That(full?.MatchedVariant, Is.EqualTo(editionTitle));
                Assert.That(shortened?.IsMatch, Is.True);
                Assert.That(shortened?.MatchedVariant, Is.EqualTo(title));
                Assert.That(shortened?.PrimaryTitle, Is.EqualTo(editionTitle));
            });
        }

        [Test]
        public void a_contradicted_full_edition_title_should_not_retry_a_shorter_title()
        {
            var author = new Author { Name = "Frank Herbert" };
            var target = new Book
            {
                Author = author, Title = "Dune Messiah", HardcoverBookId = "hc:427393",
                Editions = new List<Edition> { new Edition { Title = "Dune: Messiah", Monitored = true } }
            };
            var sibling = new Book
            {
                Author = author, Title = "Children of Dune", HardcoverBookId = "hc:427314",
                Editions = new List<Edition> { new Edition { Title = "Children of Dune", Monitored = true } }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch("Frank Herbert - Dune: Messiah - Children of Dune", author.Name, new[] { target }, null, new[] { target, sibling });
            Assert.That(result?.IsMatch, Is.False);
            Assert.That(result?.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SiblingTitleContradiction));
            Assert.That(result?.MatchedVariant, Is.EqualTo("Dune: Messiah"));
        }

        [TestCase("He Who Fights with Monsters 11", null)]
        [TestCase("Stephen King - He Who Fights with Monsters 11", "Stephen King")]
        public void subtitle_fallback_should_still_require_matching_author_evidence(string release, string authorHint)
        {
            var book = new Book
            {
                Title = "He Who Fights with Monsters 11", Author = new Author { Name = "Shirtaloon" },
                Editions = new List<Edition> { new Edition { Title = "He Who Fights with Monsters 11: A LitRPG Adventure", Monitored = true } }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(release, book.Author.Name, new[] { book }, authorHint);
            Assert.That(result?.IsMatch, Is.False);
        }

        [Test]
        public void another_row_for_the_same_provider_work_should_not_block_subtitle_fallback()
        {
            var author = new Author { Name = "Neil Gaiman" };
            var target = new Book
            {
                Id = 1, Title = "Good Omens", Author = author, HardcoverBookId = "hc:434342",
                Editions = new List<Edition> { new Edition { Title = "Good Omens: The Nice and Accurate Prophecies of Agnes Nutter, Witch", Monitored = true } }
            };
            var sameWork = new Book
            {
                Id = 2, Title = "Good Omens", Author = author, HardcoverBookId = target.HardcoverBookId,
                Editions = new List<Edition> { new Edition { Title = "Good Omens", Monitored = true } }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch("Neil Gaiman - Good Omens", author.Name, new[] { target }, null, new[] { target, sameWork });
            Assert.That(result?.IsMatch, Is.True);
        }

        [TestCase("He Who Fights with Monsters", "He Who Fights with Monsters: 11", "Shirtaloon", "Shirtaloon - He Who Fights with Monsters")]
        public void subtitle_fallback_should_preserve_number_discriminators(string title, string editionTitle, string authorName, string release)
        {
            var author = new Author { Name = authorName };
            var book = new Book
            {
                Title = title, Author = author,
                Editions = new List<Edition> { new Edition { Title = editionTitle, Monitored = true } }
            };

            Assert.That(ReleaseTitleMatchScorer.FindBestMatch(release, authorName, new[] { book }), Is.Null);
        }

        [TestCase("Frank Herbert - Dune", false)]
        [TestCase("Frank Herbert - Dune: Messiah", true)]
        public void subtitle_fallback_should_not_borrow_another_works_displayed_title(string release, bool expected)
        {
            var author = new Author { Name = "Frank Herbert" };
            var target = new Book
            {
                Id = 1, Title = "Dune", Author = author, HardcoverBookId = "hc:427393",
                Editions = new List<Edition> { new Edition { Title = "Dune: Messiah", Monitored = true } }
            };
            var sibling = new Book
            {
                Id = 2, Title = "Dune", Author = author, HardcoverBookId = "hc:312460",
                Editions = new List<Edition> { new Edition { Title = "Dune", Monitored = true } }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, new[] { target }, null, new[] { target, sibling });
            Assert.That(result?.IsMatch == true, Is.EqualTo(expected));
        }

        [TestCase("Shirtaloon - He Who Fights with Monsters 11")]
        [TestCase("Shirtaloon - He Who Fights with Monsters 11: A LitRPG Adventure")]
        public void subtitle_fallback_should_not_borrow_a_numbered_siblings_prefix(string release)
        {
            var author = new Author { Name = "Shirtaloon" };
            var target = new Book
            {
                Id = 1, Title = "He Who Fights with Monsters", Author = author,
                SeriesName = "He Who Fights with Monsters", SeriesPosition = "1",
                Editions = new List<Edition> { new Edition { Title = "He Who Fights with Monsters: A LitRPG Adventure", Monitored = true } }
            };
            var sibling = new Book
            {
                Id = 2, Title = "He Who Fights with Monsters 11", Author = author,
                Editions = new List<Edition> { new Edition { Title = "He Who Fights with Monsters 11: A LitRPG Adventure", Monitored = true } }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, new[] { target }, null, new[] { target, sibling });
            Assert.That(result, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void a_supported_subtitle_base_should_also_protect_against_a_more_specific_sibling(bool reverseCandidates)
        {
            var author = new Author { Name = "Frank Herbert" };
            var dune = new Book
            {
                Id = 1, Title = "Dune", Author = author, HardcoverBookId = "hc:312460",
                Editions = new List<Edition> { new Edition { Title = "Dune", Monitored = true } }
            };
            var messiah = new Book
            {
                Id = 2, Title = "Dune Messiah", Author = author, HardcoverBookId = "hc:427393",
                Editions = new List<Edition> { new Edition { Title = "Dune Messiah: édition illustrée", Monitored = true } }
            };
            var catalog = reverseCandidates ? new[] { messiah, dune } : new[] { dune, messiah };
            var release = "Frank Herbert - Dune Messiah";

            var generic = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, new[] { dune }, null, catalog);
            var best = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, catalog, null, catalog);
            Assert.Multiple(() =>
            {
                Assert.That(generic?.IsMatch, Is.False);
                Assert.That(generic?.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SiblingTitleContradiction));
                Assert.That(best?.IsMatch, Is.True);
                Assert.That(best?.Book, Is.SameAs(messiah));
            });
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
        public void edition_subtitle_fallback_should_not_depend_on_the_book_title_without_a_collision()
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

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo("Sherlock Holmes"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void edition_subtitle_fallback_should_require_a_unique_pocket(bool includeSibling)
        {
            var author = new Author { Name = "J.K. Rowling" };
            var book = new Book
            {
                Title = "Pocket Potters: Harry Potter: Little Guides to the Harry Potter Stories",
                HardcoverBookId = "hc:2181337",
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

            var catalog = new List<Book> { book };
            if (includeSibling)
            {
                catalog.Add(new Book
                {
                    Title = "Pocket Potters: Hermione Granger", Author = author, HardcoverBookId = "hc:2181171",
                    Editions = new List<Edition> { new Edition { Title = "Pocket Potters: Hermione Granger", Monitored = true } }
                });
            }

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "J K Rowling - Pocket Potters",
                "J.K. Rowling",
                new[] { book },
                "J K Rowling",
                catalog);

            Assert.That(result?.IsMatch == true, Is.EqualTo(!includeSibling));
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
        public void should_reject_when_any_repeated_title_occurrence_is_explained_by_a_sibling()
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
                Editions = new List<Edition> { new Edition { Title = "The Housemaid Is Watching", Monitored = true } },
                Author = author
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "The Housemaid - Freida McFadden - uploader - The Housemaid Is Watching sample",
                "Freida McFadden",
                new[] { targetBook },
                "Freida McFadden",
                new[] { targetBook, siblingBook });

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.False);
            Assert.That(result.MatchedVariant, Is.EqualTo("The Housemaid"));
            Assert.That(result.MeaningfulLeftovers, Does.Contain("The Housemaid Is Watching"));
        }

        [TestCase("(Narrated by Stephen Fry)", "Stephen Fry", true)]
        [TestCase("[Read by Stephen Fry]", "Stephen Fry", true)]
        [TestCase("(Performed by Stephen Fry)", "Stephen Fry", true)]
        [TestCase("(Stephen Fry)", "Stephen Fry", true)]
        [TestCase("(Narrated by Stephen Fry)", "Jim Dale", true)]
        [TestCase("(Narrated by Stephen Fry)", null, true)]
        [TestCase("(Narrated by Jim Dale)", "Stephen Fry", false)]
        [TestCase("(Narrated by Stephen Fry, Part 2 of 2)", "Stephen Fry", true)]
        [TestCase("(Narrated by Stephen Fry) (Part 2 of 2)", "Stephen Fry", false)]

        [TestCase("(Narrated by Stephen Fry): Part 2 of 2", "Stephen Fry", false)]
        [TestCase("(Stephen Fry and the Prisoner of Azkaban)", "Stephen Fry", true)]
        public void known_narrator_credits_may_be_omitted_but_other_title_content_remains_required(string qualifier, string selectedNarrator, bool expectedMatch)
        {
            var author = new Author { Name = "J.K. Rowling", GoodreadsAuthorId = "gr:1077326" };
            var book = new Book
            {
                Title = "Harry Potter and the Chamber of Secrets",
                Author = author,
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Harry Potter and the Chamber of Secrets " + qualifier,
                        Monitored = true,
                        NarratorNames = selectedNarrator == null ? new List<string>() : new List<string> { selectedNarrator }
                    },
                    new Edition { Id = 2, Title = "Harry Potter and the Chamber of Secrets", Narrator = "Stephen Fry" }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "J K Rowling read by Stephen Fry - Harry Potter and the Chamber of Secrets (2000) MP3",
                author.Name, new[] { book });

            Assert.That(result?.IsMatch == true, Is.EqualTo(expectedMatch));
            if (expectedMatch)
            {
                Assert.That(result.MatchedVariant, Is.EqualTo(book.Title));
                Assert.That(result.PrimaryTitle, Is.EqualTo(book.Editions[0].Title));
            }
        }

        [TestCase("J K Rowling - Harry Potter and the Chamber of Secrets (2000) MP3", true)]
        [TestCase("J K Rowling read by Jim Dale - Harry Potter and the Chamber of Secrets (2000) MP3", true)]
        [TestCase("J K Rowling - Harry Potter and the Chamber of Secrets (Narrated by Stephen Fry) (2000) MP3", true)]
        [TestCase("J K Rowling read by Jim Dale - Harry Potter and the Chamber of Secrets (Narrated by Stephen Fry) (2000) MP3", true)]
        [TestCase("J K Rowling read by Stephen Fry - Harry Potter and the Prisoner of Azkaban (2000) MP3", false)]
        public void known_narrator_annotation_may_be_omitted_without_an_automatic_narrator_gate(string release, bool expected)
        {
            var author = new Author { Name = "J.K. Rowling" };
            var book = new Book
            {
                Title = "Harry Potter and the Chamber of Secrets",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Harry Potter and the Chamber of Secrets (Narrated by Stephen Fry)",
                        Monitored = true,
                        Narrator = "Stephen Fry"
                    }
                }
            };

            Assert.That(ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, new[] { book })?.IsMatch == true, Is.EqualTo(expected));
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
                SeriesName = "Dresden Files",
                SeriesPosition = "3",
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

        [TestCase("Part 2 of 2")]
        [TestCase("Part Two")]
        [TestCase("Part II")]
        [TestCase("The Other Story")]
        public void substantive_bracketed_title_content_must_not_be_omitted(string qualifier)
        {
            var author = new Author { Name = "Rebecca Yarros" };
            var book = new Book
            {
                Title = $"Iron Flame ({qualifier}) (Dramatized Adaptation)",
                Author = author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = $"Iron Flame ({qualifier}) (Dramatized Adaptation)", Monitored = true }
                }
            };

            var result = ReleaseTitleMatchScorer.FindBestMatch(
                "Rebecca Yarros - Iron Flame [GraphicAudio]",
                "Rebecca Yarros",
                new[] { book },
                null,
                new[] { book });

            Assert.That(result?.IsMatch == true, Is.False);
            Assert.That(ReleaseTitleMatchScorer.FindBestMatch($"Rebecca Yarros - Iron Flame ({qualifier}) [GraphicAudio]",
                author.Name, new[] { book })?.IsMatch, Is.True);
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
        public void should_ignore_unselected_editions_even_when_their_titles_extend_the_selected_title()
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
            Assert.That(result.IsMatch, Is.True);
            Assert.That(result.MatchedVariant, Is.EqualTo("Dune"));
            Assert.That(result.MeaningfulLeftovers, Is.Empty);
        }

        [Test]
        public void should_use_the_displayed_sibling_edition_as_contradiction_evidence()
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
            Assert.That(result.MeaningfulLeftovers, Does.Contain("Sons of Ares"));
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

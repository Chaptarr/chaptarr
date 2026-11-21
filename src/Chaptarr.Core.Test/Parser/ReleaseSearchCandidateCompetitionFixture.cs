using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleaseSearchCandidateCompetitionFixture
    {
        private static Book Make(int id, Author author, string title, string series = "", string position = "", string work = null)
        {
            return new Book
            {
                Id = id, AuthorId = author.Id, Author = author, Title = title,
                SeriesName = series, SeriesPosition = position,
                HardcoverBookId = work ?? $"hc:{id}",
                Editions = new List<Edition> { new Edition { Id = id, Title = title, Monitored = true } }
            };
        }

        private static bool Matches(Book target, List<Book> catalog, string releaseTitle, string structuredAuthor = null, string structuredBook = null)
        {
            return FindMatch(target, catalog, releaseTitle, structuredAuthor, structuredBook)?.IsMatch == true;
        }

        private static TitleMatchResult FindMatch(Book target, List<Book> catalog, string releaseTitle, string structuredAuthor = null, string structuredBook = null)
        {
            var release = new ReleaseInfo { Title = releaseTitle, Author = structuredAuthor, Book = structuredBook };
            return ReleaseTitleMatchScorer.FindBestMatchForSearch(release, target.Author.Name, new[] { target },
                structuredAuthor, catalog, new ReleaseTitleMatchScorer.CatalogPreparation());
        }

        [TestCase("Brandon Sanderson - Mistborn: The Final Empire", 33880)]
        [TestCase("Brandon.Sanderson.Mistborn.The.Final.Empire", 33880)]
        [TestCase("Brandon Sanderson - The Final Empire", 33880)]
        [TestCase("Brandon Sanderson - Mistborn", 34086)]
        [TestCase("Brandon Sanderson - Mistborn: The Final Empire - Annotations", 34092)]
        public void complete_title_and_series_evidence_beats_an_omitted_qualification(string release, int expectedId)
        {
            // Captured catalog titles, IDs and memberships; constructed releases.
            var author = new Author { Id = 1, Name = "Brandon Sanderson" };
            var novel = Make(33880, author, "The Final Empire", "Mistborn, Era 1: Original Trilogy", "1");
            novel.Editions[0].Id = 81885;
            var shortTitle = Make(34086, author, "Mistborn");
            shortTitle.Editions[0].Id = 98105;
            var annotations = Make(34092, author, "Mistborn: The Final Empire - Annotations");
            annotations.Editions[0].Id = 82652;
            annotations.Editions[0].Subtitle = "The Final Empire - Annotations";
            AssertSearchAndRssWinner(new List<Book> { novel, shortTitle, annotations }, release, expectedId);
        }

        [TestCase("Example Writer - Crystal Sea: Dawn", 1)]
        [TestCase("Example Writer - Crystal Sea: Dawn - Commentary", 2)]
        [TestCase("Example Writer - Dawn", 1)]
        public void series_explanation_does_not_require_a_third_book_named_after_the_series(string release, int expectedId)
        {
            var author = new Author { Id = 1, Name = "Example Writer" };
            var target = Make(1, author, "Dawn", "Crystal Sea Chronicles", "1");
            var commentary = Make(2, author, "Crystal Sea: Dawn - Commentary");
            AssertSearchAndRssWinner(new List<Book> { target, commentary }, release, expectedId);
        }

        [TestCase("Frank Herbert - Dune", 1)]
        [TestCase("Frank Herbert - Dune Messiah", 2)]
        [TestCase("Frank Herbert - Dune - Dune: Messiah", 2)]
        public void a_more_specific_title_with_a_missing_suffix_still_beats_a_contained_title(string release, int expectedId)
        {
            var author = new Author { Id = 1, Name = "Frank Herbert" };
            var catalog = new List<Book>
            {
                Make(1, author, "Dune", "Dune", "1"),
                Make(2, author, "Dune Messiah: édition illustrée", "Dune", "2")
            };
            AssertSearchAndRssWinner(catalog, release, expectedId);
        }

        [TestCase("Crystal Sea Chronicles", true)]
        [TestCase("Crystal Seaside Chronicles", false)]
        [TestCase("Sea Crystal Chronicles", false)]
        [TestCase("Crystal Distant Sea Chronicles", false)]
        [TestCase("", false)]
        public void a_series_explanation_requires_an_exact_contiguous_phrase(string series, bool expected)
        {
            var author = new Author { Id = 1, Name = "Example Writer" };
            var target = Make(1, author, "Dawn", series, "1");
            var sibling = Make(2, author, "Crystal Sea");
            var catalog = new List<Book> { target, sibling };
            Assert.That(Matches(target, catalog, "Example Writer - Crystal Sea: Dawn"), Is.EqualTo(expected));
        }

        [Test]
        public void unrelated_omitted_suffixes_remain_ambiguous()
        {
            var author = new Author { Id = 1, Name = "Example Writer" };
            var catalog = new List<Book> { Make(1, author, "Dawn: Commentary"), Make(2, author, "Dawn: Study Guide") };
            foreach (var target in catalog)
            {
                Assert.That(Matches(target, catalog, "Example Writer - Dawn"), Is.False);
            }
        }

        [TestCase("Example Writer - Dawn", 1)]
        [TestCase("Example Writer - Dawn: Commentary", 2)]
        public void known_series_prefix_is_not_an_unsupported_title_omission(string release, int expectedId)
        {
            var author = new Author { Id = 1, Name = "Example Writer" };
            var catalog = new List<Book>
            {
                Make(1, author, "Crystal Sea: Dawn", "Crystal Sea", "1"),
                Make(2, author, "Dawn: Commentary")
            };
            AssertSearchAndRssWinner(catalog, release, expectedId);
        }

        private static void AssertSearchAndRssWinner(List<Book> catalog, string title, int expectedId)
        {
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            {
                foreach (var book in catalog)
                {
                    book.MediaType = media;
                }

                foreach (var reverse in new[] { false, true })
                {
                    if (reverse)
                    {
                        catalog.Reverse();
                    }

                    var release = new ReleaseInfo { Title = title };
                    var rss = ReleaseTitleMatchScorer.FindBestMatches(release, catalog[0].Author.Name,
                        catalog, null, catalog, new ReleaseTitleMatchScorer.CatalogPreparation());
                    Assert.That(rss.Select(match => match.Book.Id), Is.EqualTo(new[] { expectedId }),
                        $"RSS {media}, reversed {reverse}");
                    foreach (var target in catalog)
                    {
                        Assert.That(Matches(target, catalog, title), Is.EqualTo(target.Id == expectedId),
                            $"Search {target.Id}, {media}, reversed {reverse}");
                    }
                }
            }
        }

        private static (Author Author, List<Book> Catalog, Book Target) JamesHunt()
        {
            var author = new Author { Id = 631, Name = "James Hunt" };
            var target = Make(42421, author, "Powerless", "A Tale Of Survival in a Powerless World", "1-4");
            var catalog = new List<Book>
            {
                target,
                Make(42407, author, "The Final Homestead", "EMP Survival in a Powerless World", "1"),
                Make(42428, author, "The Last Orchard", "EMP Survival in a Powerless World", "8"),
                Make(42454, author, "Broken Roads", "A Tale of Survival in a Powerless World", "2"),
                Make(42404, author, "Broken Lines", "A Tale of Survival in a Powerless World", "1")
            };

            return (author, catalog, target);
        }

        [TestCase("Scott Bartlett - Spacers: Thatcher's Gambit")]
        [TestCase("Scott.Bartlett.Spacers.Thatcher's.Gambit")]
        [TestCase("Scott Bartlett - Thatcher's Gambit")]
        public void linked_series_should_explain_a_competing_title_without_being_required(string release)
        {
            // Captured catalog identities and membership; constructed release strings.
            var author = new Author { Id = 554, Name = "Scott Bartlett" };
            var target = Make(36369, author, "Thatcher's Gambit");
            target.Editions[0].Id = 89719;
            target.SeriesLinks = new List<SeriesBookLink>
            {
                new() { BookId = target.Id, Position = "6", Series = new Series { Title = "Spacers", GoodreadsSeriesId = "gr:288426" } }
            };
            var sibling = Make(36305, author, "Spacers");
            sibling.Editions[0].Id = 89632;
            var catalog = new List<Book> { sibling, target };

            var result = FindMatch(target, catalog, release);
            var best = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, catalog, null, catalog);
            Assert.Multiple(() =>
            {
                Assert.That(result?.IsMatch, Is.True);
                Assert.That(best?.IsMatch, Is.True);
                Assert.That(best?.Book.Editions[0].Id, Is.EqualTo(89719));
                Assert.That(Matches(sibling, catalog, release), Is.False);
            });
        }

        [TestCase("Example Writer - Long Series Name: Dawn")]
        [TestCase("Example Writer - Dawn")]
        public void explained_series_should_not_beat_a_shorter_independent_edition_title(string release)
        {
            var author = new Author { Id = 1, Name = "Example Writer" };
            var target = Make(1, author, "Dawn", "Long Series Name", "1");
            var sibling = Make(2, author, "Long Series Name");
            var catalog = new List<Book> { sibling, target };
            var best = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, catalog, null, catalog);
            Assert.That(best?.IsMatch, Is.True);
            Assert.That(best?.Book, Is.SameAs(target));
        }

        [TestCase("Frank Herbert - Dune", 1)]
        [TestCase("Frank Herbert - Dune: Messiah", 2)]
        [TestCase("Frank.Herbert.Dune.Messiah", 2)]
        public void a_contained_title_must_not_defeat_the_complete_edition_title(string release, int expectedId)
        {
            var author = new Author { Id = 1, Name = "Frank Herbert" };
            var catalog = new List<Book> { Make(1, author, "Dune", "Dune", "1"), Make(2, author, "Dune: Messiah", "Dune", "2") };
            foreach (var target in catalog)
            {
                Assert.That(Matches(target, catalog, release), Is.EqualTo(target.Id == expectedId));
            }
        }

        [TestCase("James Hunt - The Final Homestead - EMP Survival in a Powerless World (epub)", false)]
        [TestCase("Broken Roads: A Tale of Survival in a Powerless World", false)]
        [TestCase("The Last Orchard: A Tale Of Survival In A Powerless World", false)]
        [TestCase("James Hunt - Powerless (epub)", true)]
        [TestCase("James Hunt - Powerless - A Tale Of Survival in a Powerless World", true)]
        public void a_sibling_that_explains_the_release_better_should_beat_the_searched_book(string release, bool expected)
        {
            var (_, catalog, target) = JamesHunt();

            Assert.That(Matches(target, catalog, release), Is.EqualTo(expected), release);
        }

        [Test]
        public void author_evidence_is_still_required_and_a_structured_author_still_supplies_it()
        {
            var (_, catalog, target) = JamesHunt();

            // Rule 1: a bare title with no author evidence anywhere is not acceptance evidence.
            Assert.That(Matches(target, catalog, "Powerless"), Is.False);

            // A structured author field (MAM-style) supplies that evidence.
            Assert.That(Matches(target, catalog, "Powerless", "James Hunt"), Is.True);

            // ...but it must not rescue a release a sibling explains better.
            Assert.That(Matches(target, catalog, "Broken Roads: A Tale of Survival in a Powerless World", "James Hunt"), Is.False);
        }

        [Test]
        public void a_nonblank_structured_book_is_the_only_title_and_competition_evidence()
        {
            var (_, catalog, target) = JamesHunt();
            const string rawSiblingTitle = "James Hunt - The Final Homestead - EMP Survival in a Powerless World";

            var structuredTarget = FindMatch(target, catalog, rawSiblingTitle, "James Hunt", "Powerless");
            var structuredSibling = FindMatch(target, catalog, "James Hunt - Powerless", "James Hunt", "The Final Homestead");

            Assert.Multiple(() =>
            {
                Assert.That(structuredTarget?.IsMatch, Is.True, "A sibling named only in the raw title must not override the structured target title.");
                Assert.That(structuredTarget?.Book, Is.SameAs(target));
                Assert.That(structuredTarget?.EvidenceTitle, Is.EqualTo("Powerless"));
                Assert.That(structuredSibling?.IsMatch, Is.Not.True, "The raw title must not rescue a target contradicted by the structured title field.");
            });
        }

        [Test]
        public void an_empty_structured_author_hint_falls_back_to_author_evidence_in_the_raw_title()
        {
            var (_, catalog, target) = JamesHunt();

            var result = FindMatch(target, catalog, "James Hunt - Powerless", string.Empty, "Powerless");

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result?.EvidenceTitle, Is.EqualTo("Powerless"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void a_missing_structured_book_keeps_raw_title_matching_and_competition(string structuredBook)
        {
            var (_, catalog, target) = JamesHunt();

            var targetTitle = FindMatch(target, catalog, "James Hunt - Powerless", "James Hunt", structuredBook);
            var siblingTitle = FindMatch(target, catalog,
                "James Hunt - The Final Homestead - EMP Survival in a Powerless World", "James Hunt", structuredBook);

            Assert.Multiple(() =>
            {
                Assert.That(targetTitle?.IsMatch, Is.True);
                Assert.That(siblingTitle?.IsMatch, Is.Not.True);
            });
        }

        [Test]
        public void a_same_work_clone_must_not_out_compete_the_searched_book()
        {
            var author = new Author { Id = 371, Name = "Frank Herbert" };
            var target = Make(39406, author, "Dune", "Dune", "1", "hc:312460");
            var clone = Make(23727, author, "Dune", "Dune", "1", "hc:312460"); // audiobook row, SAME work
            var catalog = new List<Book> { target, clone, Make(39408, author, "Dune Messiah", "Dune", "2") };

            Assert.That(Matches(target, catalog, "Frank Herbert - Dune (epub)"), Is.True);
            Assert.That(Matches(target, catalog, "Frank Herbert - [Dune 01] - Dune (retail) (epub)"), Is.True);
        }

        [Test]
        public void an_edition_titled_as_the_author_must_not_compete()
        {
            var author = new Author { Id = 371, Name = "Frank Herbert" };
            var target = Make(39406, author, "Dune", "Dune", "1");
            var catalog = new List<Book> { target, Make(39446, author, "Frank Herbert") };

            // The author-named edition is author evidence, never a rival title.
            Assert.That(Matches(target, catalog, "Frank Herbert - Dune (epub)"), Is.True);
            Assert.That(Matches(target, catalog, "Dune by Frank Herbert EPUB"), Is.True);
        }

        [Test]
        public void a_generic_series_title_should_still_match_its_own_releases()
        {
            var author = new Author { Id = 371, Name = "Frank Herbert" };
            var target = Make(39406, author, "Dune", "Dune", "1");
            var catalog = new List<Book>
            {
                target,
                Make(39408, author, "Dune Messiah", "Dune", "2"),
                Make(23733, author, "Dune: The Gateway Collection", "Dune", "1-6"),
                Make(50192, author, "The Road to Dune")
            };

            Assert.That(Matches(target, catalog, "Frank Herbert - Dune (epub)"), Is.True);
            Assert.That(Matches(target, catalog, "Herbert, Frank - Dune 01 - Dune"), Is.True);
            Assert.That(Matches(target, catalog, "Dune The Gateway Collection - Frank Herbert"), Is.False);
            Assert.That(Matches(target, catalog, "The Road to Dune"), Is.False);
            Assert.That(Matches(target, catalog, "Frank Herbert - Dune Messiah (epub)"), Is.False);
        }

        [Test]
        public void series_decoration_should_survive_candidate_competition()
        {
            var author = new Author { Id = 617, Name = "J.K. Rowling" };
            var target = Make(44388, author, "Pocket Potters: Hermione Granger", "Pocket Potters", "3");
            var catalog = new List<Book>
            {
                target,
                Make(44384, author, "Pocket Potters: Harry Potter", "Pocket Potters", "1"),
                Make(44389, author, "Pocket Potters: Ron Weasley", "Pocket Potters", "2")
            };

            Assert.That(Matches(target, catalog, "J.K. Rowling - Pocket Potters 3 - Hermione Granger"), Is.True);
            Assert.That(Matches(target, catalog, "J.K. Rowling - Pocket Potters: Hermione Granger"), Is.True);
            Assert.That(Matches(target, catalog, "J.K. Rowling - Pocket Potters 2 - Ron Weasley"), Is.False);
        }

        [Test]
        public void a_sibling_named_only_inside_the_targets_own_subtitle_should_not_win()
        {
            var author = new Author { Id = 9001, Name = "George R.R. Martin" };
            var target = Make(1, author, "Fire & Blood: 300 Years Before A Game of Thrones");
            var catalog = new List<Book> { target, Make(2, author, "A Game of Thrones", "A Song of Ice and Fire", "1") };

            // The sibling title is part of the target's own stored title, so it cannot out-explain it.
            Assert.That(Matches(target, catalog, "George R.R. Martin - Fire & Blood: 300 Years Before A Game of Thrones"), Is.True);
        }

        [Test]
        public void an_unlinked_same_title_sibling_should_not_displace_the_searched_book()
        {
            var author = new Author { Id = 371, Name = "Frank Herbert" };
            var target = Make(39406, author, "Dune", "Dune", "1", "hc:312460");
            var unlinkedTwin = Make(23793, author, "Dune", "Dune", "1", work: null);
            unlinkedTwin.HardcoverBookId = null;
            var otherWorkTwin = Make(51004, author, "Dune", "Dune", "1", "hc:1356611");
            var catalog = new List<Book> { target, unlinkedTwin, otherWorkTwin };

            var release = new ReleaseInfo { Title = "Frank Herbert - Dune (epub)", Author = "Frank Herbert" };
            var result = ReleaseTitleMatchScorer.FindBestMatchForSearch(release, author.Name, new[] { target },
                "Frank Herbert", catalog, new ReleaseTitleMatchScorer.CatalogPreparation());

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.Book.Id, Is.EqualTo(39406));
        }

        [TestCase("The Richest Man in Babylon: Timeless Financial Wisdom for Today's Economy")]
        [TestCase("THE RICHEST MAN IN BABYLON — TIMELESS FINANCIAL WISDOM FOR TODAY’S ECONOMY")]
        public void identical_full_edition_titles_should_not_block_subtitle_fallback(string duplicateTitle)
        {
            var author = new Author { Id = 1, Name = "George S. Clason" };
            var target = Make(1, author, "The Richest Man in Babylon: Timeless Financial Wisdom for Today's Economy");
            var twin = Make(2, author, duplicateTitle, "Financial Classics", "3");
            target.MediaType = twin.MediaType = BookMediaType.Audiobook;
            target.Editions[0].ReadingFormatId = 2;
            twin.Editions[0].ReadingFormatId = 1;
            twin.Title = "A different work-level title";
            Assert.That(WorkIdMatcher.WorkIdMatches(target, twin), Is.False);

            var release = new ReleaseInfo { Title = "George S. Clason - The Richest Man in Babylon" };
            foreach (var catalog in new[] { new[] { target }, new[] { target, twin }, new[] { twin, target } })
            {
                var result = ReleaseTitleMatchScorer.FindBestMatchForSearch(release, author.Name, new[] { target },
                    null, catalog, new ReleaseTitleMatchScorer.CatalogPreparation());

                Assert.That(result?.IsMatch, Is.True);
                Assert.That(result.Book, Is.SameAs(target));
                Assert.That(result.MatchedVariant, Is.EqualTo("The Richest Man in Babylon"));
            }
        }

        [Test]
        public void a_leading_article_on_a_sibling_title_must_still_block_the_contradiction()
        {
            var author = new Author { Id = 371, Name = "Frank Herbert" };
            var target = Make(39406, author, "Dune", "Dune", "1");
            var encyclopedia = Make(51049, author, "The Dune Encyclopedia");
            target.Editions[0].Language = "English";
            encyclopedia.Editions[0].Language = "English";
            target.AudiobookMonitored = true;
            encyclopedia.AudiobookMonitored = false;
            var catalog = new List<Book> { target, encyclopedia };

            var searchedAsDune = FindMatch(target, catalog, "Dune Encyclopedia - Frank Herbert", "Frank Herbert");
            Assert.That(searchedAsDune?.IsMatch == true, Is.False);
            Assert.That(searchedAsDune?.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SiblingTitleContradiction));

            var searchedAsEncyclopedia = FindMatch(encyclopedia, catalog, "Dune Encyclopedia - Frank Herbert", "Frank Herbert");
            Assert.That(searchedAsEncyclopedia?.IsMatch, Is.True);
            Assert.That(searchedAsEncyclopedia.Book, Is.SameAs(encyclopedia));

            // The no-article sibling title remains a contradiction control.
            var siblingWithoutArticle = Make(51050, author, "Dune Encyclopedia");
            siblingWithoutArticle.Editions[0].Language = "English";
            var catalogNoArticle = new List<Book> { target, siblingWithoutArticle };
            Assert.That(Matches(target, catalogNoArticle, "Dune Encyclopedia - Frank Herbert", "Frank Herbert"), Is.False);
        }

        [TestCase(null)]
        [TestCase("und")]
        [TestCase("dan")]
        [TestCase("ita")]
        [TestCase("unknown")]
        [TestCase("French")]
        public void a_title_initial_article_is_not_optional_without_a_listed_edition_language(string editionLanguage)
        {
            var author = new Author { Id = 9901, Name = "Test Writer" };
            var target = Make(9902, author, "The Amber Road");
            target.Editions[0].Language = editionLanguage;

            var result = FindMatch(target, new List<Book> { target }, "Test Writer - Amber Road", "Test Writer");
            var exactTitle = FindMatch(target, new List<Book> { target }, "Test Writer - The Amber Road", "Test Writer");

            Assert.That(result?.IsMatch == true, Is.False);
            Assert.That(exactTitle?.IsMatch, Is.True);
            Assert.That(exactTitle.MatchedVariant, Is.EqualTo("The Amber Road"));
        }

        [TestCase("English", "The", "it")]
        public void a_subtitle_initial_article_uses_the_monitored_editions_language_and_full_title_variant(string editionLanguage, string article, string bookLanguageCode)
        {
            var author = new Author { Id = 9903, Name = "Test Writer" };
            var target = Make(9904, author, "Book Level Decoy", work: "hc:9904");
            target.LanguageCode = bookLanguageCode;
            target.LanguageName = bookLanguageCode == "it" ? "Italian" : "English";
            var editionTitle = "Dune: " + article + " Regno";
            target.Editions = new List<Edition>
            {
                new Edition { Id = 9903, Title = "The Unmonitored Name", Language = "English", Monitored = false },
                new Edition { Id = 9904, Title = editionTitle, Language = editionLanguage, Monitored = true }
            };

            var sameWorkEdition = Make(9905, author, "Dune: Regno", work: "hc:9904");
            sameWorkEdition.Editions[0].Language = "English";
            var catalog = new List<Book> { target, sameWorkEdition };

            var result = FindMatch(target, catalog, "Dune Regno - Test Writer", "Test Writer");

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.Book, Is.SameAs(target));
            Assert.That(result.PrimaryTitle, Is.EqualTo(editionTitle));
            Assert.That(result.MatchedVariant, Is.EqualTo("Dune: Regno"));
        }

        [Test]
        public void a_subtitle_initial_article_may_be_inserted_at_the_subtitle_boundary()
        {
            var author = new Author { Id = 9905, Name = "Test Writer" };
            var target = Make(9906, author, "Terrible Worlds: Destinations");
            target.Editions[0].Language = "English";

            var result = FindMatch(target, new List<Book> { target }, "Terrible Worlds: The Destinations - Test Writer", "Test Writer");

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.Book, Is.SameAs(target));
            Assert.That(result.MatchedVariant, Is.EqualTo("Terrible Worlds: The Destinations"));
        }

        [TestCase("English", "A")]
        [TestCase("English", "An")]
        [TestCase("eng", "The")]
        [TestCase("en", "The")]
        [TestCase("English", "The")]
        public void an_initial_catalog_article_may_be_omitted_for_its_listed_edition_language(string language, string article)
        {
            var author = new Author { Id = 9911, Name = "Test Writer" };
            var title = article + " Paper Lanterns";
            var target = Make(9912, author, title);
            target.Editions[0].Language = language;

            var result = FindMatch(target, new List<Book> { target }, "Test Writer - Paper Lanterns", "Test Writer");

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.Book, Is.SameAs(target));
            Assert.That(result.PrimaryTitle, Is.EqualTo(title));
        }

        [TestCase("Il")]
        [TestCase("Lo")]
        [TestCase("La")]
        [TestCase("I")]
        [TestCase("Gli")]
        [TestCase("Le")]
        [TestCase("Un")]
        [TestCase("Uno")]
        [TestCase("Una")]
        public void an_unsupported_italian_article_must_remain_in_the_title(string article)
        {
            var author = new Author { Id = 9911, Name = "Test Writer" };
            var title = article + " Paper Lanterns";
            var target = Make(9912, author, title);
            target.Editions[0].Language = "ita";

            var omittedArticle = FindMatch(target, new List<Book> { target }, "Test Writer - Paper Lanterns", "Test Writer");
            var exactTitle = FindMatch(target, new List<Book> { target }, "Test Writer - " + title, "Test Writer");

            Assert.That(omittedArticle?.IsMatch == true, Is.False);
            Assert.That(exactTitle?.IsMatch, Is.True);
        }

        [Test]
        public void an_unlisted_subtitle_article_uses_only_the_existing_generic_head_fallback()
        {
            var author = new Author { Id = 9913, Name = "Test Writer" };
            var target = Make(9914, author, "Tale: The Garden");
            target.LanguageCode = "en";
            target.LanguageName = "English";
            target.Editions[0].Language = "French";

            var result = FindMatch(target, new List<Book> { target }, "Test Writer - Tale Garden", "Test Writer");

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.Book, Is.SameAs(target));
            Assert.That(result.MatchedVariant, Is.EqualTo("Tale"));
        }

        [TestCase("La coscienza di Zeno", true)]
        [TestCase("Coscienza di Zeno", false)]
        [TestCase("Coscienza Zeno", false)]
        [TestCase("Coscienza de Zeno", false)]
        public void an_italian_provider_title_requires_its_complete_edition_title(string releaseTitle, bool expected)
        {
            var author = new Author { Id = 9950, Name = "Italo Svevo" };
            var target = Make(9951, author, "La coscienza di Zeno", work: "hc:591277");
            target.Editions[0].Language = "ita";

            var result = FindMatch(target, new List<Book> { target }, "Italo Svevo - " + releaseTitle, author.Name);

            Assert.That(result?.IsMatch == true, Is.EqualTo(expected));
        }

        [TestCase("The Name of the Wind", "Name Wind")]
        [TestCase("I Married a Naga", "Married a Naga")]
        [TestCase("The Amber Road", "Amber")]
        [TestCase("The Amber Road", "Amber Roadd")]
        [TestCase("The Dune Encyclopedia", "DuneThe Encyclopedia")]
        [TestCase("I Married a Naga", "I Married Naga")]
        public void an_article_alias_must_preserve_all_other_title_words_and_boundaries(string editionTitle, string releaseBookTitle)
        {
            var author = new Author { Id = 9915, Name = "Test Writer" };
            var target = Make(9916, author, editionTitle);
            target.Editions[0].Language = "English";

            var result = FindMatch(target, new List<Book> { target }, "Test Writer - " + releaseBookTitle, "Test Writer");

            Assert.That(result?.IsMatch == true, Is.False);
        }

        [Test]
        public void a_short_exact_title_remains_a_valid_match()
        {
            var author = new Author { Id = 9917, Name = "Test Writer" };
            var target = Make(9918, author, "It");
            target.Editions[0].Language = "English";

            var result = FindMatch(target, new List<Book> { target }, "Test Writer - It", "Test Writer");

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.Book, Is.SameAs(target));
        }

        [TestCase("Stand")]
        [TestCase("A Stand")]
        public void an_initial_article_alias_must_not_steal_a_distinct_full_sibling_title(string siblingTitle)
        {
            var author = new Author { Id = 9919, Name = "Test Writer" };
            var target = Make(9920, author, "The Stand");
            var sibling = Make(9921, author, siblingTitle);
            target.Editions[0].Language = "English";
            sibling.Editions[0].Language = "English";

            var result = FindMatch(target, new List<Book> { target, sibling }, "Test Writer - Stand", "Test Writer");

            Assert.That(result?.IsMatch == true, Is.False);
        }

        [Test]
        public void an_article_alias_collision_uses_existing_compact_split_title_equivalence()
        {
            var author = new Author { Id = 9931, Name = "Test Writer" };
            var target = Make(9932, author, "The Muad’Dib");
            var sibling = Make(9933, author, "MuadDib");
            target.Editions[0].Language = "English";

            var result = FindMatch(target, new List<Book> { target, sibling }, "Muad Dib - Test Writer", "Test Writer");

            Assert.That(result?.IsMatch == true, Is.False);
        }

        [Test]
        public void an_article_alias_cannot_combine_with_an_omitted_title_qualifier_to_collapse_distinct_full_titles()
        {
            var author = new Author { Id = 9934, Name = "Test Writer" };
            var target = Make(9935, author, "The Stand (Complete Edition)");
            var sibling = Make(9936, author, "A Stand (Complete Edition)");
            target.Editions[0].Language = "English";
            sibling.Editions[0].Language = "English";

            var result = FindMatch(target, new List<Book> { target, sibling }, "Stand - Test Writer", "Test Writer");

            Assert.That(result?.IsMatch == true, Is.False);
        }

        [TestCase(BookMediaType.Audiobook, BookMediaType.Ebook)]
        [TestCase(BookMediaType.Ebook, BookMediaType.Audiobook)]
        public void a_sibling_on_the_other_media_side_cannot_veto_the_searched_book(BookMediaType targetMedia, BookMediaType siblingMedia)
        {
            var author = new Author { Id = 9922, Name = "Test Writer" };
            var target = Make(9923, author, "Dune");
            var sibling = Make(9924, author, "The Dune Encyclopedia");
            target.MediaType = targetMedia;
            sibling.MediaType = siblingMedia;
            target.Editions[0].Language = "English";
            sibling.Editions[0].Language = "English";

            var result = FindMatch(target, new List<Book> { target, sibling }, "Dune Encyclopedia - Test Writer", "Test Writer");

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.Book, Is.SameAs(target));
        }

        [Test]
        public void a_same_work_duplicate_with_a_different_title_does_not_compete()
        {
            var author = new Author { Id = 9925, Name = "Test Writer" };
            var target = Make(9926, author, "Dune", work: "hc:9926");
            var linkedDuplicate = Make(9927, author, "Dune Messiah", work: "hc:9926");
            target.MediaType = BookMediaType.Ebook;
            linkedDuplicate.MediaType = BookMediaType.Ebook;
            target.Editions[0].Language = "English";
            linkedDuplicate.Editions[0].Language = "English";

            var result = FindMatch(target, new List<Book> { target, linkedDuplicate }, "Dune Messiah - Test Writer", "Test Writer");

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.Book, Is.SameAs(target));
        }

        [Test]
        public void an_exact_monitored_edition_title_still_matches_with_a_distinct_article_sibling()
        {
            var author = new Author { Id = 9928, Name = "Test Writer" };
            var target = Make(9929, author, "The Stand");
            var sibling = Make(9930, author, "Stand");
            target.Editions[0].Language = "English";
            sibling.Editions[0].Language = "English";

            var result = FindMatch(target, new List<Book> { target, sibling }, "The Stand - Test Writer", "Test Writer");

            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.Book, Is.SameAs(target));
            Assert.That(result.MatchedVariant, Is.EqualTo("The Stand"));
        }

        [Test]
        public void the_searched_title_must_be_present_and_no_other_book_may_explain_it_better()
        {
            // The whole contract in one place, both directions.
            var author = new Author { Id = 371, Name = "Frank Herbert" };
            var dune = Make(39406, author, "Dune", "Dune", "1");
            var messiah = Make(39408, author, "Dune Messiah", "Dune", "2");
            var subtitled = Make(39409, author, "Dune: Messiah", "Dune", "2", "hc:39408");
            var catalog = new List<Book> { dune, messiah };

            // Searching for the sibling: its own title is present, nothing explains it better.
            Assert.That(Matches(messiah, catalog, "Frank Herbert - Dune Messiah (epub)", "Frank Herbert"), Is.True);
            Assert.That(Matches(messiah, catalog, "Frank Herbert - Dune: Messiah (epub)", "Frank Herbert"), Is.True);

            // Searching for the shorter title: the sibling explains the same release better.
            Assert.That(Matches(dune, catalog, "Frank Herbert - Dune Messiah (epub)", "Frank Herbert"), Is.False);
            Assert.That(Matches(dune, catalog, "Frank Herbert - Dune: Messiah (epub)", "Frank Herbert"), Is.False);

            // And the shorter title still grabs its own releases.
            Assert.That(Matches(dune, catalog, "Frank Herbert - Dune (epub)", "Frank Herbert"), Is.True);

            // A colon in the stored title changes nothing about either direction.
            var colonCatalog = new List<Book> { dune, subtitled };
            Assert.That(Matches(subtitled, colonCatalog, "Frank Herbert - Dune Messiah (epub)", "Frank Herbert"), Is.True);
            Assert.That(Matches(dune, colonCatalog, "Frank Herbert - Dune Messiah (epub)", "Frank Herbert"), Is.False);
        }

    }
}

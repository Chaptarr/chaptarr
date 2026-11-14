using System.Linq;
using System.Text;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Books.Extensions;
using NzbDrone.Core.CustomFormats;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class AuthorNameMatcherFixture
    {
        [TestCaseSource(nameof(SharedJoiners))]
        public void each_shared_joiner_should_preserve_people_and_shared_family_credits(string joiner)
        {
            var joined = $"Ann Jones {joiner} Pat Smith";
            var family = $"Frank {joiner} Brian Herbert";
            Assert.Multiple(() =>
            {
                Assert.That(AuthorNameMatcher.AuthorNamesMatch("Ann Smith", joined), Is.False);
                Assert.That(AuthorNameMatcher.AuthorNamesMatch(joined, "Ann Smith"), Is.False);
                Assert.That(AuthorNameMatcher.ContainsAuthorName("Ann Smith", joined), Is.False);
                Assert.That(AuthorNameMatcher.ContainsAuthorName("Pat Smith", joined), Is.True);
                Assert.That(AuthorNameMatcher.AuthorNamesMatch("Frank Herbert", family), Is.True);
                Assert.That(AuthorNameMatcher.AuthorNamesMatch(family, "Frank Herbert"), Is.True);
                Assert.That(AuthorNameMatcher.ContainsAuthorName("Frank Herbert", family), Is.True);
                Assert.That(AuthorNameMatcher.ContainsAuthorName("Brian Herbert", family), Is.True);
                Assert.That(AuthorNameMatcher.AuthorNamesMatch("Brian Herbert", family), Is.True);
                Assert.That(AuthorNameMatcher.AuthorNamesMatch(family, "Brian Herbert"), Is.True);
                Assert.That(AuthorNameMatcher.AuthorNamesMatch($"Pat {joiner} Smith", $"Pat {joiner} Smith"), Is.True);
                Assert.That(AuthorNameMatcher.AuthorNamesMatch($"Pat {joiner} Smith", "Pat Q Smith"), Is.False);
            });
        }

        [TestCaseSource(nameof(SharedWordJoiners))]
        public void each_word_joiner_should_preserve_incomplete_suffixes_and_exact_known_credits(string joiner)
        {
            var leading = $"{joiner} Smith";
            var suffix = $"Fiona Shaw {joiner} Concert";
            var known = suffix + " Hall";
            Assert.Multiple(() =>
            {
                Assert.That(AuthorNameMatcher.SplitCredits(leading), Is.EqualTo(new[] { leading }));
                Assert.That(AuthorNameMatcher.SplitCredits("Pat Jones and " + leading), Is.EqualTo(new[] { "Pat Jones", leading }));
                Assert.That(AuthorNameMatcher.SplitCredits(suffix), Is.EqualTo(new[] { suffix }));
                Assert.That(AuthorNameMatcher.SplitCredits(known, new[] { known }), Is.EqualTo(new[] { known }));
                Assert.That(AuthorNameMatcher.SplitCredits("Pat Jones and " + known, new[] { known }), Is.EqualTo(new[] { "Pat Jones", known }));
            });
        }

        private static System.Collections.Generic.IEnumerable<string> SharedWordJoiners => ExpectedJoiners.Where(joiner => joiner != "&");

        private static readonly string[] ExpectedJoiners =
        {
            "and", "und", "et", "y", "e", "en", "och", "og", "i", "и", "ja",
            "ed", "és", "ve", "și", "a", "та", "і", "&",
            "και", "ir", "un", "in", "dan", "và", "dhe", "agus", "eta", "və"
        };

        private static System.Collections.Generic.IEnumerable<string> SharedJoiners => ExpectedJoiners;

        [Test]
        public void the_verified_joiner_inventory_must_not_silently_lose_an_entry()
        {
            Assert.That(AuthorNameMatcher.NameJoiners, Is.EquivalentTo(ExpectedJoiners));
        }

        [TestCaseSource(nameof(SharedJoiners))]
        public void actual_folder_and_narrator_callers_should_accept_either_shared_family_member(string joiner)
        {
            var family = $"Frank {joiner} Brian Herbert";
            var folder = new AuthorFolderMatchingService(null, LogManager.GetCurrentClassLogger());
            Assert.Multiple(() =>
            {
                Assert.That(folder.ValidateFolderMatchesAuthor(family, "Frank Herbert"), Is.True);
                Assert.That(folder.ValidateFolderMatchesAuthor(family, "Brian Herbert"), Is.True);
                Assert.That(NarratorNameMatcher.IsMatch(family, "Frank Herbert"), Is.True);
                Assert.That(NarratorNameMatcher.IsMatch(family, "Brian Herbert"), Is.True);
                Assert.That(PreferredNarratorMatcher.IsNarratorNameMatch(family, "Frank Herbert"), Is.True);
                Assert.That(PreferredNarratorMatcher.IsNarratorNameMatch(family, "Brian Herbert"), Is.True);
                Assert.That(folder.ValidateFolderMatchesAuthor($"Ann Jones {joiner} Pat Smith", "Ann Smith"), Is.False);
                Assert.That(NarratorNameMatcher.IsMatch($"Ann Jones {joiner} Pat Smith", "Ann Smith"), Is.False);
            });
        }

        [TestCaseSource(nameof(SharedJoiners))]
        public void the_raw_splitter_should_use_each_shared_joiner_without_inventing_a_family(string joiner)
        {
            Assert.Multiple(() =>
            {
                Assert.That(AuthorNameMatcher.SplitCredits($"Pat Smith {joiner} Ann Jones"), Is.EqualTo(new[] { "Pat Smith", "Ann Jones" }));
                Assert.That(AuthorNameMatcher.SplitCredits($"Frank {joiner} Brian Herbert"), Is.EqualTo(new[] { "Frank", "Brian Herbert" }));
            });
        }

        [TestCase("Ed Greenwood")]
        [TestCase("Dan Brown")]
        [TestCase("In Smith")]
        [TestCase("At Smith")]
        [TestCase("And Smith")]
        [TestCase("Ir Smith")]
        [TestCase("Un Smith")]
        [TestCase("Và Smith")]
        [TestCase("Dhe Smith")]
        [TestCase("Agus Smith")]
        [TestCase("Eta Smith")]
        [TestCase("Və Smith")]
        [TestCase("Και Smith")]
        public void a_leading_given_name_that_spells_a_connector_must_remain_one_person(string name)
        {
            Assert.Multiple(() =>
            {
                Assert.That(AuthorNameMatcher.SplitCredits(name), Is.EqualTo(new[] { name }));
                Assert.That(AuthorNameMatcher.SplitCredits("Pat Jones and " + name), Is.EqualTo(new[] { "Pat Jones", name }));
                Assert.That(AuthorNameMatcher.ContainsAuthorName(name, "credit " + name), Is.True);
                Assert.That(AuthorNameMatcher.AuthorNamesMatch(name, name), Is.True);
            });
        }

        [TestCase("John Smith, Jane Doe", "John Smith", "Jane Doe", "John Smith", "Jane Doe")]
        [TestCase("Smith, John", "John Smith", "Jane Doe", "Smith, John", null)]
        [TestCase("Smith, John, Doe, Jane", "John Smith", "Jane Doe", "Smith, John", "Doe, Jane")]
        [TestCase("John Smith, Doe, Jane", "John Smith", "Jane Doe", "John Smith", "Doe, Jane")]
        public void known_complete_names_should_resolve_comma_inversion_without_compatibility_guesses(string value, string first, string second, string firstPart, string secondPart)
        {
            var expected = secondPart == null ? new[] { firstPart } : new[] { firstPart, secondPart };
            Assert.That(AuthorNameMatcher.SplitCredits(value, new[] { first, second }), Is.EqualTo(expected));
        }

        [TestCase("Smith, John")]
        [TestCase("John Smith, Jane Doe")]
        [TestCase("Jean-Paul Sartre")]
        [TestCase("Pat Smith with Ann Jones")]
        [TestCase("Ann O. Smith")]
        [TestCase("Ann I Smith")]
        public void raw_ambiguous_commas_hyphens_ordinary_words_and_initials_should_remain_unchanged(string value)
        {
            Assert.That(AuthorNameMatcher.SplitCredits(value), Is.EqualTo(new[] { value }));
        }

        [TestCase("John Smith and Jane Doe", "John Smith and Jane Doe")]
        [TestCase("Ann In Smith", "Ann In Smith")]
        [TestCase("Ann In Smith and Pat Jones", "Ann In Smith")]
        public void exact_known_complete_spelling_should_resolve_a_word_connector_collision(string value, string known)
        {
            var expected = value == known ? new[] { value } : new[] { known, "Pat Jones" };
            Assert.That(AuthorNameMatcher.SplitCredits(value, new[] { known }), Is.EqualTo(expected));
        }

        [TestCase(";")]
        [TestCase("；")]
        [TestCase("/")]
        [TestCase("|")]
        [TestCase("+")]
        [TestCase(" · ")]
        [TestCase(" • ")]
        [TestCase("、")]
        [TestCase("，")]
        [TestCase("×")]
        [TestCase(" - ")]
        [TestCase(" — ")]
        public void punctuation_credit_boundaries_should_be_shared_by_comparison_and_splitting(string separator)
        {
            var value = "Ann Jones" + separator + "Pat Smith";
            Assert.Multiple(() =>
            {
                Assert.That(AuthorNameMatcher.SplitCredits(value), Is.EqualTo(new[] { "Ann Jones", "Pat Smith" }));
                Assert.That(AuthorNameMatcher.AuthorNamesMatch("Ann Smith", value), Is.False);
                Assert.That(AuthorNameMatcher.ContainsAuthorName("Ann Smith", value), Is.False);
                Assert.That(AuthorNameMatcher.ContainsAuthorName("Pat Smith", value), Is.True);
            });
        }

        [TestCase("斯蒂芬·金")]
        [TestCase("斯蒂芬•金")]
        [TestCase("スティーヴン・キング")]
        public void attached_name_marks_should_preserve_one_literal_credit(string value)
        {
            Assert.Multiple(() =>
            {
                Assert.That(AuthorNameMatcher.SplitCredits(value), Is.EqualTo(new[] { value }));
                Assert.That(AuthorNameMatcher.SplitCredits(value, new[] { value }), Is.EqualTo(new[] { value }));
                Assert.That(AuthorNameMatcher.AuthorNamesMatch(value, value), Is.True);
                Assert.That(AuthorNameMatcher.ContainsAuthorName(value, "credit " + value), Is.True);
                Assert.That(NarratorNameMatcher.IsMatch(value, value), Is.True);
                Assert.That(ReleaseNarratorEvidenceExtractor.ExtractNames(new[] { value }, value).Names, Is.EqualTo(new[] { value }));
            });
        }

        [TestCase("Jaume Cabré", "Jaume Cabré i Fabré", true)]
        [TestCase("José Ortega", "José Ortega y Gasset", true)]
        [TestCase("Jaume Cabré", "Jaume Cabré I Fabré", false)]
        [TestCase("José Ortega", "José Ortega Y. Gasset", false)]
        public void a_compound_name_connector_needs_only_its_actual_following_token(string expected, string value, bool matches)
        {
            Assert.That(AuthorNameMatcher.ContainsAuthorName(expected, value), Is.EqualTo(matches));
        }

        [TestCase("Jim Butcher", "J. Butcher", true)]
        [TestCase("Jim Butcher", "B. Butcher", false)]
        [TestCase("Jim Butcher", "John J. Butcher", false)]
        [TestCase("J. Butcher", "credit J. Butcher", true)]
        [TestCase("Jim Butcher", "credit Jim Butcher", true)]
        [TestCase("C S Lewis", "credit CS Lewis", true)]
        [TestCase("CS Lewis", "credit CS Lewis", true)]
        [TestCase("C S Lewis", "SC Lewis", false)]
        [TestCase("Clive Staples Lewis", "L. S. Lewis", false)]
        [TestCase("Stephen King", "Owen S. King", false)]
        [TestCase("Stephen King", "OWEN S. KING", false)]
        [TestCase("Stephen King", "owen s. king", false)]
        [TestCase("Brian Herbert", "Frank B. Herbert", false)]
        [TestCase("Harper Lee", "Michelle H. Lee", false)]
        [TestCase("Clive Staples Lewis", "Pat C. S. Lewis", false)]
        [TestCase("Stephen King", "credit S. King", false)]
        [TestCase("Stephen King", "S. King", true)]
        [TestCase("Stephen King", "credit - S. King", true)]
        [TestCase("Stephen King", "Other Writer; S. King", true)]
        [TestCase("Stephen King", "12 S. King", true)]
        [TestCase("Stephen King", "Pat Smith and S. King", true)]
        [TestCase("Stephen King", "credit Stephen King", true)]
        [TestCase("S. King", "credit S. King", true)]
        [TestCase("J. K. Rowling", "credit J. K. Rowling", true)]
        [TestCase("Clive Staples Lewis", "C. S. Lewis", true)]
        [TestCase("Clive Staples Lewis", "credit Clive Staples Lewis", true)]
        [TestCase("Jennifer L. Armentrout", "credit Jennifer Q. Armentrout", false)]
        [TestCase("Jennifer L. Armentrout", "credit Jennifer Armentrout", true)]
        public void expanding_an_initial_must_not_borrow_a_different_undelimited_given_name(string expected, string value, bool matches)
        {
            Assert.Multiple(() =>
            {
                Assert.That(AuthorNameMatcher.ContainsAuthorName(expected, value), Is.EqualTo(matches));
                Assert.That(AuthorNameMatcher.ContainsAuthorName(AuthorNameMatcher.PrepareName(expected), value,
                    ReleaseTitleMatchScorer.TokenizeWithSpans(value)), Is.EqualTo(matches));
            });
        }


        [TestCaseSource(nameof(SingleLetterJoiners))]
        public void single_letter_joiners_should_retain_uppercase_and_dotted_initials(string joiner)
        {
            var upper = joiner.ToUpperInvariant();
            Assert.Multiple(() =>
            {
                Assert.That(AuthorNameMatcher.AuthorNamesMatch($"Pat {upper} Smith", $"Pat {upper}. Smith"), Is.True);
                Assert.That(AuthorNameMatcher.AuthorNamesMatch($"Pat {upper} Smith", "Pat Q Smith"), Is.False);
                Assert.That(AuthorNameMatcher.ContainsAuthorName("Brian Herbert", $"Frank {upper} Brian Herbert"), Is.False);
                Assert.That(AuthorNameMatcher.ContainsAuthorName("Brian Herbert", $"Frank {joiner}. Brian Herbert"), Is.False);
                Assert.That(AuthorNameMatcher.AuthorNamesMatch("Ann Smith", $"Ann Jones {upper} Pat Smith"), Is.True);
                Assert.That(AuthorNameMatcher.AuthorNamesMatch("Ann Smith", $"Ann Jones {joiner}. Pat Smith"), Is.True);
                Assert.That(AuthorNameMatcher.ContainsAuthorName($"{upper}name Smith", $"Ann Jones {joiner} Pat Smith"), Is.False);
                Assert.That(AuthorNameMatcher.ContainsAuthorName($"{upper}name Smith", $"Ann Jones {upper} Pat Smith"), Is.False);
            });
        }

        private static System.Collections.Generic.IEnumerable<string> SingleLetterJoiners => AuthorNameMatcher.NameJoiners
            .Where(joiner => joiner.EnumerateRunes().Count() == 1 && joiner.EnumerateRunes().All(System.Text.Rune.IsLetter));

        [TestCase("Ann Pat Smith", "Ann and Pat Smith", "Ann & Pat Smith")]
        [TestCase("Ann Smith", "Ann Jones and Pat Smith", "Ann Jones & Pat Smith")]
        public void word_and_symbol_joiners_should_use_the_same_supplied_given_rule(string expected, string word, string symbol)
        {
            Assert.Multiple(() =>
            {
                Assert.That(AuthorNameMatcher.AuthorNamesMatch(expected, symbol),
                    Is.EqualTo(AuthorNameMatcher.AuthorNamesMatch(expected, word)));
                Assert.That(AuthorNameMatcher.ContainsAuthorName(expected, symbol),
                    Is.EqualTo(AuthorNameMatcher.ContainsAuthorName(expected, word)));
            });
        }

        [TestCase("Ann Smith", "Smith, Ann Jones & Pat", false)]
        [TestCase("Ann Smith", "Smith, Ann Jones ＆ Pat", false)]
        [TestCase("Ed Smith", "Ed A Smith", true)]
        [TestCase("Ed Smith", "credit Ed A Smith", true)]
        [TestCase("George Martin", "George RR Martin", true)]
        [TestCase("Pat Y Smith", "Pat Z Smith", false)]
        [TestCase("Pat Smith", "Titel von Pat Smith", true)]
        [TestCase("Pat Smith", "Titre par Pat Smith", true)]
        [TestCase("Ann Smith", "Ann Jones unknownjoiner Pat Smith", true)]
        [TestCase("Ann Smith", "Ann Jones; Pat Smith", false)]
        [TestCase("Ann Smith", "Ann Jones ＆ Pat Smith", false)]
        [TestCase("Ann Smith", "Ann ＆ Pat Smith", true)]
        [TestCase("Ann Smith", "Ann; Pat Smith", false)]
        [TestCase("Jennifer L. Armentrout", "Armentrout, Jennifer Q.", false)]
        [TestCase("Jennifer Q. Armentrout", "Armentrout, Jennifer L.", false)]
        [TestCase("Jane Smith", "Pat Smith, Jane Q", false)]
        [TestCase("John Smith", "Pat Smith, John Doe", false)]
        public void joiner_scope_should_preserve_surrounding_words_initials_and_other_credit_boundaries(string expected, string field, bool matches)
        {
            Assert.That(AuthorNameMatcher.ContainsAuthorName(expected, field), Is.EqualTo(matches));
        }

        [TestCase("Jennifer L. Armentrout", "Jennifer Armentrout", true)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Q. Armentrout", false)]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson", true)]
        [TestCase("Robert Louis Balfour Stevenson", "Robert Balfour Stevenson", true)]
        [TestCase("Robert Louis Balfour Stevenson", "Robert Louis Stevenson", true)]
        [TestCase("Robert Louis Balfour Stevenson", "Robert Andrew Stevenson", false)]
        [TestCase("Robert Louis Stevenson", "Robert Andrew Stevenson", false)]
        [TestCase("Robert Louis Stevenson", "Robert Q. Stevenson", false)]
        [TestCase("C S Lewis", "CS Lewis", true)]
        [TestCase("Clive Staples Lewis", "C S Lewis", true)]
        [TestCase("Clive Staples Lewis", "CS Lewis", false)]
        [TestCase("John Smith", "James O H N Smith", false)]
        [TestCase("JOHN SMITH", "James O H N Smith", false)]
        [TestCase("J K Rowling", "JK Rowling", true)]
        [TestCase("J K Rowling", "Joanne Rowling", true)]
        [TestCase("JK Rowling", "Joanne Rowling", false)]
        [TestCase("J K Rowling", "J Q Rowling", false)]
        [TestCase("J K Rowling", "JQ Rowling", false)]
        [TestCase("J K Rowling", "jq Rowling", false)]
        [TestCase("J K Rowling", "jk Rowling", true)]
        [TestCase("Le Guin, Ursula K.", "Ursula K. Guin", false)]
        [TestCase("Jo Rowling", "John Rowling", false)]
        [TestCase("John Smith", "JAMES SMITH", false)]
        [TestCase("JOHN SMITH", "James Smith", false)]
        [TestCase("John Smith", "JANE SMITH", false)]
        [TestCase("Le Guin, Ursula K.", "Ursula Le Guin", true)]
        [TestCase("Le Guin, Ursula K.", "Ursula Guin", false)]
        [TestCase("Anne Smith-Jones", "Anne Smith Jones", true)]
        [TestCase("Anne Smith-Jones", "Anne Jones", false)]
        [TestCase("John Smith", "John Smith's", true)]
        [TestCase("John Smith", "John Smith\u02bcs", true)]
        // One trailing surname s is compatible in either direction.
        [TestCase("John Smith", "John Smiths", true)]
        [TestCase("Frank Herbert", "Frank Herberts", true)]
        [TestCase("Frank Herbert", "Herberts, Frank", true)]
        [TestCase("Frank Herbert", "Frank Herbert‘s", true)]
        [TestCase("Nora Roberts", "Nora Robert", true)]
        [TestCase("Stephen Fry", "Stephen Frys", true)]
        [TestCase("Robert Stevenson", "Robert Louis Stevensons", true)]
        [TestCase("Ursula K. Le Guin", "Ursula K. Le Guins", true)]
        [TestCase("Douglas Adams", "Douglas Adam's", true)]
        [TestCase("Anne Smith-Jones", "Anne Smith-Joness", true)]
        [TestCase("Frank Herbert", "Frank & Brian Herberts", true)]
        [TestCase("Frank Herberts", "Frank & Brian Herbert", true)]
        [TestCase("Frank Herbert", "Frank and Brian Herberts", true)]
        [TestCase("Frank Herbert", "Brian Herberts", false)]
        [TestCase("Frank Herbert", "Frank Hebert", false)]
        [TestCase("Frank Herbert", "Franks Herbert", false)]
        [TestCase("James Smith", "Jame Smith", false)]
        [TestCase("John Smith", "Pat Smiths, John Doe", false)]
        [TestCase("Anne Smith-Jones", "Anne Smiths-Jones", false)]
        [TestCase("Le Guin, Ursula K.", "Ursula Les Guin", false)]
        [TestCase("Brian Herbert", "Frank Herberts and Pat Smith", false)]
        [TestCase("Лев Толстой", "Лев Толстойс", false)]
        [TestCase("山田 太郎", "山田 太郎子", false)]
        [TestCase("Voltaire", "Voltaires", false)]
        [TestCase("Frank Herbert", "FrankHerberts", false)]
        [TestCase("Лев Н. Толстой", "Лев Толстой", true)]
        [TestCase("José García", "Jose Garcia", true)]
        [TestCase("がとう", "かとう", false)]
        [TestCase("がとう", "か\u3099とう", true)]
        [TestCase("Лев Н. Толстой", "Лев П. Толстой", false)]
        [TestCase("Stephen King", "StephenKing", true)]
        [TestCase("Stephen King", "Stephen Kingsley", false)]
        [TestCase("Gabriel Garcia Marquez", "Gabriel Jose de la Concordia Garcia Marquez", true)]
        [TestCase("Gabriel Garcia Marquez", "Gabriel Jose de la Concordia Francisco Marquez", false)]
        [TestCase("Лев Толстой", "Лев Николаевич Иванович Петрович Толстой", true)]
        [TestCase("Лев Николаевич Толстой", "Лев Александрович Толстой", false)]
        [TestCase("Li Wei", "L I Wei", true)]
        [TestCase("Li Wei", "Louise Irene Wei", false)]
        [TestCase("Li Wei", "Lee Wei", false)]
        [TestCase("C-S Lewis", "CS Lewis", true)]
        [TestCase("Jean-Paul Sartre", "Jean Paul Sartre", true)]
        [TestCase("Jean-Paul Sartre", "Jean Louis Sartre", false)]
        [TestCase("Jean-Paul Sartre", "Jean Sartre", false)]
        [TestCase("Ann Smith", "Ann and Pat Smith", true)]
        [TestCase("Ann Smith", "Ann und Pat Smith", true)]
        [TestCase("Ann and Pat Smith", "Ann und Pat Smith", false)]
        [TestCase("Ann & Pat Smith", "Ann & Pat Smith", true)]
        [TestCase("Ann & Pat Smith", "Ann ＆ Pat Smith", true)]
        [TestCase("Ann & Pat Smith", "Ann; Pat Smith", true)]
        [TestCase("Ann & Pat Smith", "Ann & Jane Smith", false)]
        [TestCase("Ann & Pat Smith", "Pat Smith", true)]
        [TestCase("Ann & Pat Smith", "Ann and Pat Smith", false)]
        [TestCase("Ann & Pat Smith", "A & P Smith", false)]
        [TestCase("Ann ＆ Pat Smith", "Ann & Pat Smith", true)]
        [TestCase("Ann； Pat Smith", "Ann; Pat Smith", true)]
        [TestCase("Ann Smith", "Ann ＆ Pat Smith", true)]
        [TestCase("Ann Smith", "Ann； Pat Smith", false)]
        [TestCase("David McCullough, Jr.", "David McCullough Jr.", true)]
        [TestCase("Smith, John, Jr.", "Smith, John, Jr.", true)]
        [TestCase("Smith, John, Jr.", "Smith John Jr.", true)]
        [TestCase("山田 太郎, 二世", "山田 太郎 二世", true)]
        [TestCase("García Márquez, Gabriel", "Gabriel García Márquez", true)]
        [TestCase("David McCullough, Jr.", "David McCullough, Sr.", false)]
        [TestCase("Ann Smith", "Ann Jones and Pat Smith", false)]
        [TestCase("John Smith", "John Adams and Mary Smith", false)]
        [TestCase("Ann Smith", "Ann Jones Smith", true)]
        [TestCase("Ann Smith", "Ann Jones Smith and Pat Smith", false)]
        [TestCase("Ann A Smith", "Ann A and Pat Smith", true)]
        [TestCase("C S Lewis", "CS and Pat Lewis", true)]
        [TestCase("AB Smith", "A; B Smith", false)]
        [TestCase("John Smith", "John; Smith", false)]
        [TestCase("John A Smith", "John; A Smith", false)]
        [TestCase("John; A Smith", "John A; Smith", false)]
        [TestCase("Ann & Pat Smith", "Ann — Pat Smith", true)]
        [TestCase("Smith, John, Jr.", "Smith, John; Jr.", false)]
        public void should_compare_one_author_consistently_in_both_directions(string expected, string candidate, bool matches)
        {
            Assert.That(AuthorNameMatcher.AuthorNamesMatch(expected, candidate), Is.EqualTo(matches));
            Assert.That(AuthorNameMatcher.AuthorNamesMatch(candidate, expected), Is.EqualTo(matches));
        }

        [TestCase("'", "john smiths", true)]
        [TestCase("’", "john smiths", true)]
        [TestCase("‘", "john smiths", false)]
        [TestCase("ʼ", "john smith's", true)]
        public void possessive_proof_preserves_the_released_stored_key_and_name_alphabets(string apostrophe, string key, bool exact)
        {
            var credit = "John Smith" + apostrophe + "s";
            Assert.Multiple(() =>
            {
                Assert.That(StringSuperNormalizer.NormalizeForFts(credit), Is.EqualTo(key));
                Assert.That(ReleaseTitleMatchScorer.Tokenize(credit), Is.EqualTo(new[] { "john", "smiths" }));
                Assert.That(AuthorNameMatcher.AuthorNamesExactlyMatch(AuthorNameMatcher.PrepareName("John Smith"),
                    AuthorNameMatcher.PrepareName(credit)), Is.EqualTo(exact));
                Assert.That(AuthorNameMatcher.ContainsAuthorName("John Smith", credit + " - A Book"), Is.True);
            });
        }

        [TestCase("Frank Herbert", "Frank Herberts")]
        [TestCase("Nora Roberts", "Nora Robert")]
        public void surname_compatibility_must_not_change_exact_identity(string expected, string candidate)
        {
            Assert.That(AuthorNameMatcher.AuthorNamesExactlyMatch(AuthorNameMatcher.PrepareName(expected),
                AuthorNameMatcher.PrepareName(candidate)), Is.False);
        }

        [TestCase("Pat Smith", "Titel von Pat Smith", true)]
        [TestCase("Frank Herbert", "Frank Herberts Dune", true)]
        [TestCase("Frank Herberts", "Frank Herbert Dune", true)]
        [TestCase("Frank Herbert", "Herberts, Frank - Dune", true)]
        [TestCase("Frank Herbert", "Frank & Brian Herberts - Dune", true)]
        [TestCase("Frank Herbert", "Brian Herberts - Dune", false)]
        [TestCase("Frank Herbert", "Franks Herbert - Dune", false)]
        [TestCase("Michael Ende", "Michael Endes Momo", true)]
        [TestCase("Лев Толстой", "Лев Толстойс Война и мир", false)]
        [TestCase("Лев Толстой", "Лев Толстые Война и мир", false)]
        [TestCase("Voltaire", "Voltaires Candide", false)]
        [TestCase("Robert Williams", "Robert William Chambers - Title", true)]
        [TestCase("Pat Smith", "Titre par Pat Smith", true)]
        [TestCase("Pat Smith", "Título por Pat Smith", true)]
        [TestCase("Pat Smith", "Gelesen von Pat Smith", true)]
        [TestCase("Pat Smith", "Lu par Pat Smith", true)]
        [TestCase("Pat Smith", "Leído por Pat Smith", true)]
        [TestCase("Pat Smith", "Ann und Pat Smith", true)]
        [TestCase("Pat Smith", "Ann et Pat Smith", true)]
        [TestCase("Jennifer L. Armentrout", "Armentrout, Jennifer Q.", false)]
        [TestCase("Jennifer Q. Armentrout", "Armentrout, Jennifer L.", false)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Q. Armentrout", false)]
        [TestCase("Jennifer Q. Armentrout", "Jennifer L. Armentrout", false)]
        [TestCase("John Smith", "Pat Smith, John", false)]
        [TestCase("John Smith", "Smith, John Jr", true)]
        [TestCase("John Smith", "Smith, John - extra words", true)]
        [TestCase("John Smith", "label Smith, John", false)]
        [TestCase("John Smith", "label - Smith, John - extra words", true)]
        [TestCase("John Smith", "John; Smith", false)]
        [TestCase("John Smith", "John & Smith", false)]
        [TestCase("Jane Smith", "Pat Smith, Jane Q", false)]
        [TestCase("Jane Smith", "Smith, Jane", true)]
        [TestCase("Robert Stevenson", "Robert Louis Stevenson", true)]
        [TestCase("Robert Louis Stevenson", "Robert Andrew Stevenson", false)]
        [TestCase("Robert Louis Stevenson", "Robert Q Stevenson", false)]
        [TestCase("John Smith", "John Smith’s", true)]
        // One trailing surname s is compatible in either direction.
        [TestCase("John Smith", "John Smiths", true)]
        [TestCase("Gabriel Garcia Marquez", "Gabriel Jose de la Concordia Garcia Marquez", true)]
        [TestCase("Gabriel Garcia Marquez", "Marquez, Gabriel Jose de la Concordia Garcia", true)]
        [TestCase("Gabriel Garcia Marquez", "Gabriel Jose de la Concordia Francisco Marquez", false)]
        [TestCase("Лев Толстой", "Лев Николаевич Иванович Петрович Толстой", true)]
        [TestCase("Лев Николаевич Толстой", "Толстой, Лев Александрович", false)]
        [TestCase("Ann Smith", "Ann; Pat Smith", false)]
        [TestCase("Ann Smith", "Ann & Pat Smith", true)]
        [TestCase("Ann Smith", "Ann and Pat Smith", true)]
        [TestCase("Ann Smith", "Ann und Pat Smith", true)]
        [TestCase("Pat Smith", "Ann and Pat Smith", true)]
        [TestCase("Ann and Pat Smith", "Ann und Pat Smith", false)]
        [TestCase("Stephen King", "StephenKing", true)]
        [TestCase("Stephen King", "StephenKingsley", false)]
        [TestCase("John Smith", "John Smith\u02bcs", true)]
        [TestCase("John Smith", "Smith John, Pat Doe", true)]
        [TestCase("John Smith", "Smith, John; Pat Doe", true)]
        [TestCase("Robert L Stevenson", "Robert Stevenson L Stevenson", true)]
        [TestCase("John Smith", "Smith John J A N E", false)]
        [TestCase("John Smith", null, false)]
        [TestCase("John Smith", "", false)]
        [TestCase("Ann & Pat Smith", "Ann & Pat Smith", true)]
        [TestCase("Ann & Pat Smith", "Ann ＆ Pat Smith - A Book Title", true)]
        [TestCase("Ann & Pat Smith", "Ann; Pat Smith - A Book Title", true)]
        [TestCase("Ann & Pat Smith", "Ann & Jane Smith", false)]
        [TestCase("Ann & Pat Smith", "Pat Smith", false)]
        [TestCase("Ann & Pat Smith", "A & P Smith", false)]
        [TestCase("Ann ＆ Pat Smith", "Ann & Pat Smith - A Book Title", true)]
        [TestCase("Ann； Pat Smith", "Ann; Pat Smith - A Book Title", true)]
        [TestCase("Ann Smith", "Ann ＆ Pat Smith", true)]
        [TestCase("Ann Smith", "Ann； Pat Smith", false)]
        [TestCase("Ann Smith", "Mary Ann Smith", true)]
        [TestCase("Ann Smith", "Mary Rose Ann Smith", true)]
        [TestCase("Ann Smith", "Ann Smith Jones", true)]
        [TestCase("Ann Smith", "Ann Smith, Pat Jones", true)]
        [TestCase("Lee", "Stan Lee", true)]
        [TestCase("Lee", "Other Writer, Lee", true)]
        [TestCase("Brian Herbert", "Kevin J Anderson, Brian Herbert", true)]
        [TestCase("David McCullough Jr.", "David McCullough, Jr. - A Book Title", true)]
        [TestCase("David McCullough, Jr.", "David McCullough Jr. - A Book Title", true)]
        [TestCase("Smith, John, Jr.", "Smith, John, Jr.", true)]
        [TestCase("John Smith", "Pat Smith, John Doe", false)]
        [TestCase("Ursula K. Le Guin", "Written by Ursula K. Le Guin", true)]
        [TestCase("Frank Herbert", "Frank Herbert's Dune Messiah", true)]
        [TestCase("Frank Herbert", "Frank Herbert’s Dune Messiah", true)]
        [TestCase("Robert L Stevenson", "Robert Stevenson Louis Stevenson", true)]
        [TestCase("Ann Smith", "Ann Jones and Pat Smith", false)]
        [TestCase("John Smith", "John Adams and Mary Smith", false)]
        [TestCase("Ann Smith", "Ann Jones Smith and Pat Smith", true)]
        [TestCase("Ann Smith", "Ann Jones and Pat Smith - A Book Title", false)]
        [TestCase("Pat Smith", "Ann Jones and Pat Smith", true)]
        [TestCase("John; Smith", "Smith, John", false)]
        [TestCase("John Smith, Pat, Doe", "Doe John Smith; Pat", false)]
        [TestCase("John Smith", "John Smith read by Peter Jones", true)]
        [TestCase("John Smith", "John Smith narrated by Peter Jones", true)]
        [TestCase("John Smith", "John Smith performed by Peter Jones", true)]
        [TestCase("John Smith", "John Smith read Peter Jones", true)]
        [TestCase("John Smith", "John Smith Peter Jones", true)]
        [TestCase("Stephen Fry", "Narrator..................Stephen Fry", true)]
        [TestCase("Stephen Fry", "CREDIT........Stephen Fry", true)]
        [TestCase("Stephen Fry", "标签........Stephen Fry", true)]
        [TestCase("Stephen Fry", "Other.Stephen Fry", true)]
        [TestCase("John Smith", "John.Smith", true)]
        [TestCase("John Smith", "Mary.John.Smith", true)]
        [TestCase("John Smith", "Q...John Smith", false)]
        [TestCase("John Smith", "John Smith...Q", false)]
        public void field_evidence_should_allow_surrounding_words_without_discarding_conflicts(string expected, string field, bool matches)
        {
            Assert.Multiple(() =>
            {
                Assert.That(AuthorNameMatcher.ContainsAuthorName(expected, field), Is.EqualTo(matches));
                Assert.That(AuthorNameMatcher.ContainsAuthorName(AuthorNameMatcher.PrepareName(expected), field,
                    ReleaseTitleMatchScorer.TokenizeWithSpans(field)), Is.EqualTo(matches));
            });
        }

        [TestCase("David McCullough, Jr.", "David McCullough Jr.", true)]
        [TestCase("Smith, John, Jr.", "Smith John Jr.", true)]
        [TestCase("Smith, John", "John Smith", true)]
        [TestCase("Ann Smith", "Ann A Smith", false)]
        [TestCase("Ana Bell", "An Abell", false)]
        [TestCase("Stephen King", "StephenKing", false)]
        [TestCase("J. K. Rowling", "J K Rowling", true)]
        [TestCase("J. K. Rowling", "JK Rowling", false)]
        [TestCase("John Smith", "John; Smith", false)]
        [TestCase("John; A Smith", "John A; Smith", false)]
        public void exact_prepared_spelling_should_preserve_word_boundaries_before_compatible_forms(string expected, string candidate, bool matches)
        {
            Assert.That(AuthorNameMatcher.AuthorNamesExactlyMatch(AuthorNameMatcher.PrepareName(expected), AuthorNameMatcher.PrepareName(candidate)),
                Is.EqualTo(matches));
        }

        [TestCase("by John; Smith", "John; Smith")]
        [TestCase("Read by John; Smith", "John; Smith")]
        [TestCase("narrated by A; B Smith", "A; B Smith")]
        [TestCase("Performed by がとう; 𐐀 Reader", "がとう; 𐐀 Reader")]
        public void surrounding_words_should_preserve_the_stored_complete_credit(string source, string credit)
        {
            Assert.That(AuthorNameMatcher.ContainsAuthorName(credit, source), Is.True);
        }

        [TestCase("Jennifer L. Armentrout", "Jennifer.L.Armentrout.Daemonentochter.02.Verlockende.Angst", "Verlockende Angst", true)]
        [TestCase("Jennifer L. Armentrout", "Jennifer.L.Armentrout.Dämonentochter.02.Verlockende.Angst", "Verlockende Angst", true)]
        [TestCase("Andy Weir", "Andy Weir Collection-Project Hail Mary", "Project Hail Mary", true)]
        [TestCase("Ann Smith", "Mary Ann Smith Collection - A Book Title", "A Book Title", true)]
        [TestCase("Lee", "Stan Lee Collection - A Book Title", "A Book Title", true)]
        [TestCase("John Smith", "John Smith Jones - A Book Title", "A Book Title", true)]
        [TestCase("John Smith", "John Smith Q Collection - A Book Title", "A Book Title", false)]
        [TestCase("John Smith", "John Smith-Jones Collection - A Book Title", "A Book Title", false)]
        [TestCase("Ann Smith", "Ann Jones and Pat Smith Collection - A Book Title", "A Book Title", false)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Q. Armentrout Collection - Verlockende Angst", "Verlockende Angst", false)]
        [TestCase("John Smith", "A Book Title John Smith Collection", "A Book Title", true)]
        public void known_title_context_should_keep_the_same_supplied_name_conflicts(string expected, string source, string title, bool matches)
        {
            var tokens = ReleaseTitleMatchScorer.TokenizeWithSpans(source);
            var names = AuthorNameMatcher.FindNameSpans(source, tokens, AuthorNameMatcher.PrepareName(expected)).ToList();
            var titleSpan = ReleaseTitleMatchScorer.FindExactTitleSpans(tokens.Select(token => token.Value).ToList(),
                ReleaseTitleMatchScorer.Tokenize(title)).Single();

            Assert.Multiple(() =>
            {
                Assert.That(names.Any(name => AuthorNameMatcher.IsCoherentNameSpan(source, tokens, name,
                    titleSpan.Start, titleSpan.End)), Is.EqualTo(matches));
                Assert.That(AuthorNameMatcher.ContainsAuthorName(expected, source), Is.EqualTo(matches));
            });
        }

        [Test]
        public void an_inverted_credit_must_not_crop_an_unseparated_supplied_given_group()
        {
            var source = "Smith John Collection A Book Title";
            var tokens = ReleaseTitleMatchScorer.TokenizeWithSpans(source);
            Assert.That(AuthorNameMatcher.IsCoherentNameSpan(source, tokens, (0, 1, true), 3, 5), Is.False);
        }

        [TestCase(3, 3)]
        [TestCase(3, 2)]
        [TestCase(-1, -1)]
        public void unrelated_title_context_should_not_suppress_field_name_evidence(int matchedStart, int matchedEnd)
        {
            var source = "John Smith Collection";
            var tokens = ReleaseTitleMatchScorer.TokenizeWithSpans(source);
            Assert.That(AuthorNameMatcher.IsCoherentNameSpan(source, tokens, (0, 1, false), matchedStart, matchedEnd), Is.True);
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        [NonParallelizable]
        public void span_search_should_bound_candidates_and_log_work_even_when_stopped_early(bool stopAfterFirst, bool hasAnchor, bool surnameSuffix)
        {
            var previous = LogManager.Configuration;
            var previousThreshold = LogManager.GlobalThreshold;
            var memory = new MemoryTarget { Layout = "${level}|${message}" };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Trace, LogLevel.Fatal, memory, typeof(AuthorNameMatcher).FullName);
            try
            {
                LogManager.GlobalThreshold = LogLevel.Trace;
                LogManager.Configuration = configuration;
                var source = (hasAnchor ? (surnameSuffix ? "Robert Louis Stevensons " : "Robert Louis Stevenson ") : "Alice Mary Doe ") + string.Join(" ", Enumerable.Repeat("padding", 100));
                var tokens = ReleaseTitleMatchScorer.TokenizeWithSpans(source);
                var spans = AuthorNameMatcher.FindNameSpans(source, tokens, AuthorNameMatcher.PrepareName("Robert Stevenson"));
                if (stopAfterFirst)
                {
                    Assert.That(spans.Any(), Is.EqualTo(hasAnchor));
                }
                else
                {
                    Assert.That(spans.ToList(), Has.Count.EqualTo(hasAnchor ? 1 : 0));
                }

                LogManager.Flush();
                Assert.That(memory.Logs, Has.Count.EqualTo(1));
                Assert.That(memory.Logs[0], Does.Contain($"tokens=103, maxSpan=103, examined={(hasAnchor ? 1 : 0)}"));
                Assert.That(memory.Logs[0], Does.Match(@"matched=\d+, elapsedMs=\d+[.,]\d{3}"));
                Assert.That(memory.Logs[0], Does.Contain($"possessiveAnchors={(surnameSuffix ? 1 : 0)}"));
            }
            finally
            {
                LogManager.GlobalThreshold = previousThreshold;
                LogManager.Configuration = previous;
                LogManager.ReconfigExistingLoggers();
            }
        }
    }
}

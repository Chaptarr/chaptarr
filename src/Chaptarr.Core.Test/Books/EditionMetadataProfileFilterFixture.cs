using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class EditionMetadataProfileFilterFixture
    {
        private static IEditionMetadataProfileFilter CreateSut()
        {
            return new EditionMetadataProfileFilter(new TestTermMatcherService());
        }

        [Test]
        public void should_treat_isbn10_as_valid_when_skip_missing_isbn_is_enabled()
        {
            var profile = new MetadataProfile { SkipMissingIsbn = true };
            var edition = new Edition { Isbn10 = "0441172717" };

            var allowed = EditionMetadataProfileFilter.MeetsIdentifierRequirements(edition, profile);

            Assert.That(allowed, Is.True);
        }

        [Test]
        public void should_require_asin_when_skip_missing_asin_is_enabled()
        {
            var profile = new MetadataProfile { SkipMissingAsin = true };

            Assert.That(
                EditionMetadataProfileFilter.MeetsIdentifierRequirements(new Edition { Isbn13 = "9780441172719" }, profile),
                Is.False);

            Assert.That(
                EditionMetadataProfileFilter.MeetsIdentifierRequirements(new Edition { Asin = "B000TEST" }, profile),
                Is.True);
        }

        [Test]
        public void should_require_both_isbn_and_asin_when_both_identifier_filters_are_enabled()
        {
            var profile = new MetadataProfile { SkipMissingIsbn = true, SkipMissingAsin = true };

            Assert.That(
                EditionMetadataProfileFilter.MeetsIdentifierRequirements(new Edition { Asin = "B000TEST" }, profile),
                Is.False);

            Assert.That(
                EditionMetadataProfileFilter.MeetsIdentifierRequirements(new Edition { Isbn13 = "9780441172719", Asin = "B000TEST" }, profile),
                Is.True);
        }

        [Test]
        public void should_parse_allowed_languages_and_unknown_bucket_consistently()
        {
            EditionMetadataProfileFilter.ParseAllowedLanguages(
                " eng, null , unknown , invalid-token ",
                out var allowedLanguages,
                out var allowUnknownLanguage,
                out var configured,
                out var unknownTokens);

            Assert.That(configured, Is.True);
            Assert.That(allowUnknownLanguage, Is.True);
            Assert.That(allowedLanguages, Is.EquivalentTo(new[] { "eng" }));
            Assert.That(unknownTokens, Is.EquivalentTo(new[] { "invalid-token" }));
        }

        [Test]
        public void should_apply_skip_missing_asin_during_non_language_profile_filtering()
        {
            var sut = CreateSut();
            var profile = new MetadataProfile { SkipMissingAsin = true };
            var editions = new List<Edition>
            {
                new Edition { Title = "With Asin", Asin = "B000TEST" },
                new Edition { Title = "Without Asin", Isbn13 = "9780441172719" }
            };

            var filtered = sut.Apply(editions, profile);

            Assert.That(filtered.Count, Is.EqualTo(1));
            Assert.That(filtered[0].Title, Is.EqualTo("With Asin"));
        }

        [Test]
        public void should_split_comma_separated_ignored_terms_using_shared_term_matching()
        {
            var sut = CreateSut();
            var profile = new MetadataProfile { Ignored = new List<string> { "movie tie-in, illustrated" } };
            var editions = new List<Edition>
            {
                new Edition { Title = "Dune (Illustrated Edition)" },
                new Edition { Title = "Dune" }
            };

            var filtered = sut.Apply(editions, profile);

            Assert.That(filtered.Count, Is.EqualTo(1));
            Assert.That(filtered[0].Title, Is.EqualTo("Dune"));
        }

        [Test]
        public void should_filter_by_allowed_language_including_unknown_bucket()
        {
            var sut = CreateSut();
            var profile = new MetadataProfile { AllowedLanguages = "eng, null" };
            var editions = new List<Edition>
            {
                new Edition { Title = "English", Language = "eng" },
                new Edition { Title = "Unknown", Language = null },
                new Edition { Title = "French", Language = "fra" }
            };

            var filtered = sut.Apply(editions, profile);

            Assert.That(filtered.Count, Is.EqualTo(2));
            Assert.That(filtered.Exists(e => e.Title == "English"), Is.True);
            Assert.That(filtered.Exists(e => e.Title == "Unknown"), Is.True);
        }

        [Test]
        public void should_preserve_protected_editions_even_when_out_of_profile_or_ignored()
        {
            var sut = CreateSut();
            var profile = new MetadataProfile
            {
                AllowedLanguages = "eng",
                Ignored = new List<string> { "special edition" },
                SkipMissingAsin = true
            };

            var editions = new List<Edition>
            {
                new Edition { ForeignEditionId = "eng-ok", Title = "Dune", Language = "eng", Asin = "B000OK" },
                new Edition { ForeignEditionId = "fra-protected", Title = "Dune Special Edition", Language = "fra" }
            };

            var filtered = sut.Apply(editions, profile, new HashSet<string> { "fra-protected" });

            Assert.That(filtered.Count, Is.EqualTo(2));
            Assert.That(filtered.Exists(e => e.ForeignEditionId == "fra-protected"), Is.True);
        }

        [Test]
        public void should_disable_language_filter_when_no_valid_allowed_language_tokens_are_parsed()
        {
            var sut = CreateSut();
            var profile = new MetadataProfile { AllowedLanguages = "totally-invalid-token" };
            var editions = new List<Edition>
            {
                new Edition { Title = "English", Language = "eng" },
                new Edition { Title = "French", Language = "fra" }
            };

            var filtered = sut.Apply(editions, profile);

            Assert.That(filtered.Count, Is.EqualTo(2));
        }
    }
}

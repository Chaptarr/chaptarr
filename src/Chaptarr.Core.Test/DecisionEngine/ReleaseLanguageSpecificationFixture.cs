using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class ReleaseLanguageSpecificationFixture
    {
        private static ReleaseLanguageSpecification CreateSubject(params MetadataProfile[] profiles)
        {
            return new ReleaseLanguageSpecification(new StubMetadataProfileService(profiles), LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_reject_known_foreign_language_from_title_when_profile_only_allows_english()
        {
            var profile = new MetadataProfile
            {
                Id = 2,
                Name = "Ebook Default",
                AllowedLanguages = "eng"
            };

            var subject = CreateSubject(profile);
            var remoteBook = BuildRemoteBook(
                "George.Orwell.Nitton.Attiofyra.1984.2025.SWEDiSH.RETAiL.ePub.eBOOK-DECiPHER-xpost",
                profileId: 2);

            var decision = subject.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.True);
            Assert.That(decision.Category, Is.EqualTo("Language"));
            Assert.That(decision.Reason, Does.Contain("not allowed"));
            Assert.That(decision.Reason, Does.Contain("swe"));
        }

        [Test]
        public void should_accept_known_allowed_language_from_indexer_metadata()
        {
            var profile = new MetadataProfile
            {
                Id = 2,
                Name = "Ebook Default",
                AllowedLanguages = "eng"
            };

            var subject = CreateSubject(profile);
            var remoteBook = BuildRemoteBook(
                "1984 by George Orwell",
                profileId: 2,
                languages: new List<Language> { Language.English });

            var decision = subject.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_allow_unknown_language_when_release_has_no_explicit_language_signal()
        {
            var profile = new MetadataProfile
            {
                Id = 2,
                Name = "Ebook Default",
                AllowedLanguages = "eng"
            };

            var subject = CreateSubject(profile);
            var remoteBook = BuildRemoteBook(
                "1984 by George Orwell",
                profileId: 2);

            var decision = subject.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_not_reject_when_language_word_is_only_part_of_the_actual_book_title()
        {
            var profile = new MetadataProfile
            {
                Id = 2,
                Name = "Ebook Default",
                AllowedLanguages = "eng"
            };

            var subject = CreateSubject(profile);
            var remoteBook = BuildRemoteBook(
                "Learn to Speak Spanish by Jane Doe EPUB",
                profileId: 2,
                bookTitle: "Learn to Speak Spanish",
                authorName: "Jane Doe");

            var decision = subject.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_not_reject_when_english_word_is_only_part_of_the_actual_book_title()
        {
            var profile = new MetadataProfile
            {
                Id = 2,
                Name = "Ebook Default",
                AllowedLanguages = "eng"
            };

            var subject = CreateSubject(profile);
            var remoteBook = BuildRemoteBook(
                "Plain English by Jane Doe EPUB",
                profileId: 2,
                bookTitle: "Plain English",
                authorName: "Jane Doe");

            var decision = subject.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_reject_when_language_word_remains_after_book_title_and_author_are_removed()
        {
            var profile = new MetadataProfile
            {
                Id = 2,
                Name = "Ebook Default",
                AllowedLanguages = "eng"
            };

            var subject = CreateSubject(profile);
            var remoteBook = BuildRemoteBook(
                "Learn to Speak Spanish by Jane Doe Spanish EPUB",
                profileId: 2,
                bookTitle: "Learn to Speak Spanish",
                authorName: "Jane Doe");

            var decision = subject.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Does.Contain("spa"));
        }

        [Test]
        public void should_reject_foreign_language_when_release_resolves_to_ebook_context_even_if_quality_is_unknown_text()
        {
            var profile = new MetadataProfile
            {
                Id = 2,
                Name = "Ebook Default",
                AllowedLanguages = "eng"
            };

            var subject = CreateSubject(profile);
            var remoteBook = BuildRemoteBook(
                "Orwell, George - 1984 NL",
                profileId: 2,
                quality: Quality.Unknown,
                mediaType: BookMediaType.Ebook);

            var decision = subject.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Does.Contain("nld"));
        }

        [Test]
        public void should_reject_foreign_language_when_title_contains_bracketed_ebook_hint_even_if_quality_is_unknown_text()
        {
            var profile = new MetadataProfile
            {
                Id = 2,
                Name = "Ebook Default",
                AllowedLanguages = "eng"
            };

            var subject = CreateSubject(profile);
            var remoteBook = BuildRemoteBook(
                "1984 - George Orwell [Ebook][Spanish]",
                profileId: 2,
                quality: Quality.Unknown,
                mediaType: BookMediaType.Ebook);

            var decision = subject.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Does.Contain("spa"));
        }

        [Test]
        public void should_prefer_explicit_indexer_languages_over_title_inference()
        {
            var profile = new MetadataProfile
            {
                Id = 2,
                Name = "Ebook Default",
                AllowedLanguages = "eng"
            };

            var subject = CreateSubject(profile);
            var remoteBook = BuildRemoteBook(
                "Learn to Speak Spanish by Jane Doe EPUB",
                profileId: 2,
                bookTitle: "Learn to Speak Spanish",
                authorName: "Jane Doe",
                languages: new List<Language> { Language.English });

            var decision = subject.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_not_treat_raw_release_title_fallback_as_a_protected_book_title()
        {
            var profile = new MetadataProfile
            {
                Id = 2,
                Name = "Ebook Default",
                AllowedLanguages = "eng"
            };

            var subject = CreateSubject(profile);
            var remoteBook = BuildRemoteBook(
                "George Orwell - 1984 Spanish",
                profileId: 2,
                bookTitle: "1984",
                authorName: "George Orwell");

            remoteBook.SearchCriteriaMatch = null;
            remoteBook.ParsedBookInfo.BookTitle = remoteBook.Release.Title;

            var decision = subject.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Does.Contain("spa"));
        }

        private static RemoteBook BuildRemoteBook(string title, int profileId, string bookTitle = "1984", string authorName = "George Orwell", List<Language> languages = null, Quality quality = null, BookMediaType mediaType = BookMediaType.Ebook)
        {
            var book = new Book
            {
                Title = bookTitle,
                MediaType = mediaType,
                Editions = new List<Edition>
                {
                    new Edition { Title = bookTitle }
                }
            };

            return new RemoteBook
            {
                Author = new Author
                {
                    Name = authorName,
                    EbookMetadataProfileId = profileId
                },
                Books = new List<Book> { book },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = authorName,
                    BookTitle = bookTitle,
                    Quality = new QualityModel(quality ?? Quality.EPUB)
                },
                SearchCriteriaMatch = new TitleMatchResult
                {
                    PrimaryTitle = bookTitle,
                    MatchedVariant = bookTitle,
                    Book = book,
                    IsMatch = true
                },
                Release = new ReleaseInfo
                {
                    Title = title,
                    Author = authorName,
                    Languages = languages ?? new List<Language>()
                }
            };
        }

        private sealed class StubMetadataProfileService : IMetadataProfileService
        {
            private readonly Dictionary<int, MetadataProfile> _profiles;

            public StubMetadataProfileService(params MetadataProfile[] profiles)
            {
                _profiles = new Dictionary<int, MetadataProfile>();

                foreach (var profile in profiles ?? Array.Empty<MetadataProfile>())
                {
                    _profiles[profile.Id] = profile;
                }
            }

            public MetadataProfile Add(MetadataProfile profile) => throw new NotImplementedException();
            public void Update(MetadataProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public List<MetadataProfile> All() => new List<MetadataProfile>(_profiles.Values);
            public MetadataProfile Get(int id) => _profiles.TryGetValue(id, out var profile) ? profile : null;
            public bool Exists(int id) => _profiles.ContainsKey(id);
            public List<Book> FilterBooks(Author input, int profileId) => throw new NotImplementedException();
        }
    }
}

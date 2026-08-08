using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Releases;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class ReleaseRestrictionsSpecificationFixture
    {
        [Test]
        public void should_make_ignored_release_profile_terms_bypassable_for_interactive_grabs()
        {
            var profile = new ReleaseProfile
            {
                Enabled = true,
                Ignored = new List<string> { "graphic audio" }
            };

            var decision = CreateSubject(profile).IsSatisfiedBy(BuildRemoteBook("Golden Son Graphic Audio"), null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Category, Is.EqualTo("Release Profile"));
            Assert.That(decision.CanBypass, Is.True);
        }

        [Test]
        public void should_make_required_release_profile_terms_bypassable_for_interactive_grabs()
        {
            var profile = new ReleaseProfile
            {
                Enabled = true,
                Required = new List<string> { "unabridged" }
            };

            var decision = CreateSubject(profile).IsSatisfiedBy(BuildRemoteBook("Golden Son Graphic Audio"), null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Category, Is.EqualTo("Release Profile"));
            Assert.That(decision.CanBypass, Is.True);
        }

        private static ReleaseRestrictionsSpecification CreateSubject(params ReleaseProfile[] profiles)
        {
            return new ReleaseRestrictionsSpecification(
                new ContainsTermMatcherService(),
                new StubReleaseProfileService(profiles),
                LogManager.GetCurrentClassLogger());
        }

        private static RemoteBook BuildRemoteBook(string title)
        {
            return new RemoteBook
            {
                Author = new Author
                {
                    Id = 1,
                    Name = "Pierce Brown",
                    Tags = new HashSet<int>()
                },
                Books = new List<Book>
                {
                    new Book
                    {
                        Id = 1,
                        AuthorId = 1,
                        Title = "Golden Son",
                        MediaType = BookMediaType.Audiobook
                    }
                },
                Release = new ReleaseInfo
                {
                    Title = title,
                    IndexerId = 0
                }
            };
        }

        private sealed class ContainsTermMatcherService : ITermMatcherService
        {
            public bool IsMatch(string term, string value)
            {
                return MatchingTerm(term, value) != null;
            }

            public string MatchingTerm(string term, string value)
            {
                if (string.IsNullOrWhiteSpace(term) || string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                return value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ? term : null;
            }
        }

        private sealed class StubReleaseProfileService : IReleaseProfileService
        {
            private readonly List<ReleaseProfile> _profiles;

            public StubReleaseProfileService(IEnumerable<ReleaseProfile> profiles)
            {
                _profiles = new List<ReleaseProfile>(profiles);
            }

            public List<ReleaseProfile> EnabledForTags(HashSet<int> tagIds, int indexerId)
            {
                return _profiles;
            }

            public List<ReleaseProfile> All() => _profiles;
            public List<ReleaseProfile> AllForTag(int tagId) => _profiles;
            public List<ReleaseProfile> AllForTags(HashSet<int> tagIds) => _profiles;
            public ReleaseProfile Get(int id) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public ReleaseProfile Add(ReleaseProfile restriction) => throw new NotImplementedException();
            public ReleaseProfile Update(ReleaseProfile restriction) => throw new NotImplementedException();
        }
    }
}

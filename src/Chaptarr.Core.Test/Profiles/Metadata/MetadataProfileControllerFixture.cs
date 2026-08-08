using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using Chaptarr.Http.REST;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Profiles.Metadata;
using MetadataApi = Chaptarr.Api.V1.Profiles.Metadata;

namespace Chaptarr.Core.Test.Profiles.Metadata
{
    [TestFixture]
    public class MetadataProfileControllerFixture
    {
        private sealed class StubMetadataProfileService : IMetadataProfileService
        {
            private readonly List<MetadataProfile> _profiles;

            public StubMetadataProfileService(params MetadataProfile[] profiles)
            {
                _profiles = profiles.ToList();
            }

            public MetadataProfile Add(MetadataProfile profile) => profile;
            public void Update(MetadataProfile profile) { }
            public void Delete(int id) { }
            public List<MetadataProfile> All() => _profiles;
            public MetadataProfile Get(int id) => _profiles.First(p => p.Id == id);
            public bool Exists(int id) => _profiles.Any(p => p.Id == id);
            public List<Book> FilterBooks(Author input, int profileId) => input.Books;
        }

        [Test]
        public void list_should_return_all_profiles_when_media_type_is_not_provided()
        {
            var controller = new MetadataApi.MetadataProfileController(
                new StubMetadataProfileService(
                    CreateProfile(1, MetadataProfileType.General),
                    CreateProfile(2, MetadataProfileType.Audiobook),
                    CreateProfile(3, MetadataProfileType.Ebook)),
                authorService: null,
                commandQueueManager: null);

            var resources = controller.GetAll();

            Assert.That(resources.Select(p => p.Id), Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void list_should_return_general_and_audiobook_profiles_for_audiobook_media_type()
        {
            var controller = new MetadataApi.MetadataProfileController(
                new StubMetadataProfileService(
                    CreateProfile(1, MetadataProfileType.General),
                    CreateProfile(2, MetadataProfileType.Audiobook),
                    CreateProfile(3, MetadataProfileType.Ebook)),
                authorService: null,
                commandQueueManager: null);

            var resources = controller.GetAll("audiobook");

            Assert.That(resources.Select(p => p.Id), Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void list_should_return_general_and_ebook_profiles_for_ebook_media_type()
        {
            var controller = new MetadataApi.MetadataProfileController(
                new StubMetadataProfileService(
                    CreateProfile(1, MetadataProfileType.General),
                    CreateProfile(2, MetadataProfileType.Audiobook),
                    CreateProfile(3, MetadataProfileType.Ebook)),
                authorService: null,
                commandQueueManager: null);

            var resources = controller.GetAll("ebook");

            Assert.That(resources.Select(p => p.Id), Is.EqualTo(new[] { 1, 3 }));
        }

        [Test]
        public void list_should_reject_unknown_media_type()
        {
            var controller = new MetadataApi.MetadataProfileController(
                new StubMetadataProfileService(),
                authorService: null,
                commandQueueManager: null);

            var exception = Assert.Throws<BadRequestException>(() => controller.GetAll("audio"));

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(exception.Content.ToString(), Does.Contain("mediaType"));
        }

        [Test]
        public void resource_should_reject_invalid_profile_type()
        {
            var resource = new MetadataApi.MetadataProfileResource
            {
                Name = "Invalid",
                ProfileType = 99
            };

            var exception = Assert.Throws<BadRequestException>(() => MetadataApi.MetadataProfileResourceMapper.ToModel(resource));

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(exception.Content.ToString(), Does.Contain("Profile type"));
        }

        [Test]
        public void update_should_not_refresh_authors_when_allowed_languages_are_equivalent()
        {
            var previous = CreateProfile(1, MetadataProfileType.Audiobook);
            previous.AllowedLanguages = "English, unknown";

            var current = CreateProfile(1, MetadataProfileType.Audiobook);
            current.AllowedLanguages = "null, eng";

            Assert.That(ShouldRefreshAuthorsForProfileFilterChange(previous, current), Is.False);
        }

        [Test]
        public void update_should_refresh_authors_when_allowed_languages_change()
        {
            var previous = CreateProfile(1, MetadataProfileType.Audiobook);
            previous.AllowedLanguages = "eng";

            var current = CreateProfile(1, MetadataProfileType.Audiobook);
            current.AllowedLanguages = "swe";

            Assert.That(ShouldRefreshAuthorsForProfileFilterChange(previous, current), Is.True);
        }

        private static MetadataProfile CreateProfile(int id, MetadataProfileType type)
        {
            return new MetadataProfile
            {
                Id = id,
                Name = $"Profile {id}",
                ProfileType = type,
                Ignored = new List<string>()
            };
        }

        private static bool ShouldRefreshAuthorsForProfileFilterChange(MetadataProfile previous, MetadataProfile current)
        {
            var method = typeof(MetadataApi.MetadataProfileController).GetMethod(
                "ShouldRefreshAuthorsForProfileFilterChange",
                BindingFlags.NonPublic | BindingFlags.Static);

            return (bool)method.Invoke(null, new object[] { previous, current });
        }
    }
}

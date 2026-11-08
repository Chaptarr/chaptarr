using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http.REST;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Profiles.Qualities;

namespace Chaptarr.Core.Test.Api.MediaTypes
{
    [TestFixture]
    public class MediaTypeParameterParserFixture
    {
        [TestCase("audiobook", null, null)]
        [TestCase(null, "audiobook", null)]
        [TestCase(null, null, "ebook")]
        [TestCase("ebook", "ebook", "ebook")]
        public void one_format_and_both_are_conflicting_delete_instructions(string facade, string query, string body)
        {
            var error = Assert.Throws<NzbDrone.Core.Exceptions.NzbDroneClientException>(() =>
                MediaTypeParameterParser.ParseDeletionScope(true, facade, query, body));
            Assert.That(error.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Conflict));
            Assert.That(MediaTypeParameterParser.ParseDeletionScope(false, facade, query, body), Is.Not.Null);
            Assert.That(MediaTypeParameterParser.ParseDeletionScope(true, null), Is.Null);
        }

        [TestCase(FolderType.Audiobook, "audiobook")]
        [TestCase(FolderType.Ebook, "ebook")]
        [TestCase(FolderType.Mixed, "audiobook")]
        public void named_root_is_the_authority_for_inferred_format(FolderType type, string expected)
        {
            Assert.That(MediaTypeParameterParser.InferWriteScope(new RootFolder { FolderType = type },
                new Author { AudiobookQualityProfileId = 1, AudiobookRootFolderPath = "/audio" }), Is.EqualTo(expected));
        }

        [TestCase(ProfileType.Audiobook, "audiobook")]
        [TestCase(ProfileType.Ebook, "ebook")]
        public void typed_profile_resolves_a_mixed_root_before_the_author(ProfileType type, string expected)
        {
            Assert.That(MediaTypeParameterParser.InferWriteScope(new RootFolder { FolderType = FolderType.Mixed },
                new Author { AudiobookQualityProfileId = 1, AudiobookRootFolderPath = "/audio" }, type), Is.EqualTo(expected));
        }

        [TestCase(FolderType.Audiobook, ProfileType.Ebook)]
        [TestCase(FolderType.Ebook, ProfileType.Audiobook)]
        public void profile_cannot_contradict_a_single_format_root(FolderType root, ProfileType profile)
        {
            Assert.Throws<BadRequestException>(() => MediaTypeParameterParser.InferWriteScope(new RootFolder { FolderType = root }, null, profile));
        }

        [TestCase(true, false, "audiobook")]
        [TestCase(false, true, "ebook")]
        [TestCase(true, true, null)]
        [TestCase(false, false, null)]
        public void existing_author_infers_only_one_configured_side(bool audio, bool ebook, string expected)
        {
            Assert.That(MediaTypeParameterParser.InferWriteScope(null, new Author
            {
                AudiobookQualityProfileId = audio ? 1 : null, AudiobookRootFolderPath = audio ? "/audio" : null,
                EbookQualityProfileId = ebook ? 2 : null, EbookRootFolderPath = ebook ? "/ebook" : null
            }), Is.EqualTo(expected));
        }

        [TestCase(BookMediaType.Audiobook, 1, "/audio", true)]
        [TestCase(BookMediaType.Ebook, 2, "/books", true)]
        [TestCase(BookMediaType.Audiobook, null, "/audio", false)]
        [TestCase(BookMediaType.Ebook, 0, "/books", false)]
        [TestCase(BookMediaType.Audiobook, 1, " ", false)]
        [TestCase(BookMediaType.Ebook, 2, null, false)]
        public void format_inference_and_book_add_share_the_configured_root_check(BookMediaType media, int? profile, string root, bool expected)
        {
            var model = new Author();
            var resource = new Chaptarr.Api.V1.Author.AuthorResource();
            if (media == BookMediaType.Audiobook)
            {
                model.AudiobookQualityProfileId = resource.AudiobookQualityProfileId = profile;
                model.AudiobookRootFolderPath = resource.AudiobookRootFolderPath = root;
            }
            else
            {
                model.EbookQualityProfileId = resource.EbookQualityProfileId = profile;
                model.EbookRootFolderPath = resource.EbookRootFolderPath = root;
            }
            Assert.That(MediaTypeParameterParser.HasConfiguredMediaSettings(model, media), Is.EqualTo(expected));
            Assert.That(MediaTypeParameterParser.HasConfiguredMediaSettings(resource, media), Is.EqualTo(expected));
            Assert.That(MediaTypeParameterParser.InferWriteScope(null, model), Is.EqualTo(expected ? MediaTypeParameterParser.ToApiValue(media) : null));
        }

        [Test]
        public void optional_media_type_should_treat_empty_and_all_as_unfiltered()
        {
            Assert.That(MediaTypeParameterParser.ParseOptional(null), Is.Null);
            Assert.That(MediaTypeParameterParser.ParseOptional(""), Is.Null);
            Assert.That(MediaTypeParameterParser.ParseOptional("all"), Is.Null);
        }

        [Test]
        public void optional_media_type_should_parse_known_values()
        {
            Assert.That(MediaTypeParameterParser.ParseOptional("audiobook"), Is.EqualTo(BookMediaType.Audiobook));
            Assert.That(MediaTypeParameterParser.ParseOptional("EBOOK"), Is.EqualTo(BookMediaType.Ebook));
        }

        [Test]
        public void optional_media_type_should_reject_unknown_values()
        {
            Assert.Throws<BadRequestException>(() => MediaTypeParameterParser.ParseOptional("banana"));
        }

        [Test]
        public void required_media_type_should_not_accept_all()
        {
            Assert.Throws<BadRequestException>(() => MediaTypeParameterParser.ParseRequired("all"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("banana")]
        public void required_media_type_should_reject_absent_and_unknown_values(string mediaType)
        {
            Assert.Throws<BadRequestException>(() => MediaTypeParameterParser.ParseRequired(mediaType));
        }

        [Test]
        public void required_media_type_should_parse_known_values()
        {
            Assert.That(MediaTypeParameterParser.ParseRequired("audiobook"), Is.EqualTo(BookMediaType.Audiobook));
            Assert.That(MediaTypeParameterParser.ParseRequired(" Ebook "), Is.EqualTo(BookMediaType.Ebook));
        }
    }
}

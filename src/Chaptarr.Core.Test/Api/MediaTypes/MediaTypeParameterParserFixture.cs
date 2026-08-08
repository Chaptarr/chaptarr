using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http.REST;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Api.MediaTypes
{
    [TestFixture]
    public class MediaTypeParameterParserFixture
    {
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

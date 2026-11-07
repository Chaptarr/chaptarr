using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class CalibreProxySetFieldsInputsFixture
    {
        [Test]
        public void should_build_empty_tags_when_genres_are_missing()
        {
            Assert.That(CalibreProxy.BuildTags(null), Is.Empty);
            Assert.That(CalibreProxy.BuildTags(new Book { Genres = null }), Is.Empty);
        }

        [Test]
        public void should_title_case_tags_and_drop_blank_genres()
        {
            var tags = CalibreProxy.BuildTags(new Book { Genres = new List<string> { "science-fiction", "", "fantasy" } });

            Assert.That(tags, Is.EqualTo(new[] { "Science Fiction", "Fantasy" }));
        }

        [Test]
        public void should_default_rating_to_zero_when_ratings_are_missing()
        {
            Assert.That(CalibreProxy.BuildRating(new Edition { Ratings = null }), Is.EqualTo(0));
            Assert.That(CalibreProxy.BuildRating(null), Is.EqualTo(0));
        }

        [Test]
        public void should_double_rating_for_calibre_scale()
        {
            Assert.That(CalibreProxy.BuildRating(new Edition { Ratings = new Ratings { Value = 4.5m } }), Is.EqualTo(9));
        }
    }
}

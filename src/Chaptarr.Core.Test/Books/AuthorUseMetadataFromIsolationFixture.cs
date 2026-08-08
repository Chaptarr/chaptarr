using System.Collections.Generic;
using System.Linq;
using CoreMediaCover = NzbDrone.Core.MediaCover.MediaCover;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorUseMetadataFromIsolationFixture
    {
        [Test]
        public void should_clone_mutable_metadata_from_source_author()
        {
            var source = new Author
            {
                Name = "Brandon Sanderson",
                Aliases = new List<string> { "B$" },
                Genres = new List<string> { "Fantasy" },
                Pseudonyms = new List<string> { "Alcatraz" },
                Links = new List<Links>
                {
                    new Links { Name = "goodreads", Url = "https://www.goodreads.com/author/show/38550" }
                },
                Images = new List<CoreMediaCover>
                {
                    new CoreMediaCover(MediaCoverTypes.Poster, "https://assets.hardcover.app/author/204214/6001568168434228.jpg")
                },
                Ratings = new Ratings
                {
                    Votes = 10,
                    Value = 4.9m
                }
            };

            var target = new Author();

            target.UseMetadataFrom(source);

            target.Aliases[0] = "mutated";
            target.Genres[0] = "Sci-Fi";
            target.Pseudonyms[0] = "mutated";
            target.Links[0].Url = "https://mutated.example.com";
            target.Images[0].Url = "https://mutated.example.com/poster.jpg";
            target.Ratings.Value = 1.0m;

            Assert.That(source.Aliases[0], Is.EqualTo("B$"));
            Assert.That(source.Genres[0], Is.EqualTo("Fantasy"));
            Assert.That(source.Pseudonyms[0], Is.EqualTo("Alcatraz"));
            Assert.That(source.Links[0].Url, Is.EqualTo("https://www.goodreads.com/author/show/38550"));
            Assert.That(source.Images[0].Url, Is.EqualTo("https://assets.hardcover.app/author/204214/6001568168434228.jpg"));
            Assert.That(source.Ratings.Value, Is.EqualTo(4.9m));
        }

        [Test]
        public void should_clone_remote_provider_ids_from_source_author()
        {
            var source = new Author
            {
                RemoteProviderIds = new HashSet<string> { "gr:1077326", "hc:80626" }
            };

            var target = new Author();

            target.UseMetadataFrom(source);
            target.RemoteProviderIds.Add("hc:255839");

            Assert.That(target.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:1077326", "hc:80626", "hc:255839" }));
            Assert.That(source.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:1077326", "hc:80626" }));
            Assert.That(target.RemoteProviderIds, Is.Not.SameAs(source.RemoteProviderIds));
        }

        [Test]
        public void should_not_persist_known_provider_placeholder_images()
        {
            const string placeholder = "https://assets.hardcover.app/author/910003/provider-default.jpg";
            const string realPhoto = "https://images.example/real-author.jpg";
            MediaCoverRendition.RegisterKnownPlaceholderImage(placeholder, "47736d50a054c80f646c094ecd9c00c3fb12f5c585817fa5a581140c364da30e");
            var source = new Author
            {
                Images = new List<CoreMediaCover>
                {
                    new(MediaCoverTypes.Poster, placeholder),
                    new(MediaCoverTypes.Poster, realPhoto)
                }
            };
            var target = new Author
            {
                Images = new List<CoreMediaCover>
                {
                    new(MediaCoverTypes.Poster, placeholder)
                }
            };

            target.UseMetadataFrom(source);

            Assert.That(target.Images.Select(image => image.Url), Is.EqualTo(new[] { realPhoto }));
        }

        [Test]
        public void should_scrub_existing_placeholder_when_no_real_photo_exists()
        {
            const string placeholder = "https://assets.hardcover.app/author/910004/provider-default.jpg";
            MediaCoverRendition.RegisterKnownPlaceholderImage(placeholder, "47736d50a054c80f646c094ecd9c00c3fb12f5c585817fa5a581140c364da30e");
            var source = new Author
            {
                Images = new List<CoreMediaCover>
                {
                    new(MediaCoverTypes.Poster, placeholder)
                }
            };
            var target = new Author
            {
                Images = new List<CoreMediaCover>
                {
                    new(MediaCoverTypes.Poster, placeholder)
                }
            };

            target.UseMetadataFrom(source);

            Assert.That(target.Images, Is.Empty);
        }
    }
}

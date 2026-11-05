using System.Linq;
using Chaptarr.Api.V1.BookFiles;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookFileResourceMatchProvenanceFixture
    {
        [TestCase(10, 100)]
        [TestCase(11, 110)]
        [TestCase(12, 105)]
        public void bookshelf_file_should_preserve_original_default_audio_weight(int qualityId, int weight)
        {
            var model = new BookFile { Quality = new QualityModel((Quality)qualityId, new Revision(2, 1)) };
            var native = model.ToResource();
            var legacy = model.ToResource(bookshelfDefaultWeights: true);

            var mapped = model.ToResource(new Author { Id = 20 }, null, bookshelfDefaultWeights: true);
            Assert.That(mapped.QualityWeight, Is.EqualTo(weight + 12));
            Assert.That(legacy.QualityWeight, Is.EqualTo(weight + 12));
            Assert.That(native.QualityWeight, Is.EqualTo(Quality.DefaultQualityDefinitions.Single(definition => definition.Quality.Id == qualityId).Weight + 12));
        }

        [Test]
        public void resource_should_expose_the_persisted_match_provenance_without_reconstruction()
        {
            var provenance = new MatchProvenance
            {
                DecisionId = "decision-api",
                Mode = "Balanced",
                Route = "global/embedded_tags"
            };
            var model = new BookFile
            {
                Id = 1,
                Edition = new Edition { BookId = 10 },
                Quality = new QualityModel { Quality = Quality.EPUB },
                MediaInfo = new MediaInfoModel(),
                MatchProvenance = provenance
            };

            var resource = model.ToResource();

            Assert.That(resource.MatchProvenance, Is.SameAs(provenance));
            Assert.That(resource.MatchProvenance.DecisionId, Is.EqualTo("decision-api"));
        }
    }
}

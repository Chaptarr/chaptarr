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

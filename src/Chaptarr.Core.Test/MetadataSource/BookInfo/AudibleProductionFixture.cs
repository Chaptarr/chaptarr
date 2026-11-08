using System;
using System.IO;
using System.Linq;
using System.Net;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Http;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.Audible;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MetadataSource.BookInfo
{
    [TestFixture]
    public class AudibleProductionFixture
    {
        // Sanitized provider response; capture details are in the companion fixture.
        private static JObject CapturedProduct(string filename = "audible-rogues-production.json") => JObject.Parse(File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "Fixtures", "ProviderSearch", filename)));

        private static Edition Search(JObject payload, bool byId)
        {
            var requests = 0;
            var cached = InterfaceProxy.Proxy<ICachedHttpResponseService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo("Get"));
                requests++;
                var response = byId ? payload : new JObject { ["products"] = new JArray(payload["product"].DeepClone()) };
                return new HttpResponse((HttpRequest)args[0], new HttpHeader { ContentType = "application/json" }, response.ToString(), HttpStatusCode.OK);
            });
            var logger = LogManager.GetCurrentClassLogger();
            var health = InterfaceProxy.Proxy<IMetadataServerHealthGate>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo("CanAttemptWithoutProbe"));
                args[0] = TimeSpan.Zero;
                return false;
            });
            var proxy = new BookInfoProxy(null, null, null, null, new AudibleCatalogProxy(cached, logger),
                null, null, null, null, null, logger, new CacheManager(), health);
            var results = proxy.SearchForNewEntity(byId ? (string)payload["product"]["asin"] : (string)payload["product"]["title"], "audible");
            Assert.That(requests, Is.EqualTo(1));
            Assert.That(results.OfType<Book>().Count(), Is.EqualTo(1));
            return results.OfType<Book>().Single().Editions.Single();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void anthology_cast_size_never_invents_publisher_or_replaces_credits(bool byId)
        {
            var payload = CapturedProduct();
            var edition = Search(payload, byId);
            Assert.That(edition.Publisher, Is.EqualTo("Random House Audio"));
            Assert.That(edition.IsGraphicAudio, Is.False);
            Assert.That(edition.AudioProductionType, Is.Null);
            Assert.That(edition.NarratorNames, Is.EqualTo(payload["product"]["narrators"].Select(n => (string)n["name"])));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void pottermore_full_cast_keeps_all_fifteen_credits_without_graphicaudio_brand(bool byId)
        {
            var payload = CapturedProduct("audible-pottermore-production.json");
            var edition = Search(payload, byId);
            Assert.That(edition.Publisher, Is.EqualTo("Pottermore Publishing and Audible Studios"));
            Assert.That(edition.NarratorNames, Has.Count.EqualTo(15));
            Assert.That(edition.NarratorNames, Is.EqualTo(payload["product"]["narrators"].Select(n => (string)n["name"])));
            Assert.That(edition.IsGraphicAudio, Is.False);
            var author = new Author { Name = "J.K. Rowling" };
            edition.Book = new Book { Author = author, MediaType = BookMediaType.Audiobook };
            var file = new BookFile { Path = "/books/Harry Potter.m4b", Quality = new QualityModel(Quality.M4B) };
            Assert.That(AudioProductionDetector.HasGraphicAudioBrand(file, edition), Is.False);
            Assert.That(AudioProductionDetector.GetProductionLabel(file, edition), Is.EqualTo("Full Cast"));
            var config = NamingConfig.Default;
            config.RenameBooks = true;
            config.StandardBookFormat = "{Book Title}{ - Narrator}";
            var builder = Organizer.FileNameBuilderSeriesTokensFixture.CreateBuilder();
            Assert.That(builder.BuildBookFileName(author, edition, file, config, new()), Is.EqualTo(edition.Title + " - Full Cast"));
            config.StandardBookFormat = "{Narrators}";
            Assert.That(builder.BuildBookFileName(author, edition, file, config, new()), Is.EqualTo(string.Join(", ", edition.NarratorNames)));
        }

        // Constructed publisher/style controls using the captured response shape, not live observations.
        [TestCase("Full Cast Audio", "Full Cast", false)]
        [TestCase("Pottermore Publishing", "Full Cast", false)]
        [TestCase("GraphicAudio", "Full Cast", true)]
        [TestCase("Graphic Audio, LLC", "Full Cast", true)]
        [TestCase("Random House Audio", "Lancaster", false)]
        public void publisher_and_collective_credit_are_separate_evidence(string publisher, string narrator, bool graphicAudio)
        {
            var payload = CapturedProduct();
            payload["product"]["publisher_name"] = publisher;
            payload["product"]["narrators"] = new JArray(new JObject { ["name"] = narrator });
            var edition = Search(payload, true);
            Assert.That(edition.IsGraphicAudio, Is.EqualTo(graphicAudio));
            Assert.That(edition.NarratorNames, Is.EqualTo(new[] { narrator }));
            Assert.That(AudioProductionDetector.GetProductionLabel(new BookFile(), edition),
                Is.EqualTo(graphicAudio ? "GraphicAudio" : narrator == "Full Cast" ? "Full Cast" : null));
            Assert.That(edition.AudioProductionType, Is.EqualTo(graphicAudio ? "GraphicAudio" : narrator == "Full Cast" ? "Full Cast" : null));
            if (!graphicAudio && narrator == "Full Cast")
            {
                var author = new Author { Name = "Test Author" };
                edition.Book = new Book { Author = author, MediaType = BookMediaType.Audiobook };
                var config = NamingConfig.Default;
                config.RenameBooks = true;
                config.StandardBookFormat = "{Book Title}{ - Narrator}";
                var file = new BookFile { Path = "/books/recording.m4b", Quality = new QualityModel(Quality.M4B) };
                Assert.That(Organizer.FileNameBuilderSeriesTokensFixture.CreateBuilder()
                    .BuildBookFileName(author, edition, file, config, new()), Is.EqualTo(edition.Title + " - Full Cast"));
            }
        }
    }
}

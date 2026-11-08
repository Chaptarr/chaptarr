using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Chaptarr.Api.V1.Config;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Organizer.NamingPattern;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Organizer
{
    public partial class FileNameBuilderSeriesTokensFixture
    {
        [TestCase("Full Cast", "Full Cast")]
        [TestCase("diverse Sprecher", "diverse Sprecher + Bob Reader")]
        [TestCase("varios narradores", "varios narradores + Bob Reader")]
        [TestCase("narratori vari", "narratori vari + Bob Reader")]
        [TestCase("GraphicAudio", "GraphicAudio + Bob Reader")]
        [TestCase("Full Castle", "Full Castle + Bob Reader")]
        [TestCase("Alice Reader", "Alice Reader + Bob Reader")]
        public void two_narrator_credits_keep_the_released_combined_name(string credit, string expected)
        {
            var author = new Author { Name = "Author" };
            var edition = new Edition { Title = "Title", Book = new Book { Author = author },
                Narrator = credit, NarratorNames = new List<string> { credit, "Bob Reader" } };
            var file = new BookFile { Path = "/library/Title.m4b", Quality = new QualityModel(Quality.M4B) };
            var config = NamingConfig.Default;
            config.RenameBooks = true;
            config.StandardBookFormat = "{Book Title} - {Narrator}";
            Assert.That(CreateBuilder().BuildBookFileName(author, edition, file, config, new List<CustomFormat>()),
                Is.EqualTo("Title - " + expected));
        }

        [TestCase(null, "Title - GraphicAudio")]
        [TestCase(false, "Title - Alice Reader")]
        [TestCase(true, "Title - GraphicAudio")]
        public void recording_override_drives_smart_narrator_without_a_hidden_text_sentinel(bool? correction, string expected)
        {
            var author = new Author { Name = "Author" };
            var edition = new Edition { Title = "Title", Book = new Book { Author = author },
                Publisher = "GraphicAudio", IsGraphicAudio = true, AudioProductionType = "GraphicAudio", NarratorNames = new List<string> { "Alice Reader" } };
            var file = new BookFile { Path = "/library/Title.m4b", Quality = new QualityModel(Quality.M4B), AudioProductionOverride = correction };
            var config = NamingConfig.Default;
            config.RenameBooks = true;
            config.StandardBookFormat = "{Book Title}{ - Smart Narrator:AudioProductionType,FirstNarrator}";
            Assert.That(CreateBuilder().BuildBookFileName(author, edition, file, config, new List<CustomFormat>()), Is.EqualTo(expected));
        }

        [TestCase(null, "GraphicAudio", 3, "GraphicAudio")]
        [TestCase(null, "Booktrack", 1, "Booktrack")]
        [TestCase(null, "Dramatized", 1, "Dramatized")]
        [TestCase(null, "Full Cast", 1, "Full Cast")]
        [TestCase(null, null, 3, "Full Cast")]
        [TestCase(null, null, 2, "Alice Reader, Bob Reader")]
        [TestCase(null, null, 1, "Alice Reader")]
        [TestCase(null, null, 0, "")]
        [TestCase("Narrators,AudioProductionType", "GraphicAudio", 2, "Alice Reader, Bob Reader")]
        [TestCase("Narrators, AudioProductionType", "GraphicAudio", 2, "Alice Reader, Bob Reader")]
        [TestCase("Narrators,AudioProductionType", "Booktrack", 0, "Booktrack")]
        [TestCase("FullCast,Narrators", null, 1, "Alice Reader")]
        [TestCase("FullCast", null, 2, "")]
        [TestCase("FullCast", null, 3, "Full Cast")]
        [TestCase("NarratorInitials,Narrators", null, 1, "AR")]
        [TestCase("AudioProductionType,FirstNarrator", "GraphicAudio", 3, "GraphicAudio")]
        // Three credited narrators display Full Cast; saved fallback order still applies.
        [TestCase("AudioProductionType,FirstNarrator", null, 3, "Full Cast")]
        [TestCase("AudioProductionType,FirstNarrator", null, 2, "Alice Reader")]
        [TestCase("AudioProductionType,FirstNarrator", null, 0, "")]
        [TestCase("FirstNarrator,AudioProductionType", "Booktrack", 3, "Alice Reader")]
        public void smart_narrator_uses_first_available_token_in_saved_order(string order, string production, int count, string expected)
        {
            var names = new[] { "Alice Reader", "Bob Reader", "Carol Reader" }.Take(count).ToList();
            var originalNames = names.ToArray();
            var author = new Author { Name = "Author" };
            var edition = new Edition
            {
                Title = "Title", Book = new Book { Author = author },
                NarratorNames = names, AudioProductionType = production, Publisher = production == "GraphicAudio" ? production : null
            };
            var file = new BookFile { Path = "/downloads/source.m4b", Quality = new QualityModel(Quality.M4B) };
            var config = NamingConfig.Default;
            config.RenameBooks = true;
            config.StandardBookFormat = "{Book Title}{ - Smart Narrator" + (order == null ? "" : ":" + order) + "}";
            var expectedName = "Title" + (expected.Length == 0 ? "" : " - " + expected);

            Assert.That(CreateBuilder().BuildBookFileName(author, edition, file, config, new List<CustomFormat>()), Is.EqualTo(expectedName));
            var controller = new NamingPatternController(new NamingPatternService(new PatternCompiler(), CreateBuilder()));
            var preview = controller.Preview(new PreviewRequest
            {
                Pattern = config.StandardBookFormat,
                Sample = new SampleContext { BookTitle = "Title", NarratorName = "", NarratorNames = names, AudioProductionType = production, Publisher = edition.Publisher }
            });
            Assert.That(JsonSerializer.SerializeToElement(preview).GetProperty("path").GetString(), Is.EqualTo(expectedName));
            Assert.That(edition.NarratorNames, Is.EqualTo(originalNames));
            Assert.That(edition.IsGraphicAudio, Is.False);
            Assert.That(file.IsGraphicAudio, Is.False);
        }

        [TestCase("{Smart Narrator:Narrators,AudioProductionType}")]
        [TestCase("{ - Smart Narrator:FullCast,Narrators}")]
        [TestCase("{ [SMART_NARRATOR:AudioProductionType,Narrators]}")]
        [TestCase("{Smart Narrator}")]
        [TestCase("{SmartNarrator}")]
        [TestCase("{ - SmartNarrator:Narrators,AudioProductionType}")]
        [TestCase("{Smart Narrator:AudioProductionType,FirstNarrator}")]
        [TestCase("{First Narrator}")]
        public void smart_narrator_order_roundtrips_through_both_api_serializers(string token)
        {
            var pattern = "{Book Title}" + token + "{ (PartNumber:smart)}";
            var compiler = new PatternCompiler();
            var ast = compiler.Decompile(pattern);
            Assert.That(compiler.Compile(ast), Is.EqualTo(pattern));
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var stj = JsonSerializer.Deserialize<PatternAst>(JsonSerializer.Serialize(ast, options), options);
            Assert.That(compiler.Compile(stj), Is.EqualTo(pattern));
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(ast, new Newtonsoft.Json.JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
            });
            Assert.That(compiler.Compile(Newtonsoft.Json.JsonConvert.DeserializeObject<PatternAst>(json)), Is.EqualTo(pattern));
        }

        [TestCase("Unknown")]
        [TestCase("Narrators,Narrators")]
        [TestCase("SmartNarrator")]
        [TestCase("Narrators,")]
        [TestCase("Narrators, ")]
        [TestCase("Narrators, Narrators")]
        [TestCase("")]
        [TestCase("Narrators|FullCast")]
        public void invalid_smart_order_is_rejected_by_preview_and_naming_save(string order)
        {
            foreach (var token in new[] { "Smart Narrator", "SmartNarrator" })
            {
                var config = NamingConfig.Default;
                config.StandardBookFormat = "{Book Title}{ - " + token + ":" + order + "}{ (PartNumber:smart)}";
                var builder = CreateBuilder();
                var patternService = new NamingPatternService(new PatternCompiler(), builder);
                Assert.That(patternService.ValidatePattern(config.StandardBookFormat).IsValid, Is.False, token);
                var namingService = new StubNamingConfigService();
                var controller = new NamingConfigController(namingService, new FileNameSampleService(builder), new FileNameValidationService(new FileNameSampleService(builder)), builder);
                Assert.Throws<FluentValidation.ValidationException>(() => controller.UpdateNamingConfig(config.ToResource()));
                Assert.That(namingService.SaveCount, Is.Zero);
            }
        }

        [TestCase("audiobook", "{Book Title}{ - Smart Narrator:Narrators,AudioProductionType}", "Title - Alice Reader")]
        [TestCase("audiobook", "{Book Title}{ - Smart Narrator}", "Title - GraphicAudio")]
        [TestCase("audiobook", "{Book Title}{ - SmartNarrator}", "Title - GraphicAudio")]
        [TestCase("audiobook", "{Book Title}{ - SmartNarrator:Narrators,AudioProductionType}", "Title - Alice Reader")]
        [TestCase("audiobook", "{Book Title}{ [SMART_NARRATOR]}", "Title [GRAPHICAUDIO]")]
        [TestCase("audiobook", "{Book Title}{ - Narrator}", "Title - GraphicAudio - Alice Reader")]
        [TestCase("ebook", "{Book Title}{ - Smart Narrator}", "Title")]
        [TestCase("ebook", "{Book Title}{ - SmartNarrator}", "Title")]
        public void smart_order_controls_production_decoration_without_changing_legacy_templates(string media, string pattern, string expected)
        {
            var author = new Author { Name = "Author" };
            var edition = new Edition { Title = "Title", Book = new Book { Author = author }, Narrator = "Alice Reader", Publisher = "GraphicAudio" };
            var file = new BookFile { Path = "/downloads/source.m4b", MediaType = media, IsGraphicAudio = true, Quality = new QualityModel(media == "ebook" ? Quality.EPUB : Quality.M4B) };
            var config = NamingConfig.Default;
            config.RenameBooks = config.EbookRenameBooks = true;
            config.StandardBookFormat = config.EbookStandardBookFormat = pattern;
            Assert.That(CreateBuilder().BuildBookFileName(author, edition, file, config, new List<CustomFormat>()), Is.EqualTo(expected));
        }

        [Test]
        public void smart_token_preserves_standard_case_separator_and_empty_punctuation_rules()
        {
            var service = new NamingPatternService(new PatternCompiler(), CreateBuilder());
            Assert.That(service.PreviewPattern("{Book Title}{ [SMART_NARRATOR:Narrators]}", sampleContext: new SampleContext { NarratorName = "Alice Reader" }).Path,
                Is.EqualTo("Book Title [ALICE_READER]"));
            Assert.That(service.PreviewPattern("{Book Title}{ [Smart Narrator]}", sampleContext: new SampleContext { NarratorName = "" }).Path,
                Is.EqualTo("Book Title"));
        }

        [TestCase("{First Narrator}")]
        [TestCase("{Smart Narrator:AudioProductionType,FirstNarrator}")]
        public void first_narrator_uses_the_first_listed_credit_and_falls_back_to_the_primary_name(string token)
        {
            var service = new NamingPatternService(new PatternCompiler(), CreateBuilder());
            var sample = new SampleContext
            {
                NarratorName = "David de Vries",
                NarratorNames = new List<string> { "Emily Rankin", "Jim Butcher", "David de Vries" }
            };

            Assert.That(service.PreviewPattern("{Book Title}{ - " + token.Trim('{', '}') + "}", sampleContext: sample).Path,
                // ProductionType-first ordering uses the three-reader Full Cast label.
                Is.EqualTo(token.Contains("AudioProductionType") ? "Book Title - Full Cast" : "Book Title - Emily Rankin"));
            sample.NarratorNames.Clear();
            Assert.That(service.PreviewPattern("{Book Title}{ - " + token.Trim('{', '}') + "}", sampleContext: sample).Path,
                Is.EqualTo("Book Title - David de Vries"));
        }
    }
}

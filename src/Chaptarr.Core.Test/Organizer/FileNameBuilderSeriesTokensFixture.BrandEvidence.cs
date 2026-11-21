using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Organizer
{
    public partial class FileNameBuilderSeriesTokensFixture
    {
        [TestCase("publisher")]
        [TestCase("tags")]
        [TestCase("release")]
        [TestCase("filename")]
        [TestCase("originalFilename")]
        public void explicit_brand_evidence_controls_both_production_and_brand_tokens(string source)
        {
            var author = new Author { Name = "Brandon Sanderson" };
            var edition = new Edition { Title = "Elantris", Publisher = "Other publisher",
                Book = new Book { Author = author }, NarratorNames = new List<string> { "Reader One", "Reader Two", "Reader Three" } };
            var file = new BookFile { Path = "/books/Elantris.m4b", Quality = new QualityModel(Quality.M4B) };
            switch (source)
            {
                case "publisher": edition.Publisher = "GraphicAudio"; break;
                case "tags": file.AllTags = new() { ["COMM"] = new() { "Elantris — GraphicAudio" } }; break;
                case "release": file.SceneName = "Elantris GraphicAudio M4B"; break;
                case "filename": file.Path = @"C:\books\Elantris.GraphicAudio.m4b"; break;
                case "originalFilename": file.MatchProvenance = new MatchProvenance { ImportSourcePaths = new List<string> { "/downloads/Elantris GraphicAudio.m4b" } }; break;
            }

            var naming = NamingConfig.Default;
            naming.RenameBooks = true;
            naming.StandardBookFormat = "{GraphicAudio} - {AudioProductionType}";
            Assert.That(CreateBuilder().BuildBookFileName(author, edition, file, naming, new List<CustomFormat>()), Is.EqualTo("GraphicAudio - GraphicAudio"));
            file.AudioProductionOverride = false;
            Assert.That(AudioProductionDetector.HasGraphicAudioBrand(file, edition), Is.False);
            Assert.That(AudioProductionDetector.GetProductionLabel(file, edition), Is.EqualTo("Full Cast"),
                "Standard narration does not remove the edition's three known narrator credits or establish a publisher");
        }

        [TestCase(1, null)]
        [TestCase(2, null)]
        [TestCase(3, "Full Cast")]
        [TestCase(10, "Full Cast")]
        public void narrator_count_labels_full_cast_without_inventing_the_publisher_or_changing_matching(int readers, string expected)
        {
            var edition = new Edition { Title = "Rogues", Publisher = "Random House Audio", Book = new Book { Author = new Author { Name = "George R. R. Martin" } } };
            for (var i = 0; i < readers; ++i) edition.NarratorNames.Add("Reader " + i);
            var file = new BookFile { Path = "/books/Rogues.m4b", Quality = new QualityModel(Quality.M4B) };
            Assert.That(AudioProductionDetector.GetProductionLabel(file, edition), Is.EqualTo(expected));
            Assert.That(AudioProductionDetector.HasGraphicAudioBrand(file, edition), Is.False);
            Assert.That(AudioProductionDetector.IsDramatized(file, edition), Is.False, "Narrator count is a display convention, not new matching/custom-format evidence.");
            var naming = NamingConfig.Default;
            naming.RenameBooks = true;
            naming.StandardBookFormat = "{Book Title}";
            Assert.That(CreateBuilder().BuildBookFileName(edition.Book.Author, edition, file, naming, new List<CustomFormat>()), Is.EqualTo("Rogues"), "A count-only display label does not add a new title suffix.");
        }

        [TestCase("/books/GraphicAudio/Elantris.m4b", "Graphic Audiobook")]
        [TestCase("/books/Full Cast/Elantris.m4b", "Undramatized")]
        public void category_folder_legacy_style_and_partial_brand_words_do_not_establish_the_publisher(string path, string tag)
        {
            var edition = new Edition { Publisher = "Full Cast Audio", NarratorNames = new List<string> { "Alice", "Alice", "  " } };
            var file = new BookFile { Path = path, IsGraphicAudio = true, AudioProductionType = "GraphicAudio", AllTags = new() { ["COMMENT"] = new() { tag } } };
            Assert.That(AudioProductionDetector.HasGraphicAudioBrand(file, edition), Is.False);
            Assert.That(AudioProductionDetector.GetProductionLabel(file, edition), Is.EqualTo("Dramatized"));
        }
    }
}

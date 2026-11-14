using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Organizer
{
    public partial class FileNameBuilderSeriesTokensFixture
    {
        [TestCase("GraphicAudio", true, "GraphicAudio", "GraphicAudio")]
        [TestCase("Full Cast Audio", true, "GraphicAudio", "Dramatized")]
        [TestCase("Pottermore Publishing", true, "Full Cast", "Full Cast")]
        [TestCase("Random House Audio", true, null, "Dramatized")]
        [TestCase(null, true, "GraphicAudio", "Dramatized")]
        public void generic_recording_style_never_invents_graphicaudio_in_names(string publisher, bool flagged, string storedType, string expected)
        {
            var author = new Author { Name = "Author" };
            var edition = new Edition { Title = "Title", Publisher = publisher, Book = new Book { Author = author },
                IsGraphicAudio = flagged, AudioProductionType = storedType, NarratorNames = new List<string> { "Alice Reader" } };
            var file = new BookFile { Path = "/library/Title.m4b", Quality = new QualityModel(Quality.M4B) };
            var config = NamingConfig.Default;
            config.RenameBooks = true;
            var builder = CreateBuilder();
            config.StandardBookFormat = "{Book Title}{ - Smart Narrator:AudioProductionType,FirstNarrator}";
            Assert.That(builder.BuildBookFileName(author, edition, file, config, new List<CustomFormat>()), Is.EqualTo("Title - " + expected));
            config.StandardBookFormat = "{Book Title}";
            Assert.That(builder.BuildBookFileName(author, edition, file, config, new List<CustomFormat>()), Is.EqualTo("Title - " + expected));
            config.StandardBookFormat = "{GraphicAudio}";
            Assert.That(builder.BuildBookFileName(author, edition, file, config, new List<CustomFormat>()), Is.EqualTo(publisher == "GraphicAudio" ? "GraphicAudio" : ""));
            file.AudioProductionOverride = false;
            config.StandardBookFormat = "{Book Title}{ - Smart Narrator:AudioProductionType,FirstNarrator}";
            Assert.That(builder.BuildBookFileName(author, edition, file, config, new List<CustomFormat>()), Is.EqualTo("Title - Alice Reader"));
        }

        [TestCase("{Book Title}{ - Narrator}", 3, "Title - Full Cast")]
        [TestCase("{Book Title}{ - Narrator CleanName}", 3, "Title - Full Cast")]
        [TestCase("{Book Title}{ - FullCast}", 3, "Title - Full Cast")]
        [TestCase("{Book Title}{ - AudioProductionType}", 3, "Title - Full Cast")]
        [TestCase("{Book Title}", 3, "Title - Full Cast")]
        [TestCase("{Book Title}{ - AudioProductionType}", 1, "Title - Dramatized")]
        public void production_label_is_not_duplicated_and_legacy_combined_style_is_normalized(string pattern, int narratorCount, string expected)
        {
            var author = new Author { Name = "Author" };
            var edition = new Edition { Title = "Title", Book = new Book { Author = author },
                AudioProductionType = "Dramatized / Full-Cast", NarratorNames = new List<string> { "Alice Reader", "Bob Reader", "Carol Reader" }.GetRange(0, narratorCount) };
            var file = new BookFile { Path = "/library/Title.m4b", Quality = new QualityModel(Quality.M4B) };
            var config = NamingConfig.Default;
            config.RenameBooks = true;
            config.StandardBookFormat = pattern;
            Assert.That(CreateBuilder().BuildBookFileName(author, edition, file, config, new List<CustomFormat>()), Is.EqualTo(expected));
        }
    }
}

using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class AudioProductionFileFixture
    {
        [TestCase("title", "[Dramatized Adaptation]", true)]
        [TestCase("subtitle", "Radio Theatre", true)]
        [TestCase("publisher", "GraphicAudio", true)]
        [TestCase("narrator", "Full Cast", true)]
        [TestCase("credits", "Full Cast", true)]
        [TestCase("credits", "Dramatization", true)]
        [TestCase("title", "Full-cast Dramatization", true)]
        [TestCase("title", "Dramatic Reading", true)]
        [TestCase("publisher", "Theater", false)]
        [TestCase("publisher", "Theatre", false)]
        [TestCase("publisher", "Soundbooth Theater", false)]
        [TestCase("publisher", "L A Theatre Works", false)]
        [TestCase("publisher", "American Listeners Theatre", false)]
        [TestCase("title", "Cast", false)]
        [TestCase("title", "Undramatized", false)]
        [TestCase("publisher", "Audible Studios", false)]
        public void edition_correction_uses_shared_production_vocabulary(string field, string value, bool expected)
        {
            var edition = new NzbDrone.Core.Books.Edition();
            switch (field)
            {
                case "title": edition.Title = value; break;
                case "subtitle": edition.Subtitle = value; break;
                case "publisher": edition.Publisher = value; break;
                case "narrator": edition.Narrator = value; break;
                case "credits": edition.NarratorNames = new() { value }; break;
            }
            Assert.That(AudioProductionDetector.IsDramatizedEdition(edition), Is.EqualTo(expected));
        }

        [Test]
        public void standard_correction_keeps_explicit_cast_credit_label_without_changing_its_class()
        {
            var edition = new NzbDrone.Core.Books.Edition { NarratorNames = new() { "Full Cast" } };
            var file = new BookFile { AudioProductionOverride = false, Edition = edition };
            Assert.That(AudioProductionDetector.GetProductionLabel(file, edition), Is.EqualTo("Full Cast"));
            Assert.That(AudioProductionDetector.IsDramatized(file), Is.False);
            Assert.That(AudioProductionDetector.IsDramatizedRecording(file), Is.False);
        }

        [TestCase(2, false)]
        [TestCase(3, true)]
        [TestCase(15, true)]
        public void chosen_edition_counts_distinct_narrators_without_changing_automatic_detection(int count, bool expected)
        {
            var edition = new NzbDrone.Core.Books.Edition();
            for (var i = 0; i < count; ++i) edition.NarratorNames.Add("Reader " + i);
            edition.NarratorNames.Add("reader 0");
            edition.NarratorNames.Add("  ");
            Assert.That(AudioProductionDetector.IsDramatizedEdition(edition), Is.EqualTo(expected));
            Assert.That(AudioProductionDetector.IsDramatized(new BookFile { Edition = edition }), Is.False);
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void author_counts_only_when_listed_in_the_edition_narrator_credits(bool credited, bool expected)
        {
            var author = new NzbDrone.Core.Books.Author { Name = "Author Reader" };
            var edition = new NzbDrone.Core.Books.Edition
            {
                Book = new NzbDrone.Core.Books.Book { Author = author },
                NarratorNames = new() { "First Reader", "Second Reader" }
            };
            if (credited) edition.NarratorNames.Add(author.Name);
            Assert.That(AudioProductionDetector.IsDramatizedEdition(edition), Is.EqualTo(expected));
        }

        [Test]
        public void unrecognized_production_text_is_not_positive_evidence()
        {
            var edition = new NzbDrone.Core.Books.Edition { Title = "Der Zauberer von Oz (Hörspiel)", Language = "deu" };
            Assert.That(AudioProductionDetector.IsDramatizedEdition(edition), Is.False);
        }

        [TestCase("/books/Dune - Full Cast.m4b", null, null, false)]
        [TestCase("/books/Dune.m4b", "Full Cast", "Full Cast", true)]
        [TestCase("/books/Dune - Dramatized.m4b", null, "Dramatized", true)]
        [TestCase("/books/Dune.epub", "Full Cast", null, false)]
        public void stored_recording_type_uses_file_evidence_and_keeps_media_boundaries(string path, string credit, string expectedType, bool expectedFlag)
        {
            var file = new LocalBook { Path = path,
                RawTags = new RawFileTags { AllTags = new Dictionary<string, List<string>> { ["ARTIST"] = new() { credit } } },
                Edition = new NzbDrone.Core.Books.Edition { IsGraphicAudio = true, AudioProductionType = "Dramatized" } };
            Assert.That(AudioProductionDetector.GetRecordingProductionType(file), Is.EqualTo(expectedType));
            Assert.That(AudioProductionDetector.DetectRecording(file), Is.EqualTo(expectedFlag));
        }

        [Test]
        public void bare_cast_credit_does_not_make_an_edition_title_qualifier_optional()
        {
            Assert.That(AudioProductionDetector.ContainsIndicator(new[] { "Cast" }), Is.True);
            Assert.That(AudioProductionDetector.IsProductionLabel("Cast"), Is.False);
            Assert.That(AudioProductionDetector.IsProductionLabel("Full Cast"), Is.True);
        }

        [TestCase("/downloads/Dark Age (GraphicAudio)/Dark Age.m4b", true)]
        [TestCase("/downloads/Dark Age/Dark.Age.Graphic.Audio.m4b", true)]
        [TestCase(@"C:\downloads\Dark Age (GraphicAudio)\Dark Age.m4b", true)]
        [TestCase("/downloads/Dark Age (Dramatized Adaptation)/Part 1.mp3", true)]
        [TestCase("/library/Dark Age - Full Cast/Dark Age.m4b", false)]
        [TestCase("/library/Dark Age/Dark Age - Full Cast.m4b", false)]
        [TestCase("/library/GraphicAudio/Pierce Brown/Dark Age/Dark Age.m4b", false)]
        [TestCase("/downloads/Dark Age/Dark Age.m4b", false)]
        [TestCase("/downloads/Dark Age/Graphic Audiobook.m4b", false)]
        [TestCase("/downloads/Dark Age/Undramatized.m4b", false)]
        [TestCase("/downloads/Dark Age/Melodramatized.m4b", false)]
        [TestCase(null, false)]
        public void file_detection_requires_explicit_production_evidence_in_filename_or_immediate_folder(string path, bool expected)
        {
            var root = path?.StartsWith("/library/") == true ? "/library/GraphicAudio" : path?.StartsWith("C:") == true ? @"C:\downloads" : "/downloads";
            Assert.That(AudioProductionDetector.IsDramatizedFile(false, null, null, path, root), Is.EqualTo(expected));
        }

        [Test]
        public void embedded_cast_evidence_still_identifies_the_recording()
        {
            var tags = new Dictionary<string, List<string>> { ["ARTIST"] = new() { "Full Cast" } };
            Assert.That(AudioProductionDetector.IsDramatizedFile(false, null, tags, "/downloads/Dark Age.m4b"), Is.True);
        }

        [Test]
        public void explicitly_corrected_standard_narration_is_not_reclassified_by_its_old_path_or_tags()
        {
            var tags = new Dictionary<string, List<string>> { ["ARTIST"] = new() { "Full Cast" } };
            var file = new LocalBook { AudioProductionOverride = false, IsGraphicAudio = true,
                RawTags = new RawFileTags { AllTags = tags }, Path = "/library/Dark Age (GraphicAudio).m4b" };
            Assert.That(AudioProductionDetector.IsDramatized(file), Is.False);
            Assert.That(AudioProductionDetector.IsDramatized(new BookFile { AudioProductionOverride = false,
                IsGraphicAudio = true, AllTags = tags, Path = file.Path }), Is.False);
        }

        [Test]
        public void category_and_unknown_folder_boundary_are_not_recording_evidence()
        {
            const string path = "/downloads/GraphicAudio/Book.m4b";
            Assert.That(AudioProductionDetector.IsDramatizedFile(false, null, null, path, "/downloads/GraphicAudio"), Is.False);
            Assert.That(AudioProductionDetector.IsDramatizedFile(false, null, null, path), Is.False);
            Assert.That(AudioProductionDetector.IsDramatizedFile(false, null, null, path, "/downloads"), Is.True);
        }

        // Constructed metadata contexts pin the existing vocabulary independently of the catalog.
        [TestCase("Source: GraphicAudio", "Graphic Audiobook", true)]
        [TestCase("Source: Graphic Audio", "Graphic Audiofile", true)]
        [TestCase("Edition: A Movie in Your Mind", "Mindfulness", true)]
        [TestCase("Edition: Dramatized", "Undramatized", true)]
        [TestCase("Edition: Dramatised", "Undramatised", true)]
        [TestCase("Edition: Dramatized Adaptation", "Undramatized Adaptation", true)]
        [TestCase("Edition: Dramatised Adaptation", "Undramatised Adaptation", true)]
        [TestCase("Edition: Radio dramatization", "Radiography", true)]
        [TestCase("Edition: Radio dramatisation", "Radiographic study", true)]
        [TestCase("Edition: Radio drama", "Radio dramascope", true)]
        [TestCase("Edition: Audio drama", "Audio dramascope", true)]
        [TestCase("Edition: Audio theatre", "Audio theatricality", true)]
        [TestCase("Edition: Audio theater", "Audio theatergoer", true)]
        [TestCase("Edition: Immersive audio experience", "Immersive audio experiences", true)]
        [TestCase("Edition: Sound effects and music", "Sound effects without music", true)]
        [TestCase("Narrator: Fullcast", "Fullcastle", false)]
        [TestCase("Narrator: Full cast", "Full Castaneda", false)]
        [TestCase("Narrator: Full Cast Audio", "Full Castaneda Audio", false)]
        [TestCase("Narrator: Full Cast Production", "Full Castaneda Production", false)]
        [TestCase("Narrator: Multicast", "Multicasting", false)]
        [TestCase("Narrator: Multi cast", "Multi Castaneda", false)]
        [TestCase("Narrator: Cast performance", "Cast performances", false)]
        [TestCase("Narrator: Ensemble cast", "Ensemble Castaneda", false)]
        [TestCase("Narrator: Voice cast", "Voice Castaneda", false)]
        public void known_production_words_keep_boundaries_and_do_not_treat_cast_folders_as_evidence(string observed, string control, bool filenameEvidence)
        {
            Assert.That(AudioProductionDetector.IsDramatizedOrFullCast(false, null, observed), Is.True);
            Assert.That(AudioProductionDetector.IsDramatizedOrFullCast(false, null, control), Is.False);
            Assert.That(AudioProductionDetector.IsDramatizedFile(false, null, null, "/books/" + observed + ".m4b"), Is.EqualTo(filenameEvidence));
        }

        [TestCase("Radio theatre", "Radio theatricality")]
        [TestCase("Radio theater", "Radio theatergoer")]
        [TestCase("Dramatization", "Undramatization")]
        [TestCase("Dramatisation", "Undramatisation")]
        [TestCase("Dramatic Reading", "Melodramatic Reading")]
        public void edition_only_terms_do_not_change_release_or_automatic_evidence(string observed, string control)
        {
            Assert.That(AudioProductionDetector.IsDramatizedEdition(new NzbDrone.Core.Books.Edition { Title = observed }), Is.True);
            Assert.That(AudioProductionDetector.IsDramatizedEdition(new NzbDrone.Core.Books.Edition { Title = control }), Is.False);
            Assert.That(AudioProductionDetector.ContainsIndicator(new[] { observed }), Is.False);
            Assert.That(AudioProductionDetector.GetProductionType(new[] { observed }), Is.Null);
            Assert.That(AudioProductionDetector.IsProductionLabel(observed), Is.False);
            Assert.That(AudioProductionDetector.IsDramatized(new BookFile
            {
                AllTags = new() { ["COMMENT"] = new() { observed } },
                Path = "/books/" + observed + ".m4b"
            }), Is.False);
        }

        [TestCase("Cast", true)]
        [TestCase("The Cast of Characters", false)]
        [TestCase("Cast in Stone", false)]
        public void bare_cast_credit_is_not_a_word_inside_a_book_title(string value, bool expected)
        {
            Assert.That(AudioProductionDetector.IsDramatizedOrFullCast(false, null, value), Is.EqualTo(expected));
            Assert.That(AudioProductionDetector.IsDramatizedFile(false, null, null, "/books/" + value + ".m4b"), Is.False);
        }

        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        public void filename_brand_is_only_fallback_when_embedded_tags_are_absent(bool tagged, bool originalPath, bool expected)
        {
            var file = new BookFile
            {
                Path = originalPath ? "/books/Dark Age.m4b" : "/books/Dark Age - GraphicAudio.m4b",
                AllTags = tagged ? new() { ["TITLE"] = new() { "Dark Age" } } : null,
                MatchProvenance = originalPath ? new() { ImportSourcePaths = new() { "/download/Dark Age (GraphicAudio).m4b" } } : null
            };
            Assert.That(AudioProductionDetector.HasGraphicAudioBrand(file), Is.EqualTo(expected));
            file.SceneName = "Dark Age (GraphicAudio)";
            Assert.That(AudioProductionDetector.HasGraphicAudioBrand(file), Is.True, "the grabbed release remains independent evidence");
        }
    }
}

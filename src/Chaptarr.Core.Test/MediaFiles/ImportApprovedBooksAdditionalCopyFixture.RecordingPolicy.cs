using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        private static LocalBook RecordingPart(int editionId, string path, long size, BookMediaType media = BookMediaType.Audiobook)
        {
            var author = new Author { Id = 1, Name = "Fixture Author" };
            var book = new Book { Id = 10, AuthorId = author.Id, Author = author, MediaType = media, Title = "Fixture Book" };
            return new LocalBook
            {
                Author = author, Book = book, Edition = new Edition { Id = editionId, BookId = book.Id, Book = book },
                Path = path, Size = size, Quality = new QualityModel(media == BookMediaType.Audiobook ? Quality.MP3 : Quality.EPUB),
                RawTags = new RawFileTags { AllTags = new Dictionary<string, List<string>>() }
            };
        }

        private static CustomFormatCalculationService RecordingFormats(params CustomFormat[] formats)
        {
            return new CustomFormatCalculationService(InterfaceProxy.Proxy<ICustomFormatService>((_, _) => formats.ToList()), LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void scoring_combines_recording_files_regardless_of_part_numbers_but_keeps_ebook_alternatives_separate()
        {
            var a = RecordingPart(100, "/recording/1.mp3", 10); a.Part = 1; a.PartCount = 3;
            var b = RecordingPart(100, "/recording/2.mp3", 20); b.Part = 2; b.PartCount = 3;
            Assert.That(RecordingCustomFormatHelper.GroupForScoring(new[] { a, b }).Single(), Has.Count.EqualTo(2),
                "Known parts of an incomplete recording still share evidence");
            b.Part = 1;
            Assert.That(RecordingCustomFormatHelper.GroupForScoring(new[] { a, b }).Single(), Has.Count.EqualTo(2));
            a.Part = b.Part = a.PartCount = b.PartCount = 0;
            b.Path = "/scattered/chapter.mp3";
            Assert.That(RecordingCustomFormatHelper.GroupForScoring(new[] { a, b }).Single(), Has.Count.EqualTo(2),
                "Folder layout and missing part numbers do not split a recording");
            a.Book.MediaType = b.Book.MediaType = BookMediaType.Ebook;
            b.Part = 2;
            Assert.That(RecordingCustomFormatHelper.GroupForScoring(new[] { a, b }), Has.Count.EqualTo(2));
        }

        [Test]
        public void stored_and_incoming_custom_formats_share_filename_and_originalpath_input()
        {
            var part = RecordingPart(100, "/recording/Retail.mp3", 10);
            part.SceneName = "Release";
            var title = new CustomFormat("Filename retail", new ReleaseTitleSpecification { Value = "Retail" }) { Id = 96 };
            var calculator = RecordingFormats(title);
            var stored = new NzbDrone.Core.MediaFiles.BookFile
            {
                Path = part.Path, Author = part.Author, Edition = part.Edition, Quality = part.Quality,
                SceneName = part.SceneName, OriginalFilePath = "/source/Original.mp3"
            };
            Assert.That(calculator.ParseCustomFormat(part), Does.Contain(title));
            Assert.That(calculator.ParseCustomFormat(stored), Does.Contain(title));
            stored.Path = "/library/renamed.mp3";
            stored.SceneName = null;
            stored.OriginalFilePath = "/source/Retail.mp3";
            Assert.That(calculator.ParseCustomFormat(stored), Does.Contain(title));
        }

        [Test]
        public void recording_size_custom_format_does_not_borrow_bytes_from_another_edition_of_the_same_book()
        {
            var parts = new[] { RecordingPart(100, "/recording/a1.mp3", 15), RecordingPart(100, "/recording/a2.mp3", 20),
                RecordingPart(200, "/recording/b1.mp3", 5), RecordingPart(200, "/recording/b2.mp3", 6) };
            var format = new CustomFormat("Recording size", new SizeSpecification { Min = 30d / (1024 * 1024 * 1024), Max = 1 }) { Id = 81 };
            var calculator = RecordingFormats(format);
            var inputs = RecordingCustomFormatHelper.GroupByRecording(parts).Select(group => RecordingCustomFormatHelper.CreateInput(group)).ToDictionary(input => input.Edition.Id);
            Assert.That(inputs.Count, Is.EqualTo(2));
            Assert.That(inputs[100].Size, Is.EqualTo(35L));
            Assert.That(inputs[200].Size, Is.EqualTo(11L));
            Assert.That(calculator.ParseCustomFormat(inputs[100]).Select(match => match.Id), Is.EqualTo(new[] { 81 }));
            Assert.That(calculator.ParseCustomFormat(inputs[200]), Is.Empty, "The smaller recording must not qualify from the other recording's bytes");
        }

        [Test]
        public void recording_groups_keep_exact_book_edition_and_media_identity_and_isolate_unknown_editions()
        {
            var first = RecordingPart(100, "/recording/a.mp3", 15);
            var sameRecording = RecordingPart(100, "/recording/b.mp3", 20);
            var otherBook = RecordingPart(100, "/recording/c.mp3", 30); otherBook.Book.Id = 11; otherBook.Edition.BookId = 11;
            var otherMedia = RecordingPart(100, "/recording/d.epub", 40, BookMediaType.Ebook);
            var unknown = RecordingPart(0, "/recording/e.mp3", 50);
            var otherUnknown = RecordingPart(0, "/recording/f.mp3", 60);
            var groups = RecordingCustomFormatHelper.GroupByRecording(new[] { first, sameRecording, otherBook, otherMedia, unknown, otherUnknown });
            Assert.That(groups.Count, Is.EqualTo(5));
            Assert.That(groups.Single(group => group.Contains(first)), Is.EquivalentTo(new[] { first, sameRecording }));
            Assert.That(groups.Where(group => group.Contains(unknown) || group.Contains(otherUnknown)), Has.All.Count.EqualTo(1));
        }

        [Test]
        public void generated_output_is_a_separate_representation_from_its_same_edition_source_parts()
        {
            var first = RecordingPart(100, "/recording/a.mp3", 10);
            var second = RecordingPart(100, "/recording/b.mp3", 20);
            var generated = RecordingPart(100, "/recording/output.m4b", 13);
            generated.IsGeneratedConversion = true;
            var groups = RecordingCustomFormatHelper.GroupByRecording(new[] { first, second, generated });
            Assert.That(groups.Count, Is.EqualTo(2));
            Assert.That(RecordingCustomFormatHelper.CreateInput(groups.Single(group => group.Contains(first))).Size, Is.EqualTo(30L));
            Assert.That(RecordingCustomFormatHelper.CreateInput(groups.Single(group => group.Contains(generated))).Size, Is.EqualTo(13L));
        }

        [Test]
        public void recording_projection_counts_duplicate_source_paths_once_and_leaves_source_quality_and_sizes_unchanged()
        {
            var first = RecordingPart(100, "/recording/a.mp3", 15);
            var duplicate = first.ShallowClone();
            var second = RecordingPart(100, "/recording/b.mp3", 20);
            var input = RecordingCustomFormatHelper.CreateInput(new[] { first, duplicate, second }, new QualityModel(Quality.M4B));
            Assert.That(input.Size, Is.EqualTo(35L));
            Assert.That(input.Quality.Quality, Is.EqualTo(Quality.M4B));
            Assert.That(first.Size, Is.EqualTo(15L));
            Assert.That(second.Size, Is.EqualTo(20L));
            Assert.That(first.Quality.Quality, Is.EqualTo(Quality.MP3));
            Assert.That(input, Is.Not.SameAs(first));
        }

        [Test]
        public void recording_custom_format_uses_tag_evidence_from_any_part_without_a_clean_part_veto()
        {
            var first = RecordingPart(100, "/recording/a.mp3", 15);
            var second = RecordingPart(100, "/recording/b.mp3", 20);
            first.RawTags.AllTags["comment"] = new() { "An unmarked opening chapter" };
            second.RawTags.AllTags["description"] = new() { "A GraphicAudio production" };
            var format = new CustomFormat("Dramatized recording", new AudioProductionSpecification()) { Id = 82 };
            var calculator = RecordingFormats(format);
            Assert.That(calculator.ParseCustomFormat(first), Is.Empty);
            Assert.That(calculator.ParseCustomFormat(second).Select(match => match.Id), Is.EqualTo(new[] { 82 }));
            var input = RecordingCustomFormatHelper.CreateInput(new[] { first, second });
            Assert.That(calculator.ParseCustomFormat(input).Select(match => match.Id), Is.EqualTo(new[] { 82 }));
            Assert.That(first.RawTags.AllTags.ContainsKey("description"), Is.False);
            Assert.That(second.RawTags.AllTags.ContainsKey("comment"), Is.False);
            input.RawTags.AllTags["description"].Add("projection-only");
            Assert.That(second.RawTags.AllTags["description"], Is.EqualTo(new[] { "A GraphicAudio production" }));
        }

        [Test]
        public void recording_projection_unions_observed_narrators_without_mutating_parts()
        {
            var first = RecordingPart(100, "/recording/a.mp3", 15); first.Narrator = "Narrator One";
            var second = RecordingPart(100, "/recording/b.mp3", 20); second.Narrator = "Narrator Two";
            var input = RecordingCustomFormatHelper.CreateInput(new[] { first, second });
            Assert.That(input.Narrator, Does.Contain("Narrator One").And.Contain("Narrator Two"));
            Assert.That(first.Narrator, Is.EqualTo("Narrator One"));
            Assert.That(second.Narrator, Is.EqualTo("Narrator Two"));
        }

        [Test]
        public void recording_projection_preserves_explicit_non_dramatized_choice_over_union_tag_evidence()
        {
            var first = RecordingPart(100, "/recording/a.mp3", 15);
            first.AudioProductionOverrideSpecified = true; first.AudioProductionOverride = false;
            var second = RecordingPart(100, "/recording/b.mp3", 20);
            second.AudioProductionOverrideSpecified = true; second.AudioProductionOverride = false;
            second.RawTags.AllTags["description"] = new() { "A GraphicAudio production" };
            var input = RecordingCustomFormatHelper.CreateInput(new[] { first, second });
            var format = new CustomFormat("Dramatized recording", new AudioProductionSpecification()) { Id = 83 };
            Assert.That(input.AudioProductionOverrideSpecified, Is.True);
            Assert.That(input.AudioProductionOverride, Is.False);
            Assert.That(RecordingFormats(format).ParseCustomFormat(input), Is.Empty);
        }
    }
}

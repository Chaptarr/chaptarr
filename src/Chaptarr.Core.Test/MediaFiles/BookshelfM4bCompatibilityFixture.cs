using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class BookshelfM4bCompatibilityFixture
    {
        [TestCase("true", true)]
        [TestCase("TRUE", true)]
        [TestCase("false", false)]
        [TestCase("yes", false)]
        [TestCase("", false)]
        public void merge_flag_is_opt_in(string value, bool expected)
        {
            Assert.That(BookshelfM4bCompatibility.ParseMergeEnabled(value), Is.EqualTo(expected));
        }

        [Test]
        public void bitrate_uses_legacy_default_and_enforces_supported_range()
        {
            Assert.That(BookshelfM4bCompatibility.ParseBitrateKbps(null, out var missingInvalid), Is.EqualTo(128));
            Assert.That(missingInvalid, Is.False);
            Assert.That(BookshelfM4bCompatibility.ParseBitrateKbps("48", out var minimumInvalid), Is.EqualTo(48));
            Assert.That(minimumInvalid, Is.False);
            Assert.That(BookshelfM4bCompatibility.ParseBitrateKbps("320", out var maximumInvalid), Is.EqualTo(320));
            Assert.That(maximumInvalid, Is.False);
            Assert.That(BookshelfM4bCompatibility.ParseBitrateKbps("47", out var lowInvalid), Is.EqualTo(128));
            Assert.That(lowInvalid, Is.True);
            Assert.That(BookshelfM4bCompatibility.ParseBitrateKbps("321", out var highInvalid), Is.EqualTo(128));
            Assert.That(highInvalid, Is.True);
            Assert.That(BookshelfM4bCompatibility.ParseBitrateKbps("bad", out var malformedInvalid), Is.EqualTo(128));
            Assert.That(malformedInvalid, Is.True);
        }

        [Test]
        public void legacy_merge_requires_an_explicit_opt_in_and_one_approved_audio_edition()
        {
            var profile = new QualityProfile { ProfileType = ProfileType.Audiobook };
            var sameBook = new Book { Id = 10, MediaType = BookMediaType.Audiobook };
            var sameEdition = new Edition { Id = 11, BookId = sameBook.Id };
            var decisions = new List<ImportDecision<LocalBook>>
            {
                new(new LocalBook { Path = "/downloads/part-01.mp3", Book = sameBook, Edition = sameEdition }),
                new(new LocalBook { Path = "/downloads/part-02.mp3", Book = sameBook, Edition = sameEdition })
            };

            Assert.That(BookshelfM4bCompatibility.ShouldUseLegacyMerge(true, profile, decisions, false), Is.True);
            Assert.That(BookshelfM4bCompatibility.ShouldUseLegacyMerge(false, profile, decisions, false), Is.False);
            Assert.That(BookshelfM4bCompatibility.ShouldUseLegacyMerge(true, profile, decisions, true), Is.False);

            profile.ProfileType = ProfileType.Ebook;
            Assert.That(BookshelfM4bCompatibility.ShouldUseLegacyMerge(true, profile, decisions, false), Is.False);
            profile.ProfileType = ProfileType.Audiobook;

            decisions[1].Item.Edition = new Edition { Id = 12, BookId = sameBook.Id };
            Assert.That(BookshelfM4bCompatibility.ShouldUseLegacyMerge(true, profile, decisions, false), Is.False);
            decisions[1].Item.Edition = sameEdition;

            decisions[1].Item.Path = "/downloads/part-02.m4b";
            Assert.That(BookshelfM4bCompatibility.ShouldUseLegacyMerge(true, profile, decisions, false), Is.False);
        }

        [Test]
        public void legacy_track_order_uses_disc_and_track_tags_before_filename_numbers()
        {
            var decisions = new List<ImportDecision<LocalBook>>
            {
                new(CreateTaggedBook("/downloads/part-3.mp3", "2", "1", "Last disc")),
                new(CreateTaggedBook("/downloads/part-1.mp3", "1", "2", "Second track")),
                new(CreateTaggedBook("/downloads/part-2.mp3", "1", "1", "First track"))
            };

            var ordered = BookshelfM4bCompatibility.OrderTracks(decisions);

            Assert.That(ordered.Select(decision => decision.Item.Path), Is.EqualTo(new[]
            {
                "/downloads/part-2.mp3",
                "/downloads/part-1.mp3",
                "/downloads/part-3.mp3"
            }));
            Assert.That(BookshelfM4bCompatibility.GetChapterTitles(ordered), Is.EqualTo(new[]
            {
                "First track",
                "Second track",
                "Last disc"
            }));
        }

        [Test]
        public void chapter_title_falls_back_to_the_filename_when_no_single_title_tag_exists()
        {
            var decisions = new List<ImportDecision<LocalBook>>
            {
                new(new LocalBook { Path = "/downloads/01 Opening.mp3" }),
                new(CreateTaggedBook("/downloads/02 Main.mp3", "1", "2", null))
            };

            Assert.That(BookshelfM4bCompatibility.GetChapterTitles(decisions), Is.EqualTo(new[]
            {
                "01 Opening",
                "02 Main"
            }));
        }

        [Test]
        public void custom_ffmpeg_path_is_added_to_the_tool_search_path_only_for_named_ffmpeg_executable()
        {
            var directory = Path.Combine(Path.GetTempPath(), "bookshelf-m4b-ffmpeg");
            var fileName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            var customPath = Path.Combine(directory, fileName);
            var existingPath = "existing-path";

            Assert.That(BookshelfM4bCompatibility.TryBuildFfmpegSearchPath(customPath, existingPath, out var searchPath), Is.True);
            Assert.That(searchPath, Is.EqualTo(directory + Path.PathSeparator + existingPath));
            Assert.That(BookshelfM4bCompatibility.TryBuildFfmpegSearchPath(fileName, existingPath, out var pathOnlySearchPath), Is.True);
            Assert.That(pathOnlySearchPath, Is.EqualTo(existingPath));
            Assert.That(BookshelfM4bCompatibility.TryBuildFfmpegSearchPath(Path.Combine(directory, "ffmpeg-custom"), existingPath, out var unchangedPath), Is.False);
            Assert.That(unchangedPath, Is.EqualTo(existingPath));
        }

        private static LocalBook CreateTaggedBook(string path, string disc, string track, string title)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID3v2:TPOS"] = new List<string> { disc },
                ["ID3v2:TRCK"] = new List<string> { track }
            };

            if (!string.IsNullOrWhiteSpace(title))
            {
                tags["ID3v2:TIT2"] = new List<string> { title };
            }

            return new LocalBook
            {
                Path = path,
                RawTags = new RawFileTags { AllTags = tags }
            };
        }
    }
}

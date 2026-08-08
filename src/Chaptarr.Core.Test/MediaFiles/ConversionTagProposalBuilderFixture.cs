using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ConversionTagProposalBuilderFixture
    {
        private static readonly IContainmentValidator ContainmentValidator = new TestContainmentValidator();

        [Test]
        public void preserve_mode_should_use_common_source_book_value_instead_of_first_track_title_for_multi_file_merge()
        {
            var sources = new[]
            {
                LocalBook("/books/cd01.mp3", "Harry Potter And The Goblet Of Fire - CD 01", "Harry Potter And The Goblet Of Fire"),
                LocalBook("/books/cd02.mp3", "Harry Potter And The Goblet Of Fire - CD 02", "Harry Potter And The Goblet Of Fire"),
                LocalBook("/books/cd03.mp3", "Harry Potter And The Goblet Of Fire - CD 03", "Harry Potter And The Goblet Of Fire")
            };

            var options = BuildOptions(sources, ConversionTagModes.Preserve);

            Assert.That(options.Mode, Is.EqualTo(ConversionTagModes.Preserve));
            Assert.That(options.Name, Is.EqualTo("Harry Potter And The Goblet Of Fire"));
            Assert.That(options.Album, Is.EqualTo("Harry Potter And The Goblet Of Fire"));
            Assert.That(options.Artist, Is.EqualTo("J.K. Rowling"));
            Assert.That(options.AlbumArtist, Is.EqualTo("J.K. Rowling"));
            Assert.That(options.Writer, Is.EqualTo("Narrator..................Stephen Fry"));
            Assert.That(options.IgnoreSourceTags, Is.False);
            Assert.That(options.UseFilenamesAsChapters, Is.True);
            Assert.That(options.ManifestJson, Does.Contain("Harry Potter And The Goblet Of Fire - CD 01"));
            Assert.That(options.ManifestJson, Does.Contain("ID3v2:TIT2"));
        }

        [Test]
        public void preserve_mode_should_use_db_title_only_for_merged_identity_when_no_common_source_book_value_exists()
        {
            var sources = new[]
            {
                LocalBook("/books/cd01.mp3", "Harry Potter And The Goblet Of Fire - CD 01", null),
                LocalBook("/books/cd02.mp3", "Harry Potter And The Goblet Of Fire - CD 02", null),
                LocalBook("/books/cd03.mp3", "Harry Potter And The Goblet Of Fire - CD 03", null)
            };

            var options = BuildOptions(sources, ConversionTagModes.Preserve);

            Assert.That(options.Name, Is.EqualTo("Harry Potter and the Goblet of Fire"));
            Assert.That(options.Album, Is.EqualTo("Harry Potter and the Goblet of Fire"));
            Assert.That(options.Artist, Is.EqualTo("J.K. Rowling"));
            Assert.That(options.Writer, Is.EqualTo("Narrator..................Stephen Fry"));
            Assert.That(options.IgnoreSourceTags, Is.False);
            Assert.That(options.ManifestJson, Does.Contain("Harry Potter And The Goblet Of Fire - CD 02"));
        }

        [Test]
        public void preserve_mode_should_keep_single_file_source_title_when_it_proves_the_matched_book()
        {
            var sources = new[]
            {
                LocalBook("/books/fantastic-beast.mp3", "Fantastic Beast and Where to Find Them", "Fantastic Beast and Where to Find Them")
            };

            var options = ConversionTagProposalBuilder.BuildOptions(
                sources,
                new Book { Title = "Fantastic Beasts and Where to Find Them" },
                new Author { Name = "J.K. Rowling" },
                new Edition { Title = "Fantastic Beasts and Where to Find Them", NarratorNames = new List<string> { "Eddie Redmayne" } },
                ContainmentValidator,
                ConversionTagModes.Preserve);

            Assert.That(options.Name, Is.EqualTo("Fantastic Beast and Where to Find Them"));
            Assert.That(options.Album, Is.EqualTo("Fantastic Beast and Where to Find Them"));
            Assert.That(options.UseFilenamesAsChapters, Is.False);
            Assert.That(options.IgnoreSourceTags, Is.False);
        }

        [Test]
        public void clean_mode_should_write_canonical_db_tags_and_suppress_source_tag_bleed()
        {
            var sources = new[]
            {
                LocalBook("/books/cd01.mp3", "Harry Potter And The Goblet Of Fire - CD 01", "Harry Potter And The Goblet Of Fire")
            };

            var options = BuildOptions(sources, ConversionTagModes.Clean);

            Assert.That(options.Mode, Is.EqualTo(ConversionTagModes.Clean));
            Assert.That(options.Name, Is.EqualTo("Harry Potter and the Goblet of Fire"));
            Assert.That(options.Album, Is.EqualTo("Harry Potter and the Goblet of Fire"));
            Assert.That(options.Artist, Is.EqualTo("J.K. Rowling"));
            Assert.That(options.AlbumArtist, Is.EqualTo("J.K. Rowling"));
            Assert.That(options.Writer, Is.EqualTo("Stephen Fry"));
            Assert.That(options.Year, Is.EqualTo("2000"));
            Assert.That(options.Genre, Is.EqualTo("Fantasy; Young Adult"));
            Assert.That(options.Comment, Is.EqualTo("The fourth Harry Potter audiobook."));
            Assert.That(options.Copyright, Is.EqualTo("Pottermore Publishing"));
            Assert.That(options.Series, Is.EqualTo("Harry Potter"));
            Assert.That(options.SeriesPart, Is.EqualTo("4"));
            Assert.That(options.IgnoreSourceTags, Is.True);
        }

        [Test]
        public void manifest_should_include_selected_values_and_clamped_raw_source_tags_for_forensics()
        {
            var sources = new[]
            {
                LocalBook("/books/cd01.mp3", "Harry Potter And The Goblet Of Fire - CD 01", "Harry Potter And The Goblet Of Fire")
            };

            var options = BuildOptions(sources, ConversionTagModes.Clean);
            using var document = JsonDocument.Parse(options.ManifestJson);

            Assert.That(document.RootElement.GetProperty("mode").GetString(), Is.EqualTo(ConversionTagModes.Clean));
            Assert.That(document.RootElement.GetProperty("selected").GetProperty("name").GetString(), Is.EqualTo("Harry Potter and the Goblet of Fire"));
            Assert.That(document.RootElement.GetProperty("selected").GetProperty("ignoreSourceTags").GetBoolean(), Is.True);

            var firstSource = document.RootElement.GetProperty("sources")[0];
            Assert.That(firstSource.GetProperty("path").GetString(), Is.EqualTo("/books/cd01.mp3"));
            Assert.That(firstSource.GetProperty("tags").GetProperty("ID3v2:TIT2")[0].GetString(), Is.EqualTo("Harry Potter And The Goblet Of Fire - CD 01"));
        }

        private static ConversionTagOptions BuildOptions(IReadOnlyList<LocalBook> sources, string mode)
        {
            return ConversionTagProposalBuilder.BuildOptions(
                sources,
                new Book
                {
                    Title = "Harry Potter and the Goblet of Fire",
                    Overview = "The fourth Harry Potter audiobook.",
                    Genres = new List<string> { "Fantasy", "Young Adult", "Fantasy" },
                    Publisher = "Fallback Publisher",
                    SeriesName = "Harry Potter",
                    SeriesPosition = "4"
                },
                new Author { Name = "J.K. Rowling" },
                new Edition
                {
                    Title = "Harry Potter and the Goblet of Fire",
                    Overview = string.Empty,
                    ReleaseDate = new DateTime(2000, 7, 8),
                    Publisher = "Pottermore Publishing",
                    NarratorNames = new List<string> { "Stephen Fry" }
                },
                ContainmentValidator,
                mode);
        }

        private static LocalBook LocalBook(string path, string title, string album)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID3v2:TIT2"] = new List<string> { title },
                ["ID3v2:TPE1"] = new List<string> { "J.K. Rowling" },
                ["ID3v2:TPE2"] = new List<string> { "J.K. Rowling" },
                ["ID3v2:TCOM"] = new List<string> { "Narrator..................Stephen Fry" },
                ["ID3v2:TCON"] = new List<string> { "Fantasy" },
                ["ID3v2:TDRC"] = new List<string> { "2000-07-08" }
            };

            if (!string.IsNullOrWhiteSpace(album))
            {
                tags["ID3v2:TALB"] = new List<string> { album };
            }

            return new LocalBook
            {
                Path = path,
                RawTags = new RawFileTags
                {
                    AllTags = tags
                }
            };
        }

        private sealed class TestContainmentValidator : IContainmentValidator
        {
            public bool Contains(string haystack, string needle)
            {
                var normalizedHaystack = Normalize(haystack);
                var normalizedNeedle = Normalize(needle);
                return normalizedHaystack.Length > 0 &&
                       normalizedNeedle.Length > 0 &&
                       normalizedHaystack.Contains(normalizedNeedle, StringComparison.Ordinal);
            }

            public bool ValidateAuthorInTags(string authorName, IDictionary<string, List<string>> allTags)
            {
                return Values(allTags).Any(value => Contains(value, authorName));
            }

            public bool ValidateEditionInTags(string editionTitle, IDictionary<string, List<string>> allTags)
            {
                return GetEditionTitleEvidence(editionTitle, allTags).Count > 0;
            }

            public IReadOnlyList<EditionTitleEvidence> GetEditionTitleEvidence(string editionTitle, IDictionary<string, List<string>> allTags, bool includeDurationGatedNearExact = false)
            {
                return (allTags ?? new Dictionary<string, List<string>>())
                    .Where(kv => kv.Value != null)
                    .SelectMany(kv => kv.Value
                        .Where(value => IsTitleEvidence(value, editionTitle))
                        .Select(value => new EditionTitleEvidence(kv.Key, value, editionTitle)))
                    .ToList();
            }

            private static IEnumerable<string> Values(IDictionary<string, List<string>> tags)
            {
                return (tags ?? new Dictionary<string, List<string>>())
                    .Where(kv => kv.Value != null)
                    .SelectMany(kv => kv.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value));
            }

            private static string Normalize(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return string.Empty;
                }

                return Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();
            }

            private static bool IsTitleEvidence(string value, string title)
            {
                if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(title))
                {
                    return false;
                }

                if (Normalize(value).Contains(Normalize(title), StringComparison.Ordinal) ||
                    Normalize(title).Contains(Normalize(value), StringComparison.Ordinal))
                {
                    return true;
                }

                var valueTokens = Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var titleTokens = Normalize(title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (valueTokens.Length != titleTokens.Length)
                {
                    return false;
                }

                return valueTokens.Zip(titleTokens).All(pair =>
                    string.Equals(pair.First, pair.Second, StringComparison.Ordinal) ||
                    string.Equals(pair.First + "s", pair.Second, StringComparison.Ordinal) ||
                    string.Equals(pair.First, pair.Second + "s", StringComparison.Ordinal));
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class FileMatchingServiceConsensusSamplingFixture
    {
        [Test]
#pragma warning disable SYSLIB0050
        public void select_spread_samples_should_use_more_than_two_files_for_large_groups()
        {
            var method = typeof(FileMatchingService).GetMethod("SelectSpreadSamples", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var files = new List<DiscoveredFileWithMetadata>();
            for (var index = 1; index <= 9; index++)
            {
                files.Add(File($"/library/book/{index:00}.mp3", null));
            }

            var samples = (List<DiscoveredFileWithMetadata>)method.Invoke(null, new object[] { files, 5 });
            Assert.That(samples, Is.Not.Null);
            Assert.That(samples.Count, Is.EqualTo(5));
            Assert.That(samples[0].Path, Is.EqualTo("/library/book/01.mp3"));
            Assert.That(samples[2].Path, Is.EqualTo("/library/book/05.mp3"));
            Assert.That(samples[4].Path, Is.EqualTo("/library/book/09.mp3"));
        }

        [Test]
        public void build_group_consensus_tags_should_not_trust_representative_alltags_when_consensus_exists()
        {
            var service = (FileMatchingService)FormatterServices.GetUninitializedObject(typeof(FileMatchingService));
            var method = typeof(FileMatchingService).GetMethod("BuildGroupConsensusTags", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var files = new List<DiscoveredFileWithMetadata>
            {
                File("/library/rowling/01.m4b", Tags(("ALBUM", "Pocket Potters: Harry Potter"), ("ALBUMARTIST", "J.K. Rowling"))),
                File("/library/rowling/02.m4b", Tags(("ALBUM", "Harry Potter and the Order of the Phoenix"), ("ALBUMARTIST", "J.K. Rowling"), ("TITLE", "Track 2"))),
                File("/library/rowling/03.m4b", Tags(("ALBUM", "Harry Potter and the Order of the Phoenix"), ("ALBUMARTIST", "J.K. Rowling"), ("TITLE", "Track 3"))),
                File("/library/rowling/04.m4b", Tags(("ALBUM", "Harry Potter and the Order of the Phoenix"), ("ALBUMARTIST", "J.K. Rowling"), ("TITLE", "Track 4"))),
                File("/library/rowling/05.m4b", Tags(("ALBUM", "Harry Potter and the Order of the Phoenix"), ("ALBUMARTIST", "J.K. Rowling"), ("TITLE", "Track 5")))
            };

            var result = (Dictionary<string, List<string>>)method.Invoke(service, new object[] { files });
            Assert.That(result, Is.Not.Null);
            Assert.That(result.ContainsKey("ALBUM"), Is.True);
            Assert.That(result["ALBUM"], Contains.Item("Harry Potter and the Order of the Phoenix"));
            Assert.That(result["ALBUM"], Does.Not.Contain("Pocket Potters: Harry Potter"));
        }

        [Test]
        public void build_group_consensus_tags_should_drop_singleton_track_titles_for_multi_file_units()
        {
            var service = (FileMatchingService)FormatterServices.GetUninitializedObject(typeof(FileMatchingService));
            var method = typeof(FileMatchingService).GetMethod("BuildGroupConsensusTags", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var files = new List<DiscoveredFileWithMetadata>();
            for (var index = 1; index <= 6; index++)
            {
                files.Add(File(
                    $"/library/herbert/{index:00}.mp3",
                    Tags(("ALBUM", "Dune"), ("ALBUMARTIST", "Frank Herbert"), ("TITLE", $"Track {index}"))));
            }

            var result = (Dictionary<string, List<string>>)method.Invoke(service, new object[] { files });
            Assert.That(result, Is.Not.Null);
            Assert.That(result.ContainsKey("ALBUM"), Is.True);
            Assert.That(result["ALBUM"], Is.EquivalentTo(new[] { "Dune" }));
            Assert.That(result.ContainsKey("TITLE"), Is.False);
        }

        [Test]
        public void build_group_consensus_tags_should_not_fallback_to_representative_when_large_group_has_no_consensus()
        {
            var service = (FileMatchingService)FormatterServices.GetUninitializedObject(typeof(FileMatchingService));
            var method = typeof(FileMatchingService).GetMethod("BuildGroupConsensusTags", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var files = new List<DiscoveredFileWithMetadata>
            {
                File("/library/mixed/01.mp3", Tags(("ALBUM", "Pocket Potters: Harry Potter"), ("TITLE", "Pocket Potters: Harry Potter"))),
                File("/library/mixed/02.mp3", Tags(("ALBUM", "Order of the Phoenix"), ("TITLE", "Track 2"))),
                File("/library/mixed/03.mp3", Tags(("ALBUM", "Goblet of Fire"), ("TITLE", "Track 3"))),
                File("/library/mixed/04.mp3", Tags(("ALBUM", "Prisoner of Azkaban"), ("TITLE", "Track 4"))),
                File("/library/mixed/05.mp3", Tags(("ALBUM", "Half-Blood Prince"), ("TITLE", "Track 5"))),
                File("/library/mixed/06.mp3", Tags(("ALBUM", "Deathly Hallows"), ("TITLE", "Track 6")))
            };

            var result = (Dictionary<string, List<string>>)method.Invoke(service, new object[] { files });
            Assert.That(result, Is.Empty, "large groups without stable consensus should fail closed instead of reusing the representative tags");
        }

        [Test]
        public void resolve_create_media_types_should_not_create_sibling_type_for_mixed_roots()
        {
            var method = typeof(FileMatchingService).GetMethod("ResolveCreateMediaTypes", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var mixed = new RootFolder { FolderType = FolderType.Mixed };

            var audiobook = ((bool CreateAudiobook, bool CreateEbook))method.Invoke(null, new object[] { mixed, BookMediaType.Audiobook });
            var ebook = ((bool CreateAudiobook, bool CreateEbook))method.Invoke(null, new object[] { mixed, BookMediaType.Ebook });

            Assert.Multiple(() =>
            {
                Assert.That(audiobook.CreateAudiobook, Is.True);
                Assert.That(audiobook.CreateEbook, Is.False);
                Assert.That(ebook.CreateAudiobook, Is.False);
                Assert.That(ebook.CreateEbook, Is.True);
            });
        }

        [Test]
        public void resolve_create_media_types_should_respect_dedicated_root_type()
        {
            var method = typeof(FileMatchingService).GetMethod("ResolveCreateMediaTypes", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var audiobookRoot = new RootFolder { FolderType = FolderType.Audiobook };
            var ebookRoot = new RootFolder { FolderType = FolderType.Ebook };

            var audiobook = ((bool CreateAudiobook, bool CreateEbook))method.Invoke(null, new object[] { audiobookRoot, BookMediaType.Ebook });
            var ebook = ((bool CreateAudiobook, bool CreateEbook))method.Invoke(null, new object[] { ebookRoot, BookMediaType.Audiobook });

            Assert.Multiple(() =>
            {
                Assert.That(audiobook.CreateAudiobook, Is.True);
                Assert.That(audiobook.CreateEbook, Is.False);
                Assert.That(ebook.CreateAudiobook, Is.False);
                Assert.That(ebook.CreateEbook, Is.True);
            });
        }
#pragma warning restore SYSLIB0050

        private static DiscoveredFileWithMetadata File(string path, Dictionary<string, List<string>> tags)
        {
            return new DiscoveredFileWithMetadata
            {
                Path = path,
                AllTags = tags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            };
        }

        private static Dictionary<string, List<string>> Tags(params (string Key, string Value)[] entries)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in entries)
            {
                if (!tags.TryGetValue(key, out var values))
                {
                    values = new List<string>();
                    tags[key] = values;
                }

                values.Add(value);
            }

            return tags;
        }
    }
}

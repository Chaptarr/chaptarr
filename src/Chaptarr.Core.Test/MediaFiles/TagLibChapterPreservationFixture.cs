using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NUnit.Framework;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class TagLibChapterPreservationFixture
    {
        private const string FixtureRelativePath = "src/Chaptarr.Core.Test/MediaFiles/Fixtures/chpl-version1.m4b";
        private const string UpdatedTitle = "TagLib chapter preservation regression";

        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"taglib-chpl-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [Test]
        public void should_preserve_version_one_nero_chapters_when_writing_tags()
        {
            var path = Path.Combine(_directory, "chapter.m4b");
            System.IO.File.Copy(FindFixturePath(), path);

            var originalChapterAtom = ReadChapterAtom(path);
            Assert.That(originalChapterAtom[8], Is.EqualTo(1), "Fixture must contain a version-one Nero chapter atom.");

            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Title = UpdatedTitle;
                file.Save();
            }

            var savedChapterAtom = ReadChapterAtom(path);
            Assert.That(savedChapterAtom, Is.EqualTo(originalChapterAtom), "Tag writes must preserve opaque Nero chapter bytes exactly.");

            using (var file = TagLib.File.Create(path))
            {
                Assert.That(file.Tag.Title, Is.EqualTo(UpdatedTitle), "The tag write must still update the requested metadata.");
            }
        }

        [Test]
        public void should_project_bookshelf_audio_book_and_title_from_taglib_fields()
        {
            var path = FindFixturePath();
            string expectedBook;
            string expectedTitle;

            using (var file = TagLib.File.Create(path, TagLib.ReadStyle.None))
            {
                expectedBook = file.Tag?.Album ?? file.Tag?.AlbumSort ?? string.Empty;
                expectedTitle = file.Tag?.Title ?? file.Tag?.TitleSort ?? string.Empty;
            }

            var tags = new TagLibSharpExtractor().ExtractTags(path);

            Assert.Multiple(() =>
            {
                Assert.That(tags[BookshelfAudioTagFields.Book], Is.EqualTo(new[] { expectedBook }));
                Assert.That(tags[BookshelfAudioTagFields.Title], Is.EqualTo(new[] { expectedTitle }));
            });
        }

        private static string FindFixturePath()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, FixtureRelativePath);
                if (System.IO.File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException($"Could not find {FixtureRelativePath} from {TestContext.CurrentContext.TestDirectory}");
        }

        private static byte[] ReadChapterAtom(string path)
        {
            using var stream = System.IO.File.OpenRead(path);
            var movie = FindChildAtom(stream, 0, stream.Length, "moov");
            var userData = FindChildAtom(stream, movie.Offset + movie.HeaderSize, movie.Offset + movie.Size, "udta");
            var chapter = FindChildAtom(stream, userData.Offset + userData.HeaderSize, userData.Offset + userData.Size, "chpl");

            if (chapter.Size > int.MaxValue)
            {
                throw new InvalidDataException("The Nero chapter atom is too large for this test fixture.");
            }

            stream.Position = chapter.Offset;
            return ReadExactly(stream, (int)chapter.Size);
        }

        private static Atom FindChildAtom(Stream stream, long start, long end, string expectedType)
        {
            var position = start;
            var header = new byte[8];

            while (position + header.Length <= end)
            {
                stream.Position = position;
                ReadExactly(stream, header);

                var size = (ulong)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
                var type = Encoding.ASCII.GetString(header, 4, 4);
                var headerSize = 8;

                if (size == 1)
                {
                    var extendedSize = new byte[8];
                    ReadExactly(stream, extendedSize);
                    size = BinaryPrimitives.ReadUInt64BigEndian(extendedSize);
                    headerSize = 16;
                }
                else if (size == 0)
                {
                    size = (ulong)(end - position);
                }

                if (size < (ulong)headerSize || size > (ulong)(end - position))
                {
                    throw new InvalidDataException($"Invalid {type} atom size in the test media.");
                }

                if (type == expectedType)
                {
                    return new Atom(position, (long)size, headerSize);
                }

                position += (long)size;
            }

            throw new InvalidDataException($"Could not find {expectedType} atom in the test media.");
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            var buffer = new byte[count];
            ReadExactly(stream, buffer);
            return buffer;
        }

        private static void ReadExactly(Stream stream, byte[] buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of an MP4 atom.");
                }

                offset += read;
            }
        }

        private readonly struct Atom
        {
            public Atom(long offset, long size, int headerSize)
            {
                Offset = offset;
                Size = size;
                HeaderSize = headerSize;
            }

            public long Offset { get; }
            public long Size { get; }
            public int HeaderSize { get; }
        }
    }
}

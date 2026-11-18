using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Text;
using NUnit.Framework;
using NzbDrone.Common.Disk;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public partial class DiskProviderBaseUnicodePathResolutionFixture
    {
        private sealed class TestDiskProvider : DiskProviderBase
        {
            public TestDiskProvider(IFileSystem fileSystem)
                : base(fileSystem)
            {
            }

            public override long? GetAvailableSpace(string path) => throw new NotImplementedException();
            public override void InheritFolderPermissions(string filename) => throw new NotImplementedException();
            public override void SetEveryonePermissions(string filename) => throw new NotImplementedException();
            public override void SetFilePermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void SetPermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void CopyPermissions(string sourcePath, string targetPath) => throw new NotImplementedException();
            public override long? GetTotalSize(string path) => throw new NotImplementedException();
            public override bool TryCreateHardLink(string source, string destination) => throw new NotImplementedException();
        }

        private string _tempDir;
        private TestDiskProvider _diskProvider;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-unicode-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _diskProvider = new TestDiskProvider(new FileSystem());
        }

        [TearDown]
        public void TearDown()
        {
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void generated_file_stays_complete_until_replacement_and_survives_failed_write(bool fail)
        {
            var path = Path.Combine(_tempDir, "画像.png");
            var oldBytes = new byte[] { 1, 2, 3, 4 };
            var newBytes = new byte[] { 5, 6, 7, 8 };
            File.WriteAllBytes(path, oldBytes);
            // Windows refuses to replace a file that another handle has open.
            using var reader = OperatingSystem.IsWindows() ? null : File.OpenRead(path);
            byte[] duringWrite = null;
            TestDelegate write = () => _diskProvider.WriteFileAtomically(path, temporaryPath =>
            {
                Assert.That(Path.GetDirectoryName(temporaryPath), Is.EqualTo(_tempDir));
                Assert.That(Path.GetExtension(temporaryPath), Is.EqualTo(".png"));
                using var stream = _diskProvider.OpenWriteStream(temporaryPath);
                stream.Write(newBytes, 0, 2);
                stream.Flush();
                duringWrite = File.ReadAllBytes(path);
                if (fail)
                {
                    throw new IOException("Interrupted image write");
                }

                stream.Write(newBytes, 2, 2);
            });

            if (fail)
            {
                Assert.That(write, Throws.TypeOf<IOException>().With.Message.EqualTo("Interrupted image write"));
            }
            else
            {
                Assert.DoesNotThrow(write);
            }

            Assert.That(duringWrite, Is.EqualTo(oldBytes), "Readers must never see an incomplete replacement");
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(fail ? oldBytes : newBytes));
            if (reader != null)
            {
                using var readBytes = new MemoryStream();
                reader.CopyTo(readBytes);
                Assert.That(readBytes.ToArray(), Is.EqualTo(oldBytes), "An already-open reader keeps the complete old file");
            }
            Assert.That(Directory.GetFiles(_tempDir), Is.EquivalentTo(new[] { path }));
        }

        [Test]
        public void refused_atomic_move_keeps_old_file_and_cleans_temporary_output()
        {
            var path = Path.Combine(_tempDir, "poster.png");
            File.WriteAllText(path, "old image");
            var real = new FileSystem();
            var moves = 0;
            var file = InterfaceProxy.Proxy<IFile>((method, args) =>
            {
                if (method.Name == nameof(IFile.Move))
                {
                    moves++;
                    Assert.That(args[1], Is.EqualTo(path));
                    Assert.That(args[2], Is.EqualTo(true));
                    throw new IOException("Replacement refused");
                }

                return method.Invoke(real.File, args);
            });
            var fileSystem = InterfaceProxy.Proxy<IFileSystem>((_, _) => file);
            var disk = new TestDiskProvider(fileSystem);

            Assert.That(() => disk.WriteFileAtomically(path, temporaryPath => File.WriteAllText(temporaryPath, "new image")),
                Throws.TypeOf<IOException>().With.Message.EqualTo("Replacement refused"));
            Assert.That(moves, Is.EqualTo(1));
            Assert.That(File.ReadAllText(path), Is.EqualTo("old image"));
            Assert.That(Directory.GetFiles(_tempDir), Is.EquivalentTo(new[] { path }));
        }

        [Test]
        public void should_resolve_replacement_character_in_file_name()
        {
            var actualPath = Path.Combine(_tempDir, "Renée Ballard 02 - Dark Sacred Night - Michael Connelly (2018).epub");
            File.WriteAllText(actualPath, "ebook");

            var requestedPath = Path.Combine(_tempDir, "Ren\uFFFDe Ballard 02 - Dark Sacred Night - Michael Connelly (2018).epub");

            Assert.Multiple(() =>
            {
                Assert.That(_diskProvider.FileExists(requestedPath), Is.True);
                Assert.That(_diskProvider.GetFileSize(requestedPath), Is.EqualTo(5));
                Assert.That(_diskProvider.GetFileInfo(requestedPath).FullName, Is.EqualTo(actualPath));
            });
        }

        [Test]
        public void should_resolve_paths_when_directory_and_file_drop_diacritics()
        {
            var actualDirectory = Path.Combine(_tempDir, "Gabriel García Márquez");
            Directory.CreateDirectory(actualDirectory);

            var actualPath = Path.Combine(actualDirectory, "Cien años de soledad.epub");
            File.WriteAllText(actualPath, "novel");

            var requestedPath = Path.Combine(_tempDir, "Gabriel Garcia Marquez", "Cien anos de soledad.epub");

            Assert.Multiple(() =>
            {
                Assert.That(_diskProvider.FileExists(requestedPath), Is.True);
                Assert.That(_diskProvider.GetFileInfo(requestedPath).FullName, Is.EqualTo(actualPath));
            });
        }

        [Test]
        public void should_not_delete_loose_replacement_character_match()
        {
            var actualPath = Path.Combine(_tempDir, "notebook.epub");
            File.WriteAllText(actualPath, "keep");

            var requestedPath = Path.Combine(_tempDir, "bo\uFFFDok.epub");

            Assert.Multiple(() =>
            {
                Assert.That(_diskProvider.FileExists(requestedPath), Is.True);
                Assert.Throws<FileNotFoundException>(() => _diskProvider.DeleteFile(requestedPath));
                Assert.That(File.Exists(actualPath), Is.True);
                Assert.That(File.ReadAllText(actualPath), Is.EqualTo("keep"));
            });
        }

        [Test]
        public void should_not_delete_diacritic_stripped_match()
        {
            var actualPath = Path.Combine(_tempDir, "café.epub");
            File.WriteAllText(actualPath, "keep");

            var requestedPath = Path.Combine(_tempDir, "cafe.epub");

            Assert.Multiple(() =>
            {
                Assert.That(_diskProvider.FileExists(requestedPath), Is.True);
                Assert.Throws<FileNotFoundException>(() => _diskProvider.DeleteFile(requestedPath));
                Assert.That(File.Exists(actualPath), Is.True);
            });
        }

        [Test]
        public void should_not_treat_a_loose_apostrophe_match_as_the_same_tracked_file()
        {
            var actualPath = Path.Combine(_tempDir, "Philosopher's Stone.m4b");
            var trackedPath = Path.Combine(_tempDir, "Philosopher’s Stone.m4b");
            File.WriteAllText(actualPath, "keep");

            Assert.Multiple(() =>
            {
                Assert.That(_diskProvider.FileExists(trackedPath), Is.True);
                Assert.That(_diskProvider.FileExistsCanonical(trackedPath), Is.False);
                Assert.That(_diskProvider.FileExistsCanonical(actualPath), Is.True);
            });
        }

        [Test]
        public void should_allow_canonical_unicode_match_for_write()
        {
            var actualPath = Path.Combine(_tempDir, "Renée.epub".Normalize(NormalizationForm.FormD));
            File.WriteAllText(actualPath, "delete");

            var requestedPath = Path.Combine(_tempDir, "Renée.epub".Normalize(NormalizationForm.FormC));

            _diskProvider.DeleteFile(requestedPath);

            Assert.That(File.Exists(actualPath), Is.False);
        }

        [Test]
        public void should_not_walk_loose_directory_segment_for_write()
        {
            var actualDirectory = Path.Combine(_tempDir, "Notebook");
            Directory.CreateDirectory(actualDirectory);

            var actualPath = Path.Combine(actualDirectory, "target.epub");
            File.WriteAllText(actualPath, "keep");

            var requestedPath = Path.Combine(_tempDir, "bo\uFFFDok", "target.epub");

            Assert.Multiple(() =>
            {
                Assert.That(_diskProvider.FileExists(requestedPath), Is.True);
                Assert.Throws<FileNotFoundException>(() => _diskProvider.DeleteFile(requestedPath));
                Assert.That(File.Exists(actualPath), Is.True);
            });
        }

        [Test]
        public void should_not_overwrite_loose_destination_match()
        {
            var sourcePath = Path.Combine(_tempDir, "source.epub");
            var destinationActualPath = Path.Combine(_tempDir, "notebook.epub");
            var destinationRequestedPath = Path.Combine(_tempDir, "bo\uFFFDok.epub");

            File.WriteAllText(sourcePath, "source");
            File.WriteAllText(destinationActualPath, "keep");

            Assert.Multiple(() =>
            {
                Assert.Throws<IOException>(() => _diskProvider.CopyFile(sourcePath, destinationRequestedPath, true));
                Assert.That(File.Exists(sourcePath), Is.True);
                Assert.That(File.Exists(destinationActualPath), Is.True);
                Assert.That(File.ReadAllText(destinationActualPath), Is.EqualTo("keep"));
            });
        }

        [Test]
        public void should_not_delete_source_when_destination_is_same_canonical_file()
        {
            var sourcePath = Path.Combine(_tempDir, "Renée.epub".Normalize(NormalizationForm.FormD));
            var destinationPath = Path.Combine(_tempDir, "Renée.epub".Normalize(NormalizationForm.FormC));

            File.WriteAllText(sourcePath, "keep");

            Assert.Multiple(() =>
            {
                Assert.Throws<IOException>(() => _diskProvider.CopyFile(sourcePath, destinationPath, true));
                Assert.That(File.Exists(sourcePath), Is.True);
                Assert.That(File.ReadAllText(sourcePath), Is.EqualTo("keep"));
            });
        }
    }
}

using System;
using System.IO;
using System.Text;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    [NonParallelizable]
    public class ArchiveServiceFixture
    {
        [Test]
        public void extract_zip_should_reject_path_traversal_entries()
        {
            var sut = new ArchiveService(LogManager.GetCurrentClassLogger());

            var baseDir = GetTempDir();
            try
            {
                var destinationDir = Path.Combine(baseDir, "dest");
                Directory.CreateDirectory(destinationDir);

                var zipPath = Path.Combine(baseDir, "test.zip");
                CreateZipWithEntry(zipPath, "../evil.txt", "pwned");

                var outsidePath = Path.Combine(baseDir, "evil.txt");

                Assert.Throws<IOException>(() => sut.Extract(zipPath, destinationDir));
                Assert.That(File.Exists(outsidePath), Is.False);
            }
            finally
            {
                TryDeleteDirectory(baseDir);
            }
        }

        [Test]
        public void extract_tgz_should_reject_path_traversal_entries()
        {
            var sut = new ArchiveService(LogManager.GetCurrentClassLogger());

            var baseDir = GetTempDir();
            try
            {
                var destinationDir = Path.Combine(baseDir, "dest");
                Directory.CreateDirectory(destinationDir);

                var tgzPath = Path.Combine(baseDir, "test.tar.gz");
                CreateTgzWithEntry(tgzPath, "../evil.txt", "pwned");

                var outsidePath = Path.Combine(baseDir, "evil.txt");

                Assert.Throws<IOException>(() => sut.Extract(tgzPath, destinationDir));
                Assert.That(File.Exists(outsidePath), Is.False);
            }
            finally
            {
                TryDeleteDirectory(baseDir);
            }
        }

        [Test]
        public void extract_zip_should_extract_files_inside_destination()
        {
            var sut = new ArchiveService(LogManager.GetCurrentClassLogger());

            var baseDir = GetTempDir();
            try
            {
                var destinationDir = Path.Combine(baseDir, "dest");
                Directory.CreateDirectory(destinationDir);

                var zipPath = Path.Combine(baseDir, "test.zip");
                CreateZipWithEntry(zipPath, "folder/file.txt", "ok");

                sut.Extract(zipPath, destinationDir);

                Assert.That(File.Exists(Path.Combine(destinationDir, "folder", "file.txt")), Is.True);
            }
            finally
            {
                TryDeleteDirectory(baseDir);
            }
        }

        [Test]
        public void extract_zip_should_enforce_entry_count_limit()
        {
            var sut = new ArchiveService(LogManager.GetCurrentClassLogger());

            var previousMaxEntries = ArchiveExtractionLimits.MaxEntries;
            var previousMaxSingleEntryBytes = ArchiveExtractionLimits.MaxSingleEntryBytes;
            var previousMaxTotalBytes = ArchiveExtractionLimits.MaxTotalBytes;

            var baseDir = GetTempDir();
            try
            {
                ArchiveExtractionLimits.MaxEntries = 1;
                ArchiveExtractionLimits.MaxSingleEntryBytes = 1024;
                ArchiveExtractionLimits.MaxTotalBytes = 10 * 1024;

                var destinationDir = Path.Combine(baseDir, "dest");
                Directory.CreateDirectory(destinationDir);

                var zipPath = Path.Combine(baseDir, "test.zip");
                CreateZipWithEntries(zipPath, new[]
                {
                    ("a.txt", "a"),
                    ("b.txt", "b")
                });

                Assert.Throws<IOException>(() => sut.Extract(zipPath, destinationDir));
            }
            finally
            {
                ArchiveExtractionLimits.MaxEntries = previousMaxEntries;
                ArchiveExtractionLimits.MaxSingleEntryBytes = previousMaxSingleEntryBytes;
                ArchiveExtractionLimits.MaxTotalBytes = previousMaxTotalBytes;
                TryDeleteDirectory(baseDir);
            }
        }

        [Test]
        public void extract_tgz_should_enforce_entry_size_limit()
        {
            var sut = new ArchiveService(LogManager.GetCurrentClassLogger());

            var previousMaxEntries = ArchiveExtractionLimits.MaxEntries;
            var previousMaxSingleEntryBytes = ArchiveExtractionLimits.MaxSingleEntryBytes;
            var previousMaxTotalBytes = ArchiveExtractionLimits.MaxTotalBytes;

            var baseDir = GetTempDir();
            try
            {
                ArchiveExtractionLimits.MaxEntries = 100;
                ArchiveExtractionLimits.MaxSingleEntryBytes = 5;
                ArchiveExtractionLimits.MaxTotalBytes = 10 * 1024;

                var destinationDir = Path.Combine(baseDir, "dest");
                Directory.CreateDirectory(destinationDir);

                var tgzPath = Path.Combine(baseDir, "test.tar.gz");
                CreateTgzWithEntry(tgzPath, "file.txt", "123456");

                Assert.Throws<IOException>(() => sut.Extract(tgzPath, destinationDir));
            }
            finally
            {
                ArchiveExtractionLimits.MaxEntries = previousMaxEntries;
                ArchiveExtractionLimits.MaxSingleEntryBytes = previousMaxSingleEntryBytes;
                ArchiveExtractionLimits.MaxTotalBytes = previousMaxTotalBytes;
                TryDeleteDirectory(baseDir);
            }
        }

        private static string GetTempDir()
        {
            var baseDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, nameof(ArchiveServiceFixture), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }

        private static void CreateZipWithEntry(string zipPath, string entryName, string contents)
        {
            var data = Encoding.UTF8.GetBytes(contents);

            using var fileStream = File.Create(zipPath);
            using var zipOutputStream = new ZipOutputStream(fileStream);

            var entry = new ZipEntry(entryName)
            {
                DateTime = DateTime.UtcNow,
                Size = data.Length
            };

            zipOutputStream.PutNextEntry(entry);
            zipOutputStream.Write(data, 0, data.Length);
            zipOutputStream.CloseEntry();
            zipOutputStream.Finish();
        }

        private static void CreateZipWithEntries(string zipPath, (string entryName, string contents)[] entries)
        {
            using var fileStream = File.Create(zipPath);
            using var zipOutputStream = new ZipOutputStream(fileStream);

            foreach (var (entryName, contents) in entries)
            {
                var data = Encoding.UTF8.GetBytes(contents);

                var entry = new ZipEntry(entryName)
                {
                    DateTime = DateTime.UtcNow,
                    Size = data.Length
                };

                zipOutputStream.PutNextEntry(entry);
                zipOutputStream.Write(data, 0, data.Length);
                zipOutputStream.CloseEntry();
            }

            zipOutputStream.Finish();
        }

        private static void CreateTgzWithEntry(string tgzPath, string entryName, string contents)
        {
            var data = Encoding.UTF8.GetBytes(contents);

            using var fileStream = File.Create(tgzPath);
            using var gzipOutputStream = new GZipOutputStream(fileStream);
            using var tarOutputStream = new TarOutputStream(gzipOutputStream, Encoding.UTF8);

            var entry = TarEntry.CreateTarEntry(entryName);
            entry.Size = data.Length;

            tarOutputStream.PutNextEntry(entry);
            tarOutputStream.Write(data, 0, data.Length);
            tarOutputStream.CloseEntry();
            tarOutputStream.Flush();
        }
    }
}

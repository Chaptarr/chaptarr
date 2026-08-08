using System;
using System.IO;
using System.IO.Abstractions;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ImportOrchestratorStagingFilterFixture
    {
        private static bool ShouldStageFile(
            IFileInfo diskFile,
            BookFile knownFile,
            bool forceStage,
            FilterFilesType filter = FilterFilesType.Known)
        {
            var method = typeof(ImportOrchestratorV2).GetMethod("ShouldStageFile", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            return (bool)method.Invoke(null, new object[] { diskFile, knownFile, forceStage, filter });
        }

        private static IFileInfo CreateDiskFile(string root, string name, string contents, DateTime modifiedUtc)
        {
            var path = Path.Combine(root, name);
            File.WriteAllText(path, contents);
            File.SetLastWriteTimeUtc(path, modifiedUtc);

            return new FileSystem().FileInfo.FromFileName(path);
        }

        [Test]
        public void routine_scan_should_skip_known_unchanged_mapped_file()
        {
            var root = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, $"stage_filter_{Guid.NewGuid():N}")).FullName;
            try
            {
                var modified = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                var diskFile = CreateDiskFile(root, "mapped.m4b", "audio", modified);
                var knownFile = new BookFile
                {
                    Path = diskFile.FullName,
                    Size = diskFile.Length,
                    Modified = modified,
                    EditionId = 10
                };

                Assert.That(ShouldStageFile(diskFile, knownFile, forceStage: false), Is.False);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void routine_scan_should_skip_known_unchanged_unmapped_file()
        {
            var root = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, $"stage_filter_{Guid.NewGuid():N}")).FullName;
            try
            {
                var modified = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                var diskFile = CreateDiskFile(root, "unmapped.mp3", "audio", modified);
                var knownFile = new BookFile
                {
                    Path = diskFile.FullName,
                    Size = diskFile.Length,
                    Modified = modified,
                    EditionId = 0
                };

                Assert.That(ShouldStageFile(diskFile, knownFile, forceStage: false), Is.False);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void routine_scan_should_stage_known_file_when_size_or_mtime_changed()
        {
            var root = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, $"stage_filter_{Guid.NewGuid():N}")).FullName;
            try
            {
                var modified = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                var diskFile = CreateDiskFile(root, "changed.mp3", "new audio", modified);
                var knownFile = new BookFile
                {
                    Path = diskFile.FullName,
                    Size = diskFile.Length - 1,
                    Modified = modified,
                    EditionId = 10
                };

                Assert.That(ShouldStageFile(diskFile, knownFile, forceStage: false), Is.True);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void explicit_retry_should_stage_known_unchanged_file()
        {
            var root = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, $"stage_filter_{Guid.NewGuid():N}")).FullName;
            try
            {
                var modified = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                var diskFile = CreateDiskFile(root, "retry.mp3", "audio", modified);
                var knownFile = new BookFile
                {
                    Path = diskFile.FullName,
                    Size = diskFile.Length,
                    Modified = modified,
                    EditionId = 0
                };

                Assert.That(ShouldStageFile(diskFile, knownFile, forceStage: true), Is.True);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void routine_scan_should_stage_new_file()
        {
            var root = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, $"stage_filter_{Guid.NewGuid():N}")).FullName;
            try
            {
                var diskFile = CreateDiskFile(root, "new.mp3", "audio", new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));

                Assert.That(ShouldStageFile(diskFile, knownFile: null, forceStage: false), Is.True);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void matched_filter_should_stage_unchanged_unmapped_file_but_skip_mapped_file()
        {
            var root = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, $"stage_filter_{Guid.NewGuid():N}")).FullName;
            try
            {
                var modified = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                var diskFile = CreateDiskFile(root, "retry-unmapped.mp3", "audio", modified);
                var knownFile = new BookFile
                {
                    Path = diskFile.FullName,
                    Size = diskFile.Length,
                    Modified = modified,
                    EditionId = 0
                };

                Assert.That(ShouldStageFile(diskFile, knownFile, false, FilterFilesType.Matched), Is.True);

                knownFile.EditionId = 10;
                Assert.That(ShouldStageFile(diskFile, knownFile, false, FilterFilesType.Matched), Is.False);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void none_filter_should_stage_known_unchanged_file()
        {
            var root = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, $"stage_filter_{Guid.NewGuid():N}")).FullName;
            try
            {
                var modified = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                var diskFile = CreateDiskFile(root, "full-refresh.mp3", "audio", modified);
                var knownFile = new BookFile
                {
                    Path = diskFile.FullName,
                    Size = diskFile.Length,
                    Modified = modified,
                    EditionId = 10
                };

                Assert.That(ShouldStageFile(diskFile, knownFile, false, FilterFilesType.None), Is.True);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

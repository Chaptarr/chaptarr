using System;
using System.IO;
using System.IO.Abstractions;
using System.Reflection;
using System.Runtime.InteropServices;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    [Platform(Exclude = "Win", Reason = "The fixture creates a real Unix hardlink")]
    public class FileMutationSafetyServiceFixture
    {
        [DllImport("libc", SetLastError = true)]
        private static extern int link(string oldpath, string newpath);

        private sealed class TestDiskProvider : DiskProviderBase
        {
            public TestDiskProvider()
                : base(new FileSystem())
            {
            }

            public int? LinkCount { get; set; }

            public override long? GetAvailableSpace(string path) => throw new NotImplementedException();
            public override void InheritFolderPermissions(string filename) => throw new NotImplementedException();
            public override void SetEveryonePermissions(string filename) => throw new NotImplementedException();
            public override void SetFilePermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void SetPermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void CopyPermissions(string sourcePath, string targetPath) => throw new NotImplementedException();
            public override long? GetTotalSize(string path) => throw new NotImplementedException();
            public override bool TryCreateHardLink(string source, string destination) => throw new NotImplementedException();
            public override int? GetFileLinkCount(string path) => LinkCount;
        }

        private class ConfigProxy : DispatchProxy
        {
            public FileDateType FileDate { get; set; } = FileDateType.None;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_FileDate" => FileDate,
                    "get_SetPermissionsLinux" => false,
                    "get_WriteAudioTags" => WriteAudioTagsType.No,
                    _ => throw new NotImplementedException($"Unexpected call to IConfigService.{targetMethod?.Name}")
                };
            }
        }

        private string _tempDirectory;
        private TestDiskProvider _diskProvider;
        private IConfigService _configService;
        private ConfigProxy _configProxy;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), $"chaptarr-hardlink-safety-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDirectory);
            _diskProvider = new TestDiskProvider();
            _configService = DispatchProxy.Create<IConfigService, ConfigProxy>();
            _configProxy = (ConfigProxy)(object)_configService;
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void enabled_import_mutation_should_break_real_hardlink_before_write()
        {
            var (source, destination) = CreateHardlinkedPair();
            _configProxy.FileDate = FileDateType.BookReleaseDate;

            BuildSubject().PrepareImportDestination(
                new BookFile { Path = destination },
                TransferMode.HardLink);

            File.WriteAllText(destination, "library-mutated");

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(source), Is.EqualTo("original"));
                Assert.That(File.ReadAllText(destination), Is.EqualTo("library-mutated"));
            });
        }

        [Test]
        public void disabled_import_mutations_should_preserve_hardlink()
        {
            var (source, destination) = CreateHardlinkedPair();

            BuildSubject().PrepareImportDestination(
                new BookFile { Path = destination },
                TransferMode.HardLink);

            File.WriteAllText(destination, "shared");

            Assert.That(File.ReadAllText(source), Is.EqualTo("shared"));
        }

        [Test]
        public void later_mutation_should_break_existing_hardlink_using_link_count()
        {
            var (source, destination) = CreateHardlinkedPair();
            _diskProvider.LinkCount = 2;

            BuildSubject().EnsureMutableFile(destination);
            File.WriteAllText(destination, "retagged");

            Assert.That(File.ReadAllText(source), Is.EqualTo("original"));
        }

        [Test]
        public void unknown_link_count_should_fail_closed_without_leaving_a_temporary_copy()
        {
            var path = Path.Combine(_tempDirectory, "unknown-link-count.mp3");
            File.WriteAllText(path, "original");
            _diskProvider.LinkCount = null;

            var exception = Assert.Throws<IOException>(() => BuildSubject().EnsureMutableFile(path));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Does.Contain("hardlink count could not be determined"));
                Assert.That(File.ReadAllText(path), Is.EqualTo("original"));
                Assert.That(Directory.GetFiles(_tempDirectory, "*.chaptarr-mutable~*"), Is.Empty);
            });
        }

        [Test]
        public void single_link_file_should_not_be_copied_before_mutation()
        {
            var path = Path.Combine(_tempDirectory, "single-link.mp3");
            File.WriteAllText(path, "original");
            _diskProvider.LinkCount = 1;

            BuildSubject().EnsureMutableFile(path);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(path), Is.EqualTo("original"));
                Assert.That(Directory.GetFiles(_tempDirectory, "*.chaptarr-mutable~*"), Is.Empty);
            });
        }

        private FileMutationSafetyService BuildSubject()
        {
            return new FileMutationSafetyService(_diskProvider, _configService, LogManager.GetLogger("hardlink-safety-test"));
        }

        private (string Source, string Destination) CreateHardlinkedPair()
        {
            var source = Path.Combine(_tempDirectory, $"source-{Guid.NewGuid():N}.mp3");
            var destination = Path.Combine(_tempDirectory, $"destination-{Guid.NewGuid():N}.mp3");
            File.WriteAllText(source, "original");

            if (link(source, destination) != 0)
            {
                throw new InvalidOperationException($"Unable to create test hardlink. errno={Marshal.GetLastPInvokeError()}");
            }

            return (source, destination);
        }
    }
}

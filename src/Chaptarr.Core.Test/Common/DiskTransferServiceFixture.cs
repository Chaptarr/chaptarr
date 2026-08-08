using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Disk;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class DiskTransferServiceFixture
    {
        private LoggingConfiguration _originalConfiguration;
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _originalConfiguration = LogManager.Configuration;
            _tempDir = Path.Combine(Path.GetTempPath(), "chaptarr-disk-transfer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            LogManager.Configuration = _originalConfiguration;
            LogManager.ReconfigExistingLoggers();

            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }

        [Test]
        public void hardlink_or_copy_should_log_info_when_hardlink_falls_back_to_copy()
        {
            var memoryTarget = ConfigureLogging();
            var sourceRoot = Path.Combine(_tempDir, "downloads");
            var targetRoot = Path.Combine(_tempDir, "library");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(targetRoot);

            var sourcePath = Path.Combine(sourceRoot, "book.m4b");
            var targetPath = Path.Combine(targetRoot, "book.m4b");
            File.WriteAllText(sourcePath, "audiobook");

            var diskProvider = new HardlinkFailingDiskProvider(sourceRoot, targetRoot);
            var subject = new DiskTransferService(diskProvider, LogManager.GetLogger("DiskTransferService"));

            var result = subject.TransferFile(sourcePath, targetPath, TransferMode.HardLinkOrCopy);

            var combinedLogs = string.Join("\n", memoryTarget.Logs);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(TransferMode.Copy));
                Assert.That(diskProvider.HardlinkAttempts, Is.EqualTo(1));
                Assert.That(File.ReadAllText(targetPath), Is.EqualTo("audiobook"));
                Assert.That(combinedLogs, Does.Contain("Hardlink failed"));
                Assert.That(combinedLogs, Does.Contain("copying file instead"));
                Assert.That(combinedLogs, Does.Contain(sourceRoot));
                Assert.That(combinedLogs, Does.Contain(targetRoot));
                Assert.That(combinedLogs, Does.Contain("ext4"));
            });
        }

        private static MemoryTarget ConfigureLogging()
        {
            var memoryTarget = new MemoryTarget("memory")
            {
                Layout = "${level}|${logger}|${message}"
            };

            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Info, LogLevel.Fatal, memoryTarget, "DiskTransferService");

            LogManager.Configuration = config;
            LogManager.ReconfigExistingLoggers();

            return memoryTarget;
        }

        private sealed class HardlinkFailingDiskProvider : DiskProviderBase
        {
            private readonly string _sourceRoot;
            private readonly string _targetRoot;

            public HardlinkFailingDiskProvider(string sourceRoot, string targetRoot)
                : base(new FileSystem())
            {
                _sourceRoot = sourceRoot;
                _targetRoot = targetRoot;
            }

            public int HardlinkAttempts { get; private set; }

            public override long? GetAvailableSpace(string path) => throw new NotImplementedException();
            public override void InheritFolderPermissions(string filename) => throw new NotImplementedException();
            public override void SetEveryonePermissions(string filename) => throw new NotImplementedException();
            public override void SetFilePermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void SetPermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void CopyPermissions(string sourcePath, string targetPath) => throw new NotImplementedException();
            public override long? GetTotalSize(string path) => throw new NotImplementedException();

            public override bool TryCreateHardLink(string source, string destination)
            {
                HardlinkAttempts++;
                return false;
            }

            public override bool TryCreateRefLink(string source, string destination)
            {
                return false;
            }

            public override IMount GetMount(string path)
            {
                if (path.StartsWith(_sourceRoot, StringComparison.Ordinal))
                {
                    return new TestMount("downloads-mount", _sourceRoot, "ext4");
                }

                if (path.StartsWith(_targetRoot, StringComparison.Ordinal))
                {
                    return new TestMount("library-mount", _targetRoot, "ext4");
                }

                return null;
            }
        }

        private sealed class TestMount : IMount
        {
            public TestMount(string name, string rootDirectory, string driveFormat)
            {
                Name = name;
                RootDirectory = rootDirectory;
                DriveFormat = driveFormat;
                MountOptions = new MountOptions(new Dictionary<string, string>());
            }

            public long AvailableFreeSpace => 0;
            public string DriveFormat { get; }
            public DriveType DriveType => DriveType.Fixed;
            public bool IsReady => true;
            public MountOptions MountOptions { get; }
            public string Name { get; }
            public string RootDirectory { get; }
            public long TotalFreeSpace => 0;
            public long TotalSize => 0;
            public string VolumeLabel => Name;
            public string VolumeName => Name;
        }
    }
}

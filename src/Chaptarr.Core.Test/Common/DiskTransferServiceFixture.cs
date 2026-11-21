using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Text;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class DiskTransferServiceFixture
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);

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

        [TestCase("file")]
        [TestCase("folder")]
        [TestCase("mirror")]
        public void transfer_accepts_an_intermediate_parent_component(string operation)
        {
            var intermediate = Path.Combine(_tempDir, "long intermediate directory");
            Directory.CreateDirectory(intermediate);
            AssertTransferThroughAlias(Path.Combine(intermediate, ".."), operation);
        }

        [TestCase("file")]
        [TestCase("folder")]
        [TestCase("mirror")]
        public void transfer_preserves_child_names_with_repeated_separators(string operation)
        {
            AssertTransferThroughAlias(_tempDir + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar, operation);
        }

        [TestCase("folder", ".")]
        [TestCase("folder", "..")]
        [TestCase("folder", "trailing-separator")]
        [TestCase("mirror", ".")]
        [TestCase("mirror", "..")]
        [TestCase("mirror", "trailing-separator")]
        public void folder_transfer_enumerates_only_the_selected_source(string operation, string ending)
        {
            var parent = Path.Combine(_tempDir, "parent");
            var source = Path.Combine(parent, "wanted");
            var child = Path.Combine(source, "child");
            var sibling = Path.Combine(parent, "unrelated");
            var target = Path.Combine(_tempDir, "target");
            foreach (var directory in new[] { child, sibling, target })
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(Path.Combine(source, "config.xml"), "wanted");
            File.WriteAllText(Path.Combine(sibling, "private.xml"), "leave this sibling alone");
            var alias = ending == ".." ? Path.Combine(child, "..")
                : ending == "." ? Path.Combine(source, ".")
                : source + Path.DirectorySeparatorChar;
            var firstEnumeration = true;
            var disk = RealDiskProvider.Create(LogManager.GetCurrentClassLogger());
            var proxy = InterfaceProxy.Proxy<IDiskProvider>((method, args) =>
            {
                if (method.Name == nameof(IDiskProvider.GetDirectoryInfos) && firstEnumeration)
                {
                    // Stop the failing version before it can recurse into an unrelated folder.
                    Assert.That(Path.GetFullPath((string)args[0]).GetCleanPath().GetActualCasing(),
                        Is.EqualTo(source.GetActualCasing()), "Only the selected source folder may be enumerated");
                    if (ending == "trailing-separator")
                    {
                        Assert.That((string)args[0], Is.EqualTo(source.GetActualCasing()),
                            "Folder transfers drop the redundant trailing separator");
                    }
                    firstEnumeration = false;
                }

                try
                {
                    return method.Invoke(disk, args);
                }
                catch (System.Reflection.TargetInvocationException ex)
                {
                    throw ex.InnerException;
                }
            });
            var subject = new DiskTransferService(proxy, LogManager.GetCurrentClassLogger());
            if (operation == "folder")
            {
                subject.TransferFolder(alias, target, TransferMode.Copy);
            }
            else
            {
                subject.MirrorFolder(alias, target);
            }

            Assert.That(firstEnumeration, Is.False);
            Assert.That(Directory.GetFiles(target, "*", SearchOption.AllDirectories),
                Is.EqualTo(new[] { Path.Combine(target, "config.xml") }));
            Assert.That(File.ReadAllText(Path.Combine(target, "config.xml")), Is.EqualTo("wanted"));
            Assert.That(File.ReadAllText(Path.Combine(source, "config.xml")), Is.EqualTo("wanted"));
            Assert.That(File.ReadAllText(Path.Combine(sibling, "private.xml")), Is.EqualTo("leave this sibling alone"));
        }

        [TestCase("file")]
        [TestCase("folder")]
        [TestCase("mirror")]
        [Platform(Include = "Win")]
        public void transfer_preserves_child_names_through_windows_short_parent_names(string operation)
        {
            var length = GetShortPathName(_tempDir, null, 0);
            Assert.That(length, Is.GreaterThan(0), "Could not obtain the temporary directory's short path");
            var shortPath = new StringBuilder((int)length);
            Assert.That(GetShortPathName(_tempDir, shortPath, length), Is.GreaterThan(0));
            if (string.Equals(shortPath.ToString(), _tempDir, StringComparison.OrdinalIgnoreCase))
            {
                Assert.Ignore("This volume does not provide an 8.3 alias for the test directory");
            }

            AssertTransferThroughAlias(shortPath.ToString(), operation);
        }

        private void AssertTransferThroughAlias(string alias, string operation)
        {
            var source = Path.Combine(_tempDir, "source");
            var target = Path.Combine(_tempDir, "target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "config.xml"), "preserve the complete child name");
            var subject = new DiskTransferService(RealDiskProvider.Create(LogManager.GetCurrentClassLogger()), LogManager.GetCurrentClassLogger());
            var sourceAlias = Path.Combine(alias, "source");
            var targetAlias = Path.Combine(alias, "target");

            Assert.DoesNotThrow(() =>
            {
                if (operation == "file") subject.TransferFile(Path.Combine(sourceAlias, "config.xml"), Path.Combine(targetAlias, "config.xml"), TransferMode.Copy);
                else if (operation == "folder") subject.TransferFolder(sourceAlias, targetAlias, TransferMode.Copy);
                else subject.MirrorFolder(sourceAlias, targetAlias);
            }, "Normalizing the parent must preserve the source and destination child names");
            Assert.That(File.ReadAllText(Path.Combine(target, "config.xml")), Is.EqualTo("preserve the complete child name"));
            Assert.That(File.Exists(Path.Combine(source, "config.xml")), Is.True);
            Assert.That(Directory.GetFiles(target), Is.EqualTo(new[] { Path.Combine(target, "config.xml") }));
        }

        [Test]
        public void copy_collision_after_preflight_does_not_rollback_someone_elses_file()
        {
            var source = Path.Combine(_tempDir, "source.nfo");
            var target = Path.Combine(_tempDir, "target.nfo");
            File.WriteAllText(source, "new notes");
            var disk = RealDiskProvider.Create(LogManager.GetCurrentClassLogger());
            var proxy = InterfaceProxy.Proxy<IDiskProvider>((method, args) =>
            {
                if (method.Name == nameof(IDiskProvider.CopyFile))
                {
                    File.WriteAllText(target, "existing notes");
                    throw new FileAlreadyExistsException("Destination appeared", target);
                }

                try { return method.Invoke(disk, args); }
                catch (System.Reflection.TargetInvocationException ex) { throw ex.InnerException; }
            });
            var subject = new DiskTransferService(proxy, LogManager.GetCurrentClassLogger());

            Assert.Throws<FileAlreadyExistsException>(() => subject.TransferFile(source, target, TransferMode.Copy, false));
            Assert.That(File.ReadAllText(source), Is.EqualTo("new notes"));
            Assert.That(File.ReadAllText(target), Is.EqualTo("existing notes"));
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

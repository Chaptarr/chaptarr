using System;
using System.IO;
using System.Security.AccessControl;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class FileMutationSafetyServiceFixture
    {
        private string _directory;
        private string _source;
        private string _destination;
        private IDiskProvider _disk;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_directory);
            _source = Path.Combine(_directory, "source.mp3");
            _destination = Path.Combine(_directory, "destination.mp3");
            File.WriteAllText(_source, "original");
            _disk = RealDiskProvider.Create(_logger);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_directory, true);

        [TestCase(true)]
        [TestCase(false)]
        public void import_and_rename_should_preserve_requested_link_mode_despite_enabled_mutation_settings(bool hardlinks)
        {
            if (hardlinks)
            {
                Link();
                File.Delete(_destination);
            }

            var library = Path.Combine(_directory, "library");
            var authorPath = Path.Combine(library, "Author");
            Directory.CreateDirectory(authorPath);
            var date = new DateTime(2020, 1, 1);
            File.SetLastWriteTime(_source, date);
            var author = new Author { Name = "Author", AudiobookRootFolderPath = library };
            var book = new Book { Author = author, MediaType = BookMediaType.Audiobook, ReleaseDate = date };
            var edition = new Edition { Book = book, Title = "Book" };
            var file = new BookFile { Path = _source, Edition = edition, Quality = new QualityModel(Quality.MP3) };
            var local = new LocalBook { Path = _source, Author = author, Book = book, Edition = edition };
            var config = Config(hardlinks);
            var watcher = Proxy<IRootFolderWatchingService>((_, _) => null);
            var name = "Imported";
            var mover = new BookFileMovingService(null,
                new UpdateBookFileService(_disk, config, null, watcher, _logger),
                Proxy<IBuildFileNames>((_, _) => name), Proxy<IBuildAuthorPaths>((_, _) => authorPath), null,
                Proxy<IEbookColocationPlanner>((_, _) => new EbookColocationPlan()),
                new DiskTransferService(_disk, _logger), _disk, null, watcher,
                new MediaFileAttributeService(config, _disk, _logger), null, config, _logger);

            mover.CopyBookFile(file, local);
            Assert.That(_disk.GetFileLinkCount(file.Path), Is.EqualTo(hardlinks ? 2 : 1), "Import must honor the hardlink setting even with tags, dates and permissions enabled.");
            name = "Renamed";
            local.Path = file.Path;
            mover.MoveBookFile(file, local);
            Assert.Multiple(() =>
            {
                Assert.That(_disk.GetFileLinkCount(file.Path), Is.EqualTo(hardlinks ? 2 : 1), "Rename must not replace a linked file with a copy.");
                Assert.That(File.ReadAllText(_source), Is.EqualTo("original"));
                Assert.That(File.GetLastWriteTimeUtc(file.Path), Is.EqualTo(DateTime.SpecifyKind(date, DateTimeKind.Utc)));
                Assert.That(File.GetLastWriteTimeUtc(_source), Is.EqualTo(hardlinks ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : date.ToUniversalTime()), "Copy mode must preserve the source date; hardlink mode deliberately shares the new date.");
            });
        }

        [Test]
        public void applying_permissions_twice_should_keep_the_shared_file()
        {
            Link();
            if (OperatingSystem.IsWindows())
            {
                var source = new FileInfo(_source);
                var permissions = source.GetAccessControl();
                permissions.SetAccessRuleProtection(true, true);
                source.SetAccessControl(permissions);
            }
            else
            {
                File.SetUnixFileMode(_source, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var subject = new MediaFileAttributeService(Config(), _disk, _logger);
            subject.SetFilePermissions(_destination);
            subject.SetFilePermissions(_destination);
            Assert.That(_disk.GetFileLinkCount(_destination), Is.EqualTo(2));
            if (OperatingSystem.IsWindows())
            {
                Assert.That(new FileInfo(_source).GetAccessControl().AreAccessRulesProtected, Is.False);
            }
            else
            {
                Assert.That(File.GetUnixFileMode(_source), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead));
            }
        }

        [Test]
        public void content_mutation_should_separate_existing_hardlink()
        {
            Link();
            new FileMutationSafetyService(_disk, new DiskTransferService(_disk, _logger), new MediaFileAttributeService(Config(), _disk, _logger), _logger).EnsureMutableFile(_destination);
            File.WriteAllText(_destination, "retagged");
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(_source), Is.EqualTo("original"));
                Assert.That(_disk.GetFileLinkCount(_source), Is.EqualTo(1));
                Assert.That(_disk.GetFileLinkCount(_destination), Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void file_symlinks_must_be_rejected_regardless_of_target_link_count(bool hardlinkedTarget)
        {
            if (hardlinkedTarget) Link();
            else File.Copy(_source, _destination);
            var symlink = Path.Combine(_directory, "linked.mp3");
            SymbolicLinkTest.CreateFile(symlink, _destination);
            var safety = new FileMutationSafetyService(_disk, new DiskTransferService(_disk, _logger), new MediaFileAttributeService(Config(), _disk, _logger), _logger);
            var error = Assert.Throws<FileSymlinkMutationException>(() => safety.EnsureMutableFile(symlink));
            Assert.That(error.Message, Does.Contain(symlink).And.Contain("file symlink"));
            Assert.That(File.ReadAllText(_source), Is.EqualTo("original"));
            Assert.That(File.ReadAllText(_destination), Is.EqualTo("original"));
            Assert.That(new FileInfo(symlink).LinkTarget, Is.EqualTo(_destination));
            Assert.That(_disk.GetFileLinkCount(_destination), Is.EqualTo(hardlinkedTarget ? 2 : 1));
            Assert.That(Directory.GetFiles(_directory, "*.chaptarr-tmp"), Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ordinary_files_in_a_symlinked_directory_must_remain_mutable(bool hardlinked)
        {
            var library = Path.Combine(_directory, "library");
            Directory.CreateDirectory(library);
            var file = Path.Combine(library, "book.mp3");
            if (hardlinked) Assert.That(_disk.TryCreateHardLink(_source, file), Is.True);
            else File.Copy(_source, file);
            var linkedLibrary = Path.Combine(_directory, "linked-library");
            SymbolicLinkTest.CreateDirectory(linkedLibrary, library);
            var path = Path.Combine(linkedLibrary, "book.mp3");
            var safety = new FileMutationSafetyService(_disk, new DiskTransferService(_disk, _logger), new MediaFileAttributeService(Config(), _disk, _logger), _logger);
            Assert.That(safety.EnsureMutableFile(path), Is.EqualTo(hardlinked));
            File.WriteAllText(path, "retagged");
            Assert.That(File.ReadAllText(file), Is.EqualTo("retagged"));
            Assert.That(File.ReadAllText(_source), Is.EqualTo("original"));
            Assert.That(new DirectoryInfo(linkedLibrary).LinkTarget, Is.EqualTo(library));
        }

        [Test]
        public void failed_copy_should_keep_the_original_links_and_remove_its_partial_copy()
        {
            Link();
            var disk = Proxy<IDiskProvider>((method, args) =>
            {
                switch (method.Name)
                {
                    case "FileExists": return File.Exists((string)args[0]);
                    case "GetFileLinkCount": return _disk.GetFileLinkCount((string)args[0]);
                    case "GetFileSize": return new FileInfo((string)args[0]).Length;
                    case "CopyFile":
                        File.WriteAllText((string)args[1], "partial");
                        throw new IOException("Simulated disk full");
                    case "DeleteFile": File.Delete((string)args[0]); return null;
                    default: return method.Invoke(_disk, args);
                }
            });
            Assert.Throws<IOException>(() => new FileMutationSafetyService(disk, new DiskTransferService(disk, _logger), new MediaFileAttributeService(Config(), _disk, _logger), _logger).EnsureMutableFile(_destination));
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(_source), Is.EqualTo("original"));
                Assert.That(_disk.GetFileLinkCount(_destination), Is.EqualTo(2));
                Assert.That(Directory.GetFiles(_directory, "*.chaptarr-tmp"), Is.Empty);
            });
        }

        [Test]
        public void long_utf8_filename_must_allow_separation()
        {
            _destination = Path.Combine(_directory, new string('界', 70) + ".mp3");
            Link();
            new FileMutationSafetyService(_disk, new DiskTransferService(_disk, _logger), new MediaFileAttributeService(Config(), _disk, _logger), _logger).EnsureMutableFile(_destination);
            Assert.That(_disk.GetFileLinkCount(_destination), Is.EqualTo(1));
            Assert.That(File.ReadAllText(_source), Is.EqualTo("original"));
            Assert.That(Directory.GetFiles(_directory, "*.chaptarr-tmp"), Is.Empty);
        }

        [Test]
        public void silent_short_copy_must_not_replace_the_library_file()
        {
            Link();
            var disk = Proxy<IDiskProvider>((method, args) =>
            {
                if (method.Name == "CopyFile") { File.WriteAllText((string)args[1], "short"); return null; }
                if (method.Name == "GetMount") return null;
                return method.Invoke(_disk, args);
            });
            var safety = new FileMutationSafetyService(disk, new DiskTransferService(disk, _logger), new MediaFileAttributeService(Config(), _disk, _logger), _logger);
            Assert.That(Assert.Throws<IOException>(() => safety.EnsureMutableFile(_destination)).Message, Does.Contain("File copy incomplete"));
            Assert.That(_disk.GetFileLinkCount(_destination), Is.EqualTo(2));
            Assert.That(File.ReadAllText(_destination), Is.EqualTo("original"));
            Assert.That(Directory.GetFiles(_directory, "*.chaptarr-tmp"), Is.Empty);
        }

        [TestCase(null)]
        [TestCase(1)]
        public void unknown_count_should_fail_closed_and_single_link_should_not_copy(int? count)
        {
            var disk = Proxy<IDiskProvider>((method, _) => method.Name switch
            {
                "FileExists" => true,
                "GetFileLinkCount" => count,
                _ => throw new InvalidOperationException("Must not copy: " + method.Name)
            });
            var subject = new FileMutationSafetyService(disk, new DiskTransferService(disk, _logger), new MediaFileAttributeService(Config(), _disk, _logger), _logger);
            if (count == null)
            {
                Assert.That(Assert.Throws<IOException>(() => subject.EnsureMutableFile(_source)).Message, Does.Contain("hardlink count could not be determined"));
            }
            else
            {
                subject.EnsureMutableFile(_source);
            }

            Assert.That(File.ReadAllText(_source), Is.EqualTo("original"));
            Assert.That(Directory.GetFiles(_directory, "*.chaptarr-tmp"), Is.Empty);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void separation_should_apply_only_configured_permissions_to_the_copy(bool managePermissions)
        {
            if (OperatingSystem.IsWindows()) Assert.Ignore("Unix permissions contract");
            Link();
            var originalMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
            File.SetUnixFileMode(_source, originalMode);
            var attributes = new MediaFileAttributeService(Config(managePermissions: managePermissions), _disk, _logger);
            new FileMutationSafetyService(_disk, new DiskTransferService(_disk, _logger), attributes, _logger).EnsureMutableFile(_destination);
            Assert.That(File.GetUnixFileMode(_source), Is.EqualTo(originalMode));
            Assert.That(File.GetUnixFileMode(_destination), Is.EqualTo(managePermissions
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                : originalMode));
            Assert.That(Mono.Unix.Native.Syscall.stat(_destination, out var stat), Is.Zero);
            Assert.That(stat.st_gid, Is.EqualTo(Mono.Unix.Native.Syscall.getegid()), "This test verifies the runner's own group, not membership in another service's group.");
        }

        private IConfigService Config(bool hardlinks = true, bool managePermissions = true) => Proxy<IConfigService>((method, _) => method.Name switch
        {
            "get_CopyUsingHardlinks" => hardlinks,
            "get_FileDate" => FileDateType.BookReleaseDate,
            "get_SetPermissionsLinux" => managePermissions,
            "get_ChmodFolder" => "755",
            "get_ChownGroup" => "",
            "get_WriteAudioTags" => WriteAudioTagsType.Sync,
            _ => throw new NotImplementedException(method.Name)
        });

        private void Link()
        {
            if (!_disk.TryCreateHardLink(_source, _destination))
            {
                Assert.Ignore("This test filesystem does not support hardlinks.");
            }

            Assert.That(_disk.GetFileLinkCount(_destination), Is.EqualTo(2));
        }
    }
}

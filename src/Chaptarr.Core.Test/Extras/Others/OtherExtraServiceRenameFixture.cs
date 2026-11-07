using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Extras.Others
{
    [TestFixture]
    public class OtherExtraServiceRenameFixture
    {
        private const string AuthorFolder = "/library/Zogarth";
        private const string NewBookFolder = AuthorFolder + "/The Primal Hunter/01 - Primal Hunter 1";

        private static string NormalizePath(string path) => path?.Replace('\\', '/');

        [Test]
        public void should_rename_an_extra_that_a_folder_move_carried_under_its_old_name()
        {
            var disk = new FakeDisk();
            disk.Files.Add(NewBookFolder + "/Primal Hunter 1.jpg");
            disk.Files.Add(NewBookFolder + "/Primal Hunter 1 - Tome 1.m4b");
            var extraFiles = new FakeOtherExtraFileService();
            extraFiles.Files.Add(new OtherExtraFile
            {
                Id = 5,
                AuthorId = 1,
                BookFileId = 40,
                RelativePath = "Primal Hunter/Primal Hunter 1 - Tome1/Primal Hunter 1.jpg",
                Extension = ".jpg"
            });
            var subject = new OtherExtraService(null, disk.Proxy, null, extraFiles.Proxy, null, LogManager.GetCurrentClassLogger());
            var author = new Author { Id = 1, Name = "Zogarth", Path = AuthorFolder, AudiobookPath = AuthorFolder };
            var bookFile = new BookFile { Id = 40, Path = NewBookFolder + "/Primal Hunter 1 - Tome 1.m4b" };

            subject.MoveFilesAfterRename(author, new List<BookFile> { bookFile }, new List<RenamedBookFile>());

            Assert.Multiple(() =>
            {
                Assert.That(disk.Moves, Is.EqualTo(new[] { $"{NewBookFolder}/Primal Hunter 1.jpg -> {NewBookFolder}/Primal Hunter 1 - Tome 1.jpg" }));
                Assert.That(extraFiles.Upserted.Single().RelativePath, Is.EqualTo("The Primal Hunter/01 - Primal Hunter 1/Primal Hunter 1 - Tome 1.jpg"));
            });
        }

        [Test]
        public void should_move_configured_untracked_extra_files_when_no_book_media_remains()
        {
            const string oldFolder = AuthorFolder + "/Old Book";
            const string newFolder = AuthorFolder + "/New Book";
            var sidecarPath = oldFolder + "/notes.nfo";
            var destinationPath = newFolder + "/notes.nfo";
            var disk = new FakeDisk();
            disk.Files.Add(sidecarPath);
            disk.Files.Add(newFolder + "/book.m4b");
            var service = CreateOtherExtraService(disk, moveExtraFilesOnRename: true);
            var renamedFile = new RenamedBookFile
            {
                PreviousPath = oldFolder + "/book.m4b",
                BookFile = new BookFile { Id = 41, Path = newFolder + "/book.m4b" }
            };

            service.MoveFilesAfterRename(
                new Author { Id = 1 },
                new List<BookFile> { renamedFile.BookFile },
                new List<RenamedBookFile> { renamedFile });

            Assert.That(disk.Moves, Is.EqualTo(new[] { $"{sidecarPath} -> {destinationPath}" }));
        }

        [Test]
        public void should_leave_configured_extra_files_when_an_ebook_remains_in_the_source_folder()
        {
            const string oldFolder = AuthorFolder + "/Old Book";
            const string newFolder = AuthorFolder + "/New Book";
            var sidecarPath = oldFolder + "/notes.nfo";
            var disk = new FakeDisk();
            disk.Files.Add(sidecarPath);
            disk.Files.Add(oldFolder + "/book.epub");
            disk.Files.Add(newFolder + "/book.m4b");
            var service = CreateOtherExtraService(disk, moveExtraFilesOnRename: true);
            var renamedFile = new RenamedBookFile
            {
                PreviousPath = oldFolder + "/book.m4b",
                BookFile = new BookFile { Id = 42, Path = newFolder + "/book.m4b" }
            };

            service.MoveFilesAfterRename(
                new Author { Id = 1 },
                new List<BookFile> { renamedFile.BookFile },
                new List<RenamedBookFile> { renamedFile });

            Assert.That(disk.Moves, Is.Empty);
        }

        [Test]
        public void should_not_overwrite_an_existing_extra_file_at_the_destination()
        {
            const string oldFolder = AuthorFolder + "/Old Book";
            const string newFolder = AuthorFolder + "/New Book";
            var disk = new FakeDisk();
            disk.Files.Add(oldFolder + "/notes.nfo");
            disk.Files.Add(newFolder + "/notes.nfo");
            disk.Files.Add(newFolder + "/book.m4b");
            var service = CreateOtherExtraService(disk, moveExtraFilesOnRename: true);
            var renamedFile = new RenamedBookFile
            {
                PreviousPath = oldFolder + "/book.m4b",
                BookFile = new BookFile { Id = 43, Path = newFolder + "/book.m4b" }
            };

            service.MoveFilesAfterRename(
                new Author { Id = 1 },
                new List<BookFile> { renamedFile.BookFile },
                new List<RenamedBookFile> { renamedFile });

            Assert.That(disk.Moves, Is.Empty);
        }

        [Test]
        public void should_not_move_untracked_extra_files_when_the_setting_is_disabled()
        {
            const string oldFolder = AuthorFolder + "/Old Book";
            const string newFolder = AuthorFolder + "/New Book";
            var disk = new FakeDisk();
            disk.Files.Add(oldFolder + "/notes.nfo");
            disk.Files.Add(newFolder + "/book.m4b");
            var service = CreateOtherExtraService(disk, moveExtraFilesOnRename: false);
            var renamedFile = new RenamedBookFile
            {
                PreviousPath = oldFolder + "/book.m4b",
                BookFile = new BookFile { Id = 44, Path = newFolder + "/book.m4b" }
            };

            service.MoveFilesAfterRename(
                new Author { Id = 1 },
                new List<BookFile> { renamedFile.BookFile },
                new List<RenamedBookFile> { renamedFile });

            Assert.That(disk.Moves, Is.Empty);
        }

        [Test]
        public void should_only_move_extensions_in_the_configured_list()
        {
            const string oldFolder = AuthorFolder + "/Old Book";
            const string newFolder = AuthorFolder + "/New Book";
            var disk = new FakeDisk();
            disk.Files.Add(oldFolder + "/cover.jpg");
            disk.Files.Add(newFolder + "/book.m4b");
            var service = CreateOtherExtraService(disk, moveExtraFilesOnRename: true);
            var renamedFile = new RenamedBookFile
            {
                PreviousPath = oldFolder + "/book.m4b",
                BookFile = new BookFile { Id = 45, Path = newFolder + "/book.m4b" }
            };

            service.MoveFilesAfterRename(
                new Author { Id = 1 },
                new List<BookFile> { renamedFile.BookFile },
                new List<RenamedBookFile> { renamedFile });

            Assert.That(disk.Moves, Is.Empty);
        }

        [Test]
        public void should_not_move_extras_when_a_source_folder_maps_to_multiple_destinations()
        {
            const string oldFolder = AuthorFolder + "/Old Book";
            const string firstNewFolder = AuthorFolder + "/New Book 1";
            const string secondNewFolder = AuthorFolder + "/New Book 2";
            var disk = new FakeDisk();
            disk.Files.Add(oldFolder + "/notes.nfo");
            disk.Files.Add(firstNewFolder + "/book.m4b");
            disk.Files.Add(secondNewFolder + "/book.m4b");
            var service = CreateOtherExtraService(disk, moveExtraFilesOnRename: true);
            var renamedFiles = new List<RenamedBookFile>
            {
                new RenamedBookFile
                {
                    PreviousPath = oldFolder + "/book1.m4b",
                    BookFile = new BookFile { Id = 46, Path = firstNewFolder + "/book1.m4b" }
                },
                new RenamedBookFile
                {
                    PreviousPath = oldFolder + "/book2.m4b",
                    BookFile = new BookFile { Id = 47, Path = secondNewFolder + "/book2.m4b" }
                }
            };

            service.MoveFilesAfterRename(
                new Author { Id = 1 },
                renamedFiles.Select(file => file.BookFile).ToList(),
                renamedFiles);

            Assert.That(disk.Moves, Is.Empty);
        }

        [Test]
        public void should_leave_extras_when_a_subfolder_remains_in_the_source_folder()
        {
            const string oldFolder = AuthorFolder + "/Old Book";
            const string newFolder = AuthorFolder + "/New Book";
            var disk = new FakeDisk();
            disk.Files.Add(oldFolder + "/notes.nfo");
            disk.Files.Add(newFolder + "/book.m4b");
            disk.Directories.Add(oldFolder + "/Disc 1");
            var service = CreateOtherExtraService(disk, moveExtraFilesOnRename: true);
            var renamedFile = new RenamedBookFile
            {
                PreviousPath = oldFolder + "/book.m4b",
                BookFile = new BookFile { Id = 48, Path = newFolder + "/book.m4b" }
            };

            service.MoveFilesAfterRename(
                new Author { Id = 1 },
                new List<BookFile> { renamedFile.BookFile },
                new List<RenamedBookFile> { renamedFile });

            Assert.That(disk.Moves, Is.Empty);
        }

        private static OtherExtraService CreateOtherExtraService(FakeDisk disk, bool moveExtraFilesOnRename)
        {
            var config = DispatchProxy.Create<IConfigService, ConfigProxy>();
            ((ConfigProxy)(object)config).MoveExtraFilesOnRename = moveExtraFilesOnRename;
            ((ConfigProxy)(object)config).ExtraFileExtensions = "nfo";

            return new OtherExtraService(config, disk.Proxy, null, new FakeOtherExtraFileService().Proxy, null, LogManager.GetCurrentClassLogger());
        }

        private sealed class FakeDisk
        {
            public HashSet<string> Files { get; } = new();
            public HashSet<string> Directories { get; } = new();
            public List<string> Moves { get; } = new();

            public IDiskProvider Proxy
            {
                get
                {
                    var proxy = DispatchProxy.Create<IDiskProvider, DiskProxy>();
                    ((DiskProxy)(object)proxy).Disk = this;
                    return proxy;
                }
            }
        }

        private class DiskProxy : DispatchProxy
        {
            public FakeDisk Disk { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IDiskProvider.FileExists):
                        return Disk.Files.Contains(NormalizePath((string)args[0]));
                    case nameof(IDiskProvider.FolderExists):
                        var folder = NormalizePath((string)args[0]);
                        return Disk.Files.Any(path => string.Equals(NormalizePath(Path.GetDirectoryName(path)), folder, StringComparison.OrdinalIgnoreCase));
                    case nameof(IDiskProvider.GetFiles):
                        var directory = NormalizePath((string)args[0]);
                        return Disk.Files.Where(path => string.Equals(NormalizePath(Path.GetDirectoryName(path)), directory, StringComparison.OrdinalIgnoreCase)).ToList();
                    case nameof(IDiskProvider.GetDirectories):
                        var parentDirectory = NormalizePath((string)args[0]);
                        return Disk.Directories
                            .Where(path => string.Equals(NormalizePath(Path.GetDirectoryName(path)), parentDirectory, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    case nameof(IDiskProvider.MoveFile):
                        var source = NormalizePath((string)args[0]);
                        if (!Disk.Files.Remove(source))
                        {
                            throw new System.IO.FileNotFoundException("File doesn't exist: " + source);
                        }

                        var destination = NormalizePath((string)args[1]);
                        Disk.Files.Add(destination);
                        Disk.Moves.Add($"{source} -> {destination}");
                        return null;
                    default:
                        throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
                }
            }
        }

        private class ConfigProxy : DispatchProxy
        {
            public bool MoveExtraFilesOnRename { get; set; }
            public string ExtraFileExtensions { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_MoveExtraFilesOnRename" => MoveExtraFilesOnRename,
                    "get_ExtraFileExtensions" => ExtraFileExtensions,
                    _ => throw new NotImplementedException($"Test proxy does not implement IConfigService.{targetMethod?.Name}")
                };
            }
        }

        private sealed class FakeOtherExtraFileService
        {
            public List<OtherExtraFile> Files { get; } = new();
            public List<OtherExtraFile> Upserted { get; } = new();

            public IOtherExtraFileService Proxy
            {
                get
                {
                    var proxy = DispatchProxy.Create<IOtherExtraFileService, ExtraFileServiceProxy>();
                    ((ExtraFileServiceProxy)(object)proxy).Service = this;
                    return proxy;
                }
            }
        }

        private class ExtraFileServiceProxy : DispatchProxy
        {
            public FakeOtherExtraFileService Service { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IOtherExtraFileService.GetFilesByAuthor):
                        return Service.Files;
                    case nameof(IOtherExtraFileService.Upsert) when args[0] is List<OtherExtraFile> files:
                        Service.Upserted.AddRange(files);
                        return null;
                    default:
                        throw new NotImplementedException($"Test proxy does not implement IOtherExtraFileService.{targetMethod?.Name}");
                }
            }
        }
    }
}

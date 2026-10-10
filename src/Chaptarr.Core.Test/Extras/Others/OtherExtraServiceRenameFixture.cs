using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Extras.Others
{
    [TestFixture]
    public class OtherExtraServiceRenameFixture
    {
        private const string AuthorFolder = "/library/Zogarth";
        private const string NewBookFolder = AuthorFolder + "/The Primal Hunter/01 - Primal Hunter 1";

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

            subject.MoveFilesAfterRename(author, new List<BookFile> { bookFile });

            Assert.Multiple(() =>
            {
                Assert.That(disk.Moves, Is.EqualTo(new[] { $"{NewBookFolder}/Primal Hunter 1.jpg -> {NewBookFolder}/Primal Hunter 1 - Tome 1.jpg" }));
                Assert.That(extraFiles.Upserted.Single().RelativePath, Is.EqualTo("The Primal Hunter/01 - Primal Hunter 1/Primal Hunter 1 - Tome 1.jpg"));
            });
        }

        private sealed class FakeDisk
        {
            public HashSet<string> Files { get; } = new();
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
                        return Disk.Files.Contains((string)args[0]);
                    case nameof(IDiskProvider.MoveFile):
                        var source = (string)args[0];
                        if (!Disk.Files.Remove(source))
                        {
                            throw new System.IO.FileNotFoundException("File doesn't exist: " + source);
                        }

                        Disk.Files.Add((string)args[1]);
                        Disk.Moves.Add($"{source} -> {args[1]}");
                        return null;
                    default:
                        throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
                }
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

using System.Collections.Generic;
using System.IO;
using Chaptarr.Api.V1.BookFiles;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class BookFileControllerDownloadFixture
    {
        private class FilesProxy : DispatchProxy
        {
            public Dictionary<int, BookFile> Files { get; } = new Dictionary<int, BookFile>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileService.Get))
                {
                    return Files.TryGetValue((int)args[0], out var file) ? file : null;
                }

                throw new NotImplementedException($"IMediaFileService.{targetMethod?.Name}");
            }
        }

        private class RootsProxy : DispatchProxy
        {
            public string Root { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.GetBestRootFolder) && args.Length == 1)
                {
                    var path = (string)args[0];
                    return path.StartsWith(Root + Path.DirectorySeparatorChar) ? new RootFolder { Path = Root } : null;
                }

                throw new NotImplementedException($"IRootFolderService.{targetMethod?.Name}");
            }
        }

        private string _tempDir;
        private string _root;
        private string _outside;
        private IMediaFileService _files;
        private IRootFolderService _roots;
        private FilesProxy _filesProxy;
        private RootsProxy _rootsProxy;
        private BookFileController _subject;

        [SetUp]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "bookfile-download-" + Path.GetRandomFileName());
            _root = Path.Combine(_tempDir, "library");
            _outside = Path.Combine(_tempDir, "elsewhere");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_outside);

            _files = DispatchProxy.Create<IMediaFileService, FilesProxy>();
            _filesProxy = (FilesProxy)(object)_files;
            _roots = DispatchProxy.Create<IRootFolderService, RootsProxy>();
            _rootsProxy = (RootsProxy)(object)_roots;
            _rootsProxy.Root = _root;

            _subject = new BookFileController(null, _files, null, null, null, null, null, _roots, LogManager.GetCurrentClassLogger());
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_tempDir, true);
        }

        private string Seed(int id, string path, string content = "data")
        {
            File.WriteAllText(path, content);
            _filesProxy.Files[id] = new BookFile { Id = id, Path = path };
            return path;
        }

        [Test]
        public void should_return_not_found_when_file_record_is_missing()
        {
            Assert.That(_subject.DownloadBookFile(5), Is.TypeOf<NotFoundResult>());
        }

        [Test]
        public void should_return_not_found_when_file_is_gone_from_disk()
        {
            var path = Path.Combine(_root, "gone.epub");
            _filesProxy.Files[6] = new BookFile { Id = 6, Path = path };

            Assert.That(_subject.DownloadBookFile(6), Is.TypeOf<NotFoundResult>());
        }

        [Test]
        public void should_forbid_file_outside_root_folders()
        {
            Seed(7, Path.Combine(_outside, "secret.epub"));

            var result = _subject.DownloadBookFile(7);

            Assert.That(result, Is.TypeOf<StatusCodeResult>().With.Property("StatusCode").EqualTo(403));
        }

        [Test]
        public void should_forbid_path_that_traverses_out_of_root()
        {
            Seed(8, Path.Combine(_outside, "secret.epub"));
            var traversal = Path.Combine(_root, "..", "elsewhere", "secret.epub");
            _filesProxy.Files[8] = new BookFile { Id = 8, Path = traversal };

            Assert.That(_subject.DownloadBookFile(8), Is.TypeOf<StatusCodeResult>());
        }

        [Test]
        public void should_forbid_link_pointing_outside_root()
        {
            var target = Seed(9, Path.Combine(_outside, "secret.epub"));
            var link = Path.Combine(_root, "link.epub");

            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (IOException)
            {
                Assert.Ignore("Symbolic links are not available");
            }

            _filesProxy.Files[9] = new BookFile { Id = 9, Path = link };

            Assert.That(_subject.DownloadBookFile(9), Is.TypeOf<StatusCodeResult>());
        }

        [Test]
        public void should_forbid_file_reached_through_linked_parent_directory()
        {
            Seed(11, Path.Combine(_outside, "secret.pdf"));
            var linkedDir = Path.Combine(_root, "linked");

            try
            {
                Directory.CreateSymbolicLink(linkedDir, _outside);
            }
            catch (IOException)
            {
                Assert.Ignore("Symbolic links are not available");
            }

            _filesProxy.Files[11] = new BookFile { Id = 11, Path = Path.Combine(linkedDir, "secret.pdf") };

            Assert.That(_subject.DownloadBookFile(11), Is.TypeOf<StatusCodeResult>());
        }

        [Test]
        public void should_return_attachment_for_file_inside_root()
        {
            var path = Seed(10, Path.Combine(_root, "book.pdf"));

            var result = _subject.DownloadBookFile(10) as PhysicalFileResult;

            Assert.That(result, Is.Not.Null);
            // The served path is symlink-resolved, which differs from the seeded path when the temp directory is itself a link.
            Assert.That(Path.GetFileName(result.FileName), Is.EqualTo("book.pdf"));
            Assert.That(File.ReadAllBytes(result.FileName), Is.EqualTo(File.ReadAllBytes(path)));
            Assert.That(result.FileDownloadName, Is.EqualTo("book.pdf"));
            Assert.That(result.ContentType, Is.EqualTo("application/pdf"));
            Assert.That(result.EnableRangeProcessing, Is.True);
        }
    }
}

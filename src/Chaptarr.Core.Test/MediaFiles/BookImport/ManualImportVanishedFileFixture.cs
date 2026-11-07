using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ManualImportVanishedFileFixture
    {
        private const string Folder = "/library/Author/Book";
        private const string PresentPath = Folder + "/Disc 1.mp3";
        private const string VanishedPath = Folder + "/Disc 2.mp3";

        private class FileInfoProxy : DispatchProxy
        {
            public string Path { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                var vanished = Path == VanishedPath;

                return targetMethod?.Name switch
                {
                    "get_FullName" => Path,
                    "get_Exists" => !vanished,
                    // Like System.IO.FileInfo, Length throws when the file is gone.
                    "get_Length" => vanished ? throw new FileNotFoundException("Could not find file", Path) : 1024L,
                    "get_Extension" => System.IO.Path.GetExtension(Path),
                    "get_Name" => System.IO.Path.GetFileName(Path),
                    _ => throw new NotImplementedException($"IFileInfo.{targetMethod?.Name}")
                };
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                var path = args?.FirstOrDefault() as string;
                return targetMethod?.Name switch
                {
                    nameof(IDiskProvider.FolderExists) => true,
                    nameof(IDiskProvider.FileExists) => true,
                    nameof(IDiskProvider.GetFileInfo) => CreateFileInfo(path),
                    _ => throw new NotImplementedException($"IDiskProvider.{targetMethod?.Name}")
                };
            }
        }

        private class ImportDecisionMakerProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMakeImportDecision.GetImportDecisions) &&
                    args?[0] is List<IFileInfo> files)
                {
                    // SimpleImportDecisionMaker turns a per-file failure (such as a file that vanished before its
                    // size was read) into a rejected decision that only carries the path.
                    return files.Select(file => file.FullName == VanishedPath
                        ? new ImportDecision<LocalBook>(
                            new LocalBook { Path = file.FullName },
                            new Rejection("Failed to process file: Could not find file"))
                        : new ImportDecision<LocalBook>(new LocalBook
                        {
                            Path = file.FullName,
                            Quality = new QualityModel(Quality.MP3)
                        })).ToList();
                }

                throw new NotImplementedException($"IMakeImportDecision.{targetMethod?.Name}");
            }
        }

        [Test]
        public void preview_should_return_a_rejected_item_instead_of_failing_when_a_file_vanished()
        {
            var service = CreateService();

            var result = service.GetMediaFiles(
                Folder,
                null,
                new Author { Id = 1, Name = "Author" },
                FilterFilesType.Matched,
                false,
                CancellationToken.None,
                new[] { PresentPath, VanishedPath });

            Assert.That(result.Select(item => item.Path), Is.EquivalentTo(new[] { PresentPath, VanishedPath }));

            var present = result.Single(item => item.Path == PresentPath);
            var vanished = result.Single(item => item.Path == VanishedPath);

            Assert.That(present.Size, Is.EqualTo(1024L));
            Assert.That(vanished.Size, Is.Zero);
            Assert.That(vanished.Rejections, Is.Not.Empty, "a vanished file must not be importable");
        }

        private static ManualImportService CreateService()
        {
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var decisionMaker = DispatchProxy.Create<IMakeImportDecision, ImportDecisionMakerProxy>();

            return new ManualImportService(
                diskProvider,
                null,
                null,
                null,
                decisionMaker,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                LogManager.GetCurrentClassLogger());
        }

        private static IFileInfo CreateFileInfo(string path)
        {
            var fileInfo = DispatchProxy.Create<IFileInfo, FileInfoProxy>();
            ((FileInfoProxy)(object)fileInfo).Path = path;
            return fileInfo;
        }
    }
}

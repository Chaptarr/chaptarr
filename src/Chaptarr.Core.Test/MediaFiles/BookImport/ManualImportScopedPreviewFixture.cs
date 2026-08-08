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
    public class ManualImportScopedPreviewFixture
    {
        private class FileInfoProxy : DispatchProxy
        {
            public string Path { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_FullName" => Path,
                    "get_Exists" => true,
                    "get_Length" => 1024L,
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

        private class DiskScanServiceProxy : DispatchProxy
        {
            public int Calls { get; private set; }
            public IFileInfo[] Files { get; set; } = Array.Empty<IFileInfo>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskScanService.GetBookFiles))
                {
                    Calls++;
                    return Files;
                }

                throw new NotImplementedException($"IDiskScanService.{targetMethod?.Name}");
            }
        }

        private class ImportDecisionMakerProxy : DispatchProxy
        {
            public List<string> Paths { get; private set; } = new List<string>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMakeImportDecision.GetImportDecisions) &&
                    args?[0] is List<IFileInfo> files)
                {
                    Paths = files.Select(file => file.FullName).ToList();
                    return files.Select(file => new ImportDecision<LocalBook>(new LocalBook
                    {
                        Path = file.FullName,
                        Quality = new QualityModel(Quality.MP3)
                    })).ToList();
                }

                throw new NotImplementedException($"IMakeImportDecision.{targetMethod?.Name}");
            }
        }

        [Test]
        public void row_scoped_preview_should_match_only_requested_book_unit_files()
        {
            var folder = "/library/Author/Book";
            var requested = new[]
            {
                $"{folder}/Disc 1.mp3",
                $"{folder}/Disc 2.mp3"
            };
            var unrelated = $"{folder}/Different Book.mp3";
            var (service, diskScan, decisionMaker) = CreateService(unrelated);

            var result = service.GetMediaFiles(
                folder,
                null,
                new Author { Id = 1, Name = "Author" },
                FilterFilesType.Matched,
                false,
                CancellationToken.None,
                requested);

            Assert.That(diskScan.Calls, Is.Zero, "an exact row scope must not enumerate the folder");
            Assert.That(decisionMaker.Paths, Is.EquivalentTo(requested));
            Assert.That(decisionMaker.Paths, Does.Not.Contain(unrelated));
            Assert.That(result.Select(item => item.Path), Is.EquivalentTo(requested));
        }

        [Test]
        public void exact_scope_should_allow_files_in_authoritative_descendant_disc_folders()
        {
            var folder = "/library/Author/Book";
            var requested = new[]
            {
                $"{folder}/CD1/01.mp3",
                $"{folder}/Disc 2/02.mp3"
            };
            var unrelated = $"{folder}/Other/Unrelated.mp3";
            var (service, diskScan, decisionMaker) = CreateService(unrelated);

            var result = service.GetMediaFiles(
                folder,
                null,
                new Author { Id = 1, Name = "Author" },
                FilterFilesType.Matched,
                false,
                CancellationToken.None,
                requested);

            Assert.That(diskScan.Calls, Is.Zero, "an exact descendant scope must not enumerate the folder");
            Assert.That(decisionMaker.Paths, Is.EquivalentTo(requested));
            Assert.That(decisionMaker.Paths, Does.Not.Contain(unrelated));
            Assert.That(result.Select(item => item.Path), Is.EquivalentTo(requested));
        }

        [Test]
        public void stale_empty_row_scope_should_not_expand_back_to_the_whole_folder()
        {
            var folder = "/library/Author/Book";
            var unrelated = $"{folder}/Different Book.mp3";
            var (service, diskScan, decisionMaker) = CreateService(unrelated);

            var result = service.GetMediaFiles(
                folder,
                null,
                new Author { Id = 1, Name = "Author" },
                FilterFilesType.Matched,
                false,
                CancellationToken.None,
                Array.Empty<string>());

            Assert.That(diskScan.Calls, Is.Zero, "an empty exact scope is still an exact scope");
            Assert.That(decisionMaker.Paths, Is.Empty);
            Assert.That(result, Is.Empty);
        }

        private static (ManualImportService Service, DiskScanServiceProxy DiskScan, ImportDecisionMakerProxy DecisionMaker) CreateService(string unrelatedPath)
        {
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskScanService = DispatchProxy.Create<IDiskScanService, DiskScanServiceProxy>();
            var diskScanProxy = (DiskScanServiceProxy)(object)diskScanService;
            diskScanProxy.Files = new[] { CreateFileInfo(unrelatedPath) };
            var decisionMaker = DispatchProxy.Create<IMakeImportDecision, ImportDecisionMakerProxy>();
            var decisionMakerProxy = (ImportDecisionMakerProxy)(object)decisionMaker;

            var service = new ManualImportService(
                diskProvider,
                null,
                null,
                diskScanService,
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

            return (service, diskScanProxy, decisionMakerProxy);
        }

        private static IFileInfo CreateFileInfo(string path)
        {
            var fileInfo = DispatchProxy.Create<IFileInfo, FileInfoProxy>();
            ((FileInfoProxy)(object)fileInfo).Path = path;
            return fileInfo;
        }
    }
}

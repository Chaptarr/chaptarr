using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ManualImportExecuteVanishedFileFixture
    {
        private const string Folder = "/library/Author/Book";
        private const string VanishedPath = Folder + "/Disc 1.mp3";
        private const string PresentPath = Folder + "/Disc 2.mp3";

        private static List<ImportDecision<LocalBook>> _importedDecisions;

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
                    "get_LastWriteTimeUtc" => DateTime.UtcNow,
                    "get_Extension" => System.IO.Path.GetExtension(Path),
                    "get_Name" => System.IO.Path.GetFileName(Path),
                    _ => throw new NotImplementedException($"IFileInfo.{targetMethod?.Name}")
                };
            }
        }

        // Returns default values for everything the test does not care about.
        private class DefaultProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                var returnType = targetMethod?.ReturnType;
                return returnType != null && returnType != typeof(void) && returnType.IsValueType
                    ? Activator.CreateInstance(returnType)
                    : null;
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

        private class ImportApprovedBooksProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IImportApprovedBooks.Import))
                {
                    _importedDecisions = (List<ImportDecision<LocalBook>>)args[0];
                    return new List<ImportResult>();
                }

                throw new NotImplementedException($"IImportApprovedBooks.{targetMethod?.Name}");
            }
        }

        [SetUp]
        public void SetUp()
        {
            _importedDecisions = null;
        }

        [Test]
        public void execute_should_reject_a_vanished_file_and_still_process_the_rest_of_the_batch()
        {
            var service = CreateService();

            Assert.DoesNotThrow(() => service.Execute(new ManualImportCommand
            {
                ImportMode = ImportMode.Copy,
                Files = new List<ManualImportFile>
                {
                    new ManualImportFile { Path = VanishedPath, Quality = new QualityModel(Quality.MP3) },
                    new ManualImportFile { Path = PresentPath, Quality = new QualityModel(Quality.MP3) }
                }
            }));

            Assert.That(_importedDecisions, Is.Not.Null, "the batch must reach the import step");
            Assert.That(_importedDecisions.Select(d => d.Item.Path), Is.EquivalentTo(new[] { VanishedPath, PresentPath }));

            var vanished = _importedDecisions.Single(d => d.Item.Path == VanishedPath);
            Assert.That(vanished.Approved, Is.False, "a vanished file must not be importable");
            Assert.That(vanished.Rejections.Select(r => r.Reason), Has.Some.Contains("no longer exists"));
            Assert.That(vanished.Item.Size, Is.Zero);

            Assert.That(_importedDecisions.Single(d => d.Item.Path == PresentPath).Item.Size, Is.EqualTo(1024L));
        }

        private static ManualImportService CreateService()
        {
            return new ManualImportService(
                DispatchProxy.Create<IDiskProvider, DiskProviderProxy>(),
                null,
                DispatchProxy.Create<NzbDrone.Core.RootFolders.IRootFolderService, DefaultProxy>(),
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
                DispatchProxy.Create<IImportApprovedBooks, ImportApprovedBooksProxy>(),
                null,
                DispatchProxy.Create<ITrackedDownloadService, DefaultProxy>(),
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

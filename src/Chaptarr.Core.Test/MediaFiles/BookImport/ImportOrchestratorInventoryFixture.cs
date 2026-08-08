using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ImportOrchestratorInventoryFixture
    {
        private class DiskProviderProxy : DispatchProxy
        {
            public string RootPath { get; set; }
            public string FilePath { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IDiskProvider.FolderExists) => string.Equals((string)args[0], RootPath, StringComparison.Ordinal),
                    nameof(IDiskProvider.GetDirectories) => Array.Empty<string>(),
                    nameof(IDiskProvider.GetFiles) => new[] { FilePath },
                    nameof(IDiskProvider.GetFileInfo) => CreateFileInfo(FilePath),
                    nameof(IDiskProvider.FileExists) => string.Equals((string)args[0], FilePath, StringComparison.Ordinal),
                    _ => GetDefaultValue(targetMethod?.ReturnType)
                };
            }

            private static IFileInfo CreateFileInfo(string path)
            {
                var fileInfo = DispatchProxy.Create<IFileInfo, FileInfoProxy>();
                var proxy = (FileInfoProxy)(object)fileInfo;
                proxy.FullName = path;
                proxy.LastWriteTimeUtc = new DateTime(2026, 7, 11, 1, 0, 0, DateTimeKind.Utc);
                return fileInfo;
            }
        }

        private class FileInfoProxy : DispatchProxy
        {
            public string FullName { get; set; }
            public DateTime LastWriteTimeUtc { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_Exists" => true,
                    "get_Length" => 100L,
                    "get_LastWriteTimeUtc" => LastWriteTimeUtc,
                    "get_LastWriteTime" => LastWriteTimeUtc.ToLocalTime(),
                    "get_FullName" => FullName,
                    "get_Name" => Path.GetFileName(FullName),
                    "get_Extension" => Path.GetExtension(FullName),
                    _ => GetDefaultValue(targetMethod?.ReturnType)
                };
            }
        }

        private class MediaFileRepositoryProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IMediaFileRepository.GetFilesWithBasePath) => new List<BookFile>(),
                    nameof(IMediaFileRepository.GetReplicaPathsWithBasePath) => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    _ => GetDefaultValue(targetMethod?.ReturnType)
                };
            }
        }

        private class IngestQueueRepositoryProxy : DispatchProxy
        {
            public List<IngestQueueItem> Inserted { get; } = new List<IngestQueueItem>();
            public List<List<string>> FailedRequeueCalls { get; } = new List<List<string>>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IIngestQueueRepository.InsertBatch))
                {
                    Inserted.AddRange((List<IngestQueueItem>)args[0]);
                    return null;
                }

                if (targetMethod?.Name == nameof(IIngestQueueRepository.RequeueFailedPaths))
                {
                    var paths = ((IEnumerable<string>)args[0]).ToList();
                    FailedRequeueCalls.Add(paths);
                    return paths.Count;
                }

                return GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        [Test]
#pragma warning disable SYSLIB0050
        public async Task stage_should_inventory_but_not_match_a_root_type_mismatched_media_file()
        {
            const string rootPath = "/books";
            const string filePath = "/books/misplaced.m4b";
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.RootPath = rootPath;
            diskProxy.FilePath = filePath;

            var mediaFiles = DispatchProxy.Create<IMediaFileRepository, MediaFileRepositoryProxy>();
            var ingestQueue = DispatchProxy.Create<IIngestQueueRepository, IngestQueueRepositoryProxy>();
            var ingestProxy = (IngestQueueRepositoryProxy)(object)ingestQueue;
            var sut = (ImportOrchestratorV2)FormatterServices.GetUninitializedObject(typeof(ImportOrchestratorV2));
            SetField(sut, "_diskProvider", diskProvider);
            SetField(sut, "_mediaFileRepository", mediaFiles);
            SetField(sut, "_ingestQueue", ingestQueue);
            SetField(sut, "_logger", LogManager.GetCurrentClassLogger());

            var method = typeof(ImportOrchestratorV2).GetMethod("StageFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var task = (Task)method.Invoke(sut, new object[]
            {
                rootPath,
                new RootFolder { Path = rootPath, FolderType = FolderType.Ebook },
                null,
                null,
                FilterFilesType.Known
            });
            await task;

            var stageResult = task.GetType().GetProperty("Result")?.GetValue(task);
            Assert.That(stageResult, Is.Not.Null);
            var seenPaths = (IEnumerable<string>)stageResult.GetType().GetProperty("SeenFilePaths")?.GetValue(stageResult);
            var stagedCount = (int)stageResult.GetType().GetProperty("StagedCount")?.GetValue(stageResult);

            Assert.That(seenPaths, Is.EqualTo(new[] { filePath }));
            Assert.That(stagedCount, Is.EqualTo(0), "Root-type mismatches must stay visible but must not enter matching/import");
            Assert.That(ingestProxy.Inserted, Is.Empty);
        }
#pragma warning restore SYSLIB0050

        [Test]
#pragma warning disable SYSLIB0050
        public void scheduled_root_scan_should_requeue_only_observed_type_eligible_failed_paths()
        {
            var ingestQueue = DispatchProxy.Create<IIngestQueueRepository, IngestQueueRepositoryProxy>();
            var ingestProxy = (IngestQueueRepositoryProxy)(object)ingestQueue;
            var sut = (ImportOrchestratorV2)FormatterServices.GetUninitializedObject(typeof(ImportOrchestratorV2));
            SetField(sut, "_ingestQueue", ingestQueue);

            var method = typeof(ImportOrchestratorV2).GetMethod(
                "RequeueObservedFailuresForScheduledRootScan",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var requeued = (int)method.Invoke(sut, new object[]
            {
                false,
                new RootFolder { Path = "/books", FolderType = FolderType.Ebook },
                new[] { "/books/eligible.epub", "/books/misplaced.m4b" }
            });

            Assert.That(requeued, Is.EqualTo(1));
            Assert.That(ingestProxy.FailedRequeueCalls, Has.Count.EqualTo(1));
            Assert.That(ingestProxy.FailedRequeueCalls[0], Is.EqualTo(new[] { "/books/eligible.epub" }));

            var manualRequeued = (int)method.Invoke(sut, new object[]
            {
                true,
                new RootFolder { Path = "/books", FolderType = FolderType.Ebook },
                new[] { "/books/eligible.epub" }
            });

            Assert.That(manualRequeued, Is.EqualTo(0));
            Assert.That(ingestProxy.FailedRequeueCalls, Has.Count.EqualTo(1), "Manual scans use the existing Failed+Unmapped path-wide requeue");
        }
#pragma warning restore SYSLIB0050

        private static void SetField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Unable to locate private field {fieldName}");
            field.SetValue(target, value);
        }

        private static object GetDefaultValue(Type type)
        {
            if (type == null || type == typeof(void))
            {
                return null;
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}

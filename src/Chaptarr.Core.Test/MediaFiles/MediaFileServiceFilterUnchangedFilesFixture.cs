using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MediaFileServiceFilterUnchangedFilesFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        private class MediaFileRepositoryProxy : DispatchProxy
        {
            public Func<List<string>, List<BookFile>> GetFileWithPathHandler { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileRepository.GetFileWithPath) &&
                    args?.Length == 1 &&
                    args[0] is List<string> paths)
                {
                    return GetFileWithPathHandler?.Invoke(paths) ?? new List<BookFile>();
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IMediaFileRepository).Name}.{targetMethod?.Name}");
            }
        }

        private class IngestQueueRepositoryProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IIngestQueueRepository.PurgeUnderPath))
                {
                    return 0;
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IIngestQueueRepository).Name}.{targetMethod?.Name}");
            }
        }

        [Test]
        public void matched_filter_should_reconsider_files_when_timestamps_drift()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"mediafiles_filter_{Guid.NewGuid():N}");
            var authorDir = Path.Combine(root, "books", "Andrzej Sapkowski");

            Directory.CreateDirectory(authorDir);

            var mappedPath = Path.Combine(authorDir, "Mapped.epub");
            var unmappedPath = Path.Combine(authorDir, "Unmapped.epub");

            try
            {
                File.WriteAllText(mappedPath, "mapped");
                File.WriteAllText(unmappedPath, "unmapped");

                var fileSystem = new FileSystem();

                // Simulate file-date/retag style timestamp drift
                fileSystem.File.SetLastWriteTimeUtc(mappedPath, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                fileSystem.File.SetLastWriteTimeUtc(unmappedPath, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                var mappedDisk = fileSystem.FileInfo.New(mappedPath);
                var unmappedDisk = fileSystem.FileInfo.New(unmappedPath);

                var known = new Dictionary<string, BookFile>(PathEqualityComparer.Instance)
                {
                    {
                        mappedPath,
                        new BookFile
                        {
                            Path = mappedPath,
                            Size = mappedDisk.Length,
                            Modified = DateTime.UtcNow,
                            EditionId = 42
                        }
                    },
                    {
                        unmappedPath,
                        new BookFile
                        {
                            Path = unmappedPath,
                            Size = unmappedDisk.Length,
                            Modified = DateTime.UtcNow,
                            EditionId = 0
                        }
                    }
                };

                var repo = DispatchProxy.Create<IMediaFileRepository, MediaFileRepositoryProxy>();
                var repoProxy = (MediaFileRepositoryProxy)(object)repo;
                repoProxy.GetFileWithPathHandler = paths =>
                {
                    return paths.Select(p => known.TryGetValue(p, out var file) ? file : null).Where(f => f != null).ToList();
                };

                var ingestQueue = DispatchProxy.Create<IIngestQueueRepository, IngestQueueRepositoryProxy>();
                var sut = new MediaFileService(repo, new StubEventAggregator(), ingestQueue, LogManager.GetLogger("test"));

                var result = sut.FilterUnchangedFiles(new List<IFileInfo> { mappedDisk, unmappedDisk }, FilterFilesType.Matched);
                var resultPaths = result.Select(f => f.FullName).ToList();

                Assert.That(resultPaths, Does.Contain(unmappedPath));
                Assert.That(resultPaths, Does.Contain(mappedPath), "a changed mapped file must be reconsidered during a scan");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [Test]
        public void matched_filter_should_skip_unchanged_mapped_or_successfully_searched_files()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"mediafiles_filter_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var mappedPath = Path.Combine(root, "Mapped.epub");
            var searchedPath = Path.Combine(root, "Searched.epub");
            var unsoughtPath = Path.Combine(root, "Unsought.epub");

            try
            {
                File.WriteAllText(mappedPath, "mapped");
                File.WriteAllText(searchedPath, "searched");
                File.WriteAllText(unsoughtPath, "unsought");

                var modified = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
                foreach (var path in new[] { mappedPath, searchedPath, unsoughtPath })
                {
                    File.SetLastWriteTimeUtc(path, modified);
                }

                var fileSystem = new FileSystem();
                var diskFiles = new[]
                {
                    fileSystem.FileInfo.New(mappedPath),
                    fileSystem.FileInfo.New(searchedPath),
                    fileSystem.FileInfo.New(unsoughtPath)
                };
                var known = new Dictionary<string, BookFile>(PathEqualityComparer.Instance)
                {
                    [mappedPath] = new BookFile { Path = mappedPath, Size = diskFiles[0].Length, Modified = modified, EditionId = 42 },
                    [searchedPath] = new BookFile { Path = searchedPath, Size = diskFiles[1].Length, Modified = modified, EditionId = 0, LastRemoteSearchTime = modified },
                    [unsoughtPath] = new BookFile { Path = unsoughtPath, Size = diskFiles[2].Length, Modified = modified, EditionId = 0 }
                };

                var repo = DispatchProxy.Create<IMediaFileRepository, MediaFileRepositoryProxy>();
                ((MediaFileRepositoryProxy)(object)repo).GetFileWithPathHandler = paths =>
                    paths.Select(path => known.TryGetValue(path, out var file) ? file : null).Where(file => file != null).ToList();
                var ingestQueue = DispatchProxy.Create<IIngestQueueRepository, IngestQueueRepositoryProxy>();
                var sut = new MediaFileService(repo, new StubEventAggregator(), ingestQueue, LogManager.GetLogger("test"));

                var resultPaths = sut.FilterUnchangedFiles(diskFiles.ToList(), FilterFilesType.Matched)
                    .Select(file => file.FullName)
                    .ToList();

                Assert.That(resultPaths, Is.EquivalentTo(new[] { unsoughtPath }));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }
    }
}

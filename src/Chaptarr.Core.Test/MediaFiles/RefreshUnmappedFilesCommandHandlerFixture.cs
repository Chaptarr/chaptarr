using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class RefreshUnmappedFilesCommandHandlerFixture
    {
        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<ImportStageProgressEvent> ProgressEvents { get; } = new List<ImportStageProgressEvent>();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                if (@event is ImportStageProgressEvent progress)
                {
                    ProgressEvents.Add(progress);
                }
            }
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            public List<BookFile> UnmappedFiles { get; set; } = new List<BookFile>();
            public List<BookFile> Updated { get; } = new List<BookFile>();
            public List<BookFile> Deleted { get; } = new List<BookFile>();
            public DeleteMediaFileReason? DeleteReason { get; private set; }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile)
            {
                Updated.Add(bookFile);
            }

            public void Update(List<BookFile> bookFiles)
            {
                Updated.AddRange(bookFiles ?? new List<BookFile>());
            }

            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason)
            {
                Deleted.AddRange(bookFiles ?? new List<BookFile>());
                DeleteReason = reason;
            }

            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles() => UnmappedFiles.Where(file => file.EditionId == 0).ToList();
            public List<BookFile> GetUnmappedFiles(string mediaType) => GetUnmappedFiles()
                .Where(file => string.IsNullOrWhiteSpace(mediaType) || string.Equals(file.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            public List<BookFile> GetUnmappedFiles(IEnumerable<int> ids, string mediaType)
            {
                var requested = ids?.ToHashSet() ?? new HashSet<int>();
                return GetUnmappedFiles(mediaType).Where(file => requested.Contains(file.Id)).ToList();
            }

            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public BookFile GetFileWithPath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> path) => throw new NotImplementedException();
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private sealed class StubMetadataTagService : IMetadataTagService
        {
            public Dictionary<string, List<string>> Tags { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public int? DurationSeconds { get; set; }
            public List<string> ReadAllTagsAndDurationPaths { get; } = new List<string>();

            public Dictionary<string, List<string>> ReadAllTags(IFileInfo file) => Tags;
            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(IFileInfo file)
            {
                ReadAllTagsAndDurationPaths.Add(file.FullName);
                return (Tags, DurationSeconds);
            }

            public string ReadAllTagsAsJson(IFileInfo file) => "{}";
            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false) => throw new NotImplementedException();
            public void SyncTags(List<Edition> books) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int authorId) => throw new NotImplementedException();
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public Dictionary<string, (bool Exists, long Length, DateTime LastWriteUtc)> Files { get; } =
                new Dictionary<string, (bool, long, DateTime)>(StringComparer.OrdinalIgnoreCase);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.GetFileInfo))
                {
                    var path = (string)args[0];
                    Files.TryGetValue(path, out var info);
                    var fileInfo = DispatchProxy.Create<IFileInfo, FileInfoProxy>();
                    var proxy = (FileInfoProxy)(object)fileInfo;
                    proxy.FullName = path;
                    proxy.ExistsResult = info.Exists;
                    proxy.Length = info.Length;
                    proxy.LastWriteUtc = info.LastWriteUtc;
                    return fileInfo;
                }

                return GetDefaultValue(targetMethod?.ReturnType);
            }
        }

        private class FileInfoProxy : DispatchProxy
        {
            public string FullName { get; set; }
            public bool ExistsResult { get; set; }
            public long Length { get; set; }
            public DateTime LastWriteUtc { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_Exists" => ExistsResult,
                    "get_Length" => Length,
                    "get_LastWriteTimeUtc" => LastWriteUtc,
                    "get_LastWriteTime" => LastWriteUtc.ToLocalTime(),
                    "get_FullName" => FullName,
                    "get_Name" => System.IO.Path.GetFileName(FullName),
                    "get_Extension" => System.IO.Path.GetExtension(FullName),
                    _ => GetDefaultValue(targetMethod?.ReturnType)
                };
            }
        }

        [Test]
        public void refresh_files_uses_stored_evidence_without_rereading_fresh_rows()
        {
            var modified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var file = CreateAudioFile(1, "/books/one.mp3", modified, 100, "Stored Title", 321);
            var context = CreateContext(new[] { file }, current => (true, current.Size, current.Modified));

            context.Sut.Execute(new RefreshUnmappedFilesCommand
            {
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Metadata.ReadAllTagsAndDurationPaths, Is.Empty);
            Assert.That(context.Media.Updated, Is.Empty);
            Assert.That(context.Media.Deleted, Is.Empty);
        }

        [Test]
        public void refresh_files_refreshes_changed_rows_and_persists_evidence()
        {
            var storedModified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var diskModified = storedModified.AddMinutes(5);
            var file = CreateAudioFile(1, "/books/one.mp3", storedModified, 100, "Stored Title", 321);
            var context = CreateContext(new[] { file }, _ => (true, 200, diskModified));
            context.Metadata.Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Fresh Title" }
            };
            context.Metadata.DurationSeconds = 999;

            context.Sut.Execute(new RefreshUnmappedFilesCommand
            {
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Metadata.ReadAllTagsAndDurationPaths, Is.EqualTo(new[] { "/books/one.mp3" }));
            Assert.That(context.Media.Updated, Is.EqualTo(new[] { file }));
            Assert.That(file.Size, Is.EqualTo(200));
            Assert.That(file.Modified, Is.EqualTo(diskModified));
            Assert.That(file.DurationSeconds, Is.EqualTo(999));
            Assert.That(file.AllTags["TITLE"], Is.EqualTo(new[] { "Fresh Title" }));
            Assert.That(context.Media.Deleted, Is.Empty);
        }

        [Test]
        public void refresh_files_backfills_quality_and_media_type_without_rereading_fresh_rows()
        {
            var modified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var file = CreateAudioFile(1, "/books/one.mp3", modified, 100, "Stored Title", 321);
            file.Quality = null;
            file.MediaType = null;
            var context = CreateContext(new[] { file }, current => (true, current.Size, current.Modified));

            context.Sut.Execute(new RefreshUnmappedFilesCommand
            {
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Metadata.ReadAllTagsAndDurationPaths, Is.Empty);
            Assert.That(context.Media.Updated, Is.EqualTo(new[] { file }));
            Assert.That(file.Quality, Is.Not.Null);
            Assert.That(file.MediaType, Is.EqualTo("audiobook"));
            Assert.That(context.Media.Deleted, Is.Empty);
        }

        [Test]
        public void refresh_files_preserves_unmapped_rows_whose_files_are_not_visible()
        {
            var modified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var file = CreateAudioFile(1, "/books/missing.mp3", modified, 100, "Stored Title", 321);
            var context = CreateContext(new[] { file }, _ => (false, 0, default));

            context.Sut.Execute(new RefreshUnmappedFilesCommand
            {
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Metadata.ReadAllTagsAndDurationPaths, Is.Empty);
            Assert.That(context.Media.Updated, Is.Empty);
            Assert.That(context.Media.Deleted, Is.Empty);
            Assert.That(context.Media.UnmappedFiles, Does.Contain(file));
        }

        [Test]
        public void refresh_files_honors_selected_scope_and_media_type()
        {
            var modified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var audio = CreateAudioFile(1, "/books/audio.mp3", modified, 100, null, null);
            var ebook = CreateEbookFile(2, "/books/ebook.epub", modified, 100, null);
            var context = CreateContext(new[] { audio, ebook }, current => (true, current.Size + 1, modified));
            context.Metadata.Tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Fresh Audio" }
            };
            context.Metadata.DurationSeconds = 111;

            context.Sut.Execute(new RefreshUnmappedFilesCommand
            {
                MediaType = "audiobook",
                UnmappedFiles = new UnmappedFilesSelection
                {
                    Scope = "selected",
                    BookFileIds = new List<int> { 1, 2 }
                }
            });

            Assert.That(context.Metadata.ReadAllTagsAndDurationPaths, Is.EqualTo(new[] { "/books/audio.mp3" }));
            Assert.That(context.Media.Updated.Select(file => file.Id), Is.EqualTo(new[] { 1 }));
            Assert.That(context.Media.Deleted, Is.Empty);
        }

        [Test]
        public void refresh_files_publishes_file_progress_for_the_drawer()
        {
            var modified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var files = new[]
            {
                CreateAudioFile(1, "/books/one.mp3", modified, 100, "One", 100),
                CreateAudioFile(2, "/books/two.mp3", modified, 100, "Two", 100)
            };
            var context = CreateContext(files, current => (true, current.Size, current.Modified));

            context.Sut.Execute(new RefreshUnmappedFilesCommand
            {
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Events.ProgressEvents.First().CurrentProgress, Is.EqualTo(0));
            Assert.That(context.Events.ProgressEvents.Last().CurrentProgress, Is.EqualTo(2));
            Assert.That(context.Events.ProgressEvents.All(progress => progress.TotalProgress == 2), Is.True);
            Assert.That(context.Events.ProgressEvents.Any(progress => progress.CurrentItemName == "one.mp3" && progress.CurrentItemType == "file"), Is.True);
            Assert.That(context.Events.ProgressEvents.Any(progress => progress.CurrentItemName == "two.mp3" && progress.CurrentItemType == "file"), Is.True);
        }

        [Test]
        public void refresh_files_should_bound_progress_events_for_large_selections()
        {
            const int total = 10000;
            var modified = new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc);
            var files = Enumerable.Range(1, total)
                .Select(id => CreateAudioFile(id, $"/books/{id}.mp3", modified, 100, id.ToString(), 100))
                .ToList();
            var context = CreateContext(files, current => (true, current.Size, current.Modified));

            context.Sut.Execute(new RefreshUnmappedFilesCommand
            {
                UnmappedFiles = new UnmappedFilesSelection { Scope = "all" }
            });

            Assert.That(context.Events.ProgressEvents.Count, Is.LessThanOrEqualTo(103));
            Assert.That(context.Events.ProgressEvents.First().CurrentProgress, Is.EqualTo(0));
            Assert.That(context.Events.ProgressEvents.Any(progress => progress.CurrentProgress == 1), Is.True);
            Assert.That(context.Events.ProgressEvents.Last().CurrentProgress, Is.EqualTo(total));
        }

        private static BookFile CreateAudioFile(int id, string path, DateTime modified, long size, string title, int? durationSeconds)
        {
            return CreateFile(id, path, "audiobook", Quality.MP3, modified, size, title, durationSeconds);
        }

        private static BookFile CreateEbookFile(int id, string path, DateTime modified, long size, string title)
        {
            return CreateFile(id, path, "ebook", Quality.EPUB, modified, size, title, null);
        }

        private static BookFile CreateFile(int id, string path, string mediaType, Quality quality, DateTime modified, long size, string title, int? durationSeconds)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(title))
            {
                tags["TITLE"] = new List<string> { title };
            }

            return new BookFile
            {
                Id = id,
                Path = path,
                EditionId = 0,
                MediaType = mediaType,
                Size = size,
                Modified = modified,
                AllTags = tags,
                DurationSeconds = durationSeconds,
                Quality = new QualityModel { Quality = quality }
            };
        }

        private static TestContext CreateContext(IEnumerable<BookFile> files, Func<BookFile, (bool Exists, long Size, DateTime Modified)> diskInfo)
        {
            var fileList = files.ToList();
            var media = new StubMediaFileService { UnmappedFiles = fileList };
            var metadata = new StubMetadataTagService();
            var disk = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)disk;
            var events = new RecordingEventAggregator();

            foreach (var file in fileList)
            {
                var info = diskInfo(file);
                diskProxy.Files[file.Path] = (info.Exists, info.Size, info.Modified);
            }

            var sut = new RefreshUnmappedFilesCommandHandler(
                media,
                metadata,
                disk,
                events,
                LogManager.GetCurrentClassLogger());

            return new TestContext(sut, media, metadata, events);
        }

        private static object GetDefaultValue(Type type)
        {
            if (type == null || type == typeof(void))
            {
                return null;
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private sealed class TestContext
        {
            public TestContext(RefreshUnmappedFilesCommandHandler sut, StubMediaFileService media, StubMetadataTagService metadata, RecordingEventAggregator events)
            {
                Sut = sut;
                Media = media;
                Metadata = metadata;
                Events = events;
            }

            public RefreshUnmappedFilesCommandHandler Sut { get; }
            public StubMediaFileService Media { get; }
            public StubMetadataTagService Metadata { get; }
            public RecordingEventAggregator Events { get; }
        }
    }
}

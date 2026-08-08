using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.TagExtraction;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MetadataTagServiceFixture
    {
        private sealed class CountingAudioTagService : IAudioTagService
        {
            public int ReadAllTagsCalls { get; private set; }
            public int ReadAllTagsAndDurationCalls { get; private set; }

            public Dictionary<string, List<string>> ReadAllTags(string file)
            {
                ReadAllTagsCalls++;
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "title", new List<string> { "Test Audio" } }
                };
            }

            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(string file)
            {
                ReadAllTagsAndDurationCalls++;
                return (new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "title", new List<string> { "Test Audio" } }
                }, 123);
            }

            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false) => throw new NotImplementedException();
            public void SyncTags(List<Edition> tracks) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId) => throw new NotImplementedException();
            public void RetagFiles(RetagFilesCommand message) => throw new NotImplementedException();
            public void RetagAuthor(RetagAuthorCommand message) => throw new NotImplementedException();
        }

        private sealed class ResultAudioTagService : IAudioTagService
        {
            private readonly Dictionary<string, List<string>> _tags;
            private readonly int? _durationSeconds;
            private readonly Exception _error;

            public ResultAudioTagService(Dictionary<string, List<string>> tags, int? durationSeconds = null, Exception error = null)
            {
                _tags = tags;
                _durationSeconds = durationSeconds;
                _error = error;
            }

            public int ReadCalls { get; private set; }

            public Dictionary<string, List<string>> ReadAllTags(string file)
            {
                return ReadAllTagsAndDuration(file).Tags;
            }

            public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(string file)
            {
                ReadCalls++;
                if (_error != null)
                {
                    throw _error;
                }

                return (_tags, _durationSeconds);
            }

            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false) => throw new NotImplementedException();
            public void SyncTags(List<Edition> tracks) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId) => throw new NotImplementedException();
            public void RetagFiles(RetagFilesCommand message) => throw new NotImplementedException();
            public void RetagAuthor(RetagAuthorCommand message) => throw new NotImplementedException();
        }

        private sealed class CountingEBookTagService : IEBookTagService
        {
            public int ReadAllTagsCalls { get; private set; }

            public Dictionary<string, List<string>> ReadAllTags(IFileInfo file)
            {
                ReadAllTagsCalls++;
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "title", new List<string> { "Test Ebook" } }
                };
            }

            public void WriteTags(BookFile trackfile, bool newDownload, bool force = false) => throw new NotImplementedException();
            public void SyncTags(List<Edition> books) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId) => throw new NotImplementedException();
            public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId) => throw new NotImplementedException();
            public void RetagFiles(RetagFilesCommand message) => throw new NotImplementedException();
            public void RetagAuthor(RetagAuthorCommand message) => throw new NotImplementedException();
        }

        private sealed class RecordingFileTagCacheRepository : IFileTagCacheRepository
        {
            private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

            public int UpsertCalls { get; private set; }
            public string LastExtractionStatus { get; private set; }

            public bool TryGet(string path, long mtimeNs, long sizeBytes, out string tagsJson, out int? durationSeconds, out string extractionStatus)
            {
                tagsJson = null;
                durationSeconds = null;
                extractionStatus = null;

                if (!_entries.TryGetValue(path, out var entry) ||
                    entry.MtimeNs != mtimeNs ||
                    entry.SizeBytes != sizeBytes)
                {
                    return false;
                }

                tagsJson = entry.TagsJson;
                durationSeconds = entry.DurationSeconds;
                extractionStatus = entry.ExtractionStatus;
                return true;
            }

            public void Upsert(string path, long mtimeNs, long sizeBytes, string tagsJson, int? durationSeconds, string extractionStatus)
            {
                UpsertCalls++;
                LastExtractionStatus = extractionStatus;
                _entries[path] = new CacheEntry
                {
                    MtimeNs = mtimeNs,
                    SizeBytes = sizeBytes,
                    TagsJson = tagsJson,
                    DurationSeconds = durationSeconds,
                    ExtractionStatus = extractionStatus
                };
            }

            public void PurgeOld(int daysToKeep = 30)
            {
            }

            public void SeedLegacy(string path, long mtimeNs, long sizeBytes, string tagsJson, int? durationSeconds)
            {
                _entries[path] = new CacheEntry
                {
                    MtimeNs = mtimeNs,
                    SizeBytes = sizeBytes,
                    TagsJson = tagsJson,
                    DurationSeconds = durationSeconds,
                    ExtractionStatus = null
                };
            }

            private sealed class CacheEntry
            {
                public long MtimeNs { get; set; }
                public long SizeBytes { get; set; }
                public string TagsJson { get; set; }
                public int? DurationSeconds { get; set; }
                public string ExtractionStatus { get; set; }
            }
        }

        [Test]
        public void should_cache_ebook_tag_extractions_for_unchanged_file()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metatag_cache_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "test.pdf");
            File.WriteAllText(path, "hello");

            try
            {
                var fileSystem = new FileSystem();
                var ebookTagService = new CountingEBookTagService();
                var audioTagService = new CountingAudioTagService();
                var sut = new MetadataTagService(audioTagService, ebookTagService, LogManager.GetLogger("test"));

                var first = sut.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(path));
                var second = sut.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(path));

                Assert.That(first.Tags, Is.Not.Null);
                Assert.That(second.Tags, Is.Not.Null);
                Assert.That(ebookTagService.ReadAllTagsCalls, Is.EqualTo(1));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void readalltags_should_use_cached_extraction_when_available()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metatag_cache_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "test.pdf");
            File.WriteAllText(path, "hello");

            try
            {
                var fileSystem = new FileSystem();
                var ebookTagService = new CountingEBookTagService();
                var audioTagService = new CountingAudioTagService();
                var sut = new MetadataTagService(audioTagService, ebookTagService, LogManager.GetLogger("test"));

                _ = sut.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(path));
                _ = sut.ReadAllTags(fileSystem.FileInfo.FromFileName(path));

                Assert.That(ebookTagService.ReadAllTagsCalls, Is.EqualTo(1));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void readalltags_should_cache_ebook_tag_extractions_for_unchanged_file()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metatag_cache_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "test.pdf");
            File.WriteAllText(path, "hello");

            try
            {
                var fileSystem = new FileSystem();
                var ebookTagService = new CountingEBookTagService();
                var audioTagService = new CountingAudioTagService();
                var sut = new MetadataTagService(audioTagService, ebookTagService, LogManager.GetLogger("test"));

                _ = sut.ReadAllTags(fileSystem.FileInfo.FromFileName(path));
                _ = sut.ReadAllTags(fileSystem.FileInfo.FromFileName(path));

                Assert.That(ebookTagService.ReadAllTagsCalls, Is.EqualTo(1));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void readalltags_should_seed_cache_for_readalltagsandduration_on_ebooks()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metatag_cache_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "test.pdf");
            File.WriteAllText(path, "hello");

            try
            {
                var fileSystem = new FileSystem();
                var ebookTagService = new CountingEBookTagService();
                var audioTagService = new CountingAudioTagService();
                var sut = new MetadataTagService(audioTagService, ebookTagService, LogManager.GetLogger("test"));

                _ = sut.ReadAllTags(fileSystem.FileInfo.FromFileName(path));
                _ = sut.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(path));

                Assert.That(ebookTagService.ReadAllTagsCalls, Is.EqualTo(1));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void should_cache_audio_tag_extractions_for_unchanged_file()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metatag_cache_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "test.mp3");
            File.WriteAllText(path, "not really mp3");

            try
            {
                var fileSystem = new FileSystem();
                var ebookTagService = new CountingEBookTagService();
                var audioTagService = new CountingAudioTagService();
                var sut = new MetadataTagService(audioTagService, ebookTagService, LogManager.GetLogger("test"));

                _ = sut.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(path));
                _ = sut.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(path));

                Assert.That(audioTagService.ReadAllTagsAndDurationCalls, Is.EqualTo(1));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void should_use_persisted_cache_across_service_instances_for_unchanged_file()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metatag_persisted_cache_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "test.pdf");
            File.WriteAllText(path, "hello");

            try
            {
                var fileSystem = new FileSystem();
                var fileTagCache = new RecordingFileTagCacheRepository();

                var firstEbookTagService = new CountingEBookTagService();
                var first = new MetadataTagService(
                    new CountingAudioTagService(),
                    firstEbookTagService,
                    LogManager.GetLogger("test"),
                    fileTagCache);

                var firstResult = first.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(path));

                var secondEbookTagService = new CountingEBookTagService();
                var second = new MetadataTagService(
                    new CountingAudioTagService(),
                    secondEbookTagService,
                    LogManager.GetLogger("test"),
                    fileTagCache);

                var secondResult = second.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(path));

                Assert.That(firstResult.Tags["title"], Is.EquivalentTo(new[] { "Test Ebook" }));
                Assert.That(secondResult.Tags["title"], Is.EquivalentTo(new[] { "Test Ebook" }));
                Assert.That(firstEbookTagService.ReadAllTagsCalls, Is.EqualTo(1));
                Assert.That(secondEbookTagService.ReadAllTagsCalls, Is.EqualTo(0));
                Assert.That(fileTagCache.UpsertCalls, Is.EqualTo(1));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void should_ignore_persisted_cache_when_file_identity_changes()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metatag_stale_cache_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "test.pdf");
            File.WriteAllText(path, "hello");

            try
            {
                var fileSystem = new FileSystem();
                var fileTagCache = new RecordingFileTagCacheRepository();

                var firstEbookTagService = new CountingEBookTagService();
                var first = new MetadataTagService(
                    new CountingAudioTagService(),
                    firstEbookTagService,
                    LogManager.GetLogger("test"),
                    fileTagCache);

                _ = first.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(path));

                File.WriteAllText(path, "hello changed");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

                var secondEbookTagService = new CountingEBookTagService();
                var second = new MetadataTagService(
                    new CountingAudioTagService(),
                    secondEbookTagService,
                    LogManager.GetLogger("test"),
                    fileTagCache);

                _ = second.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(path));

                Assert.That(firstEbookTagService.ReadAllTagsCalls, Is.EqualTo(1));
                Assert.That(secondEbookTagService.ReadAllTagsCalls, Is.EqualTo(1));
                Assert.That(fileTagCache.UpsertCalls, Is.EqualTo(2));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void should_use_persisted_cache_across_service_instances_for_audio_duration()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metatag_persisted_audio_cache_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "test.mp3");
            File.WriteAllText(path, "not really mp3");

            try
            {
                var fileSystem = new FileSystem();
                var fileTagCache = new RecordingFileTagCacheRepository();

                var firstAudioTagService = new CountingAudioTagService();
                var first = new MetadataTagService(
                    firstAudioTagService,
                    new CountingEBookTagService(),
                    LogManager.GetLogger("test"),
                    fileTagCache);

                var firstResult = first.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(path));

                var secondAudioTagService = new CountingAudioTagService();
                var second = new MetadataTagService(
                    secondAudioTagService,
                    new CountingEBookTagService(),
                    LogManager.GetLogger("test"),
                    fileTagCache);

                var secondResult = second.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(path));

                Assert.That(firstResult.DurationSeconds, Is.EqualTo(123));
                Assert.That(secondResult.DurationSeconds, Is.EqualTo(123));
                Assert.That(firstAudioTagService.ReadAllTagsAndDurationCalls, Is.EqualTo(1));
                Assert.That(secondAudioTagService.ReadAllTagsAndDurationCalls, Is.EqualTo(0));
                Assert.That(fileTagCache.UpsertCalls, Is.EqualTo(1));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void readalltags_should_not_seed_audio_duration_cache()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metatag_audio_tags_only_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "test.mp3");
            File.WriteAllText(path, "not really mp3");

            try
            {
                var fileSystem = new FileSystem();
                var fileTagCache = new RecordingFileTagCacheRepository();

                var firstAudioTagService = new CountingAudioTagService();
                var first = new MetadataTagService(
                    firstAudioTagService,
                    new CountingEBookTagService(),
                    LogManager.GetLogger("test"),
                    fileTagCache);

                _ = first.ReadAllTags(fileSystem.FileInfo.FromFileName(path));

                var secondAudioTagService = new CountingAudioTagService();
                var second = new MetadataTagService(
                    secondAudioTagService,
                    new CountingEBookTagService(),
                    LogManager.GetLogger("test"),
                    fileTagCache);

                var secondResult = second.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(path));

                Assert.That(secondResult.DurationSeconds, Is.EqualTo(123));
                Assert.That(firstAudioTagService.ReadAllTagsCalls, Is.EqualTo(1));
                Assert.That(firstAudioTagService.ReadAllTagsAndDurationCalls, Is.EqualTo(0));
                Assert.That(secondAudioTagService.ReadAllTagsAndDurationCalls, Is.EqualTo(1));
                Assert.That(fileTagCache.UpsertCalls, Is.EqualTo(1));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void should_persist_noisy_only_and_tagless_as_distinct_successful_outcomes()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metatag_dispositions_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var noisyPath = Path.Combine(root, "noisy.m4b");
            var taglessPath = Path.Combine(root, "tagless.m4b");
            File.WriteAllText(noisyPath, "audio");
            File.WriteAllText(taglessPath, "audio");

            try
            {
                var fileSystem = new FileSystem();
                var noisyCache = new RecordingFileTagCacheRepository();
                var noisyAudio = new ResultAudioTagService(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["comment"] = new List<string> { "Promotional description" }
                }, 10);
                var noisyService = new MetadataTagService(noisyAudio, new CountingEBookTagService(), LogManager.GetLogger("test"), noisyCache);

                _ = noisyService.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(noisyPath));

                Assert.That(noisyCache.LastExtractionStatus, Is.EqualTo("noisy_only"));

                var taglessCache = new RecordingFileTagCacheRepository();
                var taglessAudio = new ResultAudioTagService(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase), 10);
                var taglessService = new MetadataTagService(taglessAudio, new CountingEBookTagService(), LogManager.GetLogger("test"), taglessCache);

                _ = taglessService.ReadAllTagsAndDuration(fileSystem.FileInfo.FromFileName(taglessPath));

                Assert.That(taglessCache.LastExtractionStatus, Is.EqualTo("tagless"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void should_not_cache_total_extraction_failure()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metatag_failure_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "failed.m4b");
            File.WriteAllText(path, "audio");

            try
            {
                var cache = new RecordingFileTagCacheRepository();
                var audio = new ResultAudioTagService(
                    null,
                    error: new TagExtractionException(path, new IOException("transient read failure")));
                var service = new MetadataTagService(audio, new CountingEBookTagService(), LogManager.GetLogger("test"), cache);

                Assert.Throws<TagExtractionException>(() =>
                    service.ReadAllTagsAndDuration(new FileSystem().FileInfo.FromFileName(path)));
                Assert.That(cache.UpsertCalls, Is.Zero);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void should_reextract_legacy_empty_cache_row_once_instead_of_trusting_it_as_tagless()
        {
            const long unixEpochTicks = 621355968000000000L;
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"metatag_legacy_empty_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "legacy.m4b");
            File.WriteAllText(path, "audio");

            try
            {
                var file = new FileSystem().FileInfo.FromFileName(path);
                var cache = new RecordingFileTagCacheRepository();
                var mtimeNs = Math.Max(0, file.LastWriteTimeUtc.Ticks - unixEpochTicks) * 100;
                cache.SeedLegacy(path, mtimeNs, file.Length, "{}", null);

                var audio = new ResultAudioTagService(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["title"] = new List<string> { "Recovered Title" }
                }, 20);
                var service = new MetadataTagService(audio, new CountingEBookTagService(), LogManager.GetLogger("test"), cache);

                var result = service.ReadAllTagsAndDuration(file);

                Assert.That(result.Tags["title"], Is.EquivalentTo(new[] { "Recovered Title" }));
                Assert.That(audio.ReadCalls, Is.EqualTo(1));
                Assert.That(cache.UpsertCalls, Is.EqualTo(1));
                Assert.That(cache.LastExtractionStatus, Is.EqualTo("evidence"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

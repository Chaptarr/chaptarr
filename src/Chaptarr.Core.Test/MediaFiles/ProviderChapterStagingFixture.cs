using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ProviderChapterStagingFixture
    {
        private string _root;
        private string[] _inputs;
        private string _output;
        private string _metadata;
        private M4bConversionService _subject;
        private ConversionOptions _options;
        private CancellationTokenSource _cancellation;
        private Action _afterChapterWrite;
        private int _toolCalls;
        private int _audioStagingCalls;
        private IReadOnlyList<string> _arguments;

        public class CallbackProxy : DispatchProxy
        {
            public Func<MethodInfo, object[], object> Call { get; set; }
            protected override object Invoke(MethodInfo targetMethod, object[] args) => Call(targetMethod, args);
        }

        private static T Proxy<T>(Func<MethodInfo, object[], object> call) where T : class
        {
            var proxy = DispatchProxy.Create<T, CallbackProxy>();
            ((CallbackProxy)(object)proxy).Call = call;
            return proxy;
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "chaptarr-provider-chapters-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _inputs = new[] { Path.Combine(_root, "part2.mp3"), Path.Combine(_root, "part1.mp3") };
            foreach (var input in _inputs) File.WriteAllBytes(input, Enumerable.Repeat((byte)37, 32 * 1024).ToArray());
            var work = Path.Combine(_root, ".chaptarr-conversions", "download", "attempt");
            Directory.CreateDirectory(work);
            _metadata = Path.Combine(work, "chapter-metadata");
            _output = Path.Combine(work, "book.m4b");
            _cancellation = new CancellationTokenSource();
            _options = new ConversionOptions
            {
                TempDirectory = work,
                SkipCover = true,
                ExpectedSourceDuration = TimeSpan.FromMinutes(20),
                CancellationToken = _cancellation.Token,
                TagOptions = new ConversionTagOptions { ChaptersTxtContent = "00:00:00.000 Opening\n00:10:00.000 Closing\n", ProviderChapterCount = 2 }
            };
            _afterChapterWrite = null;
            _toolCalls = _audioStagingCalls = 0;
            _arguments = null;
            var disk = Proxy<IDiskProvider>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IDiskProvider.FileExists): return File.Exists((string)args[0]);
                    case nameof(IDiskProvider.FolderExists): return Directory.Exists((string)args[0]);
                    case nameof(IDiskProvider.GetFileSize): return new FileInfo((string)args[0]).Length;
                    case nameof(IDiskProvider.CreateFolder):
                        Directory.CreateDirectory((string)args[0]);
                        return null;
                    case nameof(IDiskProvider.WriteAllText):
                        File.WriteAllText((string)args[0], (string)args[1]);
                        _afterChapterWrite?.Invoke();
                        return null;
                    default:
                        _audioStagingCalls++;
                        throw new AssertionException("Unexpected disk operation: " + method.Name);
                }
            });
            var tools = Proxy<IExternalToolsService>((method, args) =>
            {
                if (method.Name.StartsWith("Is", StringComparison.Ordinal)) return true;
                if (method.Name == nameof(IExternalToolsService.GetFFmpegPath)) return "ffmpeg";
                if (method.Name == nameof(IExternalToolsService.ExecuteM4bToolDetailed))
                {
                    _toolCalls++;
                    _arguments = (IReadOnlyList<string>)args[0];
                    Assert.That((CancellationToken)args[3], Is.EqualTo(_options.CancellationToken));
                    File.WriteAllBytes(_output, new byte[32 * 1024]);
                    return new ExternalToolResult { ExitCode = 0 };
                }

                if (method.Name == nameof(IExternalToolsService.ExecuteFFprobe))
                {
                    var arguments = (IReadOnlyList<string>)args[0];
                    if (arguments.Any(value => value.Contains("stream_disposition"))) return "audio,0\n";
                    return arguments.Contains("stream=codec_type") ? "audio\n" : "1200";
                }

                throw new AssertionException("Unexpected tool call: " + method.Name);
            });
            var logger = LogManager.GetLogger(nameof(ProviderChapterStagingFixture));
            _subject = new M4bConversionService(tools, disk, logger);
        }

        [TearDown]
        public void TearDown()
        {
            _cancellation?.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void should_pass_chapter_only_directory_then_original_audio_in_order_without_links_copies_or_extra_space_checks()
        {
            var result = _subject.ConvertToM4b(_inputs, _output, _options);
            Assert.Multiple(() =>
            {
                Assert.That(result.Success, Is.True, result.ErrorMessage);
                Assert.That(_arguments.Take(4), Is.EqualTo(new[] { "merge", _metadata, _inputs[0], _inputs[1] }));
                Assert.That(Directory.GetFileSystemEntries(_metadata), Is.EqualTo(new[] { Path.Combine(_metadata, "chapters.txt") }));
                Assert.That(File.ReadAllText(Path.Combine(_metadata, "chapters.txt")), Is.EqualTo(_options.TagOptions.ChaptersTxtContent));
                Assert.That(_options.TagOptions.ProviderChapterCount, Is.EqualTo(2));
                Assert.That(_audioStagingCalls, Is.Zero);
                Assert.That(_toolCalls, Is.EqualTo(1));
                foreach (var source in _inputs) Assert.That(File.ReadAllBytes(source), Is.All.EqualTo((byte)37));
            });
        }

        [Test]
        public void should_pass_original_inputs_without_metadata_directory_when_no_provider_chapters()
        {
            _options.TagOptions = null;
            var result = _subject.ConvertToM4b(_inputs, _output, _options);
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(_arguments.Take(3), Is.EqualTo(new[] { "merge", _inputs[0], _inputs[1] }));
            Assert.That(Directory.Exists(_metadata), Is.False);
            Assert.That(_audioStagingCalls, Is.Zero);
        }

        [Test]
        public void should_report_chapter_write_failure_without_dropping_chapters_or_starting_conversion()
        {
            Directory.CreateDirectory(Path.Combine(_metadata, "chapters.txt"));
            var result = _subject.ConvertToM4b(_inputs, _output, _options);
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("Unable to prepare provider chapters"));
            Assert.That(_toolCalls, Is.Zero);
            Assert.That(_options.TagOptions.ProviderChapterCount, Is.EqualTo(2));
            Assert.That(_options.TagOptions.ChaptersTxtContent, Does.Contain("Opening"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_cancel_before_converter_starts(bool afterWrite)
        {
            if (afterWrite) _afterChapterWrite = _cancellation.Cancel;
            else _cancellation.Cancel();
            var result = _subject.ConvertToM4b(_inputs, _output, _options);
            Assert.That(result.FailureCategory, Is.EqualTo(ConversionFailureCategory.Cancelled), result.ErrorMessage);
            Assert.That(_audioStagingCalls, Is.Zero);
            Assert.That(_toolCalls, Is.Zero);
            Assert.That(Directory.Exists(_metadata), Is.EqualTo(afterWrite));
            Assert.That(File.Exists(_output), Is.False);
        }

        [Test]
        public void retry_should_overwrite_chapters_and_ignore_old_staged_audio()
        {
            var legacyStage = Path.Combine(_options.TempDirectory, "provider-chapters");
            Directory.CreateDirectory(legacyStage);
            File.WriteAllText(Path.Combine(legacyStage, "0001.mp3"), "old partial copy");
            Directory.CreateDirectory(_metadata);
            File.WriteAllText(Path.Combine(_metadata, "chapters.txt"), "old chapters");
            var result = _subject.ConvertToM4b(_inputs, _output, _options);
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(_arguments.Take(4), Is.EqualTo(new[] { "merge", _metadata, _inputs[0], _inputs[1] }));
            Assert.That(File.ReadAllText(Path.Combine(_metadata, "chapters.txt")), Is.EqualTo(_options.TagOptions.ChaptersTxtContent));
            Assert.That(File.ReadAllText(Path.Combine(legacyStage, "0001.mp3")), Is.EqualTo("old partial copy"));
            Assert.That(_audioStagingCalls, Is.Zero);
        }

        [Test]
        public void should_fail_explicitly_when_provider_chapters_have_no_workspace()
        {
            _options.TempDirectory = null;
            var result = _subject.ConvertToM4b(_inputs, _output, _options);
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("Conversion workspace is required"));
            Assert.That(_toolCalls, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void worker_should_persist_chapter_failure_or_cancellation_and_clean_its_workspace(bool cancel)
        {
            if (TableMapping.Mapper.TableMap.Count == 0) TableMapping.Map();
            using var timing = new ConversionJobServiceFixture.WorkerTiming();
            var repository = ConversionJobServiceFixture.CreateRepository(Path.Combine(_root, "jobs.db"));
            timing.Record("database created");
            var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            ConversionJobService jobs = null;
            var converter = Proxy<IM4bConversionService>((method, args) =>
            {
                if (method.Name == nameof(IM4bConversionService.ConvertToM4b))
                {
                    timing.Record("converter entered");
                    started.SetResult(jobs.Completion.Workers);
                }
                Assert.That(method.Name, Is.EqualTo(nameof(IM4bConversionService.ConvertToM4b)));
                try { return _subject.ConvertToM4b((string[])args[0], (string)args[1], (ConversionOptions)args[2]); }
                finally { timing.Record("converter returned"); }
            });
            jobs = ConversionJobServiceFixture.CreateService(timing.Observe(repository), converter, logger: timing.Logger);
            var request = new ConversionJobRequest
            {
                DownloadId = "download",
                BookTitle = "Chapter test",
                WorkRoot = Directory.GetParent(_options.TempDirectory).FullName,
                WorkFolder = _options.TempDirectory,
                OutputPath = _output,
                Sources = _inputs.Select(path => new ConversionArtifactSource
                {
                    Path = path,
                    Size = new FileInfo(path).Length,
                    ModifiedUtcTicks = File.GetLastWriteTimeUtc(path).Ticks
                }).ToList(),
                TargetQualityId = 12,
                TargetQualityName = "M4B",
                ExpectedSourceDurationTicks = _options.ExpectedSourceDuration.Ticks,
                TagOptions = _options.TagOptions,
                TagSignature = "provider chapters"
            };
            if (cancel) _afterChapterWrite = () =>
            {
                timing.Record("chapter written; cancellation requested");
                Assert.That(jobs.Cancel(request.DownloadId, ConversionRemovalOrigin.UserAction), Is.True);
                timing.Record("cancellation saved");
            };
            else Directory.CreateDirectory(Path.Combine(_metadata, "chapters.txt"));
            try
            {
                jobs.Handle(new ApplicationStartedEvent());
                jobs.Enqueue(request);
                var expected = cancel ? ConversionJobStatus.Cancelled : ConversionJobStatus.Failed;
                // One existing ten-second budget covers dispatch, persistence and cleanup.
                Assert.That(started.Task.Unwrap().Wait(TimeSpan.FromSeconds(10)), Is.True, timing.Details);
                Assert.That(repository.FindByDownloadId(request.DownloadId)?.Status, Is.EqualTo(expected));
                Assert.That(Directory.Exists(request.WorkRoot), Is.False);
                Assert.That(_toolCalls, Is.Zero);
                Assert.That(File.Exists(_output), Is.False);
                if (!cancel) Assert.That(repository.FindByDownloadId(request.DownloadId).Error, Does.Contain("Unable to prepare provider chapters"));
                foreach (var source in _inputs) Assert.That(File.ReadAllBytes(source), Is.All.EqualTo((byte)37));
            }
            finally
            {
                ConversionJobServiceFixture.ShutdownAndWait(jobs);
                SqliteConnection.ClearAllPools();
            }
        }
    }
}

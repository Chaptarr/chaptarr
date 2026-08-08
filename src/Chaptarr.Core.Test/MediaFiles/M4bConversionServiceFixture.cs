using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class M4bConversionServiceFixture
    {
        private sealed class StreamingExternalToolsService : IExternalToolsService
        {
            public List<string> M4bToolArguments { get; private set; } = new();
            public int M4bToolExitCode { get; set; }
            public bool M4bToolTimedOut { get; set; }
            public bool M4bToolCancelled { get; set; }
            public string M4bToolStandardOutput { get; set; } = "conversion output";
            public string M4bToolStandardError { get; set; }
            public int LastM4bToolTimeoutMs { get; private set; }
            public Dictionary<string, TimeSpan> Durations { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AudioFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> StreamLayouts { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<IReadOnlyList<string>> FFmpegCalls { get; } = new();
            public Action<IReadOnlyList<string>> OnFFmpegExecute { get; set; }

            public string GetFFprobePath() => throw new NotImplementedException();
            public string GetFFmpegPath() => "/usr/bin/ffmpeg";
            public string GetM4bToolPath() => "/usr/local/bin/m4b-tool";
            public bool IsFFprobeAvailable() => true;
            public bool IsFFmpegAvailable() => true;
            public bool IsM4bToolAvailable() => true;
            public string ExecuteFFprobe(string arguments) => throw new NotImplementedException();
            public string ExecuteFFprobe(IReadOnlyList<string> arguments, int timeoutMs = 10000)
            {
                var path = arguments.LastOrDefault();
                if (arguments.Any(a => a.Contains("stream_disposition=attached_pic", StringComparison.Ordinal)))
                {
                    if (path != null && StreamLayouts.TryGetValue(path, out var layout))
                    {
                        return layout;
                    }

                    return path != null && AudioFiles.Contains(path) ? "audio,0\n" : string.Empty;
                }

                if (arguments.Any(a => a.Contains("stream=codec_type", StringComparison.Ordinal)))
                {
                    return path != null && AudioFiles.Contains(path) ? "audio\n" : string.Empty;
                }

                return path != null && Durations.TryGetValue(path, out var duration)
                    ? duration.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty;
            }

            public string ExecuteFFmpeg(string arguments) => throw new NotImplementedException();
            public string ExecuteFFmpeg(IReadOnlyList<string> arguments, int timeoutMs = 10000, bool preferStderrOnEmpty = false)
            {
                var copied = arguments.ToList();
                FFmpegCalls.Add(copied);
                OnFFmpegExecute?.Invoke(copied);
                return string.Empty;
            }

            public string ExecuteM4bTool(IReadOnlyList<string> arguments, int timeoutMs = 3600000, Action<string> outputHandler = null, CancellationToken cancellationToken = default)
            {
                return ExecuteM4bToolDetailed(arguments, timeoutMs, outputHandler, cancellationToken).GetPreferredOutput(preferStderrOnEmpty: true);
            }

            public ExternalToolResult ExecuteM4bToolDetailed(IReadOnlyList<string> arguments, int timeoutMs = 3600000, Action<string> outputHandler = null, CancellationToken cancellationToken = default)
            {
                M4bToolArguments = arguments.ToList();
                LastM4bToolTimeoutMs = timeoutMs;
                outputHandler?.Invoke(" 1/2 [==============>-------------] 50%\r");
                return new ExternalToolResult
                {
                    ExitCode = M4bToolExitCode,
                    TimedOut = M4bToolTimedOut,
                    Cancelled = M4bToolCancelled,
                    StandardOutput = M4bToolStandardOutput,
                    StandardError = M4bToolStandardError,
                    TimeoutMs = timeoutMs
                };
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, long> FileSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IDiskProvider.FileExists) && args.Length >= 1)
                {
                    return ExistingFiles.Contains((string)args[0]);
                }

                if (targetMethod.Name == nameof(IDiskProvider.GetFileSize) && args.Length >= 1)
                {
                    return FileSizes.TryGetValue((string)args[0], out var size) ? size : 0L;
                }

                throw new NotImplementedException($"Unexpected IDiskProvider.{targetMethod.Name}");
            }
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Unexpected {typeof(T).Name}.{targetMethod.Name}");
            }
        }

        [Test]
        public void should_stream_progress_without_quieting_m4b_tool()
        {
            var externalTools = new StreamingExternalToolsService();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");
            diskProxy.ExistingFiles.Add("/input/chapter-02.mp3");

            var progressUpdates = new List<ConversionProgressUpdate>();
            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3", "/input/chapter-02.mp3" },
                "/output/book.m4b",
                new ConversionOptions
                {
                    ChapterLength = 300,
                    FfmpegThreads = 2,
                    TempDirectory = "/output/.chaptarr-conversions/download/attempt",
                    ProgressHandler = progressUpdates.Add
                });

            Assert.That(externalTools.M4bToolArguments, Does.Not.Contain("--quiet"));
            Assert.That(externalTools.M4bToolArguments, Does.Not.Contain("--chapters-per-file=300"));
            Assert.That(externalTools.M4bToolArguments.Any(a => a.StartsWith("--ffmpeg=", StringComparison.Ordinal)), Is.False);
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--tmp-dir=/output/.chaptarr-conversions/download/attempt"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--ffmpeg-threads=2"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("-v"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--ffmpeg-param=-id3v2_version"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--ffmpeg-param=4"));
            Assert.That(progressUpdates.Select(u => u.Progress), Does.Contain(1m));
            Assert.That(progressUpdates.Select(u => u.Progress), Does.Contain(47.5m));
            Assert.That(progressUpdates.Last().Message, Is.EqualTo("Converting to M4B - 1 of 2"));
        }

        [Test]
        public void should_pass_source_preserving_tag_arguments()
        {
            var externalTools = new StreamingExternalToolsService();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");
            diskProxy.ExistingFiles.Add("/input/chapter-02.mp3");

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3", "/input/chapter-02.mp3" },
                "/output/book.m4b",
                new ConversionOptions
                {
                    TagOptions = new ConversionTagOptions
                    {
                        Name = "Harry Potter and the Goblet of Fire",
                        Album = "Harry Potter and the Goblet of Fire",
                        Artist = "J.K. Rowling",
                        AlbumArtist = "J.K. Rowling",
                        Writer = "Stephen Fry",
                        Year = "2000",
                        Genre = "Fantasy",
                        Copyright = "Pottermore Publishing",
                        Series = "Harry Potter",
                        SeriesPart = "4",
                        UseFilenamesAsChapters = true
                    }
                });

            Assert.That(externalTools.M4bToolArguments, Does.Contain("--name=Harry Potter and the Goblet of Fire"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--album=Harry Potter and the Goblet of Fire"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--artist=J.K. Rowling"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--albumartist=J.K. Rowling"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--writer=Stephen Fry"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--year=2000"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--genre=Fantasy"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--copyright=Pottermore Publishing"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--series=Harry Potter"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--series-part=4"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--use-filenames-as-chapters"));
            Assert.That(externalTools.M4bToolArguments, Does.Not.Contain("--ignore-source-tags"));
        }

        [Test]
        public void should_pass_clean_tag_arguments_with_source_tag_inheritance_disabled()
        {
            var externalTools = new StreamingExternalToolsService();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3" },
                "/output/book.m4b",
                new ConversionOptions
                {
                    TagOptions = new ConversionTagOptions
                    {
                        Name = "Harry Potter and the Goblet of Fire",
                        Comment = "Canonical book overview",
                        IgnoreSourceTags = true
                    }
                });

            Assert.That(externalTools.M4bToolArguments, Does.Contain("--name=Harry Potter and the Goblet of Fire"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--comment=Canonical book overview"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--ignore-source-tags"));
        }

        [Test]
        public void should_extract_embedded_cover_for_clean_tag_mode_without_mutating_tag_options()
        {
            var externalTools = new StreamingExternalToolsService();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");

            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "m4b-cover-test");
            var coverPath = Path.Combine(tempDir, "source-cover.jpg");
            externalTools.OnFFmpegExecute = args =>
            {
                if (args.Contains(coverPath))
                {
                    diskProxy.ExistingFiles.Add(coverPath);
                    diskProxy.FileSizes[coverPath] = 2048;
                }
            };

            var tagOptions = new ConversionTagOptions
            {
                Name = "Harry Potter and the Goblet of Fire",
                IgnoreSourceTags = true
            };

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3" },
                "/output/book.m4b",
                new ConversionOptions
                {
                    TempDirectory = tempDir,
                    TagOptions = tagOptions
                });

            Assert.That(externalTools.FFmpegCalls, Has.Count.EqualTo(1));
            Assert.That(externalTools.FFmpegCalls[0], Does.Contain("-map"));
            Assert.That(externalTools.FFmpegCalls[0], Does.Contain("0:v:0"));
            Assert.That(externalTools.M4bToolArguments, Does.Contain($"--cover={coverPath}"));
            Assert.That(tagOptions.Cover, Is.Null);
        }

        [Test]
        public void should_extract_embedded_cover_for_preserve_tag_mode_and_prefer_it_over_fallback()
        {
            var externalTools = new StreamingExternalToolsService();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");
            diskProxy.ExistingFiles.Add("/covers/db-cover.jpg");
            diskProxy.FileSizes["/covers/db-cover.jpg"] = 4096;

            var tempDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "m4b-preserve-cover-test");
            var sourceCoverPath = Path.Combine(tempDir, "source-cover.jpg");
            externalTools.OnFFmpegExecute = args =>
            {
                if (args.Contains(sourceCoverPath))
                {
                    diskProxy.ExistingFiles.Add(sourceCoverPath);
                    diskProxy.FileSizes[sourceCoverPath] = 2048;
                }
            };

            var tagOptions = new ConversionTagOptions
            {
                Name = "The Dichotomy of Leadership",
                Cover = "/covers/db-cover.jpg",
                IgnoreSourceTags = false
            };

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3" },
                "/output/book.m4b",
                new ConversionOptions
                {
                    TempDirectory = tempDir,
                    TagOptions = tagOptions
                });

            Assert.That(externalTools.FFmpegCalls, Has.Count.EqualTo(1));
            Assert.That(externalTools.M4bToolArguments, Does.Contain($"--cover={sourceCoverPath}"));
            Assert.That(externalTools.M4bToolArguments, Does.Not.Contain("--cover=/covers/db-cover.jpg"));
            Assert.That(tagOptions.Cover, Is.EqualTo("/covers/db-cover.jpg"));
        }

        [Test]
        public void should_keep_source_sidecar_cover_over_embedded_cover()
        {
            var externalTools = new StreamingExternalToolsService();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");

            var tagOptions = new ConversionTagOptions
            {
                Name = "Harry Potter and the Goblet of Fire",
                Cover = "/input/cover.jpg",
                CoverIsSource = true
            };

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3" },
                "/output/book.m4b",
                new ConversionOptions
                {
                    TempDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "m4b-sidecar-cover-test"),
                    TagOptions = tagOptions
                });

            Assert.That(externalTools.FFmpegCalls, Is.Empty);
            Assert.That(externalTools.M4bToolArguments, Does.Contain("--cover=/input/cover.jpg"));
            Assert.That(tagOptions.Cover, Is.EqualTo("/input/cover.jpg"));
        }

        [Test]
        public void should_fail_non_zero_m4b_tool_exit_even_when_readable_output_exists()
        {
            var externalTools = new StreamingExternalToolsService
            {
                M4bToolExitCode = 1,
                M4bToolStandardError = "mp4tags: command not found"
            };
            externalTools.Durations["/input/chapter-01.mp3"] = TimeSpan.FromHours(1);
            externalTools.Durations["/output/book.m4b"] = TimeSpan.FromHours(1);
            externalTools.AudioFiles.Add("/output/book.m4b");

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");
            diskProxy.ExistingFiles.Add("/output/book.m4b");
            diskProxy.FileSizes["/output/book.m4b"] = 64 * 1024 * 1024;

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            var result = subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3" },
                "/output/book.m4b",
                new ConversionOptions { ExpectedSourceDuration = TimeSpan.FromHours(1) });

            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureCategory, Is.EqualTo(ConversionFailureCategory.DependencyMissing));
            Assert.That(result.RetainOutputOnFailure, Is.True);
            Assert.That(result.ErrorMessage, Does.Contain("will not be imported"));
        }

        [Test]
        public void should_reject_successful_tool_run_when_output_duration_is_too_short()
        {
            var externalTools = new StreamingExternalToolsService();
            externalTools.Durations["/output/book.m4b"] = TimeSpan.FromMinutes(13);
            externalTools.AudioFiles.Add("/output/book.m4b");

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");
            diskProxy.ExistingFiles.Add("/output/book.m4b");
            diskProxy.FileSizes["/output/book.m4b"] = 64 * 1024 * 1024;

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            var result = subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3" },
                "/output/book.m4b",
                new ConversionOptions { ExpectedSourceDuration = TimeSpan.FromHours(13) });

            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureCategory, Is.EqualTo(ConversionFailureCategory.OutputInvalid));
            Assert.That(result.RetainOutputOnFailure, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("duration looks wrong"));
        }

        [Test]
        public void should_use_scaled_duration_tolerance_for_output_validation()
        {
            var externalTools = new StreamingExternalToolsService();
            externalTools.Durations["/output/book.m4b"] = TimeSpan.FromHours(13).Subtract(TimeSpan.FromMinutes(30));
            externalTools.AudioFiles.Add("/output/book.m4b");

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");
            diskProxy.ExistingFiles.Add("/output/book.m4b");
            diskProxy.FileSizes["/output/book.m4b"] = 64 * 1024 * 1024;

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            var result = subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3" },
                "/output/book.m4b",
                new ConversionOptions { ExpectedSourceDuration = TimeSpan.FromHours(13) });

            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureCategory, Is.EqualTo(ConversionFailureCategory.OutputInvalid));
            Assert.That(result.ErrorMessage, Does.Contain("allowed difference is 23.4m"));
        }

        [Test]
        public void should_scale_m4b_tool_timeout_from_expected_source_duration()
        {
            var externalTools = new StreamingExternalToolsService();
            externalTools.Durations["/output/book.m4b"] = TimeSpan.FromHours(13);
            externalTools.AudioFiles.Add("/output/book.m4b");

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");
            diskProxy.ExistingFiles.Add("/output/book.m4b");
            diskProxy.FileSizes["/output/book.m4b"] = 64 * 1024 * 1024;

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3" },
                "/output/book.m4b",
                new ConversionOptions { ExpectedSourceDuration = TimeSpan.FromHours(13) });

            Assert.That(externalTools.LastM4bToolTimeoutMs, Is.EqualTo((int)TimeSpan.FromHours(52.5).TotalMilliseconds));
        }

        [Test]
        public void should_classify_cancelled_conversion_without_retaining_partial_output()
        {
            var externalTools = new StreamingExternalToolsService
            {
                M4bToolCancelled = true,
                M4bToolStandardError = "cancelled"
            };

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");
            diskProxy.ExistingFiles.Add("/output/book.m4b");
            diskProxy.FileSizes["/output/book.m4b"] = 64 * 1024 * 1024;

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            var result = subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3" },
                "/output/book.m4b");

            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureCategory, Is.EqualTo(ConversionFailureCategory.Cancelled));
            Assert.That(result.RetainOutputOnFailure, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("cancelled"));
        }

        [Test]
        public void should_classify_cover_embedding_failures()
        {
            var externalTools = new StreamingExternalToolsService
            {
                M4bToolExitCode = 1,
                M4bToolStandardError = "cover.jpg: invalid image data, failed to attach cover"
            };

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            var result = subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3" },
                "/output/book.m4b");

            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureCategory, Is.EqualTo(ConversionFailureCategory.CoverEmbedding));
            Assert.That(result.ErrorMessage, Does.Contain("cover art"));
        }

        [Test]
        public void should_not_classify_verbose_chapter_output_as_tag_failure_without_error_line_context()
        {
            var externalTools = new StreamingExternalToolsService
            {
                M4bToolExitCode = 1,
                M4bToolStandardOutput = "Processing chapter 1\nWriting chapter metadata\nDone with chapter parsing",
                M4bToolStandardError = "Unexpected failure in conversion worker"
            };

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            var result = subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3" },
                "/output/book.m4b");

            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureCategory, Is.EqualTo(ConversionFailureCategory.Unknown));
        }

        [Test]
        public void should_allow_chapter_data_streams_and_attached_cover_art()
        {
            var externalTools = new StreamingExternalToolsService();
            externalTools.Durations["/output/book.m4b"] = TimeSpan.FromHours(1);
            externalTools.AudioFiles.Add("/output/book.m4b");
            externalTools.StreamLayouts["/output/book.m4b"] = "audio,0\ndata,0\nvideo,1\n";

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");
            diskProxy.ExistingFiles.Add("/output/book.m4b");
            diskProxy.FileSizes["/output/book.m4b"] = 64 * 1024 * 1024;

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            var result = subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3" },
                "/output/book.m4b",
                new ConversionOptions { ExpectedSourceDuration = TimeSpan.FromHours(1) });

            Assert.That(result.Success, Is.True);
        }

        [Test]
        public void should_reject_successful_tool_run_with_non_attached_video_stream()
        {
            var externalTools = new StreamingExternalToolsService();
            externalTools.Durations["/output/book.m4b"] = TimeSpan.FromHours(1);
            externalTools.AudioFiles.Add("/output/book.m4b");
            externalTools.StreamLayouts["/output/book.m4b"] = "audio,0\nvideo,0\n";

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFiles.Add("/input/chapter-01.mp3");
            diskProxy.ExistingFiles.Add("/output/book.m4b");
            diskProxy.FileSizes["/output/book.m4b"] = 64 * 1024 * 1024;

            var subject = new M4bConversionService(
                externalTools,
                diskProvider,
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                LogManager.GetCurrentClassLogger());

            var result = subject.ConvertToM4b(
                new[] { "/input/chapter-01.mp3" },
                "/output/book.m4b",
                new ConversionOptions { ExpectedSourceDuration = TimeSpan.FromHours(1) });

            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureCategory, Is.EqualTo(ConversionFailureCategory.OutputInvalid));
            Assert.That(result.ErrorMessage, Does.Contain("video stream"));
        }
    }
}

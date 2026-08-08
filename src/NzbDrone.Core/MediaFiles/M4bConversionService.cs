using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.MediaFiles
{
    public interface IM4bConversionService
    {
        ConversionResult ConvertToM4b(string[] inputFiles, string outputFile, ConversionOptions options = null);
        bool CanConvert(string[] inputFiles);
        ConversionEstimate EstimateConversion(string[] inputFiles);
    }

    public enum ConversionFailureCategory
    {
        None = 0,
        ToolUnavailable = 1,
        DependencyMissing = 2,
        PermissionDenied = 3,
        InvalidInput = 4,
        CoverEmbedding = 5,
        ChapterOrTagging = 6,
        TimedOut = 7,
        OutputMissing = 8,
        OutputInvalid = 9,
        Unknown = 10,
        Cancelled = 11
    }

    public class M4bConversionService : IM4bConversionService
    {
        private readonly IExternalToolsService _externalTools;
        private readonly IDiskProvider _diskProvider;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public M4bConversionService(IExternalToolsService externalTools,
                                  IDiskProvider diskProvider,
                                  IConfigService configService,
                                  Logger logger)
        {
            _externalTools = externalTools;
            _diskProvider = diskProvider;
            _configService = configService;
            _logger = logger;
        }

        public ConversionResult ConvertToM4b(string[] inputFiles, string outputFile, ConversionOptions options = null)
        {
            var result = new ConversionResult
            {
                Success = false,
                OutputFile = outputFile
            };

            try
            {
                if (!_externalTools.IsM4bToolAvailable())
                {
                    SetFailure(result, ConversionFailureCategory.ToolUnavailable, "m4b-tool is not available");
                    _logger.Error(result.ErrorMessage);
                    return result;
                }

                if (!_externalTools.IsFFmpegAvailable())
                {
                    SetFailure(result, ConversionFailureCategory.ToolUnavailable, "FFmpeg is not available (required by m4b-tool)");
                    _logger.Error(result.ErrorMessage);
                    return result;
                }

                if (!_externalTools.IsFFprobeAvailable())
                {
                    SetFailure(result, ConversionFailureCategory.ToolUnavailable, "FFprobe is not available (required to validate converted audiobooks)");
                    _logger.Error(result.ErrorMessage);
                    return result;
                }

                // Validate input files
                foreach (var file in inputFiles)
                {
                    if (!_diskProvider.FileExists(file))
                    {
                        SetFailure(result, ConversionFailureCategory.InvalidInput, $"Input file does not exist: {file}");
                        _logger.Error(result.ErrorMessage);
                        return result;
                    }
                }

                var conversionOptions = options ?? new ConversionOptions();
                var tagOptions = CloneTagOptions(conversionOptions.TagOptions) ?? new ConversionTagOptions();
                TryApplyExtractedSourceCover(inputFiles, conversionOptions, tagOptions);
                var arguments = BuildM4bToolArguments(inputFiles, outputFile, conversionOptions, tagOptions);
                var progressParser = new M4bToolProgressParser(inputFiles.Length);
                var progressLock = new object();
                decimal? lastProgress = null;
                var lastProgressPublish = DateTime.MinValue;

                _logger.Debug("Starting M4B conversion: {0} files -> {1}", inputFiles.Length, outputFile);
                _logger.Debug("m4b-tool arguments: {0}", string.Join(" ", arguments));

                PublishProgressUpdate(conversionOptions, new ConversionProgressUpdate
                {
                    Progress = 1m,
                    Message = string.Format(CultureInfo.InvariantCulture, "Converting to M4B - 0 of {0}", inputFiles.Length),
                    CurrentFile = 0,
                    TotalFiles = inputFiles.Length
                });
                lastProgress = 1m;
                lastProgressPublish = DateTime.UtcNow;

                var toolResult = _externalTools.ExecuteM4bToolDetailed(
                    arguments,
                    timeoutMs: GetConversionTimeoutMs(conversionOptions),
                    outputHandler: chunk => PublishProgress(chunk, conversionOptions, progressParser, progressLock, ref lastProgress, ref lastProgressPublish),
                    cancellationToken: conversionOptions.CancellationToken);
                if (conversionOptions.CancellationToken.IsCancellationRequested && toolResult != null && !toolResult.Cancelled)
                {
                    toolResult.Cancelled = true;
                    toolResult.ErrorMessage = "Process cancelled";
                }

                var output = toolResult?.CombinedOutput ?? string.Empty;
                result.ConversionLog = output;

                if (toolResult == null || !toolResult.Succeeded)
                {
                    var failure = ClassifyM4bToolFailure(toolResult);
                    SetFailure(result, failure.Category, failure.Message, output);
                    if (failure.Category != ConversionFailureCategory.Cancelled)
                    {
                        TryMarkRetainableFailedOutput(result, inputFiles, outputFile, conversionOptions);
                    }

                    if (failure.Category == ConversionFailureCategory.Cancelled)
                    {
                        _logger.Debug("M4B conversion cancelled: {0}", result.ErrorMessage);
                    }
                    else
                    {
                        _logger.Error("M4B conversion failed: {0}", result.ErrorMessage);
                    }

                    if (!output.IsNullOrWhiteSpace() && failure.Category != ConversionFailureCategory.Cancelled)
                    {
                        _logger.Error("m4b-tool output: {0}", output.Length > 4000 ? output.Substring(0, 4000) + "..." : output);
                    }
                }
                else if (_diskProvider.FileExists(outputFile))
                {
                    PublishProgressUpdate(conversionOptions, new ConversionProgressUpdate
                    {
                        Progress = 96m,
                        Message = "Verifying M4B",
                        CurrentFile = inputFiles.Length,
                        TotalFiles = inputFiles.Length
                    });

                    var validation = ValidateConvertedOutput(inputFiles, outputFile, conversionOptions);
                    if (!validation.Success)
                    {
                        SetFailure(result, ConversionFailureCategory.OutputInvalid, validation.ErrorMessage, output);
                        _logger.Error("M4B conversion produced an invalid output: {0}", result.ErrorMessage);
                    }
                    else
                    {
                        result.Success = true;
                        result.OutputFileSize = GetFileSizeSafely(outputFile);

                        _logger.Debug("M4B conversion completed successfully: {0} ({1} MB)",
                            outputFile, result.OutputFileSize / 1024 / 1024);

                    }
                }
                else
                {
                    SetFailure(result, ConversionFailureCategory.OutputMissing, "m4b-tool finished but did not create the converted M4B file", output);
                    _logger.Error("M4B conversion failed: {0}", result.ErrorMessage);
                    if (!output.IsNullOrWhiteSpace())
                    {
                        _logger.Error("m4b-tool output: {0}", output.Length > 4000 ? output.Substring(0, 4000) + "..." : output);
                    }
                }
            }
            catch (Exception ex)
            {
                SetFailure(result, ConversionFailureCategory.Unknown, ex.Message, ex.ToString());
                _logger.Error(ex, "M4B conversion failed");
            }

            return result;
        }

        public bool CanConvert(string[] inputFiles)
        {
            if (!_externalTools.IsM4bToolAvailable() || !_externalTools.IsFFmpegAvailable() || !_externalTools.IsFFprobeAvailable())
            {
                return false;
            }

            // Check if all files are supported formats
            var supportedExtensions = new[] { ".mp3", ".m4a", ".m4b", ".aac", ".ogg", ".flac" };

            foreach (var file in inputFiles)
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (!supportedExtensions.Contains(extension))
                {
                    _logger.Debug("Cannot convert: unsupported file type {0}", extension);
                    return false;
                }
            }

            return true;
        }

        public ConversionEstimate EstimateConversion(string[] inputFiles)
        {
            var estimate = new ConversionEstimate
            {
                CanConvert = CanConvert(inputFiles),
                InputFileCount = inputFiles.Length
            };

            if (!estimate.CanConvert)
            {
                return estimate;
            }

            long totalSize = 0;
            foreach (var file in inputFiles.Where(_diskProvider.FileExists))
            {
                var fileInfo = _diskProvider.GetFileInfo(file);
                totalSize += fileInfo.Length;
            }

            estimate.TotalInputSize = totalSize;

            // Rough estimates based on typical conversion speeds
            // MP3 to M4B typically processes at ~10-20x realtime on modern hardware
            // For estimation, assume 15x realtime and average bitrate of 128kbps
            var estimatedDurationSeconds = (totalSize * 8) / (128 * 1024); // Convert to seconds at 128kbps
            estimate.EstimatedTime = TimeSpan.FromSeconds(estimatedDurationSeconds / 15);

            // Output size is typically 90-95% of input for MP3->M4B
            estimate.EstimatedOutputSize = (long)(totalSize * 0.92);

            return estimate;
        }

        private IReadOnlyList<string> BuildM4bToolArguments(string[] inputFiles, string outputFile, ConversionOptions options, ConversionTagOptions tagOptions = null)
        {
            var args = new List<string>
            {
                "merge"
            };

            // Add input files
            foreach (var file in inputFiles)
            {
                args.Add(file);
            }

            args.Add("--output-file");
            args.Add(outputFile);

            if (options.TempDirectory.IsNotNullOrWhiteSpace())
            {
                args.Add($"--tmp-dir={options.TempDirectory}");
            }

            // Audio quality settings
            if (options.AudioBitrate > 0)
            {
                args.Add($"--audio-bitrate={options.AudioBitrate}k");
            }
            else
            {
                // Default to 64kbps for audiobooks
                args.Add("--audio-bitrate=64k");
            }

            if (options.AudioSampleRate > 0)
            {
                args.Add($"--audio-samplerate={options.AudioSampleRate}");
            }

            if (options.AudioChannels > 0)
            {
                args.Add($"--audio-channels={options.AudioChannels}");
            }

            // Performance settings
            if (options.Jobs > 0)
            {
                args.Add($"--jobs={options.Jobs}");
            }
            else
            {
                // Default to 4 parallel jobs
                args.Add("--jobs=4");
            }

            if (options.FfmpegThreads > 0)
            {
                args.Add($"--ffmpeg-threads={options.FfmpegThreads}");
            }

            AddTagArguments(args, tagOptions ?? options.TagOptions);

            // Additional flags
            if (options.SkipCover)
            {
                args.Add("--skip-cover");
            }

            if (options.Force)
            {
                args.Add("--force");
            }

            if (options.ProgressHandler != null)
            {
                // m4b-tool only emits per-file progress in verbose mode when stdout/stderr are redirected.
                args.Add("-v");
            }

            // Use ID3v2.4 for better tag preservation.
            // Use --ffmpeg-param=<value> form: Symfony Console 7 (m4b-tool 0.5.x) parses
            // a separate "-id3v2_version" argv as short option "-i" + value "d3v2_version"
            // and rejects it because -i isn't a valid m4b-tool option.
            args.Add("--ffmpeg-param=-id3v2_version");
            args.Add("--ffmpeg-param=4");

            return args;
        }

        private void TryApplyExtractedSourceCover(string[] inputFiles, ConversionOptions options, ConversionTagOptions tagOptions)
        {
            if (tagOptions == null ||
                options?.SkipCover == true ||
                string.IsNullOrWhiteSpace(options?.TempDirectory) ||
                inputFiles == null ||
                inputFiles.Length == 0)
            {
                return;
            }

            if (tagOptions.CoverIsSource && tagOptions.Cover.IsNotNullOrWhiteSpace())
            {
                _logger.Debug("Using source sidecar cover art for M4B conversion: {0}", tagOptions.Cover);
                return;
            }

            var coverPath = Path.Combine(options.TempDirectory, "source-cover.jpg");

            foreach (var inputFile in inputFiles.Where(p => p.IsNotNullOrWhiteSpace()))
            {
                try
                {
                    Directory.CreateDirectory(options.TempDirectory);
                    _externalTools.ExecuteFFmpeg(new[]
                    {
                        "-hide_banner",
                        "-loglevel",
                        "error",
                        "-y",
                        "-i",
                        inputFile,
                        "-map",
                        "0:v:0",
                        "-frames:v",
                        "1",
                        "-an",
                        coverPath
                    }, timeoutMs: 30000, preferStderrOnEmpty: true);

                    if (_diskProvider.FileExists(coverPath) && _diskProvider.GetFileSize(coverPath) > 0)
                    {
                        tagOptions.Cover = coverPath;
                        _logger.Debug("Using embedded source cover art for M4B conversion: {0}", coverPath);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Could not extract embedded source cover art from {0}", inputFile);
                }
            }

            if (tagOptions.Cover.IsNullOrWhiteSpace())
            {
                _logger.Warn("No embedded source cover art or matched book cover was available for M4B conversion. Import will continue without cover art.");
            }
        }

        private static ConversionTagOptions CloneTagOptions(ConversionTagOptions source)
        {
            if (source == null)
            {
                return null;
            }

            return new ConversionTagOptions
            {
                Mode = source.Mode,
                Name = source.Name,
                Album = source.Album,
                Artist = source.Artist,
                AlbumArtist = source.AlbumArtist,
                Writer = source.Writer,
                Year = source.Year,
                Genre = source.Genre,
                Comment = source.Comment,
                Copyright = source.Copyright,
                Grouping = source.Grouping,
                Series = source.Series,
                SeriesPart = source.SeriesPart,
                Cover = source.Cover,
                CoverIsSource = source.CoverIsSource,
                EncodedBy = source.EncodedBy,
                UseFilenamesAsChapters = source.UseFilenamesAsChapters,
                IgnoreSourceTags = source.IgnoreSourceTags,
                ChaptersTxtContent = source.ChaptersTxtContent,
                ProviderChapterCount = source.ProviderChapterCount,
                CoverPolicySignature = source.CoverPolicySignature,
                ManifestJson = source.ManifestJson
            };
        }

        private static void AddTagArguments(List<string> args, ConversionTagOptions tagOptions)
        {
            if (tagOptions == null)
            {
                return;
            }

            AddOptionalArgument(args, "--name", tagOptions.Name);
            AddOptionalArgument(args, "--album", tagOptions.Album);
            AddOptionalArgument(args, "--artist", tagOptions.Artist);
            AddOptionalArgument(args, "--albumartist", tagOptions.AlbumArtist);
            AddOptionalArgument(args, "--writer", tagOptions.Writer);
            AddOptionalArgument(args, "--year", tagOptions.Year);
            AddOptionalArgument(args, "--genre", tagOptions.Genre);
            AddOptionalArgument(args, "--comment", tagOptions.Comment);
            AddOptionalArgument(args, "--copyright", tagOptions.Copyright);
            AddOptionalArgument(args, "--grouping", tagOptions.Grouping);
            AddOptionalArgument(args, "--series", tagOptions.Series);
            AddOptionalArgument(args, "--series-part", tagOptions.SeriesPart);
            AddOptionalArgument(args, "--cover", tagOptions.Cover);
            AddOptionalArgument(args, "--encoded-by", tagOptions.EncodedBy);

            if (tagOptions.UseFilenamesAsChapters)
            {
                args.Add("--use-filenames-as-chapters");
            }

            if (tagOptions.IgnoreSourceTags)
            {
                args.Add("--ignore-source-tags");
            }
        }

        private static void AddOptionalArgument(List<string> args, string name, string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return;
            }

            args.Add($"{name}={value.Trim()}");
        }

        private void PublishProgress(
            string chunk,
            ConversionOptions options,
            M4bToolProgressParser progressParser,
            object progressLock,
            ref decimal? lastProgress,
            ref DateTime lastProgressPublish)
        {
            if (options.ProgressHandler == null)
            {
                return;
            }

            ConversionProgressUpdate update;
            lock (progressLock)
            {
                if (!progressParser.TryParse(chunk, out update) ||
                    !update.Progress.HasValue)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                var progress = update.Progress.Value;
                var changedEnough = !lastProgress.HasValue || Math.Abs(progress - lastProgress.Value) >= 1m;
                var oldEnough = now - lastProgressPublish >= TimeSpan.FromSeconds(2);
                var terminal = progress >= 94.9m && (!lastProgress.HasValue || lastProgress.Value < 94.9m);

                if (!changedEnough && !oldEnough && !terminal)
                {
                    return;
                }

                lastProgress = progress;
                lastProgressPublish = now;
            }

            PublishProgressUpdate(options, update);
        }

        private void PublishProgressUpdate(ConversionOptions options, ConversionProgressUpdate update)
        {
            if (options?.ProgressHandler == null || update == null)
            {
                return;
            }

            try
            {
                options.ProgressHandler(update);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "M4B conversion progress handler failed");
            }
        }

        private void TryMarkRetainableFailedOutput(ConversionResult result, string[] inputFiles, string outputFile, ConversionOptions options)
        {
            if (!_diskProvider.FileExists(outputFile))
            {
                return;
            }

            var validation = ValidateConvertedOutput(inputFiles, outputFile, options);
            if (!validation.Success)
            {
                _logger.Warn("Not retaining failed conversion output because validation failed: {0}", validation.ErrorMessage);
                return;
            }

            result.OutputFileSize = GetFileSizeSafely(outputFile);
            result.RetainOutputOnFailure = true;
            result.ErrorMessage = result.ErrorMessage.IsNullOrWhiteSpace()
                ? "m4b-tool reported a failure after producing a readable M4B. The file was retained for inspection."
                : result.ErrorMessage + " A readable converted M4B was produced but will not be imported because m4b-tool reported a failure.";
        }

        private ConversionOutputValidationResult ValidateConvertedOutput(string[] inputFiles, string outputFile, ConversionOptions options)
        {
            if (!_diskProvider.FileExists(outputFile))
            {
                return ConversionOutputValidationResult.Fail("Converted M4B file was not created");
            }

            var outputSize = GetFileSizeSafely(outputFile);
            if (outputSize < 32 * 1024)
            {
                return ConversionOutputValidationResult.Fail("Converted M4B file is too small to be a valid audiobook");
            }

            if (!TryHasAudioStream(outputFile))
            {
                return ConversionOutputValidationResult.Fail("Converted M4B does not contain a readable audio stream");
            }

            if (TryFindBlockingNonAudioStream(outputFile, out var nonAudioStreamError))
            {
                return ConversionOutputValidationResult.Fail(nonAudioStreamError);
            }

            if (!TryGetFfprobeDuration(outputFile, out var outputDuration) || outputDuration <= TimeSpan.Zero)
            {
                return ConversionOutputValidationResult.Fail("Converted M4B does not have a readable audio duration");
            }

            var expectedDuration = GetExpectedSourceDuration(inputFiles, options);
            if (expectedDuration > TimeSpan.FromMinutes(5))
            {
                var allowedDifference = GetDurationValidationTolerance(expectedDuration);
                var actualDifference = (outputDuration - expectedDuration).Duration();

                if (actualDifference > allowedDifference)
                {
                    return ConversionOutputValidationResult.Fail(
                        $"Converted M4B duration looks wrong. Source duration is about {FormatDuration(expectedDuration)} but output is {FormatDuration(outputDuration)}; allowed difference is {FormatDuration(allowedDifference)}.");
                }
            }

            return ConversionOutputValidationResult.Ok();
        }

        private static TimeSpan GetDurationValidationTolerance(TimeSpan expectedDuration)
        {
            return AudiobookDurationTolerance.ForConversionValidation(expectedDuration);
        }

        private TimeSpan GetExpectedSourceDuration(string[] inputFiles, ConversionOptions options)
        {
            if (options?.ExpectedSourceDuration > TimeSpan.Zero)
            {
                return options.ExpectedSourceDuration;
            }

            var total = TimeSpan.Zero;
            foreach (var inputFile in inputFiles ?? Array.Empty<string>())
            {
                if (!TryGetFfprobeDuration(inputFile, out var duration) || duration <= TimeSpan.Zero)
                {
                    return TimeSpan.Zero;
                }

                total += duration;
            }

            return total;
        }

        private bool TryHasAudioStream(string filePath)
        {
            try
            {
                var output = _externalTools.ExecuteFFprobe(
                    new[]
                    {
                        "-v", "error",
                        "-select_streams", "a:0",
                        "-show_entries", "stream=codec_type",
                        "-of", "default=noprint_wrappers=1:nokey=1",
                        filePath
                    },
                    timeoutMs: 30000);

                return output?
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => line.Trim().Equals("audio", StringComparison.OrdinalIgnoreCase)) == true;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to validate audio stream for converted M4B: {0}", filePath);
                return false;
            }
        }

        private bool TryFindBlockingNonAudioStream(string filePath, out string error)
        {
            error = null;

            try
            {
                var output = _externalTools.ExecuteFFprobe(
                    new[]
                    {
                        "-v", "error",
                        "-show_entries", "stream=codec_type:stream_disposition=attached_pic",
                        "-of", "csv=p=0",
                        filePath
                    },
                    timeoutMs: 30000);

                foreach (var line in output?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>())
                {
                    var parts = line.Split(',').Select(part => part.Trim()).ToArray();
	                    var codecType = parts.FirstOrDefault(part =>
	                        part.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
	                        part.Equals("video", StringComparison.OrdinalIgnoreCase) ||
	                        part.Equals("subtitle", StringComparison.OrdinalIgnoreCase) ||
	                        part.Equals("data", StringComparison.OrdinalIgnoreCase));

	                    if (codecType == null || codecType.Equals("audio", StringComparison.OrdinalIgnoreCase))
	                    {
	                        continue;
	                    }

	                    // MP4/M4B chapters and timed metadata are commonly represented as data/bin_data streams.
	                    // They are valid audiobook metadata, not playable media that should block import.
	                    if (codecType.Equals("data", StringComparison.OrdinalIgnoreCase))
	                    {
	                        continue;
	                    }

	                    if (codecType.Equals("video", StringComparison.OrdinalIgnoreCase) && parts.Any(part => part == "1"))
	                    {
	                        continue;
	                    }

	                    error = $"Converted M4B contains a {codecType} stream that is not attached cover art";
	                    return true;
	                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to inspect converted M4B stream layout: {0}", filePath);
            }

            return false;
        }

        private bool TryGetFfprobeDuration(string filePath, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;

            try
            {
                var output = _externalTools.ExecuteFFprobe(
                    new[]
                    {
                        "-v", "error",
                        "-show_entries", "format=duration",
                        "-of", "default=noprint_wrappers=1:nokey=1",
                        filePath
                    },
                    timeoutMs: 30000);

                var line = output?
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()
                    ?.Trim();

                if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    duration = TimeSpan.FromSeconds(seconds);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to read ffprobe duration for converted M4B validation: {0}", filePath);
            }

            return false;
        }

        private long GetFileSizeSafely(string path)
        {
            try
            {
                return _diskProvider.GetFileSize(path);
            }
            catch
            {
                try
                {
                    return _diskProvider.GetFileInfo(path).Length;
                }
                catch
                {
                    return new FileInfo(path).Length;
                }
            }
        }

        private static void SetFailure(ConversionResult result, ConversionFailureCategory category, string message, string log = null)
        {
            result.Success = false;
            result.FailureCategory = category;
            result.ErrorMessage = message;
            result.ConversionLog = log ?? result.ConversionLog;
        }

        private static ConversionFailure ClassifyM4bToolFailure(ExternalToolResult toolResult)
        {
            if (toolResult == null)
            {
                return new ConversionFailure(ConversionFailureCategory.Unknown, "m4b-tool did not return a process result");
            }

            if (toolResult.TimedOut)
            {
                return new ConversionFailure(
                    ConversionFailureCategory.TimedOut,
                    $"m4b-tool timed out after {TimeSpan.FromMilliseconds(toolResult.TimeoutMs):g}. The conversion was stopped.");
            }

            if (toolResult.Cancelled)
            {
                return new ConversionFailure(
                    ConversionFailureCategory.Cancelled,
                    "Conversion was cancelled.");
            }

            var combined = string.Join("\n", toolResult.CombinedOutput, toolResult.ErrorMessage).Trim();
            var normalized = combined.ToLowerInvariant();
            var firstLine = FirstMeaningfulLine(combined);
            var errorText = GetErrorText(combined);
            var normalizedErrors = errorText.ToLowerInvariant();
            var snippetLine = FirstMeaningfulLine(errorText) ?? firstLine;

            if (ContainsAny(normalized, "permission denied", "access denied", "failed to open stream", "unauthorizedaccessexception"))
            {
                return WithSnippet(ConversionFailureCategory.PermissionDenied, "Conversion could not write or rename files in the conversion folder. Check destination folder ownership and permissions.", snippetLine);
            }

            if (ContainsAny(normalized, "required extension", "extension \"curl\"", "extension \"zip\"", "class \"ziparchive\"") ||
                (ContainsAny(normalized, "not found", "no such file", "exit code 127") &&
                 ContainsAny(normalized, "ffmpeg", "mp4info", "mp4tags", "mp4chaps", "php", "curl", "zip")))
            {
                return WithSnippet(ConversionFailureCategory.DependencyMissing, "Conversion environment is missing a required tool or PHP extension. Rebuild/update the container image, then retry.", snippetLine);
            }

            if (ContainsAny(normalizedErrors, "cover", "attached_pic", "mjpeg", "image2", "embed picture", "attached picture") &&
                ContainsAny(normalizedErrors, "error", "failed", "invalid", "could not", "unable"))
            {
                return WithSnippet(ConversionFailureCategory.CoverEmbedding, "m4b-tool failed while embedding cover art. Retry without embedded cover art may succeed.", snippetLine);
            }

            if (ContainsAny(normalizedErrors, "chapter", "mp4tags", "mp4chaps", "mp4info") &&
                ContainsAny(normalizedErrors, "error", "failed", "invalid", "could not", "unable"))
            {
                return WithSnippet(ConversionFailureCategory.ChapterOrTagging, "m4b-tool failed while writing chapters or tags. The converted audio is not being imported automatically.", snippetLine);
            }

            if (ContainsAny(normalized, "invalid data found", "could not open input", "error while decoding", "moov atom not found", "header missing", "unsupported codec"))
            {
                return WithSnippet(ConversionFailureCategory.InvalidInput, "m4b-tool/ffmpeg could not decode one of the source audio files.", snippetLine);
            }

            return WithSnippet(ConversionFailureCategory.Unknown, $"m4b-tool failed with exit code {toolResult.ExitCode}.", snippetLine);
        }

        private static int GetConversionTimeoutMs(ConversionOptions options)
        {
            if (options?.TimeoutMs > 0)
            {
                return options.TimeoutMs;
            }

            var minimum = TimeSpan.FromHours(1);
            var maximum = TimeSpan.FromDays(7);
            var timeout = minimum;

            if (options?.ExpectedSourceDuration > TimeSpan.Zero)
            {
                var scaledTicks = options.ExpectedSourceDuration.Ticks > maximum.Ticks / 4
                    ? maximum.Ticks
                    : options.ExpectedSourceDuration.Ticks * 4;
                timeout = TimeSpan.FromTicks(scaledTicks).Add(TimeSpan.FromMinutes(30));
                if (timeout < minimum)
                {
                    timeout = minimum;
                }
            }

            if (timeout > maximum)
            {
                timeout = maximum;
            }

            return (int)Math.Ceiling(timeout.TotalMilliseconds);
        }

        private static string GetErrorText(string output)
        {
            if (output.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            var lines = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !line.IsNullOrWhiteSpace())
                .ToList();

            var errorLines = lines
                .Where(IsErrorLine)
                .ToList();

            return string.Join("\n", errorLines.Count > 0 ? errorLines : lines.Take(5));
        }

        private static bool IsErrorLine(string line)
        {
            if (line.IsNullOrWhiteSpace())
            {
                return false;
            }

            var normalized = line.TrimStart().ToLowerInvariant();
            return normalized.StartsWith("error", StringComparison.Ordinal) ||
                   normalized.StartsWith("fatal", StringComparison.Ordinal) ||
                   normalized.StartsWith("exception", StringComparison.Ordinal) ||
                   normalized.Contains(" error:") ||
                   normalized.Contains(" failed") ||
                   normalized.Contains(" failure") ||
                   normalized.Contains(" could not") ||
                   normalized.Contains(" unable");
        }

        private static ConversionFailure WithSnippet(ConversionFailureCategory category, string message, string firstLine)
        {
            if (!firstLine.IsNullOrWhiteSpace())
            {
                message += " Tool output: " + firstLine;
            }

            return new ConversionFailure(category, message);
        }

        private static string FirstMeaningfulLine(string text)
        {
            return text?
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !line.IsNullOrWhiteSpace());
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            return needles.Any(needle => text.Contains(needle));
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return $"{(int)duration.TotalHours}h {duration.Minutes:D2}m";
            }

            return $"{duration.TotalMinutes:0.#}m";
        }
    }

    internal sealed class ConversionFailure
    {
        public ConversionFailure(ConversionFailureCategory category, string message)
        {
            Category = category;
            Message = message;
        }

        public ConversionFailureCategory Category { get; }
        public string Message { get; }
    }

    internal sealed class ConversionOutputValidationResult
    {
        private ConversionOutputValidationResult(bool success, string errorMessage)
        {
            Success = success;
            ErrorMessage = errorMessage;
        }

        public bool Success { get; }
        public string ErrorMessage { get; }

        public static ConversionOutputValidationResult Ok()
        {
            return new ConversionOutputValidationResult(true, null);
        }

        public static ConversionOutputValidationResult Fail(string errorMessage)
        {
            return new ConversionOutputValidationResult(false, errorMessage);
        }
    }

    internal sealed class M4bToolProgressParser
    {
        private const int MaxBufferLength = 8192;
        private const decimal ToolProgressCeiling = 95m;
        private static readonly Regex AnsiRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
        private static readonly Regex PercentRegex = new(@"(?<percent>\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);
        private static readonly Regex StepRegex = new(@"(?<!\d)(?<current>\d{1,5})\s*/\s*(?<total>\d{1,5})(?!\d)", RegexOptions.Compiled);
        private static readonly Regex StepOfRegex = new(@"\b(?<current>\d{1,5})\s+of\s+(?<total>\d{1,5})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RemainingRegex = new(@"\b(?<remaining>\d{1,5})\s+remaining\s*/\s*(?<total>\d{1,5})\s+total\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly int _inputFileCount;
        private readonly StringBuilder _buffer = new();
        private decimal? _lastProgress;
        private int? _currentStep;
        private int? _totalSteps;

        public M4bToolProgressParser(int inputFileCount = 0)
        {
            _inputFileCount = Math.Max(0, inputFileCount);
        }

        public bool TryParse(string chunk, out ConversionProgressUpdate update)
        {
            update = null;

            if (string.IsNullOrEmpty(chunk))
            {
                return false;
            }

            _buffer.Append(chunk);
            if (_buffer.Length > MaxBufferLength)
            {
                _buffer.Remove(0, _buffer.Length - MaxBufferLength);
            }

            var text = AnsiRegex.Replace(_buffer.ToString(), string.Empty).Replace('\r', '\n');

            if (TryParseLastStep(text, out var currentStep, out var totalSteps, out var steppedPercent, out var conversionPhaseComplete))
            {
                var progress = NormalizeProgress(steppedPercent.Value);
                update = new ConversionProgressUpdate
                {
                    Progress = progress,
                    Message = FormatProgressMessage(progress, currentStep, totalSteps, conversionPhaseComplete),
                    CurrentFile = currentStep,
                    TotalFiles = totalSteps
                };
                return true;
            }

            var percent = TryParseLastPercent(text);
            if (percent.HasValue && TryConvertCurrentFilePercent(percent.Value, out var overallProgress))
            {
                var progress = NormalizeProgress(overallProgress);
                update = new ConversionProgressUpdate
                {
                    Progress = progress,
                    Message = FormatProgressMessage(progress, _currentStep, _totalSteps, IsLastFileComplete(percent.Value)),
                    CurrentFile = _currentStep,
                    TotalFiles = _totalSteps
                };
                return true;
            }

            return false;
        }

        private static decimal? TryParseLastPercent(string text)
        {
            decimal? parsed = null;

            foreach (Match match in PercentRegex.Matches(text))
            {
                if (decimal.TryParse(match.Groups["percent"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) &&
                    value >= 0m &&
                    value <= 100m)
                {
                    parsed = value;
                }
            }

            return parsed;
        }

        private bool TryParseLastStep(string text, out int? currentStep, out int? totalSteps, out decimal? percent, out bool conversionPhaseComplete)
        {
            currentStep = null;
            totalSteps = null;
            percent = null;
            conversionPhaseComplete = false;
            var lastProgressMatchIndex = -1;

            foreach (var match in RemainingRegex.Matches(text).Cast<Match>().OrderBy(match => match.Index))
            {
                if (!int.TryParse(match.Groups["remaining"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining) ||
                    !int.TryParse(match.Groups["total"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) ||
                    total <= 0 ||
                    remaining < 0 ||
                    remaining > total)
                {
                    continue;
                }

                if (_inputFileCount > 0 && total != _inputFileCount)
                {
                    continue;
                }

                if (match.Index < lastProgressMatchIndex)
                {
                    continue;
                }

                var current = total - remaining;
                _currentStep = current;
                _totalSteps = total;
                currentStep = current;
                totalSteps = total;
                percent = Math.Min(100m, Math.Max(0m, (decimal)current / total * 100m));
                conversionPhaseComplete = remaining == 0;
                lastProgressMatchIndex = match.Index;
            }

            foreach (var match in StepRegex.Matches(text)
                         .Cast<Match>()
                         .Concat(StepOfRegex.Matches(text).Cast<Match>())
                         .OrderBy(match => match.Index))
            {
                if (!int.TryParse(match.Groups["current"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current) ||
                    !int.TryParse(match.Groups["total"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) ||
                    total <= 0 ||
                    current < 0m ||
                    current > total)
                {
                    continue;
                }

                if (_inputFileCount > 0 && total != _inputFileCount)
                {
                    continue;
                }

                if (match.Index < lastProgressMatchIndex)
                {
                    continue;
                }

                _currentStep = current;
                _totalSteps = total;
                currentStep = current;
                totalSteps = total;
                percent = Math.Min(100m, Math.Max(0m, (decimal)current / total * 100m));
                conversionPhaseComplete = false;
                lastProgressMatchIndex = match.Index;
            }

            return percent.HasValue;
        }

        private bool TryConvertCurrentFilePercent(decimal filePercent, out decimal overallProgress)
        {
            overallProgress = 0m;

            if (!_currentStep.HasValue || !_totalSteps.HasValue || _totalSteps.Value <= 0)
            {
                return false;
            }

            var zeroBasedCurrentFile = Math.Max(0, _currentStep.Value - 1);
            var fileFraction = Math.Min(100m, Math.Max(0m, filePercent)) / 100m;
            overallProgress = Math.Min(100m, Math.Max(0m, (zeroBasedCurrentFile + fileFraction) / _totalSteps.Value * 100m));
            return true;
        }

        private bool IsLastFileComplete(decimal filePercent)
        {
            return _currentStep.HasValue &&
                   _totalSteps.HasValue &&
                   _totalSteps.Value > 0 &&
                   _currentStep.Value >= _totalSteps.Value &&
                   filePercent >= 100m;
        }

        private decimal NormalizeProgress(decimal progress)
        {
            progress = Math.Min(100m, Math.Max(0m, progress)) / 100m * ToolProgressCeiling;

            if (_lastProgress.HasValue && progress < _lastProgress.Value)
            {
                return _lastProgress.Value;
            }

            if (progress < 1m)
            {
                progress = 1m;
            }

            _lastProgress = progress;
            return progress;
        }

        private static string FormatProgressMessage(decimal progress, int? currentStep, int? totalSteps, bool conversionPhaseComplete)
        {
            if (currentStep.HasValue && totalSteps.HasValue && totalSteps.Value > 0)
            {
                if (conversionPhaseComplete && progress >= ToolProgressCeiling)
                {
                    return "Finalizing M4B";
                }

                return string.Format(CultureInfo.InvariantCulture, "Converting to M4B - {0} of {1}", currentStep.Value, totalSteps.Value);
            }

            return string.Format(CultureInfo.InvariantCulture, "Converting to M4B - {0:0.#}%", progress);
        }
    }

    public class ConversionOptions
    {
        public int AudioBitrate { get; set; } = 64; // Default 64kbps for audiobooks
        public int AudioSampleRate { get; set; } = 0; // 0 = auto
        public int AudioChannels { get; set; } = 0; // 0 = auto
        public int ChapterLength { get; set; } = 0; // Reserved; m4b-tool 0.5.x removed --chapters-per-file
        public int Jobs { get; set; } = 4; // Parallel processing
        public int FfmpegThreads { get; set; } = 0; // 0 = ffmpeg default
        public bool SkipCover { get; set; } = false;
        public bool Force { get; set; } = true; // Overwrite existing
        public string TempDirectory { get; set; }
        public TimeSpan ExpectedSourceDuration { get; set; }
        public int TimeoutMs { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public ConversionTagOptions TagOptions { get; set; }
        public Action<ConversionProgressUpdate> ProgressHandler { get; set; }
    }

    public class ConversionTagOptions
    {
        public string Mode { get; set; }
        public string Name { get; set; }
        public string Album { get; set; }
        public string Artist { get; set; }
        public string AlbumArtist { get; set; }
        public string Writer { get; set; }
        public string Year { get; set; }
        public string Genre { get; set; }
        public string Comment { get; set; }
        public string Copyright { get; set; }
        public string Grouping { get; set; }
        public string Series { get; set; }
        public string SeriesPart { get; set; }
        public string Cover { get; set; }
        public bool CoverIsSource { get; set; }
        public string EncodedBy { get; set; }
        public bool UseFilenamesAsChapters { get; set; }
        public bool IgnoreSourceTags { get; set; }
        public string ChaptersTxtContent { get; set; }
        public int? ProviderChapterCount { get; set; }
        public string CoverPolicySignature { get; set; }
        public string ManifestJson { get; set; }
    }

    public class ConversionProgressUpdate
    {
        public decimal? Progress { get; set; }
        public string Message { get; set; }
        public int? CurrentFile { get; set; }
        public int? TotalFiles { get; set; }
    }

    public class ConversionResult
    {
        public bool Success { get; set; }
        public string OutputFile { get; set; }
        public long OutputFileSize { get; set; }
        public string ErrorMessage { get; set; }
        public string ConversionLog { get; set; }
        public ConversionFailureCategory FailureCategory { get; set; } = ConversionFailureCategory.None;
        public bool RetainOutputOnFailure { get; set; }
    }

    public class ConversionEstimate
    {
        public bool CanConvert { get; set; }
        public int InputFileCount { get; set; }
        public long TotalInputSize { get; set; }
        public long EstimatedOutputSize { get; set; }
        public TimeSpan EstimatedTime { get; set; }
    }

}

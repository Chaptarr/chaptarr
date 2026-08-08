using System;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.Parser.Model;
using TagLib;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMediaInfoExtractor
    {
        MediaInfoModel ExtractMediaInfo(string filePath);
        TimeSpan GetDuration(string filePath);
        bool IsAudiobookFile(string filePath, MediaInfoModel mediaInfo = null);
    }

    public class MediaInfoExtractor : IMediaInfoExtractor
    {
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(MediaInfoExtractor));
        private readonly IExternalToolsService _externalTools;
        private readonly IAudioDurationResolver _durationResolver;

        public MediaInfoExtractor(IExternalToolsService externalTools, IAudioDurationResolver durationResolver)
        {
            _externalTools = externalTools;
            _durationResolver = durationResolver;
        }

        public MediaInfoModel ExtractMediaInfo(string filePath)
        {
            if (!System.IO.File.Exists(filePath))
            {
                Logger.Warn("File does not exist: {0}", filePath);
                return new MediaInfoModel();
            }

            // MediaInfoExtractor is intended for audio media files. Avoid invoking TagLib/ffprobe for ebooks.
            if (!MediaFileExtensions.AudioExtensions.Contains(Path.GetExtension(filePath)))
            {
                return new MediaInfoModel
                {
                    AudioFormat = string.Empty,
                    Duration = TimeSpan.Zero,
                    AudioBitrate = 0,
                    AudioChannels = 0,
                    AudioBits = 0,
                    AudioSampleRate = 0
                };
            }

            try
            {
                if (Mp3DurationReader.IsMp3Path(filePath))
                {
                    var resolution = _durationResolver.ResolveMp3(filePath);
                    if (resolution.HasFrameInfo)
                    {
                        return new MediaInfoModel
                        {
                            Duration = resolution.Duration,
                            AudioFormat = resolution.Description ?? string.Empty,
                            AudioBitrate = resolution.BitrateKbps,
                            AudioChannels = resolution.Channels,
                            AudioBits = 0,
                            AudioSampleRate = resolution.SampleRate
                        };
                    }

                    // TagLib still provides codec properties, but its MP3 duration is never trusted.
                    var mp3Model = ExtractWithTagLib(
                        filePath,
                        useTagLibDuration: false,
                        trustedDuration: resolution.Duration);
                    LogExtraction(filePath, mp3Model);
                    return mp3Model;
                }

                // Use TagLib for metadata extraction
                var model = ExtractWithTagLib(filePath);
                if (MediaFileExtensions.IsMatroskaAudioExtension(Path.GetExtension(filePath)))
                {
                    var ffprobeAudioFormat = GetAudioCodecWithFFprobe(filePath);
                    if (!string.IsNullOrWhiteSpace(ffprobeAudioFormat))
                    {
                        model.AudioFormat = ffprobeAudioFormat;
                    }
                }

                if (model.Duration <= TimeSpan.Zero)
                {
                    var ffprobeDuration = _durationResolver.GetFfprobeDuration(filePath);
                    if (ffprobeDuration > TimeSpan.Zero)
                    {
                        model.Duration = ffprobeDuration;
                        Logger.Debug("Using FFprobe duration for {0}: {1}", Path.GetFileName(filePath), ffprobeDuration);
                    }
                }

                LogExtraction(filePath, model);

                return model;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error extracting media info from file: {0}", filePath);
                // Return a model with sensible defaults instead of all nulls
                return new MediaInfoModel
                {
                    AudioFormat = string.Empty,
                    Duration = TimeSpan.Zero,
                    AudioBitrate = 0,
                    AudioChannels = 0,
                    AudioBits = 0,
                    AudioSampleRate = 0
                };
            }
        }

        public TimeSpan GetDuration(string filePath)
        {
            // Duration is only meaningful for audio media files.
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return TimeSpan.Zero;
            }

            var extension = Path.GetExtension(filePath);
            if (string.IsNullOrWhiteSpace(extension) || !MediaFileExtensions.AudioExtensions.Contains(extension))
            {
                return TimeSpan.Zero;
            }

            if (!System.IO.File.Exists(filePath))
            {
                Logger.Warn("File does not exist: {0}", filePath);
                return TimeSpan.Zero;
            }

            try
            {
                if (Mp3DurationReader.IsMp3Path(filePath))
                {
                    var resolution = _durationResolver.ResolveMp3(filePath);
                    if (resolution.HasDuration)
                    {
                        return resolution.Duration;
                    }

                    Logger.Warn("Unable to determine trustworthy MP3 duration for: {0}", filePath);
                    return TimeSpan.Zero;
                }

                // Prefer TagLib (in-process) for non-MP3 duration extraction; fallback to FFprobe when TagLib can't provide duration.
                var duration = GetDurationWithTagLib(filePath);
                if (duration > TimeSpan.Zero)
                {
                    return duration;
                }

                duration = _durationResolver.GetFfprobeDuration(filePath);
                if (duration > TimeSpan.Zero)
                {
                    Logger.Debug("FFprobe duration for {0}: {1}", Path.GetFileName(filePath), duration);
                    return duration;
                }

                Logger.Warn("Unable to determine duration for: {0}", filePath);
                return TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error extracting duration from file: {0}", filePath);
                return TimeSpan.Zero;
            }
        }

        private MediaInfoModel ExtractWithTagLib(
            string filePath,
            bool useTagLibDuration = true,
            TimeSpan trustedDuration = default)
        {
            try
            {
                using (var file = TagLib.File.Create(filePath))
                {
                    var model = new MediaInfoModel
                    {
                        Duration = useTagLibDuration ? file.Properties.Duration : trustedDuration
                    };

                    // Extract audio codec information
                    foreach (var codec in file.Properties.Codecs)
                    {
                        if (codec is TagLib.IAudioCodec audioCodec &&
                            (audioCodec.MediaTypes & MediaTypes.Audio) != MediaTypes.None)
                        {
                            model.AudioFormat = audioCodec.Description ?? string.Empty;
                            model.AudioBitrate = audioCodec.AudioBitrate;
                            model.AudioChannels = audioCodec.AudioChannels;
                            model.AudioBits = file.Properties.BitsPerSample;
                            model.AudioSampleRate = audioCodec.AudioSampleRate;

                            // Handle cases where bitrate is not available (e.g., Opus)
                            if (model.AudioBitrate == 0)
                            {
                                model.AudioBitrate = EstimateBitrate(filePath, model.Duration);
                            }

                            break;
                        }
                    }

                    // Ensure AudioFormat is never null
                    if (model.AudioFormat == null)
                    {
                        model.AudioFormat = string.Empty;
                        Logger.Debug("No audio codec found in file: {0}", filePath);
                    }

                    return model;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "TagLib extraction failed for: {0}", filePath);
                // Return a model with sensible defaults instead of all nulls
                return new MediaInfoModel
                {
                    AudioFormat = string.Empty,
                    Duration = useTagLibDuration ? TimeSpan.Zero : trustedDuration,
                    AudioBitrate = 0,
                    AudioChannels = 0,
                    AudioBits = 0,
                    AudioSampleRate = 0
                };
            }
        }

        private TimeSpan GetDurationWithTagLib(string filePath)
        {
            try
            {
                using (var file = TagLib.File.Create(filePath))
                {
                    return file.Properties.Duration;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "TagLib duration extraction failed for: {0}", filePath);
                return TimeSpan.Zero;
            }
        }

        private string GetAudioCodecWithFFprobe(string filePath)
        {
            try
            {
                if (!_durationResolver.IsFfprobeAvailable)
                {
                    return null;
                }

                var output = _externalTools.ExecuteFFprobe(
                    new[]
                    {
                        "-v", "error",
                        "-select_streams", "a:0",
                        "-show_entries", "stream=codec_name",
                        "-of", "default=noprint_wrappers=1:nokey=1",
                        filePath
                    });

                return output
                    ?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()
                    ?.Trim();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "FFprobe codec extraction failed for: {0}", filePath);
                return null;
            }
        }

        private static void LogExtraction(string filePath, MediaInfoModel model)
        {
            Logger.Debug("Media extraction complete for {0}: Duration={1}, Format={2}, Bitrate={3}",
                Path.GetFileName(filePath),
                model.Duration,
                model.AudioFormat,
                model.AudioBitrate);
        }

        private int EstimateBitrate(string filePath, TimeSpan duration)
        {
            try
            {
                if (duration.TotalSeconds <= 0)
                {
                    return 0;
                }

                var fileInfo = new FileInfo(filePath);
                var bitrate = (int)((fileInfo.Length * 8L) / (duration.TotalSeconds * 1024));

                Logger.Trace("Estimated bitrate for {0}: {1} kbps", Path.GetFileName(filePath), bitrate);
                return bitrate;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to estimate bitrate for: {0}", filePath);
                return 0;
            }
        }

        public bool IsAudiobookFile(string filePath, MediaInfoModel mediaInfo = null)
        {
            var extension = Path.GetExtension(filePath)?.ToLowerInvariant();

            if (!MediaFileExtensions.AudioExtensions.Contains(extension))
            {
                Logger.Trace("[EDITION-SELECTION] File extension {0} is not an audiobook extension", extension);
                return false;
            }

            // Additional check using MediaInfo if available
            if (mediaInfo != null && mediaInfo.AudioChannels > 0)
            {
                Logger.Trace("[EDITION-SELECTION] MediaInfo confirms audio file");
                return true;
            }

            Logger.Trace("[EDITION-SELECTION] File {0} identified as audiobook based on extension", Path.GetFileName(filePath));
            return true;
        }
    }
}

using System;
using System.Globalization;
using System.Linq;
using NLog;

namespace NzbDrone.Core.MediaFiles.TagExtraction
{
    public enum AudioDurationSource
    {
        None = 0,
        Mp3XingInfoHeader = 1,
        Mp3VbriHeader = 2,
        Mp3StableCbrEstimate = 3,
        Ffprobe = 4
    }

    public readonly struct AudioDurationResult
    {
        internal AudioDurationResult(
            TimeSpan duration,
            AudioDurationSource source,
            int bitrateKbps = 0,
            int sampleRate = 0,
            int channels = 0,
            string description = null)
        {
            Duration = duration;
            Source = source;
            BitrateKbps = bitrateKbps;
            SampleRate = sampleRate;
            Channels = channels;
            Description = description;
        }

        public TimeSpan Duration { get; }
        public AudioDurationSource Source { get; }
        public int BitrateKbps { get; }
        public int SampleRate { get; }
        public int Channels { get; }
        public string Description { get; }
        public bool HasDuration => Duration > TimeSpan.Zero;
        public bool HasFrameInfo => Source == AudioDurationSource.Mp3XingInfoHeader ||
                                    Source == AudioDurationSource.Mp3VbriHeader ||
                                    Source == AudioDurationSource.Mp3StableCbrEstimate;
    }

    public interface IAudioDurationResolver
    {
        bool IsFfprobeAvailable { get; }
        AudioDurationResult ResolveMp3(string path);
        TimeSpan GetFfprobeDuration(string path);
    }

    public class AudioDurationResolver : IAudioDurationResolver
    {
        private readonly IExternalToolsService _externalTools;
        private readonly Lazy<bool> _ffprobeAvailable;
        private readonly Logger _logger;

        public AudioDurationResolver(IExternalToolsService externalTools, Logger logger)
        {
            _externalTools = externalTools;
            _logger = logger;
            _ffprobeAvailable = new Lazy<bool>(CheckFfprobeAvailable);
        }

        public bool IsFfprobeAvailable => _ffprobeAvailable.Value;

        public AudioDurationResult ResolveMp3(string path)
        {
            if (!Mp3DurationReader.IsMp3Path(path))
            {
                return default;
            }

            if (Mp3DurationReader.TryGetInfo(path, out var info))
            {
                return new AudioDurationResult(
                    info.Duration,
                    MapSource(info.Source),
                    info.BitrateKbps,
                    info.SampleRate,
                    info.Channels,
                    info.Description);
            }

            var ffprobeDuration = GetFfprobeDuration(path);
            if (ffprobeDuration > TimeSpan.Zero)
            {
                _logger.Debug("[DURATION] FFprobe resolved unproven MP3 duration for '{0}': {1}",
                    System.IO.Path.GetFileName(path),
                    ffprobeDuration);
                return new AudioDurationResult(ffprobeDuration, AudioDurationSource.Ffprobe);
            }

            _logger.Warn("[DURATION] Unable to determine trustworthy MP3 duration for '{0}'; leaving duration unknown", path);
            return default;
        }

        public TimeSpan GetFfprobeDuration(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !IsFfprobeAvailable)
            {
                return TimeSpan.Zero;
            }

            try
            {
                var output = _externalTools.ExecuteFFprobe(
                    new[]
                    {
                        "-v", "error",
                        "-show_entries", "format=duration",
                        "-of", "default=noprint_wrappers=1:nokey=1",
                        path
                    },
                    timeoutMs: 20000);

                var value = output
                    ?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()
                    ?.Trim();

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    return TimeSpan.FromSeconds(seconds);
                }

                _logger.Warn("FFprobe returned no valid duration for '{0}': {1}", path, value ?? "<empty>");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "FFprobe duration extraction failed for '{0}'", path);
            }

            return TimeSpan.Zero;
        }

        private bool CheckFfprobeAvailable()
        {
            try
            {
                return _externalTools.IsFFprobeAvailable();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to check FFprobe availability for duration rescue");
                return false;
            }
        }

        private static AudioDurationSource MapSource(Mp3DurationReader.DurationSource source)
        {
            return source switch
            {
                Mp3DurationReader.DurationSource.XingInfoHeader => AudioDurationSource.Mp3XingInfoHeader,
                Mp3DurationReader.DurationSource.VbriHeader => AudioDurationSource.Mp3VbriHeader,
                Mp3DurationReader.DurationSource.StableCbrEstimate => AudioDurationSource.Mp3StableCbrEstimate,
                _ => AudioDurationSource.None
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TagExtraction;

namespace Chaptarr.Core.Test.MediaFiles
{
    internal sealed class DurationExternalToolsStub : IExternalToolsService
    {
        public bool FfprobeAvailable { get; set; }
        public TimeSpan FfprobeDuration { get; set; }
        public int AvailabilityChecks { get; private set; }
        public int DurationCalls { get; private set; }
        public int LastTimeoutMs { get; private set; }

        public string GetFFprobePath() => throw new NotImplementedException();
        public string GetFFmpegPath() => throw new NotImplementedException();
        public string GetM4bToolPath() => throw new NotImplementedException();

        public bool IsFFprobeAvailable()
        {
            AvailabilityChecks++;
            return FfprobeAvailable;
        }

        public bool IsFFmpegAvailable() => throw new NotImplementedException();
        public bool IsM4bToolAvailable() => throw new NotImplementedException();
        public string ExecuteFFprobe(string arguments) => throw new NotImplementedException();

        public string ExecuteFFprobe(IReadOnlyList<string> arguments, int timeoutMs = 10000)
        {
            if (!arguments.Contains("format=duration"))
            {
                return string.Empty;
            }

            DurationCalls++;
            LastTimeoutMs = timeoutMs;
            return FfprobeDuration > TimeSpan.Zero
                ? FfprobeDuration.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;
        }

        public string ExecuteFFmpeg(string arguments) => throw new NotImplementedException();
        public string ExecuteFFmpeg(IReadOnlyList<string> arguments, int timeoutMs = 10000, bool preferStderrOnEmpty = false) => throw new NotImplementedException();
        public string ExecuteM4bTool(IReadOnlyList<string> arguments, int timeoutMs = 3600000, Action<string> outputHandler = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ExternalToolResult ExecuteM4bToolDetailed(IReadOnlyList<string> arguments, int timeoutMs = 3600000, Action<string> outputHandler = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    internal sealed class DurationResolverStub : IAudioDurationResolver
    {
        public AudioDurationResult Mp3Result { get; set; }
        public TimeSpan FfprobeDuration { get; set; }
        public int ResolveMp3Calls { get; private set; }
        public int FfprobeDurationCalls { get; private set; }
        public bool IsFfprobeAvailable { get; set; }

        public AudioDurationResult ResolveMp3(string path)
        {
            ResolveMp3Calls++;
            return Mp3Result;
        }

        public TimeSpan GetFfprobeDuration(string path)
        {
            FfprobeDurationCalls++;
            return FfprobeDuration;
        }
    }
}

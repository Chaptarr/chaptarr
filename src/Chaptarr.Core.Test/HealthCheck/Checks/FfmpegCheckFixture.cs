using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.HealthCheck.Checks
{
    [TestFixture]
    public class FfmpegCheckFixture
    {
        private class ExternalToolsProxy : DispatchProxy
        {
            public bool FFmpegAvailable { get; set; }
            public bool FFprobeAvailable { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IExternalToolsService.IsFFmpegAvailable) => FFmpegAvailable,
                    nameof(IExternalToolsService.IsFFprobeAvailable) => FFprobeAvailable,
                    _ => throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}")
                };
            }
        }

        private sealed class StubLocalizationService : ILocalizationService
        {
            public Dictionary<string, string> GetLocalizationDictionary()
            {
                return new Dictionary<string, string>();
            }

            public string GetLocalizedString(string phrase)
            {
                return phrase == "ExternalToolsHealthCheckMessage"
                    ? "Required media tools are unavailable: {0}. Install FFmpeg, including FFprobe, and make sure both are available on Chaptarr's PATH."
                    : phrase;
            }

            public string GetLocalizedString(string phrase, Dictionary<string, object> tokens)
            {
                return GetLocalizedString(phrase);
            }
        }

        private static FfmpegCheck CreateSubject(bool ffmpegAvailable, bool ffprobeAvailable)
        {
            var externalTools = DispatchProxy.Create<IExternalToolsService, ExternalToolsProxy>();
            var proxy = (ExternalToolsProxy)(object)externalTools;
            proxy.FFmpegAvailable = ffmpegAvailable;
            proxy.FFprobeAvailable = ffprobeAvailable;

            return new FfmpegCheck(externalTools, new StubLocalizationService());
        }

        [Test]
        public void should_be_healthy_when_ffmpeg_and_ffprobe_are_available()
        {
            var result = CreateSubject(true, true).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Ok));
        }

        [TestCase(false, true, "FFmpeg")]
        [TestCase(true, false, "FFprobe")]
        [TestCase(false, false, "FFmpeg, FFprobe")]
        public void should_warn_and_name_each_missing_tool(bool ffmpegAvailable, bool ffprobeAvailable, string missingTools)
        {
            var result = CreateSubject(ffmpegAvailable, ffprobeAvailable).Check();

            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Warning));
            Assert.That(result.Message, Does.Contain($"unavailable: {missingTools}."));
        }
    }
}

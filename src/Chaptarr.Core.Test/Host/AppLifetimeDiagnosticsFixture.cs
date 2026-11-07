using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Moq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Host;

namespace Chaptarr.Core.Test.Host
{
    [TestFixture]
    [NonParallelizable]
    public class AppLifetimeDiagnosticsFixture
    {
        private static readonly string[] ImageEnvironmentVariables =
        {
            "BOOKSHELF_IMAGE_VERSION",
            "BOOKSHELF_IMAGE_TAG",
            "BOOKSHELF_IMAGE_REVISION",
            "BOOKSHELF_IMAGE_REF",
            "BOOKSHELF_IMAGE_FLAVOR",
            "BOOKSHELF_IMAGE_BUILD_DATE",
            "BOOKSHELF_CONTAINER",
            "DOTNET_RUNNING_IN_CONTAINER"
        };

        private Dictionary<string, string> _previousEnvironment;

        [SetUp]
        public void SetUp()
        {
            _previousEnvironment = ImageEnvironmentVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            foreach (var name in ImageEnvironmentVariables)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var (name, value) in _previousEnvironment)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        [Test]
        public void should_log_legacy_image_http_and_ui_diagnostics_at_startup()
        {
            Environment.SetEnvironmentVariable("BOOKSHELF_IMAGE_VERSION", "0.4.21.71");
            Environment.SetEnvironmentVariable("BOOKSHELF_IMAGE_TAG", "stable\r\nforged");
            Environment.SetEnvironmentVariable("BOOKSHELF_IMAGE_REVISION", "legacy-revision");
            Environment.SetEnvironmentVariable("BOOKSHELF_IMAGE_REF", "legacy/main");
            Environment.SetEnvironmentVariable("BOOKSHELF_IMAGE_FLAVOR", "standard");
            Environment.SetEnvironmentVariable("BOOKSHELF_IMAGE_BUILD_DATE", "2026-10-23T00:00:00Z");
            Environment.SetEnvironmentVariable("BOOKSHELF_CONTAINER", "true");

            using var applicationStarted = new CancellationTokenSource();
            using var applicationStopped = new CancellationTokenSource();
            var hostLifetime = new Mock<IHostApplicationLifetime>();
            hostLifetime.SetupGet(lifetime => lifetime.ApplicationStarted).Returns(applicationStarted.Token);
            hostLifetime.SetupGet(lifetime => lifetime.ApplicationStopped).Returns(applicationStopped.Token);

            var config = new Mock<IConfigFileProvider>();
            config.SetupGet(value => value.LaunchBrowser).Returns(false);
            config.SetupGet(value => value.UiFolder).Returns("UI");
            config.SetupGet(value => value.BindAddress).Returns("127.0.0.1");
            config.SetupGet(value => value.Port).Returns(8787);
            config.SetupGet(value => value.EnableSsl).Returns(true);
            config.SetupGet(value => value.SslPort).Returns(6868);
            config.SetupGet(value => value.UrlBase).Returns("sub/path");
            config.SetupGet(value => value.LogLevel).Returns("info");

            var runtime = new Mock<IRuntimeInfo>();
            runtime.SetupProperty(value => value.IsStarting, true);
            runtime.SetupProperty(value => value.IsExiting, false);

            var startup = new Mock<IStartupContext>();
            startup.SetupGet(value => value.Flags).Returns(new HashSet<string>());

            var appFolder = new Mock<IAppFolderInfo>();
            appFolder.SetupGet(value => value.StartUpFolder).Returns(
                Path.Combine(Path.GetTempPath(), "chaptarr-app-lifetime-" + Guid.NewGuid().ToString("N")));

            var deployment = new Mock<IDeploymentInfoProvider>();
            deployment.SetupGet(value => value.PackageVersion).Returns("0.9.978");
            deployment.SetupGet(value => value.PackageBranch).Returns("main");

            var memory = new MemoryTarget("app-lifetime-diagnostics") { Layout = "${level}|${message}" };
            var loggingConfiguration = new LoggingConfiguration();
            loggingConfiguration.AddRule(LogLevel.Trace, LogLevel.Fatal, memory);
            using var logFactory = new LogFactory { Configuration = loggingConfiguration };

            _ = new AppLifetime(
                hostLifetime.Object,
                config.Object,
                runtime.Object,
                appFolder.Object,
                deployment.Object,
                startup.Object,
                Mock.Of<IBrowserService>(),
                Mock.Of<NzbDrone.Common.Processes.IProcessProvider>(),
                Mock.Of<IEventAggregator>(),
                logFactory.GetLogger(nameof(AppLifetime)));

            applicationStarted.Cancel();

            var messages = memory.Logs.ToArray();
            var build = messages.Single(message => message.Contains("Build/runtime:"));
            var http = messages.Single(message => message.Contains("HTTP server:"));

            Assert.Multiple(() =>
            {
                Assert.That(build, Does.Contain("imageVersion=0.4.21.71"));
                Assert.That(build, Does.Contain("imageTag=stable  forged"), "legacy image metadata must be logged on one line");
                Assert.That(build, Does.Contain("imageRef=legacy/main"));
                Assert.That(build, Does.Contain("container=true"));
                Assert.That(build, Does.Not.Contain("\r"));
                Assert.That(build, Does.Not.Contain("\n"));
                Assert.That(http, Does.Contain("bindAddress=127.0.0.1; port=8787; sslEnabled=True; sslPort=6868"));
                Assert.That(http, Does.Contain("urlBase=/sub/path/; logLevel=info"));
                Assert.That(messages.Any(message => message.Contains("Web UI entry point is missing:")), Is.True);
                Assert.That(runtime.Object.IsStarting, Is.False);
            });
        }
    }
}

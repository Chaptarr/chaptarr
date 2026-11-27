using System.Collections.Generic;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.HealthCheck.Checks
{
    public class CalibreRootFolderCheckFixture
    {
        [Test]
        public void every_invalid_root_warns_while_health_retains_the_first_error()
        {
            using var logs = new LogFactory();
            var messages = new MemoryTarget { Layout = "${message}" };
            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Warn, LogLevel.Fatal, messages);
            logs.Configuration = config;
            var roots = new List<RootFolder>
            {
                new() { Name = "First", Path = @"C:\first".AsOsAgnostic(), IsCalibreLibrary = true, CalibreSettings = new() { Host = "localhost" } },
                new() { Name = "Second", Path = @"C:\second".AsOsAgnostic(), IsCalibreLibrary = true, CalibreSettings = new() { Host = "localhost" } }
            };
            var reads = 0;
            var proxy = Proxy<ICalibreProxy>((_, _) => { reads++; return new List<string> { @"C:\actual\author\book\file.epub".AsOsAgnostic() }; });
            var result = new CalibreRootFolderCheck(Proxy<IDiskProvider>((_, _) => true), Proxy<IRootFolderService>((_, _) => roots), proxy, null, logs.GetLogger("roots"), null).Check();
            Assert.That(reads, Is.EqualTo(2));
            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Error));
            Assert.That(messages.Logs, Has.Count.EqualTo(2));
            Assert.That(messages.Logs[0], Is.EqualTo(result.Message));
            Assert.That(messages.Logs[1], Does.Contain("Second"));
        }
    }
}

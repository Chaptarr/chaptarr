using System;
using System.Collections.Generic;
using Chaptarr.Core.Test.Common;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.ImportLists.Goodreads
{
    [TestFixture]
    public class GoodreadsImportListFailureFixture
    {
        private sealed class CaptureTarget : Target
        {
            public List<LogEventInfo> Events { get; } = new();
            protected override void Write(LogEventInfo message) => Events.Add(message);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void fetch_failure_warns_once_with_configured_name_provider_id_and_cause(bool series)
        {
            var error = new InvalidOperationException("Fixture provider failure");
            var failures = new List<int>();
            var status = InterfaceProxy.Proxy<IImportListStatusService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IImportListStatusService.RecordFailure)), "Failure must not record success.");
                failures.Add((int)args[0]);
                return null;
            });
            using var factory = new LogFactory();
            var target = new CaptureTarget();
            var configuration = new LoggingConfiguration(factory);
            configuration.AddRule(LogLevel.Warn, LogLevel.Fatal, target);
            factory.Configuration = configuration;
            var logger = factory.GetLogger(nameof(GoodreadsImportListFailureFixture));
            IImportList list = series
                ? new GoodreadsSeriesImportList(InterfaceProxy.Proxy<IProvideSeriesInfo>((_, _) => throw error),
                    status, null, null, null, null, null, null, null, logger)
                : new GoodreadsListImportList(InterfaceProxy.Proxy<IProvideListInfo>((_, _) => throw error),
                    status, null, null, null, null, null, null, null, logger);
            list.Definition = new ImportListDefinition
            {
                Id = 42, Name = "My wanted reading list",
                Settings = series ? new GoodreadsSeriesImportListSettings { SeriesId = 123 }
                    : new GoodreadsListImportListSettings { ListId = 456 }
            };

            Assert.That(list.Fetch(), Is.Empty);
            Assert.That(failures, Is.EqualTo(new[] { 42 }));
            Assert.That(target.Events, Has.Count.EqualTo(1));
            Assert.That(target.Events[0].Level, Is.EqualTo(LogLevel.Warn));
            Assert.That(target.Events[0].Exception, Is.SameAs(error));
            Assert.That(target.Events[0].FormattedMessage, Does.Contain("My wanted reading list")
                .And.Contain(series ? "SeriesId=123" : "ListId=456"));
        }
    }
}

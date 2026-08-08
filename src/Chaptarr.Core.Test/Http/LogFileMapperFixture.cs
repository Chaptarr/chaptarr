using System;
using System.Reflection;
using NLog;
using NUnit.Framework;
using Chaptarr.Http.Frontend.Mappers;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class LogFileMapperFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class AppFolderInfoStub : IAppFolderInfo
        {
            public string AppDataFolder { get; init; } = "/data";
            public string TempFolder { get; init; } = "/tmp";
            public string StartUpFolder { get; init; } = "/app";
        }

        [TestCase("/logfile/chaptarr.txt", true)]
        [TestCase("/logfile/CHAPTARR.TXT", true)]
        [TestCase("/logfile/report.log", false)]
        [TestCase("/logfile/CON.txt", false)]
        [TestCase("/logfile/trace.txt:evil", false)]
        [TestCase("/logfile/../trace.txt", true)]
        [TestCase("/logfile/a b.txt", false)]
        [TestCase("/logfile/name?.txt", false)]
        public void log_file_mapper_should_only_accept_safe_txt_file_names(string resourceUrl, bool expected)
        {
            var mapper = new LogFileMapper(
                new AppFolderInfoStub(),
                DispatchProxy.Create<IDiskProvider, ThrowingProxy<IDiskProvider>>(),
                LogManager.GetCurrentClassLogger());

            Assert.That(mapper.CanHandle(resourceUrl), Is.EqualTo(expected));
        }

        [TestCase("/updatelogfile/update.txt", true)]
        [TestCase("/updatelogfile/LPT1.txt", false)]
        [TestCase("/updatelogfile/update.txt:ads", false)]
        public void update_log_file_mapper_should_share_the_same_safe_filename_rules(string resourceUrl, bool expected)
        {
            var mapper = new UpdateLogFileMapper(
                new AppFolderInfoStub(),
                DispatchProxy.Create<IDiskProvider, ThrowingProxy<IDiskProvider>>(),
                LogManager.GetCurrentClassLogger());

            Assert.That(mapper.CanHandle(resourceUrl), Is.EqualTo(expected));
        }
    }
}

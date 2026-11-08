using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class RedownloadFailedDownloadServiceFixture
    {
        [TestCase(1)]
        [TestCase(2)]
        public void retry_should_keep_exact_failed_book_ids_even_when_they_cover_the_author(int count)
        {
            var (subject, queue) = Build();
            var ids = Enumerable.Range(101, count).ToList();

            subject.Handle(new DownloadFailedEvent { AuthorId = 10, BookIds = ids });

            Assert.That(queue.Commands, Has.Count.EqualTo(1));
            Assert.That(queue.Commands[0], Is.TypeOf<BookSearchCommand>());
            Assert.That(((BookSearchCommand)queue.Commands[0]).BookIds, Is.EqualTo(ids));
        }

        [TestCase(true, true, true)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        public void retry_should_preserve_existing_opt_outs(bool skip, bool enabled, bool interactiveEnabled)
        {
            var (subject, queue) = Build(enabled, interactiveEnabled);

            subject.Handle(new DownloadFailedEvent
            {
                AuthorId = 10, BookIds = new List<int> { 101, 102 },
                SkipRedownload = skip, ReleaseSource = ReleaseSourceType.InteractiveSearch
            });

            Assert.That(queue.Commands, Is.Empty);
        }

        private static (RedownloadFailedDownloadService, QueueProxy) Build(bool enabled = true, bool interactiveEnabled = true)
        {
            var config = DispatchProxy.Create<IConfigService, ConfigProxy>();
            ((ConfigProxy)(object)config).Enabled = enabled;
            ((ConfigProxy)(object)config).InteractiveEnabled = interactiveEnabled;
            var queue = DispatchProxy.Create<IManageCommandQueue, QueueProxy>();
            return (new RedownloadFailedDownloadService(config, queue, LogManager.GetCurrentClassLogger()), (QueueProxy)(object)queue);
        }

        private class ConfigProxy : DispatchProxy
        {
            public bool Enabled { get; set; }
            public bool InteractiveEnabled { get; set; }

            protected override object Invoke(MethodInfo method, object[] args) => method.Name switch
            {
                "get_AutoRedownloadFailed" => Enabled,
                "get_AutoRedownloadFailedFromInteractiveSearch" => InteractiveEnabled,
                _ => throw new NotImplementedException(method.Name)
            };
        }

        private class QueueProxy : DispatchProxy
        {
            public List<Command> Commands { get; } = new();

            protected override object Invoke(MethodInfo method, object[] args)
            {
                Assert.That(method.Name, Is.EqualTo("Push"));
                Commands.Add((Command)args[0]);
                return null;
            }
        }
    }
}

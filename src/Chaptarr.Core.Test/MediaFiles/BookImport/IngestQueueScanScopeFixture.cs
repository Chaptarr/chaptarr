using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class IngestQueueScanScopeFixture
    {
        private class QueueProxy : DispatchProxy
        {
            public List<string> QueriedPrefixes { get; } = new();
            public List<int> AfterIds { get; } = new();
            public List<IngestQueueItem> Items { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IIngestQueueRepository.GetQueuedItemsUnderPath))
                {
                    var prefix = (string)args[0];
                    var afterId = (int)args[2];
                    QueriedPrefixes.Add(prefix);
                    AfterIds.Add(afterId);
                    return Items.Where(item => item.Id > afterId && item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                throw new NotSupportedException(targetMethod?.Name);
            }
        }

        [Test]
        public void exact_scope_should_return_only_requested_paths_even_when_prefix_query_returns_neighbors()
        {
            var repository = DispatchProxy.Create<IIngestQueueRepository, QueueProxy>();
            var proxy = (QueueProxy)(object)repository;
            proxy.Items = new List<IngestQueueItem>
            {
                new() { Id = 1, Path = "/library/Book/Disc 1.mp3", Status = "queued" },
                new() { Id = 2, Path = "/library/Book/Disc 1.mp3.bak", Status = "queued" },
                new() { Id = 3, Path = "/library/Book/Disc 2.mp3", Status = "queued" }
            };

            var scope = new IngestQueueScanScope(
                "/library/Book",
                new[] { "/library/Book/Disc 1.mp3", "/library/Book/Disc 2.mp3" });

            var result = scope.GetQueuedItems(repository, 100);

            Assert.Multiple(() =>
            {
                Assert.That(result.Select(item => item.Id), Is.EqualTo(new[] { 1, 3 }));
                Assert.That(proxy.QueriedPrefixes, Is.EquivalentTo(scope.ExactPaths));
            });
        }

        [Test]
        public void subtree_scope_should_query_only_the_requested_subtree()
        {
            var repository = DispatchProxy.Create<IIngestQueueRepository, QueueProxy>();
            var proxy = (QueueProxy)(object)repository;
            var scope = new IngestQueueScanScope("/library/One Author");

            scope.GetQueuedItems(repository, 100);

            Assert.That(proxy.QueriedPrefixes, Is.EqualTo(new[] { "/library/One Author" }));
        }

        [Test]
        public void subtree_scope_should_forward_the_discovery_page_cursor()
        {
            var repository = DispatchProxy.Create<IIngestQueueRepository, QueueProxy>();
            var proxy = (QueueProxy)(object)repository;
            proxy.Items = new List<IngestQueueItem>
            {
                new() { Id = 2500, Path = "/library/One Author/Book A/file.m4b", Status = "queued" },
                new() { Id = 2501, Path = "/library/One Author/Book B/file.m4b", Status = "queued" }
            };
            var scope = new IngestQueueScanScope("/library/One Author");

            var result = scope.GetQueuedItems(repository, 2500, afterId: 2500);

            Assert.Multiple(() =>
            {
                Assert.That(proxy.AfterIds, Is.EqualTo(new[] { 2500 }));
                Assert.That(result.Select(item => item.Id), Is.EqualTo(new[] { 2501 }));
            });
        }
    }
}

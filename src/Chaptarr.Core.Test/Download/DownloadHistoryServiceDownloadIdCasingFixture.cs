using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DownloadHistoryServiceDownloadIdCasingFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class DownloadHistoryRepositoryProxy : DispatchProxy
        {
            public List<string> FindByDownloadIdCalls { get; } = new();
            public Dictionary<string, List<DownloadHistory>> ByDownloadId { get; } = new(StringComparer.Ordinal);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IDownloadHistoryRepository.FindByDownloadId))
                {
                    var downloadId = (string)args[0];
                    FindByDownloadIdCalls.Add(downloadId);

                    return ByDownloadId.TryGetValue(downloadId, out var items)
                        ? items
                        : new List<DownloadHistory>();
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod.DeclaringType?.Name}.{targetMethod.Name}");
            }
        }

        [Test]
        public void get_latest_download_history_item_should_normalize_to_uppercase()
        {
            var repository = DispatchProxy.Create<IDownloadHistoryRepository, DownloadHistoryRepositoryProxy>();
            var proxy = (DownloadHistoryRepositoryProxy)(object)repository;

            var grabbed = new DownloadHistory
            {
                DownloadId = "ABC",
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                Date = DateTime.UtcNow
            };

            proxy.ByDownloadId["ABC"] = new List<DownloadHistory> { grabbed };

            var historyService = DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>();
            var service = new DownloadHistoryService(repository, historyService);

            var result = service.GetLatestDownloadHistoryItem("abc");

            Assert.That(result, Is.SameAs(grabbed));
            Assert.That(proxy.FindByDownloadIdCalls, Is.EqualTo(new[] { "ABC" }));
        }

        [Test]
        public void should_preserve_bookless_imported_download_state()
        {
            var repository = DispatchProxy.Create<IDownloadHistoryRepository, DownloadHistoryRepositoryProxy>();
            var proxy = (DownloadHistoryRepositoryProxy)(object)repository;
            var imported = new DownloadHistory
            {
                AuthorId = 12,
                BookId = 0,
                DownloadId = "ABC",
                EventType = DownloadHistoryEventType.DownloadImported,
                Date = DateTime.UtcNow
            };
            proxy.ByDownloadId["ABC"] = new List<DownloadHistory> { imported };

            var historyService = DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>();
            var service = new DownloadHistoryService(repository, historyService);

            Assert.That(service.DownloadAlreadyImported("abc"), Is.True);
            Assert.That(proxy.FindByDownloadIdCalls, Is.EqualTo(new[] { "ABC" }));
        }

        [Test]
        public void should_allow_a_newer_bookless_grab_to_supersede_an_older_import()
        {
            var repository = DispatchProxy.Create<IDownloadHistoryRepository, DownloadHistoryRepositoryProxy>();
            var proxy = (DownloadHistoryRepositoryProxy)(object)repository;
            proxy.ByDownloadId["ABC"] = new List<DownloadHistory>
            {
                new DownloadHistory
                {
                    AuthorId = 12,
                    BookId = 0,
                    DownloadId = "ABC",
                    EventType = DownloadHistoryEventType.DownloadGrabbed,
                    Date = DateTime.UtcNow
                },
                new DownloadHistory
                {
                    AuthorId = 12,
                    BookId = 0,
                    DownloadId = "ABC",
                    EventType = DownloadHistoryEventType.DownloadImported,
                    Date = DateTime.UtcNow.AddMinutes(-1)
                }
            };

            var historyService = DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>();
            var service = new DownloadHistoryService(repository, historyService);

            Assert.That(service.DownloadAlreadyImported("abc"), Is.False);
            Assert.That(proxy.FindByDownloadIdCalls, Is.EqualTo(new[] { "ABC" }));
        }
    }
}

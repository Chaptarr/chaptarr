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
    }
}

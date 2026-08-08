using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.History;

namespace Chaptarr.Core.Test.History
{
    [TestFixture]
    public class HistoryServiceDownloadIdCasingFixture
    {
        private class HistoryRepositoryProxy : DispatchProxy
        {
            public List<string> FindByDownloadIdCalls { get; } = new();
            public List<(List<string> DownloadIds, EntityHistoryEventType? EventType)> FindByDownloadIdsCalls { get; } = new();
            public List<string> MostRecentForDownloadIdCalls { get; } = new();
            public Dictionary<string, List<EntityHistory>> ByDownloadId { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, EntityHistory> MostRecentByDownloadId { get; } = new(StringComparer.Ordinal);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IHistoryRepository.FindByDownloadId))
                {
                    var downloadId = (string)args[0];
                    FindByDownloadIdCalls.Add(downloadId);

                    return ByDownloadId.TryGetValue(downloadId, out var items)
                        ? items
                        : new List<EntityHistory>();
                }

                if (targetMethod.Name == nameof(IHistoryRepository.FindByDownloadIds))
                {
                    var downloadIds = ((List<string>)args[0]).ToList();
                    var eventType = args[1] is EntityHistoryEventType value ? value : (EntityHistoryEventType?)null;
                    FindByDownloadIdsCalls.Add((downloadIds, eventType));

                    return downloadIds
                        .SelectMany(downloadId => ByDownloadId.TryGetValue(downloadId, out var items) ? items : new List<EntityHistory>())
                        .Where(item => !eventType.HasValue || item.EventType == eventType.Value)
                        .ToList();
                }

                if (targetMethod.Name == nameof(IHistoryRepository.MostRecentForDownloadId))
                {
                    var downloadId = (string)args[0];
                    MostRecentForDownloadIdCalls.Add(downloadId);

                    return MostRecentByDownloadId.TryGetValue(downloadId, out var item)
                        ? item
                        : null;
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod.DeclaringType?.Name}.{targetMethod.Name}");
            }
        }

        [Test]
        public void find_by_download_id_should_normalize_to_uppercase()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var proxy = (HistoryRepositoryProxy)(object)repository;

            var historyItem = new EntityHistory
            {
                DownloadId = "ABC"
            };

            proxy.ByDownloadId["ABC"] = new List<EntityHistory> { historyItem };

            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());

            var results = service.FindByDownloadId("abc");

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0], Is.SameAs(historyItem));
            Assert.That(proxy.FindByDownloadIdCalls, Is.EqualTo(new[] { "ABC" }));
        }

        [Test]
        public void find_by_download_ids_should_normalize_to_uppercase_and_filter_event_type()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var proxy = (HistoryRepositoryProxy)(object)repository;

            var grabbed = new EntityHistory
            {
                DownloadId = "ABC",
                EventType = EntityHistoryEventType.Grabbed
            };

            var imported = new EntityHistory
            {
                DownloadId = "ABC",
                EventType = EntityHistoryEventType.BookFileImported
            };

            proxy.ByDownloadId["ABC"] = new List<EntityHistory> { grabbed, imported };

            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());

            var results = service.FindByDownloadIds(new[] { "abc", "ABC", "" }, EntityHistoryEventType.Grabbed);

            Assert.That(results, Is.EqualTo(new[] { grabbed }));
            Assert.That(proxy.FindByDownloadIdsCalls, Has.Count.EqualTo(1));
            Assert.That(proxy.FindByDownloadIdsCalls[0].DownloadIds, Is.EqualTo(new[] { "ABC" }));
            Assert.That(proxy.FindByDownloadIdsCalls[0].EventType, Is.EqualTo(EntityHistoryEventType.Grabbed));
        }

        [Test]
        public void most_recent_for_download_id_should_normalize_to_uppercase()
        {
            var repository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var proxy = (HistoryRepositoryProxy)(object)repository;

            var historyItem = new EntityHistory
            {
                DownloadId = "ABC"
            };

            proxy.MostRecentByDownloadId["ABC"] = historyItem;

            var service = new HistoryService(repository, LogManager.GetCurrentClassLogger());

            var result = service.MostRecentForDownloadId("abc");

            Assert.That(result, Is.SameAs(historyItem));
            Assert.That(proxy.MostRecentForDownloadIdCalls, Is.EqualTo(new[] { "ABC" }));
        }
    }
}

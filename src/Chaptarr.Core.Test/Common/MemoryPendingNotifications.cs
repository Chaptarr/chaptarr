using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Notifications;

namespace Chaptarr.Core.Test.Common
{
    internal sealed class MemoryPendingNotifications : IPendingNotificationRepository
    {
        private readonly List<PendingNotificationItem> _items = new();
        private int _nextId;
        public static MemoryPendingNotifications For(ICacheManager cache) =>
            cache.GetCache<MemoryPendingNotifications>(typeof(MemoryPendingNotifications)).Get("queue", () => new MemoryPendingNotifications());
        public void Enqueue(int notificationId, string path, bool isFolder = false)
        {
            if (_items.Any(item => item.NotificationId == notificationId && item.IsFolder == isFolder && PathEqualityComparer.Instance.Equals(item.Path, path))) return;
            _items.Add(new PendingNotificationItem { Id = ++_nextId, NotificationId = notificationId, Path = path, IsFolder = isFolder });
        }
        public void Enqueue(int notificationId, IEnumerable<string> paths, bool isFolder = false)
        {
            foreach (var path in paths) Enqueue(notificationId, path, isFolder);
        }
        public IEnumerable<PendingNotificationItem> GetPending(int notificationId, bool foldersOnly = false) => _items.Where(item => item.NotificationId == notificationId && (!foldersOnly || item.IsFolder)).ToList();
        public bool HasPending(int notificationId) => _items.Any(item => item.NotificationId == notificationId);
        public void Complete(IEnumerable<PendingNotificationItem> items)
        {
            var ids = items.Select(item => item.Id).ToHashSet();
            _items.RemoveAll(item => ids.Contains(item.Id));
        }
    }
}

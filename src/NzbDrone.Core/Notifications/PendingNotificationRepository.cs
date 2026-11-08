using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NLog;
using Dapper;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.Notifications
{
    public class PendingNotificationItem : ModelBase
    {
        public int NotificationId { get; set; }
        public string Path { get; set; }
        public string PathKey { get; set; }
        public bool IsFolder { get; set; }

        public static string GetPathKey(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OsInfo.IsWindows ? path.ToUpperInvariant() : path)));
    }

    public interface IPendingNotificationRepository
    {
        void Enqueue(int notificationId, string path, bool isFolder = false);
        void Enqueue(int notificationId, IEnumerable<string> paths, bool isFolder = false);
        IEnumerable<PendingNotificationItem> GetPending(int notificationId, bool foldersOnly = false);
        bool HasPending(int notificationId);
        void Complete(IEnumerable<PendingNotificationItem> items);
    }

    // Callers use their shared per-provider cache lock for enqueue and drain. Only
    // the lock is in memory; work survives provider recreation and application restart.
    public class PendingNotificationRepository : BasicRepository<PendingNotificationItem>,
        IPendingNotificationRepository, IHandle<ProviderDeletedEvent<INotification>>
    {
        public const int BatchSize = 500;
        private readonly Logger _logger;

        public PendingNotificationRepository(IMainDatabase database, IEventAggregator events, Logger logger) : base(database, events)
        {
            _logger = logger;
        }

        public void Enqueue(int notificationId, string path, bool isFolder = false) => Enqueue(notificationId, new[] { path }, isFolder);

        public void Enqueue(int notificationId, IEnumerable<string> paths, bool isFolder = false)
        {
            using var connection = _database.OpenConnection();
            foreach (var batch in paths.Distinct(OsInfo.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Chunk((SqliteVariableLimit.MaxParameters - 2) / 2))
            {
                var parameters = new DynamicParameters(new { notificationId, isFolder });
                var values = batch.Select((path, index) =>
                {
                    parameters.Add("path" + index, path);
                    parameters.Add("key" + index, PendingNotificationItem.GetPathKey(path));
                    return $"(@notificationId,@path{index},@key{index},@isFolder)";
                });
                connection.Execute(@"INSERT INTO ""PendingNotificationItems"" (""NotificationId"",""Path"",""PathKey"",""IsFolder"")
                    VALUES " + string.Join(",", values) + @"
                    ON CONFLICT (""NotificationId"",""PathKey"",""IsFolder"") DO NOTHING", parameters);
            }
        }

        public IEnumerable<PendingNotificationItem> GetPending(int notificationId, bool foldersOnly = false)
        {
            var timer = Stopwatch.StartNew();
            var afterId = 0;
            var count = 0;
            var batches = 0;
            try
            {
                using var connection = _database.OpenConnection();
                while (true)
                {
                    var batch = connection.Query<PendingNotificationItem>(@"SELECT * FROM ""PendingNotificationItems""
                        WHERE ""NotificationId"" = @notificationId AND ""Id"" > @afterId" +
                        (foldersOnly ? @" AND ""IsFolder"" = @foldersOnly" : string.Empty) +
                        @" ORDER BY ""Id"" LIMIT @limit", new { notificationId, afterId, foldersOnly, limit = BatchSize }).ToList();
                    if (batch.Count == 0) break;
                    batches++;
                    count += batch.Count;
                    afterId = batch[^1].Id;
                    foreach (var item in batch) yield return item;
                    if (batch.Count < BatchSize) break;
                }
            }
            finally
            {
                _logger.Debug("Read {0} pending notification items in {1} batches for connection {2} in {3} ms", count, batches, notificationId, timer.ElapsedMilliseconds);
            }
        }
        public bool HasPending(int notificationId)
        {
            using var connection = _database.OpenConnection();
            return connection.ExecuteScalar<bool>(@"SELECT EXISTS (SELECT 1 FROM ""PendingNotificationItems"" WHERE ""NotificationId"" = @notificationId)", new { notificationId });
        }
        public void Complete(IEnumerable<PendingNotificationItem> items) => DeleteMany(items.Select(item => item.Id));
        public void Handle(ProviderDeletedEvent<INotification> message) => Delete(item => item.NotificationId == message.ProviderId);
    }
}

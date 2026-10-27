using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IConversionJobRepository : IBasicRepository<ConversionJob>
    {
        ConversionJob FindByDownloadId(string downloadId);
        List<ConversionJob> NonCompleted();
        List<ConversionJob> ByDownloadIds(IEnumerable<string> downloadIds);
        List<ConversionJob> ByStatus(ConversionJobStatus status);
        List<ConversionJob> PendingCleanup();
        void DeleteByDownloadId(string downloadId);
        void DeleteCompletedBefore(DateTime cutoff);
    }

    public class ConversionJobRepository : BasicRepository<ConversionJob>, IConversionJobRepository
    {
        public ConversionJobRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public ConversionJob FindByDownloadId(string downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return null;
            }

            return Query(job => job.DownloadId == downloadId).SingleOrDefault();
        }

        public List<ConversionJob> NonCompleted()
        {
            return Query(job => job.Status != ConversionJobStatus.Completed);
        }

        public List<ConversionJob> ByDownloadIds(IEnumerable<string> downloadIds)
        {
            var ids = downloadIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray() ?? Array.Empty<string>();
            return ids.Length == 0 ? new List<ConversionJob>() : Query(job => ids.Contains(job.DownloadId));
        }

        public List<ConversionJob> ByStatus(ConversionJobStatus status)
        {
            return Query(job => job.Status == status);
        }

        public List<ConversionJob> PendingCleanup()
        {
            // RequestJson is written by the shared compact serializer; filter terminal payloads in SQL.
            return Query(job => job.Status == ConversionJobStatus.Cancelled && job.RequestJson.Contains("\"DiscardRequested\":true"));
        }

        public void DeleteByDownloadId(string downloadId)
        {
            if (!string.IsNullOrWhiteSpace(downloadId))
            {
                Delete(job => job.DownloadId == downloadId);
            }
        }

        public void DeleteCompletedBefore(DateTime cutoff)
        {
            Delete(job => job.Status == ConversionJobStatus.Completed && job.CompletedAt < cutoff);
        }
    }
}

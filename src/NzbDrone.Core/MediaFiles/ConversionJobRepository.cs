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
        ConversionJob NextQueued();
        List<ConversionJob> NonCompleted();
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

        public ConversionJob NextQueued()
        {
            return Query(job => job.Status == ConversionJobStatus.Queued)
                .OrderBy(job => job.CreatedAt)
                .FirstOrDefault();
        }

        public List<ConversionJob> NonCompleted()
        {
            return Query(job => job.Status != ConversionJobStatus.Completed);
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

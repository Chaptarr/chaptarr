using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Update.History
{
    public interface IUpdateHistoryRepository : IBasicRepository<UpdateHistory>
    {
        UpdateHistory LastInstalled();
        UpdateHistory PreviouslyInstalled();
        List<UpdateHistory> InstalledSince(DateTime dateTime);
    }

    public class UpdateHistoryRepository : BasicRepository<UpdateHistory>, IUpdateHistoryRepository
    {
        // This repository stores application update events in the MAIN database.
        // Previously it used ILogDatabase which caused 'no such table: UpdateHistory'
        // since the schema creates UpdateHistory in the main DB. Use IMainDatabase here.
        public UpdateHistoryRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public UpdateHistory LastInstalled()
        {
            var history = Query(x => x.EventType == UpdateHistoryEventType.Installed)
                               .OrderByDescending(v => v.Date)
                               .Take(1)
                               .FirstOrDefault();

            return history;
        }

        public UpdateHistory PreviouslyInstalled()
        {
            var history = Query(x => x.EventType == UpdateHistoryEventType.Installed)
                               .OrderByDescending(v => v.Date)
                               .Skip(1)
                               .Take(1)
                               .FirstOrDefault();

            return history;
        }

        public List<UpdateHistory> InstalledSince(DateTime dateTime)
        {
            var history = Query(v => v.EventType == UpdateHistoryEventType.Installed && v.Date >= dateTime)
                               .OrderBy(v => v.Date)
                               .ToList();

            return history;
        }
    }
}

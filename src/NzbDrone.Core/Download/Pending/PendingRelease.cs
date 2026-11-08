using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.Pending
{
    public class PendingRelease : ModelBase
    {
        public int AuthorId { get; set; }
        public string Title { get; set; }
        public DateTime Added { get; set; }
        public ParsedBookInfo ParsedBookInfo { get; set; }
        public ReleaseInfo Release { get; set; }
        public PendingReleaseReason Reason { get; set; }
        public PendingReleaseAdditionalInfo AdditionalInfo { get; set; }

        //Not persisted
        public RemoteBook RemoteBook { get; set; }
    }

    public class PendingReleaseAdditionalInfo : IEmbeddedDocument
    {
        public ReleaseSourceType ReleaseSource { get; set; }
        public List<int> BookIds { get; set; } = new();
        public bool AlternativeCopies { get; set; }
        public Dictionary<int, int> TargetEditionIds { get; set; } = new();
        public Dictionary<int, int> TargetCustomFormatScores { get; set; } = new();
    }
}

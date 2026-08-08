using System.Collections.Generic;

namespace NzbDrone.Core.DecisionEngine
{
    public class DownloadDecisionReason
    {
        public string Category { get; set; }
        public string Reason { get; set; }
        public string Details { get; set; }

        public DownloadDecisionReason(string category, string reason, string details = null)
        {
            Category = category;
            Reason = reason;
            Details = details;
        }

        public override string ToString()
        {
            return Details != null ? $"{Reason} ({Details})" : Reason;
        }
    }

    public class DownloadDecisionComparisonResult
    {
        public int Result { get; set; }
        public List<DownloadDecisionReason> Reasons { get; set; }

        public DownloadDecisionComparisonResult(int result)
        {
            Result = result;
            Reasons = new List<DownloadDecisionReason>();
        }

        public DownloadDecisionComparisonResult(int result, DownloadDecisionReason reason)
            : this(result)
        {
            if (reason != null)
            {
                Reasons.Add(reason);
            }
        }

        public DownloadDecisionComparisonResult(int result, List<DownloadDecisionReason> reasons)
            : this(result)
        {
            if (reasons != null)
            {
                Reasons.AddRange(reasons);
            }
        }
    }
}

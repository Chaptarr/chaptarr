using System.Collections.Generic;

namespace NzbDrone.Core.MediaFiles.Events
{
    public class ImportSummaryEvent : ImportProgressEvent
    {
        public int TotalAuthorsAdded { get; set; }
        public int TotalBooksMatched { get; set; }
        public int TotalBooksProcessed { get; set; }
        public List<string> FailedAuthors { get; set; }
        public long ElapsedMilliseconds { get; set; }

        public ImportSummaryEvent(
            string folderPath,
            int totalAuthorsAdded,
            int totalBooksMatched,
            int totalBooksProcessed,
            List<string> failedAuthors,
            long elapsedMilliseconds)
            : base(folderPath)
        {
            TotalAuthorsAdded = totalAuthorsAdded;
            TotalBooksMatched = totalBooksMatched;
            TotalBooksProcessed = totalBooksProcessed;
            FailedAuthors = failedAuthors ?? new List<string>();
            ElapsedMilliseconds = elapsedMilliseconds;
        }
    }
}

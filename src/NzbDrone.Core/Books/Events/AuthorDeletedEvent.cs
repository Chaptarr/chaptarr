using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class AuthorDeletedEvent : IEvent
    {
        public Author Author { get; private set; }
        public bool DeleteFiles { get; private set; }
        public bool AddImportListExclusion { get; private set; }
        public bool PreserveRetainedFileHistory { get; private set; }
        public IReadOnlyCollection<int> RetainedBookFileIds { get; private set; }
        public IReadOnlyDictionary<int, string> RetainedBookFileEditionIds { get; private set; }

        public AuthorDeletedEvent(
            Author author,
            bool deleteFiles,
            bool addImportListExclusion,
            bool preserveRetainedFileHistory = false,
            IEnumerable<int> retainedBookFileIds = null,
            IReadOnlyDictionary<int, string> retainedBookFileEditionIds = null)
        {
            Author = author;
            DeleteFiles = deleteFiles;
            AddImportListExclusion = addImportListExclusion;
            PreserveRetainedFileHistory = preserveRetainedFileHistory;
            RetainedBookFileIds = (retainedBookFileIds ?? Enumerable.Empty<int>())
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
            RetainedBookFileEditionIds = (retainedBookFileEditionIds ?? new Dictionary<int, string>())
                .Where(pair => pair.Key > 0 && !string.IsNullOrWhiteSpace(pair.Value))
                .GroupBy(pair => pair.Key)
                .ToDictionary(group => group.Key, group => group.First().Value);
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public sealed class LibraryIdentityChangedEvent : IEvent
    {
        public Author SourceAuthor { get; init; }
        public Author TargetAuthor { get; init; }
        public bool IsSplit { get; init; }
        public string SettingsResolution { get; init; }
        public HashSet<int> AuthorIds { get; }
        public HashSet<int> BookIds { get; }
        public HashSet<int> RemovedBookIds { get; }

        public LibraryIdentityChangedEvent(IEnumerable<int> authorIds, IEnumerable<int> bookIds, IEnumerable<int> removedBookIds = null)
        {
            AuthorIds = authorIds.ToHashSet();
            BookIds = bookIds.ToHashSet();
            RemovedBookIds = removedBookIds?.ToHashSet() ?? new HashSet<int>();
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Books.Events
{
    public class BooksEditedEvent : IEvent
    {
        public IReadOnlyList<BookEditedEvent> Changes { get; }

        public BooksEditedEvent(IEnumerable<BookEditedEvent> changes)
        {
            Changes = changes?.ToList() ?? new List<BookEditedEvent>();
        }
    }
}

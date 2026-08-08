using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public class ProviderAlias : Entity<ProviderAlias>
    {
        public string EntityType { get; set; }
        public int EntityId { get; set; }
        public string Scope { get; set; }
        public string Provider { get; set; }
        public string NormalizedProviderId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}

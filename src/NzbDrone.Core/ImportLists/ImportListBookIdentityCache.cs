using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.ImportLists
{
    public class ImportListBookIdentityCache : ModelBase
    {
        public string SourceProviderId { get; set; }
        public string BookProviderId { get; set; }
        public string AuthorProviderId { get; set; }
        public string Book { get; set; }
        public string Author { get; set; }
        public bool? EditionHasNarration { get; set; }
        public List<string> EditionNarratorNames { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}

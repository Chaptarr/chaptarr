using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace NzbDrone.Core.Datastore
{
    [DebuggerDisplay("{GetType().FullName} ID = {Id}")]
    public abstract class ModelBase
    {
        public int Id { get; set; }

        public virtual IEnumerable<string> PrepareForPersistence(IReadOnlyCollection<string> fields) => Array.Empty<string>();
    }
}

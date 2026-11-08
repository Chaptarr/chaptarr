using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Common;

namespace NzbDrone.Core.Download
{
    // Owned by one synchronous cleanup pass. Never retain filesystem evidence between polls.
    public sealed class DownloadCleanupContext
    {
        private readonly Dictionary<string, Lazy<HashSet<string>>> _parents = new(PathEqualityComparer.Instance);

        public int ParentProbes => _parents.Count;
        public int ReusedParentProbes { get; private set; }

        public bool ParentContainsPath(string outputPath, IDiskProvider diskProvider)
        {
            var parent = Path.GetDirectoryName(outputPath);
            if (!_parents.TryGetValue(parent, out var entries))
            {
                // Lazy also retains a failed read for this parent only. An inaccessible child
                // says nothing about another child, and other parents still get checked.
                entries = new Lazy<HashSet<string>>(() => diskProvider.GetDirectoryInfo(parent)
                    .EnumerateFileSystemInfos().Select(entry => entry.FullName)
                    .ToHashSet(PathEqualityComparer.Instance));
                _parents.Add(parent, entries);
            }
            else
            {
                ReusedParentProbes++;
            }

            return entries.Value.Contains(outputPath);
        }
    }
}

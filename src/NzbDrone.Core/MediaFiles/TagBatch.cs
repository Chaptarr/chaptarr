using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NzbDrone.Common;

namespace NzbDrone.Core.MediaFiles
{
    public class TagBatch
    {
        public readonly Stopwatch Timer = Stopwatch.StartNew();
        public readonly HashSet<int> MissingFiles = new HashSet<int>();
        public readonly HashSet<int> MissingAuthors = new HashSet<int>();
        public readonly HashSet<int> UnlinkedFiles = new HashSet<int>();
        public readonly Dictionary<string, (int Count, DateTime RetryAfter, string TriggerFile)> UnavailableLibraries = new();
        public int Read;
        public int CalibrePages;
        public int CalibreIds;
        public int Written;
        public int Skipped;
        public readonly HashSet<string> SkippedSymlinkPaths = new(PathEqualityComparer.Instance);
        public int SkippedSymlinks => SkippedSymlinkPaths.Count;
        public int Failed;
        public int Separated;
        public int RestoredTotals;
        public int NotInCalibre;
        public int Unavailable;
        public int Unconfirmed;
        public int AudioIncomplete;
        public int CalibreIncomplete;
        public int CalibreDetailsIncomplete;
        public int RootNotLinkedToCalibre;
        public int Incomplete => Unconfirmed + AudioIncomplete + CalibreIncomplete + CalibreDetailsIncomplete;
        public int MissingRootFolder;
        public readonly Dictionary<string, int> MissingCalibreBooks = new();
        public readonly Dictionary<string, int> InvalidCalibreSettings = new();
        public int Unsupported;
        public bool Stopped;
        public string CurrentAuthor;
        public int UnattemptedAuthors;

        public bool Unsuccessful => Failed > 0 || Incomplete > 0 || Unavailable > 0 || Stopped ||
            MissingFiles.Count > 0 || MissingAuthors.Count > 0;

        public bool HasProblems =>
            Unsuccessful || UnlinkedFiles.Count + NotInCalibre + MissingRootFolder + RootNotLinkedToCalibre +
                MissingCalibreBooks.Values.Sum() + InvalidCalibreSettings.Values.Sum() > 0;

        public void SkipUnavailable(string library, TimeSpan retryAfter, string triggerFile)
        {
            Unavailable++;
            UnavailableLibraries.TryGetValue(library, out var previous);
            UnavailableLibraries[library] = (previous.Count + 1, DateTime.UtcNow.Add(retryAfter), triggerFile);
        }
    }
}

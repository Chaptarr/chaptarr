using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    public static class ImportSessionProgressTracker
    {
        private const int CompletedCommandLimit = 1024;

        private class SessionState
        {
            public readonly object Sync = new object();

            public readonly HashSet<string> DiscoveredAuthorFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> DiscoveredBookUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> ProcessedAuthorFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> MatchedAuthorFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> UnmatchedAuthorFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> ProcessedBookUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<int> ImportedAuthorIds = new HashSet<int>();
            public readonly HashSet<string> ImportedBookUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int ImportedFiles;
            public bool StagingComplete;
            public DateTime LastUpdatedUtc = DateTime.UtcNow;
        }

        private static readonly ConcurrentDictionary<int, SessionState> Sessions = new ConcurrentDictionary<int, SessionState>();
        private static readonly ConcurrentDictionary<int, byte> ActiveCommands = new ConcurrentDictionary<int, byte>();
        private static readonly object LifecycleSync = new object();
        private static readonly HashSet<int> CompletedCommands = new HashSet<int>();
        private static readonly Queue<int> CompletedCommandOrder = new Queue<int>();

        public static bool Activate(int commandId)
        {
            if (commandId <= 0)
            {
                return false;
            }

            lock (LifecycleSync)
            {
                if (CompletedCommands.Contains(commandId))
                {
                    return false;
                }

                ActiveCommands.TryAdd(commandId, 0);
                Sessions.GetOrAdd(commandId, _ => new SessionState());
                return true;
            }
        }

        public static bool Complete(int commandId)
        {
            if (commandId <= 0)
            {
                return false;
            }

            lock (LifecycleSync)
            {
                if (CompletedCommands.Contains(commandId))
                {
                    return false;
                }

                if (!ActiveCommands.ContainsKey(commandId) && !Sessions.ContainsKey(commandId))
                {
                    return false;
                }

                var wasActive = ActiveCommands.TryRemove(commandId, out _);
                CompletedCommands.Add(commandId);
                CompletedCommandOrder.Enqueue(commandId);

                while (CompletedCommandOrder.Count > CompletedCommandLimit)
                {
                    CompletedCommands.Remove(CompletedCommandOrder.Dequeue());
                }

                return wasActive;
            }
        }

        public static bool IsImportActive => !ActiveCommands.IsEmpty;

        public static bool IsActive(int commandId)
        {
            return commandId > 0 && ActiveCommands.ContainsKey(commandId);
        }

        public static void BeginStagingPass(int commandId)
        {
            if (!Activate(commandId) || !Sessions.TryGetValue(commandId, out var state))
            {
                return;
            }

            lock (state.Sync)
            {
                state.StagingComplete = false;
                state.LastUpdatedUtc = DateTime.UtcNow;
            }
        }

        public static (int Processed, int Total) AddDiscoveredBookUnits(int commandId, IEnumerable<string> unitKeys)
        {
            var state = GetActiveState(commandId);
            if (state == null)
            {
                return (0, 0);
            }

            lock (state.Sync)
            {
                if (unitKeys != null)
                {
                    foreach (var key in unitKeys)
                    {
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            state.DiscoveredBookUnits.Add(key);
                        }
                    }
                }

                state.LastUpdatedUtc = DateTime.UtcNow;
                return (state.ProcessedBookUnits.Count, state.DiscoveredBookUnits.Count);
            }
        }

        public static (int Processed, int Total) MarkBookUnitsProcessed(int commandId, IEnumerable<string> unitKeys)
        {
            var state = GetActiveState(commandId);
            if (state == null)
            {
                return (0, 0);
            }

            lock (state.Sync)
            {
                if (unitKeys != null)
                {
                    foreach (var key in unitKeys)
                    {
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            state.ProcessedBookUnits.Add(key);
                        }
                    }
                }

                state.LastUpdatedUtc = DateTime.UtcNow;
                return (state.ProcessedBookUnits.Count, state.DiscoveredBookUnits.Count);
            }
        }

        public static (int Processed, int Total) AddDiscoveredAuthorFolders(int commandId, IEnumerable<string> authorFolderKeys)
        {
            var state = GetActiveState(commandId);
            if (state == null)
            {
                return (0, 0);
            }

            lock (state.Sync)
            {
                if (authorFolderKeys != null)
                {
                    foreach (var key in authorFolderKeys)
                    {
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            state.DiscoveredAuthorFolders.Add(key);
                        }
                    }
                }

                state.LastUpdatedUtc = DateTime.UtcNow;
                return (state.ProcessedAuthorFolders.Count, state.DiscoveredAuthorFolders.Count);
            }
        }

        public static (int Processed, int Total) MarkAuthorFolderProcessed(int commandId, string authorFolderKey)
        {
            var state = GetActiveState(commandId);
            if (state == null)
            {
                return (0, 0);
            }

            lock (state.Sync)
            {
                if (!string.IsNullOrWhiteSpace(authorFolderKey))
                {
                    state.ProcessedAuthorFolders.Add(authorFolderKey);
                }

                state.LastUpdatedUtc = DateTime.UtcNow;
                return (state.ProcessedAuthorFolders.Count, state.DiscoveredAuthorFolders.Count);
            }
        }

        public static (int Processed, int Total, int Matched, int Unmatched) MarkAuthorFolderOutcome(int commandId, string authorFolderKey, bool matched)
        {
            var state = GetActiveState(commandId);
            if (state == null)
            {
                return (0, 0, 0, 0);
            }

            lock (state.Sync)
            {
                if (!string.IsNullOrWhiteSpace(authorFolderKey))
                {
                    state.ProcessedAuthorFolders.Add(authorFolderKey);

                    if (matched)
                    {
                        state.MatchedAuthorFolders.Add(authorFolderKey);
                        state.UnmatchedAuthorFolders.Remove(authorFolderKey);
                    }
                    else
                    {
                        state.UnmatchedAuthorFolders.Add(authorFolderKey);
                        state.MatchedAuthorFolders.Remove(authorFolderKey);
                    }
                }

                state.LastUpdatedUtc = DateTime.UtcNow;
                return GetAuthorFolderOutcomeProgress(state);
            }
        }

        public static (int Processed, int Total) GetAuthorFolderProgress(int commandId)
        {
            if (!Sessions.TryGetValue(commandId, out var state))
            {
                return (0, 0);
            }

            lock (state.Sync)
            {
                return (state.ProcessedAuthorFolders.Count, state.DiscoveredAuthorFolders.Count);
            }
        }

        public static (int Processed, int Total, int Matched, int Unmatched) GetAuthorFolderOutcomeProgress(int commandId)
        {
            if (!Sessions.TryGetValue(commandId, out var state))
            {
                return (0, 0, 0, 0);
            }

            lock (state.Sync)
            {
                return GetAuthorFolderOutcomeProgress(state);
            }
        }

        public static (int Processed, int Total) GetBookUnitProgress(int commandId)
        {
            if (!Sessions.TryGetValue(commandId, out var state))
            {
                return (0, 0);
            }

            lock (state.Sync)
            {
                return (state.ProcessedBookUnits.Count, state.DiscoveredBookUnits.Count);
            }
        }

        public static void Clear(int commandId)
        {
            Sessions.TryRemove(commandId, out _);
            ActiveCommands.TryRemove(commandId, out _);
        }

        public static int MarkAuthorImported(int commandId, int authorId)
        {
            var state = GetActiveState(commandId);
            if (state == null)
            {
                return 0;
            }

            lock (state.Sync)
            {
                if (authorId > 0)
                {
                    state.ImportedAuthorIds.Add(authorId);
                }

                state.LastUpdatedUtc = DateTime.UtcNow;
                return state.ImportedAuthorIds.Count;
            }
        }

        public static int MarkBookUnitsImported(int commandId, IEnumerable<string> unitKeys)
        {
            var state = GetActiveState(commandId);
            if (state == null)
            {
                return 0;
            }

            lock (state.Sync)
            {
                if (unitKeys != null)
                {
                    foreach (var key in unitKeys)
                    {
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            state.ImportedBookUnits.Add(key);
                        }
                    }
                }

                state.LastUpdatedUtc = DateTime.UtcNow;
                return state.ImportedBookUnits.Count;
            }
        }

        public static int AddFilesImported(int commandId, int count)
        {
            if (count <= 0)
            {
                return GetImportedCounts(commandId).FilesImported;
            }

            var state = GetActiveState(commandId);
            if (state == null)
            {
                return 0;
            }

            lock (state.Sync)
            {
                state.ImportedFiles += count;
                state.LastUpdatedUtc = DateTime.UtcNow;
                return state.ImportedFiles;
            }
        }

        public static (int AuthorsImported, int BookUnitsImported, int FilesImported) GetImportedCounts(int commandId)
        {
            if (!Sessions.TryGetValue(commandId, out var state))
            {
                return (0, 0, 0);
            }

            lock (state.Sync)
            {
                return (state.ImportedAuthorIds.Count, state.ImportedBookUnits.Count, state.ImportedFiles);
            }
        }

        public static void MarkStagingComplete(int commandId)
        {
            var state = GetActiveState(commandId);
            if (state == null)
            {
                return;
            }

            lock (state.Sync)
            {
                state.StagingComplete = true;
                state.LastUpdatedUtc = DateTime.UtcNow;
            }
        }

        public static bool IsStagingComplete(int commandId)
        {
            if (!Sessions.TryGetValue(commandId, out var state))
            {
                return true;
            }

            lock (state.Sync)
            {
                return state.StagingComplete;
            }
        }

        private static SessionState GetActiveState(int commandId)
        {
            if (!Activate(commandId))
            {
                return null;
            }

            return Sessions.TryGetValue(commandId, out var state) ? state : null;
        }

        private static (int Processed, int Total, int Matched, int Unmatched) GetAuthorFolderOutcomeProgress(SessionState state)
        {
            return (
                state.ProcessedAuthorFolders.Count,
                state.DiscoveredAuthorFolders.Count,
                state.MatchedAuthorFolders.Count,
                state.UnmatchedAuthorFolders.Count);
        }
    }
}

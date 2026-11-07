using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NzbDrone.Core.Messaging.Commands
{
    public class CommandQueue : IEnumerable
    {
        private readonly object _mutex = new object();
        private readonly List<CommandModel> _items;
        private readonly Func<string, int> _getDiskAccessGroupLimit;
        private readonly Func<int> _getThreadLimit;

        public CommandQueue(Func<string, int> getDiskAccessGroupLimit = null, Func<int> getThreadLimit = null)
        {
            _getThreadLimit = getThreadLimit;
            _items = new List<CommandModel>();
            _getDiskAccessGroupLimit = getDiskAccessGroupLimit ?? (_ => 1);
        }

        public int Count => _items.Count;

        public int ActiveCount()
        {
            lock (_mutex)
            {
                return _items.Count(c =>
                    c.Status == CommandStatus.Queued ||
                    c.Status == CommandStatus.Started ||
                    c.Status == CommandStatus.Paused);
            }
        }

        public void Add(CommandModel item)
        {
            lock (_mutex)
            {
                _items.Add(item);

                Monitor.PulseAll(_mutex);
            }
        }

        public IEnumerator<CommandModel> GetEnumerator()
        {
            List<CommandModel> copy = null;

            lock (_mutex)
            {
                copy = new List<CommandModel>(_items);
            }

            return copy.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public List<CommandModel> All()
        {
            List<CommandModel> rval = null;

            lock (_mutex)
            {
                rval = _items;
            }

            return rval;
        }

        public CommandModel Find(int id)
        {
            return All().FirstOrDefault(q => q.Id == id);
        }

        public void RemoveMany(IEnumerable<CommandModel> commands)
        {
            lock (_mutex)
            {
                foreach (var command in commands)
                {
                    _items.Remove(command);
                }

                Monitor.PulseAll(_mutex);
            }
        }

        public bool RemoveIfQueued(int id)
        {
            var rval = false;

            lock (_mutex)
            {
                var command = _items.FirstOrDefault(q => q.Id == id);

                if (command?.Status == CommandStatus.Queued)
                {
                    _items.Remove(command);
                    rval = true;

                    Monitor.PulseAll(_mutex);
                }
            }

            return rval;
        }

        public List<CommandModel> QueuedOrStarted()
        {
            return All().Where(q => q.Status == CommandStatus.Queued ||
                                    q.Status == CommandStatus.Started ||
                                    q.Status == CommandStatus.Paused)
                        .ToList();
        }

        public IEnumerable<CommandModel> GetConsumingEnumerable()
        {
            return GetConsumingEnumerable(CancellationToken.None);
        }

        public IEnumerable<CommandModel> GetConsumingEnumerable(CancellationToken cancellationToken)
        {
            cancellationToken.Register(PulseAllConsumers);

            while (!cancellationToken.IsCancellationRequested)
            {
                CommandModel command = null;

                lock (_mutex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!TryGet(out command))
                    {
                        Monitor.Wait(_mutex);
                        continue;
                    }
                }

                if (command != null)
                {
                    yield return command;
                }
            }
        }

        public void PulseAllConsumers()
        {
            // Signal all consumers to reevaluate cancellation token
            lock (_mutex)
            {
                Monitor.PulseAll(_mutex);
            }
        }

        public bool TryGet(out CommandModel item)
        {
            var rval = true;
            item = default(CommandModel);

            lock (_mutex)
            {
                if (_items.Count == 0)
                {
                    rval = false;
                }
                else
                {
                    var startedCommands = _items.Where(c => c.Status == CommandStatus.Started)
                                                .ToList();

                    var exclusiveTypes = startedCommands.Where(x => x.Body.IsTypeExclusive)
                        .Select(x => x.Body.Name)
                        .ToList();

                    var queuedCommands = _items.Where(c => c.Status == CommandStatus.Queued);

                    if (startedCommands.Any(x => x.Body.IsTypeExclusive))
                    {
                        queuedCommands = queuedCommands.Where(c => !exclusiveTypes.Any(x => x == c.Body.Name));
                    }

                    if (startedCommands.Any(x => x.Body.IsLongRunning))
                    {
                        queuedCommands = queuedCommands.Where(c => c.Status == CommandStatus.Queued && !c.Body.IsExclusive);
                    }

                    // If any executing command is exclusive, block until it completes
                    if (startedCommands.Any(c => c.Body.IsExclusive))
                    {
                        rval = false;
                    }
                    else
                    {
                        // Scan for the first runnable candidate by priority then age
                        var orderedCandidates = queuedCommands
                            .OrderByDescending(c => c.Priority)
                            .ThenBy(c => c.QueuedAt)
                            .ToList();

                        CommandModel selected = null;

                        foreach (var candidate in orderedCandidates)
                        {
                            // Respect exclusivity: do not start any other command while an exclusive one runs
                            if (candidate.Body.IsExclusive && startedCommands.Any())
                            {
                                continue;
                            }

                            // Respect type exclusivity: do not start another command of the same type while one runs
                            if (candidate.Body.IsTypeExclusive && startedCommands.Any(r => string.Equals(r.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }

                            // Commands that leave a thread free never take the last one, so short commands
                            // keep running while a backlog of these is waiting on shared resources.
                            if (candidate.Body.LeavesThreadForOtherCommands && IsAtThreadReserveLimit(candidate, startedCommands))
                            {
                                continue;
                            }

                            // Release selection must observe a stable library inventory. Searches wait for
                            // active disk work, and disk work waits for an active search, independent of the
                            // configured concurrency for ordinary disk-access groups.
                            if (IsBlockedByLibraryState(candidate.Body, startedCommands))
                            {
                                continue;
                            }

                            // Respect disk access groups: most disk work remains exclusive, while selected
                            // groups can opt into a bounded concurrency limit.
                            if (IsBlockedByDiskAccess(candidate.Body, startedCommands))
                            {
                                continue;
                            }

                            // If any started command is long-running, avoid starting exclusive work
                            if (candidate.Body.IsExclusive && startedCommands.Any(r => r.Body.IsLongRunning))
                            {
                                continue;
                            }

                            // Candidate is runnable under current constraints
                            selected = candidate;
                            break;
                        }

                        if (selected == null)
                        {
                            // Nothing runnable at the moment
                            rval = false;
                        }
                        else
                        {
                            selected.StartedAt = DateTime.UtcNow;
                            selected.Status = CommandStatus.Started;
                            item = selected;
                        }
                    }
                }
            }

            return rval;
        }

        private bool IsAtThreadReserveLimit(CommandModel candidate, List<CommandModel> startedCommands)
        {
            if (_getThreadLimit == null)
            {
                return false;
            }

            var limit = Math.Max(1, _getThreadLimit() - 1);
            var running = startedCommands.Count(r => string.Equals(r.Name, candidate.Name, StringComparison.OrdinalIgnoreCase));

            return running >= limit;
        }

        private bool IsBlockedByDiskAccess(Command candidate, List<CommandModel> startedCommands)
        {
            if (!candidate.RequiresDiskAccess)
            {
                return false;
            }

            // Each disk access group enforces its own limit independently -- a command must never be
            // blocked by a started command in a DIFFERENT group. (Most commands share the implicit
            // "default" group and so remain exclusive with each other by virtue of that group's limit,
            // matching "most disk work remains exclusive" above; only a command that explicitly opts
            // into its own named group, e.g. RetryFailedImportCommand/DownloadedBooksScanCommand's
            // "downloadImport", is meant to run independently of "default", e.g. a long-running
            // ProcessMonitoredDownloads sweep.) A prior version of this check blocked a candidate
            // whenever ANY started disk command was in a different group, which collapsed every group
            // into one global mutex and defeated the point of naming groups at all -- see chaptarr #188.
            var candidateGroup = candidate.DiskAccessGroup ?? candidate.Name;
            var limit = Math.Max(1, _getDiskAccessGroupLimit(candidateGroup));
            var startedInGroup = startedCommands.Count(c =>
                c.Body.RequiresDiskAccess &&
                string.Equals(c.Body.DiskAccessGroup ?? c.Name, candidateGroup, StringComparison.OrdinalIgnoreCase));

            return startedInGroup >= limit;
        }

        private static bool IsBlockedByLibraryState(Command candidate, List<CommandModel> startedCommands)
        {
            if (candidate.RequiresLibraryState)
            {
                return startedCommands.Any(command => command.Body.RequiresDiskAccess || command.Body.RequiresLibraryState);
            }

            if (candidate.RequiresDiskAccess)
            {
                return startedCommands.Any(command => command.Body.RequiresLibraryState);
            }

            return false;
        }
    }
}

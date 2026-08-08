using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    /// <summary>
    /// Tracks background import/matching work that must complete before an import-related command
    /// is considered finished. This prevents progress/UI regressions caused by fire-and-forget tasks
    /// outliving the command lifecycle.
    /// </summary>
    public static class ImportCommandWorkTracker
    {
        private sealed class Session
        {
            public readonly object Sync = new object();

            public int Pending;
            public TaskCompletionSource<bool> Idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Session()
            {
                Idle.TrySetResult(true);
            }
        }

        private static readonly ConcurrentDictionary<int, Session> Sessions = new ConcurrentDictionary<int, Session>();

        public static void Activate(int commandId)
        {
            if (commandId <= 0)
            {
                return;
            }

            Sessions.GetOrAdd(commandId, _ => new Session());
        }

        public static void Track(int commandId, Task task)
        {
            if (commandId <= 0 || task == null)
            {
                return;
            }

            var session = Sessions.GetOrAdd(commandId, _ => new Session());

            lock (session.Sync)
            {
                if (session.Pending == 0)
                {
                    // Transition from idle -> non-idle: create a fresh task to await.
                    session.Idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                session.Pending++;
            }

            task.ContinueWith(
                _ => MarkComplete(session),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public static Task WaitForIdleAsync(int commandId, CancellationToken cancellationToken = default)
        {
            if (commandId <= 0)
            {
                return Task.CompletedTask;
            }

            if (!Sessions.TryGetValue(commandId, out var session))
            {
                return Task.CompletedTask;
            }

            Task idleTask;
            lock (session.Sync)
            {
                idleTask = session.Pending == 0 ? Task.CompletedTask : session.Idle.Task;
            }

            if (idleTask.IsCompleted || !cancellationToken.CanBeCanceled)
            {
                return idleTask;
            }

            return idleTask.WaitAsync(cancellationToken);
        }

        public static bool HasPendingWork(int commandId)
        {
            if (commandId <= 0)
            {
                return false;
            }

            if (!Sessions.TryGetValue(commandId, out var session))
            {
                return false;
            }

            lock (session.Sync)
            {
                return session.Pending > 0;
            }
        }

        public static void Clear(int commandId)
        {
            if (commandId <= 0)
            {
                return;
            }

            Sessions.TryRemove(commandId, out _);
        }

        private static void MarkComplete(Session session)
        {
            try
            {
                lock (session.Sync)
                {
                    session.Pending--;
                    if (session.Pending <= 0)
                    {
                        session.Pending = 0;
                        session.Idle.TrySetResult(true);
                    }
                }
            }
            catch
            {
                // best-effort only
            }
        }
    }
}


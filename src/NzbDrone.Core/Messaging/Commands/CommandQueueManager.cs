using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Composition;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Messaging.Commands
{
    public interface IManageCommandQueue
    {
        List<CommandModel> PushMany<TCommand>(List<TCommand> commands)
            where TCommand : Command;
        CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
            where TCommand : Command;
        CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified);
        IEnumerable<CommandModel> Queue(CancellationToken cancellationToken);
        List<CommandModel> All();
        CommandModel Get(int id);
        List<CommandModel> GetStarted();
        void SetMessage(CommandModel command, string message);
        void TouchProgress(CommandModel command);
        void SetResult(CommandModel command, CommandResult result);
        void Start(CommandModel command);
        void Complete(CommandModel command, string message);
        void Fail(CommandModel command, string message, Exception e);
        void Requeue();
        void Cancel(int id);
        void Pause(int id);
        void Resume(int id);
        void CleanCommands();
        CancellationToken GetCancellationToken(int commandId);
        void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource);
        void UnregisterCancellationToken(int commandId);
    }

    public class CommandQueueManager : IManageCommandQueue, IHandle<ApplicationStartedEvent>
    {
        private const string MaxManualQueueDepthEnvVar = "CHAPTARR_MAX_MANUAL_COMMAND_QUEUE";
        private const int DefaultMaxManualQueueDepth = 1000;
        private const string DiskAccessLimitEnvVar = "CHAPTARR_DISK_ACCESS_LIMIT";
        private const int DefaultDiskAccessLimit = 1;
        private static readonly int MaxManualQueueDepth = GetMaxManualQueueDepth();

        private readonly ICommandRepository _repo;
        private readonly KnownTypes _knownTypes;
        private readonly Logger _logger;

        private readonly CommandQueue _commandQueue;
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _cancellationTokenSources;

        public CommandQueueManager(ICommandRepository repo,
                                   IServiceFactory serviceFactory,
                                   KnownTypes knownTypes,
                                   Logger logger)
        {
            _repo = repo;
            _knownTypes = knownTypes;
            _logger = logger;

            _commandQueue = new CommandQueue(GetDiskAccessGroupLimit);
            _cancellationTokenSources = new ConcurrentDictionary<int, CancellationTokenSource>();
        }

        /// <summary>
        /// How many disk-access commands from the same group may run concurrently.
        /// Defaults to 1, which preserves the previous strictly-serial behaviour.
        ///
        /// Raising this helps when the library sits on a high-latency filesystem
        /// (e.g. FUSE/mergerfs or a network mount) where a single command spends most
        /// of its time waiting on per-file I/O rather than saturating CPU or disk
        /// bandwidth - work that parallelises well. It is opt-in because concurrent
        /// commands share the database and can touch the same files, so the safe
        /// default remains serial.
        /// </summary>
        private static int GetDiskAccessGroupLimit(string group)
        {
            var value = Environment.GetEnvironmentVariable(DiskAccessLimitEnvVar);

            if (!string.IsNullOrWhiteSpace(value) && int.TryParse(value, out var parsed) && parsed > 0)
            {
                return parsed;
            }

            return DefaultDiskAccessLimit;
        }

        private static int GetMaxManualQueueDepth()
        {
            var value = Environment.GetEnvironmentVariable(MaxManualQueueDepthEnvVar);

            if (!string.IsNullOrWhiteSpace(value) && int.TryParse(value, out var parsed))
            {
                // 0 or negative disables the limit.
                return parsed <= 0 ? int.MaxValue : parsed;
            }

            return DefaultMaxManualQueueDepth;
        }

        public List<CommandModel> PushMany<TCommand>(List<TCommand> commands)
            where TCommand : Command
        {
            _logger.Trace("Publishing {0} commands", commands.Count);

            lock (_commandQueue)
            {
                var commandModels = new List<CommandModel>();
                var existingCommands = _commandQueue.QueuedOrStarted();

                foreach (var command in commands)
                {
                    var existing = existingCommands.FirstOrDefault(c => c.Name == command.Name && CommandEqualityComparer.Instance.Equals(c.Body, command));

                    if (existing != null)
                    {
                        continue;
                    }

                    var commandModel = new CommandModel
                    {
                        Name = command.Name,
                        Body = command,
                        QueuedAt = DateTime.UtcNow,
                        Trigger = CommandTrigger.Unspecified,
                        Priority = CommandPriority.Normal,
                        Status = CommandStatus.Queued
                    };

                    commandModels.Add(commandModel);
                }

                _repo.InsertMany(commandModels);

                foreach (var commandModel in commandModels)
                {
                    _commandQueue.Add(commandModel);
                }

                return commandModels;
            }
        }

        public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
            where TCommand : Command
        {
            Ensure.That(command, () => command).IsNotNull();

            _logger.Trace("Publishing {0}", command.Name);
            _logger.Trace("Checking if command is queued or started: {0}", command.Name);

            lock (_commandQueue)
            {
                var existingCommands = QueuedOrStarted(command.Name);
                var existing = existingCommands.FirstOrDefault(c => CommandEqualityComparer.Instance.Equals(c.Body, command));

                if (existing != null)
                {
                    _logger.Trace("Command is already in progress: {0}", command.Name);

                    return existing;
                }

                if (trigger == CommandTrigger.Manual)
                {
                    var activeCount = _commandQueue.ActiveCount();
                    if (activeCount >= MaxManualQueueDepth)
                    {
                        _logger.Warn("Rejecting manual command {0}: command queue depth limit reached ({1})", command.Name, MaxManualQueueDepth);
                        throw new NzbDroneClientException(HttpStatusCode.TooManyRequests, "Command queue is full, try again later");
                    }
                }

                var commandModel = new CommandModel
                {
                    Name = command.Name,
                    Body = command,
                    QueuedAt = DateTime.UtcNow,
                    Trigger = trigger,
                    Priority = priority,
                    Status = CommandStatus.Queued
                };

                _logger.Trace("Inserting new command: {0}", commandModel.Name);

                _repo.Insert(commandModel);
                _commandQueue.Add(commandModel);

                return commandModel;
            }
        }

        public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
        {
            var command = GetCommand(commandName);
            command.LastExecutionTime = lastExecutionTime;
            command.LastStartTime = lastStartTime;
            command.Trigger = trigger;

            return Push(command, priority, trigger);
        }

        public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken)
        {
            return _commandQueue.GetConsumingEnumerable(cancellationToken);
        }

        public List<CommandModel> All()
        {
            _logger.Trace("Getting all commands");
            return _commandQueue.All();
        }

        public CommandModel Get(int id)
        {
            var command = _commandQueue.Find(id);

            if (command == null)
            {
                command = _repo.Get(id);
            }

            return command;
        }

        public List<CommandModel> GetStarted()
        {
            _logger.Trace("Getting started commands");
            return _commandQueue.All().Where(c => c.Status == CommandStatus.Started).ToList();
        }

        public void SetMessage(CommandModel command, string message)
        {
            command.Message = message;
            command.LastProgressAt = DateTime.UtcNow;
            try
            {
                // Persist progress heartbeat and message
                _repo.SetFields(command, c => c.Message, c => c.LastProgressAt);
            }
            catch
            {
                // Best-effort; avoid throwing from progress updates
            }
        }

        public void TouchProgress(CommandModel command)
        {
            try
            {
                command.LastProgressAt = DateTime.UtcNow;
                _repo.SetFields(command, c => c.LastProgressAt);
            }
            catch
            {
                // Best-effort
            }
        }

        public void SetResult(CommandModel command, CommandResult result)
        {
            command.Result = result;
        }

        public void Start(CommandModel command)
        {
            // Marks the command as started in the DB, the queue takes care of marking it as started on it's own
            _logger.Trace("Marking command as started: {0}", command.Name);
            _repo.Start(command);
        }

        public void Complete(CommandModel command, string message)
        {
            // If the result hasn't been set yet then set it to successful
            if (command.Result == CommandResult.Unknown)
            {
                command.Result = CommandResult.Successful;
            }

            Update(command, CommandStatus.Completed, message);

            _commandQueue.PulseAllConsumers();
        }

        public void Fail(CommandModel command, string message, Exception e)
        {
            command.Exception = e.ToString();

            Update(command, CommandStatus.Failed, message);

            _commandQueue.PulseAllConsumers();
        }

        public void Requeue()
        {
            foreach (var command in _repo.Queued())
            {
                _commandQueue.Add(command);
            }
        }

        public void Cancel(int id)
        {
            _logger.Debug("Attempting to cancel command with ID: {0}", id);

            var command = Get(id);
            if (command == null)
            {
                throw new NzbDroneClientException(HttpStatusCode.NotFound, "Command not found");
            }

            if (command.Status == CommandStatus.Completed ||
                command.Status == CommandStatus.Failed ||
                command.Status == CommandStatus.Cancelled ||
                command.Status == CommandStatus.Aborted ||
                command.Status == CommandStatus.Orphaned)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Cannot cancel a command that has already finished");
            }

            // If queued, remove from the in-memory queue to prevent execution.
            if (command.Status == CommandStatus.Queued)
            {
                _commandQueue.RemoveIfQueued(id);
            }

            // Best-effort: cancel running work if a token source exists.
            if (_cancellationTokenSources.TryRemove(id, out var cancellationTokenSource))
            {
                _logger.Info("Cancelling command with ID: {0}", id);
                cancellationTokenSource.Cancel();
                cancellationTokenSource.Dispose();
            }

            if (command.Status == CommandStatus.Queued ||
                command.Status == CommandStatus.Started ||
                command.Status == CommandStatus.Paused)
            {
                Update(command, CommandStatus.Cancelled, "Cancelled by user");
                _commandQueue.PulseAllConsumers();

                _logger.Info("Successfully cancelled command with ID: {0}", id);
                return;
            }

            throw new NzbDroneClientException(HttpStatusCode.Conflict, "Unable to cancel task - it may not support cancellation");
        }

        public void Pause(int id)
        {
            _logger.Debug("Attempting to pause command with ID: {0}", id);

            // Check if the command exists and is running
            var command = Get(id);
            if (command == null)
            {
                throw new NzbDroneClientException(HttpStatusCode.NotFound, "Command not found");
            }

            if (command.Status == CommandStatus.Paused)
            {
                _logger.Debug("Command {0} is already paused", id);
                return;
            }

            if (command.Status == CommandStatus.Completed ||
                command.Status == CommandStatus.Failed ||
                command.Status == CommandStatus.Cancelled)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Cannot pause a command that has already finished");
            }

            if (command.Status != CommandStatus.Started)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Can only pause running commands");
            }

            // Check if command has already been unregistered (completed)
            if (!_cancellationTokenSources.ContainsKey(id))
            {
                _logger.Warn("Command {0} has already completed execution, cannot pause", id);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Command has already completed execution");
            }

            // Update status to paused - DO NOT cancel the token
            // The command will check the status and wait
            Update(command, CommandStatus.Paused, "Paused by user");

            _logger.Info("Successfully paused command with ID: {0}", id);
        }

        public void Resume(int id)
        {
            _logger.Debug("Attempting to resume command with ID: {0}", id);

            // Check if the command exists and is paused
            var command = Get(id);
            if (command == null)
            {
                throw new NzbDroneClientException(HttpStatusCode.NotFound, "Command not found");
            }

            if (command.Status == CommandStatus.Started)
            {
                _logger.Debug("Command {0} is already running", id);
                return;
            }

            if (command.Status == CommandStatus.Completed ||
                command.Status == CommandStatus.Failed ||
                command.Status == CommandStatus.Cancelled)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Cannot resume a command that has already finished");
            }

            if (command.Status != CommandStatus.Paused)
            {
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Can only resume paused commands");
            }

            // Simply update status back to started - don't create new token
            // The command still has its original token
            command.Status = CommandStatus.Started;
            SetMessage(command, "Resumed");

            // Update in repository
            _repo.Update(command);

            _logger.Info("Successfully resumed command with ID: {0}", id);
        }

        public void CleanCommands()
        {
            _logger.Trace("Cleaning up old commands");

            var commands = _commandQueue.All()
                .Where(c => c.EndedAt < DateTime.UtcNow.AddMinutes(-5))
                .ToList();

            _commandQueue.RemoveMany(commands);

            _repo.Trim();
        }

        public CancellationToken GetCancellationToken(int commandId)
        {
            if (_cancellationTokenSources.TryGetValue(commandId, out var cancellationTokenSource))
            {
                return cancellationTokenSource.Token;
            }

            return CancellationToken.None;
        }

        public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource)
        {
            _logger.Debug("Registering cancellation token for command ID: {0}", commandId);
            _cancellationTokenSources.TryAdd(commandId, cancellationTokenSource);
        }

        public void UnregisterCancellationToken(int commandId)
        {
            _logger.Debug("Unregistering cancellation token for command ID: {0}", commandId);
            if (_cancellationTokenSources.TryRemove(commandId, out var cancellationTokenSource))
            {
                if (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    cancellationTokenSource.Dispose();
                }
            }
        }

        private dynamic GetCommand(string commandName)
        {
            commandName = commandName.Split('.').Last();
            var commands = _knownTypes.GetImplementations(typeof(Command));
            var commandType = commands.Single(c => c.Name.Equals(commandName, StringComparison.InvariantCultureIgnoreCase));

            return Json.Deserialize("{}", commandType);
        }

        private void Update(CommandModel command, CommandStatus status, string message)
        {
            SetMessage(command, message);

            // Only set EndedAt and Duration for terminal states
            if (status != CommandStatus.Paused)
            {
                command.EndedAt = DateTime.UtcNow;
                command.Duration = command.StartedAt.HasValue
                    ? command.EndedAt.Value.Subtract(command.StartedAt.Value)
                    : TimeSpan.Zero;
            }

            command.Status = status;

            _logger.Trace("Updating command status");
            _repo.End(command);
        }

        private List<CommandModel> QueuedOrStarted(string name)
        {
            return _commandQueue.QueuedOrStarted()
                .Where(q => q.Name == name)
                .ToList();
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _logger.Trace("Orphaning incomplete commands");
            _repo.OrphanStarted();
            Requeue();
        }
    }
}

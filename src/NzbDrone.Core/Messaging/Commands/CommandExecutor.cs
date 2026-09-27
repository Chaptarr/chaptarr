using System;
using System.Threading;
using NLog;
using NzbDrone.Common;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ProgressMessaging;

namespace NzbDrone.Core.Messaging.Commands
{
    public class CommandExecutor : IHandle<ApplicationStartedEvent>,
                                   IHandle<ApplicationShutdownRequested>
    {
        private const int DefaultThreadLimit = 3;
        private const string ThreadLimitEnvVar = "CHAPTARR_COMMAND_THREADS";

        /// <summary>
        /// Number of worker threads that execute queued commands.
        /// Defaults to 3, matching the previous hardcoded value.
        ///
        /// This is the hard ceiling on command concurrency - raising
        /// CHAPTARR_DISK_ACCESS_LIMIT alone has no effect if there are not enough
        /// threads to run the commands it now permits. Worth raising on libraries
        /// held on high-latency storage (FUSE/network mounts), where command threads
        /// spend most of their time blocked on per-file I/O rather than working.
        /// </summary>
        private static int ThreadLimit
        {
            get
            {
                var value = Environment.GetEnvironmentVariable(ThreadLimitEnvVar);

                if (!string.IsNullOrWhiteSpace(value) && int.TryParse(value, out var parsed) && parsed > 0)
                {
                    return parsed;
                }

                return DefaultThreadLimit;
            }
        }

        private readonly Logger _logger;
        private readonly IServiceFactory _serviceFactory;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEventAggregator _eventAggregator;

        private static CancellationTokenSource _cancellationTokenSource;

        public CommandExecutor(IServiceFactory serviceFactory,
                               IManageCommandQueue commandQueueManager,
                               IEventAggregator eventAggregator,
                               Logger logger)
        {
            _logger = logger;
            _serviceFactory = serviceFactory;
            _commandQueueManager = commandQueueManager;
            _eventAggregator = eventAggregator;
        }

        private void ExecuteCommands()
        {
            try
            {
                foreach (var command in _commandQueueManager.Queue(_cancellationTokenSource.Token))
                {
                    try
                    {
                        ExecuteCommand((dynamic)command.Body, command);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error occurred while executing task {0}", command.Name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Trace("Stopped one command execution pipeline");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unknown error in thread");
            }
        }

        private void ExecuteCommand<TCommand>(TCommand command, CommandModel commandModel)
            where TCommand : Command
        {
            IExecute<TCommand> handler = null;
            CancellationTokenSource cancellationTokenSource = null;
            CancellationTokenSource heartbeatCancellation = null;
            Thread heartbeatThread = null;

            try
            {
                handler = (IExecute<TCommand>)_serviceFactory.Build(typeof(IExecute<TCommand>));

                _logger.Trace("{0} -> {1}", command.GetType().Name, handler.GetType().Name);

                _commandQueueManager.Start(commandModel);
                BroadcastCommandUpdate(commandModel);

                if (ProgressMessageContext.CommandModel == null)
                {
                    ProgressMessageContext.CommandModel = commandModel;
                }

                // Create and register cancellation token for this command
                cancellationTokenSource = new CancellationTokenSource();
                _commandQueueManager.RegisterCancellationToken(commandModel.Id, cancellationTokenSource);

                // Start a lightweight heartbeat to update LastProgressAt while the command runs
                heartbeatCancellation = new CancellationTokenSource();
                heartbeatThread = new Thread(() =>
                {
                    try
                    {
                        while (!heartbeatCancellation.IsCancellationRequested)
                        {
                            _commandQueueManager.TouchProgress(commandModel);
                            Thread.Sleep(TimeSpan.FromSeconds(30));
                        }
                    }
                    catch
                    {
                        // swallow
                    }
                })
                { IsBackground = true };
                heartbeatThread.Start();

                // Try to execute with cancellation token if handler supports it
                try
                {
                    var executeMethod = handler.GetType().GetMethod("Execute", new[] { typeof(TCommand), typeof(CancellationToken) });
                    if (executeMethod != null)
                    {
                        _logger.Trace("Executing {0} with cancellation support", command.GetType().Name);
                        executeMethod.Invoke(handler, new object[] { command, cancellationTokenSource.Token });
                    }
                    else
                    {
                        _logger.Trace("Executing {0} without cancellation support", command.GetType().Name);
                        handler.Execute(command);
                    }
                }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
                {
                    // Unwrap reflection exception
                    throw ex.InnerException;
                }

                _commandQueueManager.Complete(commandModel, command.CompletionMessage ?? commandModel.Message);
            }
            catch (OperationCanceledException)
            {
                // Since we no longer cancel the token for pause, this is a real cancellation
                _logger.Info("Command {0} (ID: {1}) was cancelled", command.GetType().Name, commandModel.Id);
                _commandQueueManager.SetMessage(commandModel, "Cancelled");
                _commandQueueManager.SetResult(commandModel, CommandResult.Successful);
                commandModel.Status = CommandStatus.Cancelled;
                commandModel.EndedAt = DateTime.UtcNow;
                commandModel.Duration = commandModel.EndedAt.Value.Subtract(commandModel.StartedAt.Value);
            }
            catch (CommandFailedException ex)
            {
                _commandQueueManager.SetMessage(commandModel, "Failed");
                _commandQueueManager.Fail(commandModel, ex.Message, ex);
                throw;
            }
            catch (Exception ex)
            {
                _commandQueueManager.SetMessage(commandModel, "Failed");
                _commandQueueManager.Fail(commandModel, "Failed", ex);
                throw;
            }
            finally
            {
                // Unregister and dispose cancellation token
                if (cancellationTokenSource != null)
                {
                    _commandQueueManager.UnregisterCancellationToken(commandModel.Id);
                }

                // Stop heartbeat
                try
                {
                    if (heartbeatCancellation != null)
                    {
                        heartbeatCancellation.Cancel();
                        heartbeatCancellation.Dispose();
                    }
                }
                catch { }

                BroadcastCommandUpdate(commandModel);

                _eventAggregator.PublishEvent(new CommandExecutedEvent(commandModel));

                if (ProgressMessageContext.CommandModel == commandModel)
                {
                    ProgressMessageContext.CommandModel = null;
                }

                if (handler != null)
                {
                    _logger.Trace("{0} <- {1} [{2}]", command.GetType().Name, handler.GetType().Name, commandModel.Duration.ToString());
                }
            }
        }

        private void BroadcastCommandUpdate(CommandModel command)
        {
            if (command.Body.SendUpdatesToClient)
            {
                _eventAggregator.PublishEvent(new CommandUpdatedEvent(command));
            }
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _cancellationTokenSource = new CancellationTokenSource();

            for (var i = 0; i < ThreadLimit; i++)
            {
                var thread = new Thread(ExecuteCommands);
                thread.Start();
            }
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            _logger.Info("Shutting down task execution");
            _cancellationTokenSource.Cancel(true);
        }
    }
}

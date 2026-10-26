using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NzbDrone.Common
{
    public static class ServiceHost
    {
        internal static bool IsServiceLifetime(IHostLifetime lifetime) => lifetime is ApplicationHostLifetime;

        public static void Run(IHostBuilder builder, Action<Action<IHostBuilder>, CancellationToken> runApplication)
        {
            // The Windows dispatcher lives through application-host disposal, rollback and recovery.
            // A second WindowsServiceLifetime cannot start in the same process.
            using var service = builder.ConfigureServices(services =>
                services.AddSingleton<IHostedService>(provider => new ApplicationWorker(
                    provider.GetRequiredService<IHostApplicationLifetime>(), runApplication))).Build();
            var worker = service.Services.GetServices<IHostedService>().OfType<ApplicationWorker>().Single();
            var stopping = service.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            try
            {
                service.Run();
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            finally
            {
                worker.ExecuteTask?.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
            }

            worker.Failure?.Throw();
        }

        private sealed class ApplicationWorker : BackgroundService
        {
            private readonly IHostApplicationLifetime _serviceLifetime;
            private readonly Action<Action<IHostBuilder>, CancellationToken> _runApplication;
            public ExceptionDispatchInfo Failure { get; private set; }

            public ApplicationWorker(IHostApplicationLifetime serviceLifetime,
                Action<Action<IHostBuilder>, CancellationToken> runApplication)
            {
                _serviceLifetime = serviceLifetime;
                _runApplication = runApplication;
            }

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                try
                {
                    _runApplication(builder => builder.ConfigureServices(services =>
                        services.AddSingleton<IHostLifetime>(provider => new ApplicationHostLifetime(
                            provider.GetRequiredService<IHostApplicationLifetime>(), stoppingToken))), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    Failure = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    _serviceLifetime.StopApplication();
                }

                return Task.CompletedTask;
            }
        }

        private sealed class ApplicationHostLifetime : IHostLifetime, IDisposable
        {
            private readonly CancellationTokenRegistration _stoppingRegistration;

            public ApplicationHostLifetime(IHostApplicationLifetime applicationLifetime, CancellationToken stoppingToken)
            {
                _stoppingRegistration = stoppingToken.Register(applicationLifetime.StopApplication);
            }

            public Task WaitForStartAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public void Dispose() => _stoppingRegistration.Dispose();
        }
    }
}

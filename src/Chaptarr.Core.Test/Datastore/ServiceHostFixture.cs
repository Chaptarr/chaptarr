using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DryIoc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Composition;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Host;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    public class ServiceHostFixture
    {
        private sealed class DispatcherLifetime : IHostLifetime
        {
            public int Starts { get; private set; }
            public int Stops { get; private set; }
            public Action Starting { get; set; }

            public Task WaitForStartAsync(CancellationToken cancellationToken)
            {
                if (++Starts != 1)
                {
                    throw new InvalidOperationException("The service dispatcher can only start once per process");
                }

                Starting?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken)
            {
                Stops++;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        }

        [Test]
        public void service_stop_during_startup_is_a_normal_exit_without_starting_the_application()
        {
            var dispatcher = new DispatcherLifetime();
            var builder = new HostBuilder().ConfigureServices(services => services.AddSingleton<IHostLifetime>(provider =>
            {
                dispatcher.Starting = provider.GetRequiredService<IHostApplicationLifetime>().StopApplication;
                return dispatcher;
            }));
            Assert.DoesNotThrow(() => ServiceHost.Run(builder, (_, _) => Assert.Fail("A stopped service must not start the application")));
            Assert.That(dispatcher.Starts, Is.EqualTo(1));
        }

        [Test]
        public void recovery_replaces_the_application_host_without_restarting_the_service()
        {
            var dispatcher = new DispatcherLifetime();
            var attempts = 0;
            ServiceHost.Run(new HostBuilder().ConfigureServices(services => services.AddSingleton<IHostLifetime>(dispatcher)),
                (configureLifetime, stoppingToken) =>
                {
                    for (var i = 0; i < 2; i++)
                    {
                        var builder = new HostBuilder();
                        configureLifetime(builder);
                        var host = builder.Build();
                        Assert.That(new RuntimeInfo(LogManager.GetCurrentClassLogger(), host.Services.GetRequiredService<IHostLifetime>()).IsWindowsService, Is.True);
                        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                        lifetime.ApplicationStarted.Register(lifetime.StopApplication);
                        host.Run();
                        Assert.Throws<ObjectDisposedException>(() => host.Services.GetRequiredService<IHostApplicationLifetime>());
                        Assert.That(stoppingToken.IsCancellationRequested, Is.False);
                        Assert.That(dispatcher.Stops, Is.Zero);
                        attempts++;
                    }
                });

            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(dispatcher.Starts, Is.EqualTo(1));
            Assert.That(dispatcher.Stops, Is.EqualTo(1));
            using var console = new HostBuilder().Build();
            Assert.That(new RuntimeInfo(LogManager.GetCurrentClassLogger(), console.Services.GetRequiredService<IHostLifetime>()).IsWindowsService, Is.False);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task service_stop_reaches_the_active_host_and_waits_for_cleanup(bool afterRecovery, bool shutdownTimeout)
        {
            var dispatcher = new DispatcherLifetime();
            var active = new TaskCompletionSource<IHostApplicationLifetime>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleaningUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serviceStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var allowCleanup = new ManualResetEventSlim();
            IHostApplicationLifetime serviceLifetime = null;
            var cleaned = false;
            var run = Task.Run(() => ServiceHost.Run(new HostBuilder().ConfigureServices(services =>
            {
                if (shutdownTimeout)
                {
                    services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(50));
                }

                services.AddSingleton<IHostLifetime>(provider =>
                {
                    serviceLifetime = provider.GetRequiredService<IHostApplicationLifetime>();
                    serviceLifetime.ApplicationStopped.Register(() => serviceStopped.TrySetResult());
                    return dispatcher;
                });
            }), (configureLifetime, stoppingToken) =>
                {
                    if (afterRecovery)
                    {
                        var previousBuilder = new HostBuilder();
                        configureLifetime(previousBuilder);
                        var previous = previousBuilder.Build();
                        var previousLifetime = previous.Services.GetRequiredService<IHostApplicationLifetime>();
                        previousLifetime.ApplicationStarted.Register(previousLifetime.StopApplication);
                        previous.Run();
                    }

                    var builder = new HostBuilder();
                    configureLifetime(builder);
                    var host = builder.Build();
                    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                    lifetime.ApplicationStarted.Register(() => active.TrySetResult(lifetime));
                    host.Run();
                    Assert.That(stoppingToken.IsCancellationRequested, Is.True);
                    cleaningUp.SetResult();
                    Assert.That(allowCleanup.Wait(TimeSpan.FromSeconds(10)), Is.True);
                    cleaned = true;
                }));

            try
            {
                var lifetime = await active.Task.WaitAsync(TimeSpan.FromSeconds(10));
                serviceLifetime.StopApplication();
                await cleaningUp.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(lifetime.ApplicationStopping.IsCancellationRequested, Is.True);
                if (shutdownTimeout)
                {
                    await serviceStopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }

                Assert.That(run.IsCompleted, Is.False);
                if (!shutdownTimeout)
                {
                    Assert.That(dispatcher.Stops, Is.Zero);
                }
            }
            finally
            {
                allowCleanup.Set();
                serviceLifetime?.StopApplication();
                await run.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.That(cleaned, Is.True);
            Assert.That(dispatcher.Starts, Is.EqualTo(1));
            Assert.That(dispatcher.Stops, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void an_unrecoverable_application_failure_stops_the_service_and_preserves_the_exception(bool cancellation)
        {
            var dispatcher = new DispatcherLifetime();
            Exception failure = cancellation ? new OperationCanceledException("Application cancellation without a service stop")
                : new IOException("The original databases could not be recovered");
            var error = Assert.Catch<Exception>(() => ServiceHost.Run(
                new HostBuilder().ConfigureServices(services => services.AddSingleton<IHostLifetime>(dispatcher)),
                (_, _) => throw failure));

            Assert.That(error, Is.SameAs(failure));
            Assert.That(dispatcher.Starts, Is.EqualTo(1));
            Assert.That(dispatcher.Stops, Is.EqualTo(1));
        }

        [Test]
        public void a_stop_between_hosts_cancels_the_next_host_before_it_starts()
        {
            IHostApplicationLifetime serviceLifetime = null;
            ServiceHost.Run(new HostBuilder().ConfigureServices(services => services.AddSingleton<IHostLifetime>(provider =>
            {
                serviceLifetime = provider.GetRequiredService<IHostApplicationLifetime>();
                return new DispatcherLifetime();
            })), (configureLifetime, stoppingToken) =>
            {
                serviceLifetime.StopApplication();
                Assert.That(stoppingToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)), Is.True);
                var builder = new HostBuilder();
                configureLifetime(builder);
                using var host = builder.Build();
                var started = false;
                host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() => started = true);
                Assert.ThrowsAsync<OperationCanceledException>(() => host.StartAsync());
                Assert.That(started, Is.False);
            });
        }

        [Test]
        public void recovery_does_not_reenter_bootstrap_after_a_service_stop()
        {
            using var stopping = new CancellationTokenSource();
            stopping.Cancel();
            var start = typeof(Bootstrap).GetMethod("StartCore", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.DoesNotThrow(() => start.Invoke(null, new object[]
            {
                null, null, true, (Action<IHostBuilder>)(_ => Assert.Fail("No replacement host should be built")), stopping.Token
            }));
        }

        [Test]
        public void service_only_helpers_are_not_discovered_as_application_hosted_services()
        {
            using var container = new Container(rules => rules.WithNzbDroneRules());
            container.AutoAddServices(Bootstrap.ASSEMBLIES);
            var registrations = container.GetServiceRegistrations()
                .Where(registration => registration.Factory.ImplementationType?.DeclaringType == typeof(ServiceHost));
            Assert.That(registrations, Is.Empty);
            Assert.That(container.GetServiceRegistrations().Count(registration =>
                registration.ServiceType == typeof(IHostedService) && registration.Factory.ImplementationType == typeof(AppLifetime)), Is.EqualTo(1));

            using var previous = new Container(rules => rules.WithNzbDroneRules());
            var assemblies = AssemblyLoader.Load(Bootstrap.ASSEMBLIES);
            previous.RegisterMany(assemblies, serviceTypeCondition: type => type.IsInterface && !string.IsNullOrWhiteSpace(type.FullName) && !type.FullName.StartsWith("System"), reuse: Reuse.Singleton);
            previous.RegisterMany(assemblies, serviceTypeCondition: type => !type.IsInterface && !string.IsNullOrWhiteSpace(type.FullName) && !type.FullName.StartsWith("System"), reuse: Reuse.Transient);
            var expected = previous.GetServiceRegistrations().Where(registration => registration.ServiceType != typeof(KnownTypes) && registration.Factory.ImplementationType?.DeclaringType != typeof(ServiceHost))
                .Select(registration => (registration.ServiceType, registration.Factory.ImplementationType)).ToHashSet();
            var actual = container.GetServiceRegistrations().Where(registration => registration.ServiceType != typeof(KnownTypes))
                .Select(registration => (registration.ServiceType, registration.Factory.ImplementationType)).ToHashSet();
            Assert.That(actual.SetEquals(expected), Is.True, "Every pre-existing application service must retain its registration");
        }
    }
}

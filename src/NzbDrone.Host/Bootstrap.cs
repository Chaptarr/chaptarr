using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DryIoc;
using DryIoc.Microsoft.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using NLog;
using Npgsql;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Options;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore.Extensions;
using NzbDrone.Core.Http;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
// using NzbDrone.Core.MetadataSource.Hardcover; // Removed - using V5 API via BookInfoProxy
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Organizer;
using PostgresOptions = NzbDrone.Core.Datastore.PostgresOptions;

namespace NzbDrone.Host
{
    public static class Bootstrap
    {
        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(Bootstrap));

        public static readonly List<string> ASSEMBLIES = new List<string>
        {
            "Chaptarr.Host",
            "Chaptarr.Core",
            "Chaptarr.SignalR",
            "Chaptarr.Api.V1",
            "Chaptarr.Http"
        };

        public static void Start(string[] args, Action<IHostBuilder> trayCallback = null)
        {
            try
            {
                Logger.Info("Starting Chaptarr - {0} - Version {1}",
                            Environment.ProcessPath,
                            BuildInfo.Release);

                var startupContext = new StartupContext(args);

                LongPathSupport.Enable();
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

                var appMode = GetApplicationMode(startupContext);
                var config = GetConfiguration(startupContext);

                switch (appMode)
                {
                    case ApplicationModes.Service:
                        {
                            Logger.Debug("Service selected");

                            CreateConsoleHostBuilder(args, startupContext).UseWindowsService().Build().Run();
                            break;
                        }

                    case ApplicationModes.Interactive:
                        {
                            Logger.Debug(trayCallback != null ? "Tray selected" : "Console selected");
                            var builder = CreateConsoleHostBuilder(args, startupContext);

                            if (trayCallback != null)
                            {
                                trayCallback(builder);
                            }

                            builder.Build().Run();
                            break;
                        }

                    // Utility mode
                    default:
                        {
                            new HostBuilder()
                                .UseServiceProviderFactory(new DryIocServiceProviderFactory(new Container(rules => rules.WithNzbDroneRules())))
                                .ConfigureContainer<IContainer>(c =>
                                {
                                    c.AutoAddServices(Bootstrap.ASSEMBLIES)
                                        .RegisterHttpClients()
                                        .AddIndexerProxyProvider()
                                        .AddNzbDroneLogger()
                                        .AddDatabase()
                                        .AddFuzzyMatchingServices()
                                        .AddImportServices()
                                        .AddNamingPatternServices()
                                        // .AddHardcoverServices() // Removed - using V5 API via BookInfoProxy
                                        .AddStartupContext(startupContext)
                                        .Resolve<UtilityModeRouter>()
                                        .Route(appMode);
                                })
                                .ConfigureServices(services =>
                                {
                                    services.Configure<PostgresOptions>(config.GetSection("Chaptarr:Postgres"));
                                    services.PostConfigure<PostgresOptions>(o =>
                                    {
                                        if (string.IsNullOrWhiteSpace(o.Host))
                                        {
                                            // Rebrand compatibility (Readarr/AudioArr → Chaptarr): accept legacy section names/env vars.
                                            config.GetSection("Readarr:Postgres").Bind(o);
                                            config.GetSection("Audioarr:Postgres").Bind(o);
                                        }
                                    });
                                    services.Configure<AppOptions>(config.GetSection("Chaptarr:App"));
                                    services.Configure<AuthOptions>(config.GetSection("Chaptarr:Auth"));
                                    services.Configure<ServerOptions>(config.GetSection("Chaptarr:Server"));
                                    services.Configure<LogOptions>(config.GetSection("Chaptarr:Log"));
                                    services.Configure<UpdateOptions>(config.GetSection("Chaptarr:Update"));
                                }).Build();

                            break;
                        }
                }
            }
            catch (InvalidConfigFileException ex)
            {
                throw new ChaptarrStartupException(ex);
            }
            catch (AccessDeniedConfigFileException ex)
            {
                throw new ChaptarrStartupException(ex);
            }
            catch (TerminateApplicationException ex)
            {
                Logger.Info(ex.Message);
                LogManager.Configuration = null;
            }

            // Make sure there are no lingering database connections
            GC.Collect();
            GC.WaitForPendingFinalizers();
            NpgsqlConnection.ClearAllPools();
        }

        public static IHostBuilder CreateConsoleHostBuilder(string[] args, StartupContext context)
        {
            var config = GetConfiguration(context);

            var bindAddress = config.GetValue<string>($"Chaptarr:Server:{nameof(ServerOptions.BindAddress)}") ?? config.GetValue(nameof(ConfigFileProvider.BindAddress), "*");
            var port = config.GetValue<int?>($"Chaptarr:Server:{nameof(ServerOptions.Port)}") ?? config.GetValue(nameof(ConfigFileProvider.Port), 8789);
            var sslPort = config.GetValue<int?>($"Chaptarr:Server:{nameof(ServerOptions.SslPort)}") ?? config.GetValue(nameof(ConfigFileProvider.SslPort), 6868);
            var enableSsl = config.GetValue<bool?>($"Chaptarr:Server:{nameof(ServerOptions.EnableSsl)}") ?? config.GetValue(nameof(ConfigFileProvider.EnableSsl), false);
            var sslCertPath = config.GetValue<string>($"Chaptarr:Server:{nameof(ServerOptions.SslCertPath)}") ?? config.GetValue<string>(nameof(ConfigFileProvider.SslCertPath));
            var sslCertPassword = config.GetValue<string>($"Chaptarr:Server:{nameof(ServerOptions.SslCertPassword)}") ?? config.GetValue<string>(nameof(ConfigFileProvider.SslCertPassword));

            var urls = new List<string> { BuildUrl("http", bindAddress, port) };

            if (enableSsl && sslCertPath.IsNotNullOrWhiteSpace())
            {
                urls.Add(BuildUrl("https", bindAddress, sslPort));
            }

            return new HostBuilder()
                .UseContentRoot(Directory.GetCurrentDirectory())
                .UseServiceProviderFactory(new DryIocServiceProviderFactory(new Container(rules => rules.WithNzbDroneRules())))
                .ConfigureContainer<IContainer>(c =>
                {
                    c.AutoAddServices(Bootstrap.ASSEMBLIES)
                        .RegisterHttpClients()
                        .AddIndexerProxyProvider()
                        .AddNzbDroneLogger()
                        .AddDatabase()
                        .AddFuzzyMatchingServices()
                        .AddImportServices()
                        .AddNamingPatternServices()
                        // .AddHardcoverServices() // Removed - using V5 API via BookInfoProxy
                        .AddStartupContext(context)
                        .Resolve<IEventAggregator>().PublishEvent(new ApplicationStartingEvent());
                })
                .ConfigureServices(services =>
                {
                    services.Configure<PostgresOptions>(config.GetSection("Chaptarr:Postgres"));
                    services.PostConfigure<PostgresOptions>(o =>
                    {
                        if (string.IsNullOrWhiteSpace(o.Host))
                        {
                            // Rebrand compatibility (Readarr/AudioArr → Chaptarr): accept legacy section names/env vars.
                            config.GetSection("Readarr:Postgres").Bind(o);
                            config.GetSection("Audioarr:Postgres").Bind(o);
                        }
                    });
                    services.Configure<AppOptions>(config.GetSection("Chaptarr:App"));
                    services.Configure<AuthOptions>(config.GetSection("Chaptarr:Auth"));
                    services.Configure<ServerOptions>(config.GetSection("Chaptarr:Server"));
                    services.Configure<LogOptions>(config.GetSection("Chaptarr:Log"));
                    services.Configure<UpdateOptions>(config.GetSection("Chaptarr:Update"));
                })
                .ConfigureWebHost(builder =>
                {
                    builder.UseConfiguration(config);
                    builder.UseUrls(urls.ToArray());
                    builder.UseKestrel(options =>
                    {
                        if (enableSsl && sslCertPath.IsNotNullOrWhiteSpace())
                        {
                            options.ConfigureHttpsDefaults(configureOptions =>
                            {
                                configureOptions.ServerCertificate = ValidateSslCertificate(sslCertPath, sslCertPassword);
                            });
                        }
                    });
                    builder.ConfigureKestrel(serverOptions =>
                    {
                        serverOptions.AllowSynchronousIO = false;
                        serverOptions.Limits.MaxRequestBodySize = 100 * 1024 * 1024;
                    });
                    builder.UseStartup<Startup>();
                });
        }

        public static ApplicationModes GetApplicationMode(IStartupContext startupContext)
        {
            if (startupContext.Help)
            {
                return ApplicationModes.Help;
            }

            if (OsInfo.IsWindows && startupContext.RegisterUrl)
            {
                return ApplicationModes.RegisterUrl;
            }

            if (OsInfo.IsWindows && startupContext.InstallService)
            {
                return ApplicationModes.InstallService;
            }

            if (OsInfo.IsWindows && startupContext.UninstallService)
            {
                return ApplicationModes.UninstallService;
            }

            Logger.Debug("Getting windows service status");

            // IsWindowsService can throw sometimes, so wrap it
            var isWindowsService = false;
            try
            {
                isWindowsService = WindowsServiceHelpers.IsWindowsService();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to get service status");
            }

            if (OsInfo.IsWindows && isWindowsService)
            {
                return ApplicationModes.Service;
            }

            return ApplicationModes.Interactive;
        }

        private static IConfiguration GetConfiguration(StartupContext context)
        {
            var appFolder = new AppFolderInfo(context);
            var configPath = appFolder.GetConfigPath();

            try
            {
                return new ConfigurationBuilder()
                    .AddXmlFile(configPath, optional: true, reloadOnChange: false)
                    .AddInMemoryCollection(new List<KeyValuePair<string, string>> { new("dataProtectionFolder", appFolder.GetDataProtectionPath()) })
                    .AddEnvironmentVariables()
                    .Build();
            }
            catch (InvalidDataException ex)
            {
                Logger.Error(ex, ex.Message);

                throw new InvalidConfigFileException($"{configPath} is corrupt or invalid. Please delete the config file and Chaptarr will recreate it.", ex);
            }
        }

        private static string BuildUrl(string scheme, string bindAddress, int port)
        {
            return $"{scheme}://{bindAddress}:{port}";
        }

        private static X509Certificate2 ValidateSslCertificate(string cert, string password)
        {
            X509Certificate2 certificate;

            if (cert.IsNullOrWhiteSpace())
            {
                throw new ChaptarrStartupException("SSL certificate path is required when SSL is enabled");
            }

            if (!File.Exists(cert))
            {
                throw new ChaptarrStartupException("The SSL certificate file '{0}' does not exist", cert);
            }

            try
            {
                certificate = X509CertificateLoader.LoadPkcs12FromFile(cert, password, X509KeyStorageFlags.DefaultKeySet);
            }
            catch (CryptographicException ex)
            {
                throw new ChaptarrStartupException(ex);
            }

            return certificate;
        }
    }
}

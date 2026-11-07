using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.RateLimiting;
using Chaptarr.Api.V1.System;
using Chaptarr.Http;
using Chaptarr.Http.Authentication;
using Chaptarr.Http.ClientSchema;
using Chaptarr.Http.ErrorManagement;
using Chaptarr.Http.Frontend;
using Chaptarr.Http.Middleware;
using DryIoc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using NLog.Extensions.Logging;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Processes;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Instrumentation;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Metrics;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Host.AccessControl;
using NzbDrone.Http.Authentication;
using NzbDrone.SignalR;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using IPNetwork = System.Net.IPNetwork;

namespace NzbDrone.Host
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(b =>
            {
                b.ClearProviders();
                b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
                b.AddFilter("Microsoft.AspNetCore", Microsoft.Extensions.Logging.LogLevel.Warning);
                b.AddFilter("Chaptarr.Http.Authentication", LogLevel.Information);
                b.AddFilter("Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager", LogLevel.Error);
                b.AddNLog();
            });

            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = 1;
                options.RequireHeaderSymmetry = true;

                foreach (var proxy in HttpSecuritySettings.GetTrustedProxies(Configuration))
                {
                    if (proxy.Contains('/'))
                    {
                        options.KnownIPNetworks.Add(IPNetwork.Parse(proxy));
                    }
                    else
                    {
                        options.KnownProxies.Add(IPAddress.Parse(proxy));
                    }
                }
            });

            services.AddRouting(options => options.LowercaseUrls = true);

            services.AddResponseCompression();

            var allowedOrigins = HttpSecuritySettings.GetAllowedOrigins(Configuration);

            services.AddCors(options =>
            {
                options.AddPolicy(VersionedApiControllerAttribute.API_CORS_POLICY,
                    builder =>
                    {
                        builder.AllowAnyMethod()
                            .AllowAnyHeader()
                            .WithExposedHeaders("X-Bookshelf-Request-Id");

                        if (allowedOrigins.Length > 0)
                        {
                            builder.WithOrigins(allowedOrigins);
                        }
                    });

                options.AddPolicy("AllowGet",
                    builder =>
                    {
                        builder.WithMethods("GET", "OPTIONS")
                            .AllowAnyHeader()
                            .WithExposedHeaders("X-Bookshelf-Request-Id");

                        if (allowedOrigins.Length > 0)
                        {
                            builder.WithOrigins(allowedOrigins);
                        }
                    });
            });

            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                var requestsPerMinute = HttpSecuritySettings.GetRequestsPerMinute(Configuration);
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = requestsPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        }));
                options.OnRejected = (context, _) =>
                {
                    context.HttpContext.Response.Headers["Retry-After"] = "60";
                    return System.Threading.Tasks.ValueTask.CompletedTask;
                };
            });

            services
            .AddControllers(options =>
            {
                options.ReturnHttpNotAcceptable = true;
            })
            .AddApplicationPart(typeof(SystemController).Assembly)
            .AddApplicationPart(typeof(StaticResourceController).Assembly)
            .AddJsonOptions(options =>
            {
                STJson.ApplySerializerSettings(options.JsonSerializerOptions);
            })
            .AddControllersAsServices();

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Version = "1.0.0",
                    Title = "Chaptarr",
                    Description = "Chaptarr API docs",
                    License = new OpenApiLicense
                    {
                        Name = "GPL-3.0-only",
                        Url = new Uri("https://github.com/snapetech-org/chaptarrng/blob/main/LICENSE")
                    }
                });
                c.EnableAnnotations();
                c.OperationFilter<BookshelfBackupOpenApiOperationFilter>();

                var apiKeyHeader = new OpenApiSecurityScheme
                {
                    Name = "X-Api-Key",
                    Type = SecuritySchemeType.ApiKey,
                    Scheme = "apiKey",
                    Description = "Apikey passed as header",
                    In = ParameterLocation.Header,
                };

                c.AddSecurityDefinition("X-Api-Key", apiKeyHeader);

                c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("X-Api-Key", document)] = new List<string>()
                });

                var apikeyQuery = new OpenApiSecurityScheme
                {
                    Name = "apikey",
                    Type = SecuritySchemeType.ApiKey,
                    Scheme = "apiKey",
                    Description = "Apikey passed as query parameter",
                    In = ParameterLocation.Query,
                };

                c.AddServer(new OpenApiServer
                {
                    Url = "{protocol}://{hostpath}",
                    Variables = new Dictionary<string, OpenApiServerVariable>
                    {
                        { "protocol", new OpenApiServerVariable { Default = "http", Enum = new List<string> { "http", "https" } } },
                        { "hostpath", new OpenApiServerVariable { Default = "localhost:8789" } }
                    }
                });

                c.AddSecurityDefinition("apikey", apikeyQuery);

                c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("apikey", document)] = new List<string>()
                });

                c.DescribeAllParametersInCamelCase();
            });

            services
            .AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions = STJson.GetSerializerSettings();
            });

            // Register SignalR broadcaster
            services.AddSingleton<IBroadcastSignalRMessage, SignalRMessageBroadcaster>();

            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(Configuration["dataProtectionFolder"]));

            services.AddSingleton<IAuthorizationPolicyProvider, UiAuthorizationPolicyProvider>();
            services.AddSingleton<IAuthorizationHandler, UiAuthorizationHandler>();

            services.AddAuthorization(options =>
            {
                options.AddPolicy("SignalR", policy =>
                {
                    policy.AuthenticationSchemes.Add("SignalR");
                    policy.RequireAuthenticatedUser();
                });

                // Require auth on everything except those marked [AllowAnonymous]
                options.FallbackPolicy = new AuthorizationPolicyBuilder("API")
                .RequireAuthenticatedUser()
                .Build();
            });

            services.AddAppAuthentication();

            // Field-agnostic tag extraction services
            services.AddSingleton<NzbDrone.Core.MediaFiles.TagExtraction.ITagExtractor, NzbDrone.Core.MediaFiles.TagExtraction.TagLibSharpExtractor>();
            services.AddSingleton<NzbDrone.Core.MediaFiles.TagExtraction.ITagExtractor, NzbDrone.Core.MediaFiles.TagExtraction.FfprobeTagExtractor>();
            services.AddSingleton<NzbDrone.Core.MediaFiles.TagExtraction.ITagExtractionService, NzbDrone.Core.MediaFiles.TagExtraction.TagExtractionService>();
        }

        public void Configure(IApplicationBuilder app,
                              IContainer container,
                              IStartupContext startupContext,
                              Lazy<IMainDatabase> mainDatabaseFactory,
                              Lazy<ILogDatabase> logDatabaseFactory,
                              Lazy<ICacheDatabase> cacheDatabaseFactory,
                              DatabaseTarget dbTarget,
                              ISingleInstancePolicy singleInstancePolicy,
                              InitializeLogger initializeLogger,
                              ReconfigureLogging reconfigureLogging,
                              FirstRunAuthenticationSetup firstRunAuthenticationSetup,
                              IAppFolderFactory appFolderFactory,
                              IProvidePidFile pidFileProvider,
                              IConfigFileProvider configFileProvider,
                              IRuntimeInfo runtimeInfo,
                              IFirewallAdapter firewallAdapter,
                              IEventAggregator eventAggregator,
                              ChaptarrErrorPipeline errorHandler)
        {
            initializeLogger.Initialize();
            appFolderFactory.Register();
            pidFileProvider.Write();

            configFileProvider.EnsureDefaultConfigFile();

            reconfigureLogging.Reconfigure();

            EnsureSingleInstance(false, startupContext, singleInstancePolicy);

            // instantiate the databases to initialize/migrate them
            _ = mainDatabaseFactory.Value;
            _ = logDatabaseFactory.Value;
            _ = cacheDatabaseFactory.Value;

            dbTarget.Register();
            SchemaBuilder.Initialize(container);
            firstRunAuthenticationSetup.EnsureInitialAccountSetup();

            if (configFileProvider.AuthenticationMethod == AuthenticationType.None &&
                configFileProvider.BindAddress != "127.0.0.1" && configFileProvider.BindAddress != "::1" &&
                configFileProvider.BindAddress != "localhost")
            {
                NLog.LogManager.GetLogger("Security").Warn("Authentication is disabled while ChaptarrNG is bound to a non-loopback address. The UI initialization endpoint exposes the API key to authenticated UI sessions; enable Forms or Basic authentication before exposing this listener.");
            }

            if (OsInfo.IsNotWindows)
            {
                Console.CancelKeyPress += (sender, eventArgs) => NLog.LogManager.Configuration = null;
            }

            eventAggregator.PublishEvent(new ApplicationStartingEvent());

            if (OsInfo.IsWindows && runtimeInfo.IsAdmin)
            {
                firewallAdapter.MakeAccessible();
            }

            app.UseForwardedHeaders();
            app.UsePathBase(new PathString(configFileProvider.UrlBase));
            app.UseMiddleware<ServarrMediaTypeScopeMiddleware>();
            app.UseMiddleware<LoggingMiddleware>();
            app.UseMiddleware<SecurityHeadersMiddleware>();
            app.UseExceptionHandler(new ExceptionHandlerOptions
            {
                AllowStatusCode404Response = true,
                ExceptionHandler = errorHandler.HandleException
            });

            var metricsEnabled = Configuration.GetValue<bool?>("Chaptarr:Metrics:Enabled") ?? Configuration.GetValue<bool?>("MetricsEnabled") ?? false;
            if (metricsEnabled)
            {
                var metricsPort = Configuration.GetValue<int?>("Chaptarr:Metrics:Port") ?? Configuration.GetValue<int?>("MetricsPort") ?? 9797;
                var metricsService = app.ApplicationServices.GetRequiredService<IOperationalMetricsService>();

                // Metrics use a separate listener and intentionally bypass API authentication.
                // The listener branch only serves /metrics and returns 404 for every other path.
                app.MapWhen(context => context.Connection.LocalPort == metricsPort, metricsApp =>
                {
                    metricsApp.Run(async context =>
                    {
                        if (!HttpMethods.IsGet(context.Request.Method))
                        {
                            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                            return;
                        }

                        if (context.Request.Path != "/metrics")
                        {
                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                            return;
                        }

                        context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
                        context.Response.Headers["Cache-Control"] = "no-store";
                        await context.Response.WriteAsync(metricsService.RenderPrometheusText());
                    });
                });
            }

            app.UseRouting();
            app.UseRateLimiter();
            app.UseCors();

            // Apple's OIDC form_post callback is cross-site; cap that request
            // independently of unrelated API and media upload limits.
            app.Use(async (context, next) =>
            {
                if (HttpMethods.IsPost(context.Request.Method) &&
                    (context.Request.Path.Value ?? string.Empty).EndsWith("/signin-oidc", StringComparison.OrdinalIgnoreCase))
                {
                    const long maxCallbackBytes = 16 * 1024;
                    var bodyLimit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                    if (bodyLimit != null && !bodyLimit.IsReadOnly)
                    {
                        bodyLimit.MaxRequestBodySize = maxCallbackBytes;
                    }

                    if (context.Request.ContentLength > maxCallbackBytes ||
                        (context.Request.ContentLength == null && (bodyLimit == null || bodyLimit.IsReadOnly)))
                    {
                        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                        return;
                    }
                }

                await next();
            });
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseResponseCompression();
            app.Properties["host.AppName"] = BuildInfo.AppName;

            app.UseMiddleware<VersionMiddleware>();
            app.UseMiddleware<UrlBaseMiddleware>(configFileProvider.UrlBase);
            app.UseMiddleware<StartingUpMiddleware>();
            app.UseMiddleware<CacheHeaderMiddleware>();
            app.UseMiddleware<IfModifiedMiddleware>();
            app.UseMiddleware<BufferingMiddleware>(new[] { new PathString("/api/v1/command") });

            app.UseWebSockets();

            // Enable middleware to serve generated Swagger as a JSON endpoint.
            if (BuildInfo.IsDebug)
            {
                app.UseSwagger(c =>
                {
                    c.RouteTemplate = "docs/{documentName}/openapi.json";
                });
            }

            app.UseEndpoints(x =>
            {
                x.MapHub<MessageHub>("/signalr/messages").RequireAuthorization("SignalR");
                x.MapControllers();
            });
        }

        private void EnsureSingleInstance(bool isService, IStartupContext startupContext, ISingleInstancePolicy instancePolicy)
        {
            if (startupContext.Flags.Contains(StartupContext.NO_SINGLE_INSTANCE_CHECK))
            {
                return;
            }

            if (startupContext.Flags.Contains(StartupContext.TERMINATE))
            {
                instancePolicy.KillAllOtherInstance();
            }
            else if (startupContext.Args.ContainsKey(StartupContext.APPDATA))
            {
                instancePolicy.WarnIfAlreadyRunning();
            }
            else if (isService)
            {
                instancePolicy.KillAllOtherInstance();
            }
            else
            {
                instancePolicy.PreventStartIfAlreadyRunning();
            }
        }
    }
}

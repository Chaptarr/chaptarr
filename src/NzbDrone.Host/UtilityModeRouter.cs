using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NLog.Extensions.Logging;
using NzbDrone.Common;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Processes;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Host.AccessControl;
using IServiceProvider = NzbDrone.Common.IServiceProvider;

namespace NzbDrone.Host
{
    public interface IUtilityModeRouter
    {
        void Route(ApplicationModes applicationModes);
    }

    public class UtilityModeRouter : IUtilityModeRouter
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConsoleService _consoleService;
        private readonly IProcessProvider _processProvider;
        private readonly IRemoteAccessAdapter _remoteAccessAdapter;
        private readonly IAppFolderFactory _appFolderFactory;
        private readonly IConnectionStringFactory _connectionStringFactory;
        private readonly Lazy<IConfigService> _configService;
        private readonly Lazy<IConfigFileProvider> _configFileProvider;
        private readonly IStartupContext _startupContext;
        private readonly Logger _logger;

        public UtilityModeRouter(IServiceProvider serviceProvider,
                      IConsoleService consoleService,
                      IProcessProvider processProvider,
                      IRemoteAccessAdapter remoteAccessAdapter,
                      IAppFolderFactory appFolderFactory,
                      IConnectionStringFactory connectionStringFactory,
                      Lazy<IConfigService> configService,
                      Lazy<IConfigFileProvider> configFileProvider,
                      IStartupContext startupContext,
                      Logger logger)
        {
            _serviceProvider = serviceProvider;
            _consoleService = consoleService;
            _processProvider = processProvider;
            _remoteAccessAdapter = remoteAccessAdapter;
            _appFolderFactory = appFolderFactory;
            _connectionStringFactory = connectionStringFactory;
            _configService = configService;
            _configFileProvider = configFileProvider;
            _startupContext = startupContext;
            _logger = logger;
        }

        public void Route(ApplicationModes applicationModes)
        {
            _logger.Info("Application mode: {0}", applicationModes);

            switch (applicationModes)
            {
                case ApplicationModes.InstallService:
                    {
                        _logger.Debug("Install Service selected");
                        if (_serviceProvider.ServiceExist(ServiceProvider.SERVICE_NAME))
                        {
                            _consoleService.PrintServiceAlreadyExist();
                        }
                        else
                        {
                            _remoteAccessAdapter.MakeAccessible(true);
                            _serviceProvider.Install(ServiceProvider.SERVICE_NAME);
                            _serviceProvider.SetPermissions(ServiceProvider.SERVICE_NAME);

                            if (!_startupContext.NoStartService)
                            {
                                // Start the service and exit.
                                // Ensures that there isn't an instance of Chaptarr already running that the service account cannot stop.
                                _processProvider.SpawnNewProcess("sc.exe", $"start {ServiceProvider.SERVICE_NAME}", null, true);
                            }
                        }

                        break;
                    }

                case ApplicationModes.UninstallService:
                    {
                        _logger.Debug("Uninstall Service selected");
                        if (!_serviceProvider.ServiceExist(ServiceProvider.SERVICE_NAME))
                        {
                            _consoleService.PrintServiceDoesNotExist();
                        }
                        else
                        {
                            _serviceProvider.Uninstall(ServiceProvider.SERVICE_NAME);
                        }

                        break;
                    }

                case ApplicationModes.RegisterUrl:
                    {
                        _logger.Debug("Regiser URL selected");
                        _remoteAccessAdapter.MakeAccessible(false);
                        _appFolderFactory.SetPermissions();

                        break;
                    }

                case ApplicationModes.MigrateBookshelf:
                    {
                        _appFolderFactory.Register();
                        var database = _connectionStringFactory.MainDbConnection;
                        if (database.DatabaseType is not (DatabaseType.SQLite or DatabaseType.PostgreSQL) ||
                            DatabaseLineageGuard.Detect(database.ConnectionString, database.DatabaseType) != DatabaseLineage.BookshelfNG)
                        {
                            throw new InvalidDataException("No supported BookshelfNG SQLite or PostgreSQL database was found in this data directory. No database was changed.");
                        }

                        var previewOnly = _startupContext.PreviewBookshelfMigration;
                        if (!previewOnly && !_startupContext.ConfirmBookshelfServiceStopped)
                        {
                            throw new InvalidOperationException(
                                "Applying a BookshelfNG migration requires -confirm-bookshelf-service-stopped. Stop every process using this data directory first.");
                        }

                        var migrationController = new MigrationController(
                            _logger,
                            new NLogLoggerProvider(),
                            new AppFolderInfo(_startupContext),
                            _configFileProvider.Value);
                        var result = migrationController.MigrateBookshelf(
                            database.ConnectionString,
                            new MigrationContext(MigrationType.Main),
                            database.DatabaseType,
                            previewOnly,
                            _startupContext.MergeBookshelfDatabases);

                        Console.WriteLine(previewOnly
                            ? "BookshelfNG migration preview passed. The source database was not changed."
                            : $"BookshelfNG migration completed. Backup retained at: {result.BackupPath}");
                        if (_startupContext.MergeBookshelfDatabases && previewOnly)
                        {
                            Console.WriteLine("Both original SQLite databases were read-only; the staged merged database was discarded after preview.");
                        }
                        else if (_startupContext.MergeBookshelfDatabases)
                        {
                            Console.WriteLine("Both original SQLite databases were retained. The merged ChaptarrNG database is at chaptarr.db.");
                        }
                        Console.WriteLine("Source rows by table: source | preserved | transformed | rebuilt | reset");
                        foreach (var table in result.ImportReport.Tables.OrderBy(item => item.Key, StringComparer.Ordinal))
                        {
                            var row = table.Value;
                            Console.WriteLine($"{table.Key}: {row.SourceRows} | {row.PreservedRows} | {row.TransformedRows} | {row.RebuiltRows} | {row.ResetRows} — {row.TargetMapping}");
                        }

                        foreach (var issue in result.ImportReport.BlockingIssues)
                        {
                            Console.WriteLine("BLOCKED: " + issue);
                        }

                        break;
                    }

                case ApplicationModes.CheckBookshelfMigration:
                    {
                        _appFolderFactory.Register();
                        if (_startupContext.Args.TryGetValue("bookshelf-health-url", out var healthUrl))
                        {
                            var timeoutSeconds = 14700;
                            if (_startupContext.Args.TryGetValue("bookshelf-health-timeout-seconds", out var timeoutValue) &&
                                (!int.TryParse(timeoutValue, out timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 14700))
                            {
                                throw new InvalidDataException("BookshelfNG HTTP health timeout must be between 1 and 14700 seconds.");
                            }

                            // Do not open the migrated database until the application has
                            // published its health endpoint. This keeps a failed HTTP probe
                            // read-only and avoids repeatedly opening SQLite beside a server
                            // that is still completing first-start migration.
                            WaitForBookshelfHttpReady(
                                string.Equals(healthUrl, "auto", StringComparison.OrdinalIgnoreCase)
                                    ? GetBookshelfHttpHealthUrl().ToString()
                                    : healthUrl,
                                timeoutSeconds);
                        }

                        var database = _connectionStringFactory.MainDbConnection;
                        if (database.DatabaseType is not (DatabaseType.SQLite or DatabaseType.PostgreSQL) ||
                            DatabaseLineageGuard.Detect(database.ConnectionString, database.DatabaseType) != DatabaseLineage.Chaptarr)
                        {
                            throw new InvalidDataException("BookshelfNG handoff health check did not find a readable ChaptarrNG database.");
                        }

                        DatabaseLineageGuard.Validate(database.ConnectionString, database.DatabaseType);

                        if (!_configService.Value.BookshelfNGDatabaseMigrated)
                        {
                            throw new InvalidDataException("BookshelfNG handoff health check did not find the persisted migration marker.");
                        }

                        var compatibilityProfile = _configService.Value.BookshelfNGMigrationProfile;
                        if (!compatibilityProfile.IsCompatible)
                        {
                            throw new InvalidDataException("BookshelfNG handoff health check found a missing or unsupported compatibility profile.");
                        }
                        var providerDialect = compatibilityProfile.ProviderDialect;

                        var journal = new BookshelfMigrationJournal(new AppFolderInfo(_startupContext).AppDataFolder);
                        journal.RecordOutcome(
                            "health",
                            _startupContext.Args.ContainsKey("bookshelf-health-url")
                                ? "database-and-http-readable"
                                : "database-readable");
                        journal.RecordState("healthy");

                        Console.WriteLine(_startupContext.Args.ContainsKey("bookshelf-health-url")
                            ? $"BookshelfNG handoff health check passed. The migrated {providerDialect} ChaptarrNG database and local HTTP endpoint are ready."
                            : $"BookshelfNG handoff health check passed. The migrated {providerDialect} ChaptarrNG database is readable.");
                        break;
                    }

                case ApplicationModes.RestoreBookshelf:
                    {
                        _appFolderFactory.Register();
                        var database = _connectionStringFactory.MainDbConnection;
                        if (database.DatabaseType is not (DatabaseType.SQLite or DatabaseType.PostgreSQL))
                        {
                            throw new InvalidDataException("BookshelfNG rollback requires a SQLite or PostgreSQL database in this data directory.");
                        }

                        if (!_startupContext.Args.TryGetValue("bookshelf-backup", out var backupPath) ||
                            string.IsNullOrWhiteSpace(backupPath))
                        {
                            throw new InvalidOperationException("BookshelfNG rollback requires a retained source backup, or -bookshelf-backup=auto to select the newest retained backup.");
                        }

                        var automaticRollback = string.Equals(backupPath, "auto", StringComparison.OrdinalIgnoreCase);
                        var lineage = DatabaseLineageGuard.Detect(database.ConnectionString, database.DatabaseType);
                        if (automaticRollback && lineage == DatabaseLineage.BookshelfNG)
                        {
                            Console.WriteLine("The database is still a BookshelfNG database; conversion did not promote a ChaptarrNG target, so no database restore is needed.");
                            break;
                        }

                        if (lineage != DatabaseLineage.Chaptarr)
                        {
                            throw new InvalidDataException("BookshelfNG rollback requires a valid ChaptarrNG database or an untouched BookshelfNG source database.");
                        }

                        var previewOnly = _startupContext.PreviewBookshelfRestore;
                        if (!previewOnly && !_startupContext.ConfirmBookshelfServiceStopped)
                        {
                            throw new InvalidOperationException(
                                "Restoring a BookshelfNG database requires -confirm-bookshelf-service-stopped. Stop every process using this data directory first.");
                        }

                        var migrationController = new MigrationController(_logger, new NLogLoggerProvider(), new AppFolderInfo(_startupContext));
                        var result = migrationController.RestoreBookshelf(
                            database.ConnectionString,
                            database.DatabaseType,
                            automaticRollback ? null : backupPath,
                            previewOnly);
                        Console.WriteLine(previewOnly
                            ? $"BookshelfNG rollback preview passed. The current ChaptarrNG database and source backup were not changed: {result.BookshelfBackupPath}"
                            : result.OriginalSourcesRetained
                                ? "The original readarr.db and audioarr.db sources remain in place. The ChaptarrNG target was moved aside for a safe BookshelfNG rollback."
                                : $"BookshelfNG database restored from: {result.BookshelfBackupPath}");
                        if (!previewOnly)
                        {
                            Console.WriteLine($"The previous ChaptarrNG database, including post-migration writes, is retained at: {result.ChaptarrBackupPath}");
                            Console.WriteLine(result.OriginalSourcesRetained
                                ? "Start BookshelfNG against its original databases. ChaptarrNG can merge their latest contents again when you next choose to upgrade."
                                : "Start only BookshelfNG against this restored database. Do not start ChaptarrNG until you intentionally convert again.");
                        }

                        break;
                    }

                case ApplicationModes.CheckBookshelfPostgresBackup:
                    {
                        GetPostgresBookshelfDatabase(DatabaseLineage.BookshelfNG);
                        var databases = GetPostgresBackupDatabases();
                        var backup = new BookshelfPostgresExternalBackup(new ExternalToolRunner());
                        backup.VerifyTools();
                        foreach (var (_, database) in databases)
                        {
                            backup.ValidateDatabaseConnection(database.ConnectionString);
                        }

                        if (_startupContext.ConfirmBookshelfServiceStopped)
                        {
                            foreach (var (_, database) in databases)
                            {
                                backup.EnsureSourceDatabaseIsIdle(database.ConnectionString);
                            }
                        }

                        Console.WriteLine("PostgreSQL external backup tools are available for the BookshelfNG main, log, and cache databases.");
                        break;
                    }

                case ApplicationModes.PreviewBookshelfPostgresRestore:
                    {
                        if (!_startupContext.ConfirmBookshelfServiceStopped)
                        {
                            throw new InvalidOperationException("Validating PostgreSQL rollback dumps requires -confirm-bookshelf-service-stopped. Stop every process using these databases first.");
                        }

                        GetPostgresBookshelfDatabase(DatabaseLineage.BookshelfNG, DatabaseLineage.Chaptarr);
                        var databases = GetPostgresBackupDatabases();
                        if (_startupContext.Flags.Contains("bookshelf-postgres-log-cache-only"))
                        {
                            databases = databases.Where(database => database.Name != "main").ToArray();
                        }

                        if (!_startupContext.Args.TryGetValue("bookshelf-postgres-backup", out var backupPath) ||
                            string.IsNullOrWhiteSpace(backupPath))
                        {
                            throw new InvalidOperationException("PostgreSQL restore preview requires -bookshelf-postgres-backup=/path/to/backup-directory.");
                        }

                        var backup = new BookshelfPostgresExternalBackup(new ExternalToolRunner());
                        backup.VerifyTools();
                        backupPath = Path.GetFullPath(backupPath);
                        var restoreItems = new System.Collections.Generic.List<(string Name, DatabaseConnectionInfo Database, string Path)>();
                        foreach (var (name, database) in databases)
                        {
                            backup.ValidateDatabaseConnection(database.ConnectionString);
                            backup.EnsureSourceDatabaseIsIdle(database.ConnectionString);
                            var dumpPath = Path.Combine(backupPath, $"postgres-{name}.dump");
                            backup.ValidateDump(dumpPath);
                            restoreItems.Add((name, database, dumpPath));
                        }

                        Console.WriteLine($"PostgreSQL rollback preview passed. Validated {restoreItems.Count} retained database dump(s); no database was changed.");
                        break;
                    }

                case ApplicationModes.BackupBookshelfPostgres:
                    {
                        if (!_startupContext.ConfirmBookshelfServiceStopped)
                        {
                            throw new InvalidOperationException("Creating an independent PostgreSQL backup requires -confirm-bookshelf-service-stopped. Stop every process using these databases first.");
                        }

                        if (_connectionStringFactory.MainDbConnection.DatabaseType == DatabaseType.SQLite)
                        {
                            if (DatabaseLineageGuard.Detect(
                                    _connectionStringFactory.MainDbConnection.ConnectionString,
                                    DatabaseType.SQLite) != DatabaseLineage.BookshelfNG)
                            {
                                throw new InvalidDataException("BookshelfNG preflight did not find a supported SQLite source database.");
                            }

                            if (_connectionStringFactory.LogDbConnection.DatabaseType != DatabaseType.SQLite ||
                                _connectionStringFactory.CacheDbConnection.DatabaseType != DatabaseType.SQLite)
                            {
                                throw new InvalidDataException("Independent PostgreSQL backup does not support mixing a SQLite main database with PostgreSQL log or cache databases.");
                            }

                            Console.WriteLine("The configured BookshelfNG database is SQLite; an external PostgreSQL backup is not needed.");
                            break;
                        }

                        var logCacheOnly = _startupContext.Flags.Contains("bookshelf-postgres-log-cache-only");
                        GetPostgresBookshelfDatabase(logCacheOnly
                            ? new[] { DatabaseLineage.BookshelfNG, DatabaseLineage.Chaptarr }
                            : new[] { DatabaseLineage.BookshelfNG });
                        var databases = GetPostgresBackupDatabases();
                        if (logCacheOnly)
                        {
                            databases = databases.Where(database => database.Name != "main").ToArray();
                        }

                        if (!_startupContext.Args.TryGetValue("bookshelf-postgres-backup", out var backupPath) ||
                            string.IsNullOrWhiteSpace(backupPath))
                        {
                            throw new InvalidOperationException("PostgreSQL backup requires -bookshelf-postgres-backup=/path/to/backup-directory.");
                        }

                        var backup = new BookshelfPostgresExternalBackup(new ExternalToolRunner());
                        backup.VerifyTools();
                        backupPath = Path.GetFullPath(backupPath);
                        foreach (var (name, database) in databases)
                        {
                            backup.CreateDump(database.ConnectionString, Path.Combine(backupPath, $"postgres-{name}.dump"));
                            Console.WriteLine($"Independent PostgreSQL {name} database backup created and verified.");
                        }

                        break;
                    }

                case ApplicationModes.RestoreBookshelfPostgres:
                    {
                        if (!_startupContext.ConfirmBookshelfServiceStopped)
                        {
                            throw new InvalidOperationException("Restoring an independent PostgreSQL backup requires -confirm-bookshelf-service-stopped. Stop every process using this database first.");
                        }

                        GetPostgresBookshelfDatabase(DatabaseLineage.BookshelfNG, DatabaseLineage.Chaptarr);
                        var databases = GetPostgresBackupDatabases();
                        if (_startupContext.Flags.Contains("bookshelf-postgres-log-cache-only"))
                        {
                            databases = databases.Where(database => database.Name != "main").ToArray();
                        }

                        if (!_startupContext.Args.TryGetValue("bookshelf-postgres-backup", out var backupPath) ||
                            string.IsNullOrWhiteSpace(backupPath))
                        {
                            throw new InvalidOperationException("PostgreSQL restore requires -bookshelf-postgres-backup=/path/to/backup-directory.");
                        }

                        var backup = new BookshelfPostgresExternalBackup(new ExternalToolRunner());
                        backup.VerifyTools();
                        backupPath = Path.GetFullPath(backupPath);
                        var restoreItems = new System.Collections.Generic.List<(string Name, DatabaseConnectionInfo Database, string Path)>();
                        foreach (var (name, database) in databases)
                        {
                            backup.EnsureSourceDatabaseIsIdle(database.ConnectionString);
                            var dumpPath = Path.Combine(backupPath, $"postgres-{name}.dump");
                            backup.ValidateDump(dumpPath);
                            restoreItems.Add((name, database, dumpPath));
                        }

                        foreach (var (name, database, dumpPath) in restoreItems)
                        {
                            backup.RestoreDump(database.ConnectionString, dumpPath);
                            Console.WriteLine($"Independent PostgreSQL {name} database backup restored.");
                        }

                        break;
                    }

                default:
                    {
                        _consoleService.PrintHelp();
                        break;
                    }
            }
        }

        private static void WaitForBookshelfHttpReady(string healthUrl, int timeoutSeconds)
        {
            if (!Uri.TryCreate(healthUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !(uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) || IsLocalInterfaceAddress(uri.Host)))
            {
                throw new InvalidDataException("BookshelfNG handoff health URL must use HTTP or HTTPS on localhost, a loopback address, or an IP address assigned to this machine.");
            }

            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            };
            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                // This probe is restricted above to loopback or a local
                // interface. The operator's own certificate may be
                // self-signed, so local readiness must not depend on the
                // host machine trusting that certificate chain.
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Min(3, timeoutSeconds)) };
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed.TotalSeconds < timeoutSeconds)
            {
                try
                {
                    using var response = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        Console.WriteLine($"BookshelfNG handoff HTTP health check passed at {uri}.");
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // The standalone server can take time to bind after its database is ready.
                }
                catch (TaskCanceledException)
                {
                    // Retry short connection timeouts until the bounded deadline.
                }

                Thread.Sleep(1000);
            }

            throw new TimeoutException($"ChaptarrNG did not return HTTP 200 from {uri} within {timeoutSeconds} seconds.");
        }

        private Uri GetBookshelfHttpHealthUrl()
        {
            var config = _configFileProvider.Value;
            var bindAddress = config.BindAddress?.Trim();
            if (bindAddress is "::" or "[::]")
            {
                bindAddress = IPAddress.IPv6Loopback.ToString();
            }
            else if (string.IsNullOrWhiteSpace(bindAddress) || bindAddress is "*" or "+" or "0.0.0.0")
            {
                bindAddress = IPAddress.Loopback.ToString();
            }
            else if (bindAddress.StartsWith("[", StringComparison.Ordinal) && bindAddress.EndsWith("]", StringComparison.Ordinal))
            {
                bindAddress = bindAddress.Substring(1, bindAddress.Length - 2);
            }

            if (!string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase) &&
                !IPAddress.TryParse(bindAddress, out _))
            {
                var resolved = Dns.GetHostAddresses(bindAddress);
                if (resolved.Length == 0 || resolved.Any(address => !IPAddress.IsLoopback(address) && !IsLocalInterfaceAddress(address)))
                {
                    throw new InvalidDataException("BookshelfNG handoff cannot use the configured server bind name for a local health check because it does not resolve only to this machine's network interfaces.");
                }

                bindAddress = resolved[0].ToString();
            }

            var basePath = config.UrlBase?.Trim('/');
            var path = string.IsNullOrWhiteSpace(basePath) ? "/ping" : $"/{basePath}/ping";
            return new UriBuilder(Uri.UriSchemeHttp, bindAddress, config.Port) { Path = path }.Uri;
        }

        private static bool IsLocalInterfaceAddress(string host)
        {
            return IPAddress.TryParse(host, out var address) && IsLocalInterfaceAddress(address);
        }

        private static bool IsLocalInterfaceAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
            {
                return false;
            }

            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
                .Any(unicastAddress => unicastAddress.Address.Equals(address));
        }

        private DatabaseConnectionInfo GetPostgresBookshelfDatabase(params DatabaseLineage[] allowedLineages)
        {
            var database = _connectionStringFactory.MainDbConnection;
            if (database.DatabaseType != DatabaseType.PostgreSQL)
            {
                throw new InvalidDataException("The configured main database is not PostgreSQL.");
            }

            var lineage = DatabaseLineageGuard.Detect(database.ConnectionString, DatabaseType.PostgreSQL);
            if (!allowedLineages.Contains(lineage))
            {
                throw new InvalidDataException($"The configured PostgreSQL main database has unsupported lineage '{lineage}'.");
            }

            return database;
        }

        private (string Name, DatabaseConnectionInfo Database)[] GetPostgresBackupDatabases()
        {
            var databases = new[]
            {
                ("main", _connectionStringFactory.MainDbConnection),
                ("log", _connectionStringFactory.LogDbConnection),
                ("cache", _connectionStringFactory.CacheDbConnection)
            };

            if (databases.Any(database => database.Item2.DatabaseType != DatabaseType.PostgreSQL))
            {
                throw new InvalidDataException("BookshelfNG PostgreSQL backup requires PostgreSQL for its main, log, and cache databases.");
            }

            return databases
                .GroupBy(database => database.Item2.ConnectionString, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
        }
    }
}

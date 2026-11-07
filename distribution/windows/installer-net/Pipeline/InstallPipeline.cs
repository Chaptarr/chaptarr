using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ChaptarrNG.Installer.Pipeline;

public sealed record InstallOutcome(bool Success, string? BackupPath, string? ErrorMessage);

public static class InstallPipeline
{
    private static readonly TimeSpan StopServiceTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan VerifyRunningTimeout = TimeSpan.FromSeconds(60);
    // Database conversion completes before the target service starts. Once it
    // reports RUNNING, its local listener should become healthy promptly; a
    // failed bind must enter rollback instead of leaving a broken target active
    // for the multi-hour migration window.
    private static readonly TimeSpan VerifyHealthTimeout = TimeSpan.FromMinutes(5);

    public static async Task<InstallOutcome> RunUpgradeAsync(
        IUiReporter ui,
        CancellationToken ct = default)
    {
        var log = new Progress<LogLine>(line => ui.Log(line));
        var extractProgress = new Progress<ExtractProgress>(p =>
            ui.SetProgressPercent(p.Total == 0 ? 0 : p.Extracted * 100.0 / p.Total));
        string? dataDirectory = null;
        string? backupPath = null;
        string? stagingDirectory = null;
        string? previousBinDirectory = null;
        string? bookshelfServiceAccount = null;
        SecureString? serviceAccountPassword = null;
        var serviceStopped = false;
        var bookshelfWasRunning = false;
        var postgresDatabaseMutationMayHaveStarted = false;
        int? bookshelfServiceStartType = null;
        bool? bookshelfServiceDelayedAutoStart = null;

        try
        {
            ui.SetStep("Checking the existing BookshelfNG installation...");
            ui.SetProgressIndeterminate();

            if (!PayloadExtractor.HasEmbeddedPayload())
            {
                return Failure("This installer package does not contain the ChaptarrNG application payload.", null);
            }

            if (!ServiceLifecycle.IsInstalled(ServiceLifecycle.BookshelfServiceName))
            {
                return Failure("No Readarr service was found. This installer only migrates an existing BookshelfNG Windows service.", null);
            }

            if (ServiceLifecycle.IsInstalled(ServiceLifecycle.ChaptarrServiceName))
            {
                return Failure("A Chaptarr service is already installed. Stop and remove it using its own installer before migrating BookshelfNG.", null);
            }

            dataDirectory = ServiceLifecycle.GetBookshelfDataDirectory();
            var unsupportedSource = BackupService.GetUnsupportedSourceReason(dataDirectory);
            if (unsupportedSource is not null)
            {
                return Failure(unsupportedSource, null);
            }
            bookshelfServiceAccount = ServiceLifecycle.GetBookshelfServiceAccount();
            var postgresSource = BackupService.IsPostgresSource(dataDirectory);
            var mergeSqliteSources = !postgresSource && BookshelfDatabaseFiles.GetPopulatedDatabaseNames(dataDirectory).Count > 1;

            var providerMode = ui.ChooseProviderIdentityMode();
            if (!providerMode.HasValue)
            {
                return Failure("Migration cancelled before the BookshelfNG service was stopped.", null);
            }
            var migrationEnvironment = BookshelfServiceEnvironment.CreateMigrationOverrides(providerMode.Value);

            ui.Log($"[pipeline] BookshelfNG service data directory: {dataDirectory}");
            ui.Log($"[pipeline] Provider identity mode selected: {providerMode.Value}");
            if (postgresSource)
            {
                ui.SetStep("Checking PostgreSQL backup support...", "This handoff needs pg_dump, pg_restore, an explicit PostgresUser, and a reachable database for its independent rollback copy.");
                stagingDirectory = await PayloadExtractor.ExtractToStagingDirectoryAsync(
                    dataDirectory,
                    log,
                    extractProgress,
                    ct).ConfigureAwait(false);
                var checkExit = await ProcessStreamer.RunAsync(
                    Path.Combine(stagingDirectory, "Chaptarr.Console.exe"),
                    new[] { "-check-bookshelf-postgres-backup", $"-data={dataDirectory}" },
                    stagingDirectory,
                    log,
                    ct,
                    migrationEnvironment).ConfigureAwait(false);
                if (checkExit != 0)
                {
                    return Failure($"PostgreSQL backup preflight exited with code {checkExit}. Review the installer log, install pg_dump and pg_restore on PATH, and ensure PostgresUser is explicitly configured.", null);
                }
            }

            if (ServiceAccountPolicy.RequiresPassword(bookshelfServiceAccount))
            {
                serviceAccountPassword = ui.RequestServiceAccountPassword(bookshelfServiceAccount);
                if (serviceAccountPassword is null || serviceAccountPassword.Length == 0)
                {
                    serviceAccountPassword?.Dispose();
                    serviceAccountPassword = null;
                    return Failure("Migration cancelled before the BookshelfNG service was stopped because its Windows service-account password was not provided.", null);
                }
            }

            var initialServiceStatus = ServiceLifecycle.TryGetStatus(ServiceLifecycle.BookshelfServiceName);
            if (initialServiceStatus is not (ServiceControllerStatus.Running or ServiceControllerStatus.Stopped))
            {
                return Failure("The Readarr service is changing state; wait for it to reach RUNNING or STOPPED before migrating.", null);
            }
            bookshelfWasRunning = initialServiceStatus == ServiceControllerStatus.Running;
            bookshelfServiceStartType = ServiceLifecycle.GetBookshelfServiceStartType();
            bookshelfServiceDelayedAutoStart = ServiceLifecycle.GetBookshelfServiceDelayedAutoStart();
            ui.SetStep("Stopping BookshelfNG...", "The service must be stopped before the database backup and migration.");
            ct.ThrowIfCancellationRequested();
            if (!await ServiceLifecycle.StopAsync(
                    ServiceLifecycle.BookshelfServiceName,
                    log,
                    StopServiceTimeout,
                    CancellationToken.None).ConfigureAwait(false))
            {
                return Failure("Could not stop the BookshelfNG service. No migration was applied.", null);
            }

            serviceStopped = true;
            if (postgresSource)
            {
                ui.SetStep("Checking PostgreSQL connections...", "Stop every other application or client using the BookshelfNG database before the independent backup.");
                var idleCheckExit = await ProcessStreamer.RunAsync(
                    Path.Combine(stagingDirectory!, "Chaptarr.Console.exe"),
                    new[]
                    {
                        "-check-bookshelf-postgres-backup",
                        "-confirm-bookshelf-service-stopped",
                        $"-data={dataDirectory}"
                    },
                    stagingDirectory,
                    log,
                    CancellationToken.None,
                    migrationEnvironment).ConfigureAwait(false);
                if (idleCheckExit != 0)
                {
                    throw new InvalidOperationException($"PostgreSQL connection preflight exited with code {idleCheckExit}; the source database was not migrated.");
                }
            }

            ui.SetStep("Backing up BookshelfNG...", postgresSource
                ? "The original binaries, config, independent PostgreSQL dump, and existing backups are retained."
                : "The original binaries, config, every populated SQLite database, and existing backups are retained.");
            var migrationConsole = stagingDirectory is null
                ? null
                : Path.Combine(stagingDirectory, "Chaptarr.Console.exe");
            var backup = await BackupService.BackupAsync(
                dataDirectory,
                log,
                postgresSource,
                postgresSource
                    ? async backupSetDirectory =>
                    {
                        var backupExit = await ProcessStreamer.RunAsync(
                            migrationConsole!,
                            new[]
                            {
                                "-backup-bookshelf-postgres",
                                "-confirm-bookshelf-service-stopped",
                                $"-bookshelf-postgres-backup={backupSetDirectory}",
                                $"-data={dataDirectory}"
                            },
                            stagingDirectory,
                            log,
                            ct,
                            migrationEnvironment).ConfigureAwait(false);
                        if (backupExit != 0)
                        {
                            throw new InvalidOperationException($"Independent PostgreSQL backup exited with code {backupExit}.");
                        }
                    }
                    : null,
                ct).ConfigureAwait(false);
            backupPath = backup.Path;
            RegistryService.WriteUpgradeStart(
                backup.Path,
                dataDirectory,
                bookshelfServiceAccount,
                providerMode.Value.ToString(),
                BackupService.GetPreviousVersion(dataDirectory),
                bookshelfServiceStartType.Value,
                bookshelfServiceDelayedAutoStart.Value,
                bookshelfWasRunning);
            ui.Log($"[pipeline] Verified rollback backup recorded at {backup.Path}");

            if (!await ServiceLifecycle.SetStartTypeAsync(
                    ServiceLifecycle.BookshelfServiceName,
                    "disabled",
                    log,
                    ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The stopped BookshelfNG service could not be disabled during migration.");
            }

            stagingDirectory ??= await PayloadExtractor.ExtractToStagingDirectoryAsync(
                dataDirectory,
                log,
                extractProgress,
                ct).ConfigureAwait(false);
            var chaptarrConsole = Path.Combine(stagingDirectory, "Chaptarr.Console.exe");
            ui.SetStep("Previewing the migration...", postgresSource
                ? "Keep every other PostgreSQL client stopped through review and apply. The independent database dump is retained for rollback."
                : "The SQLite source database is checked for committed changes through operator confirmation.");
            SqliteDatabaseFingerprint.Snapshot? sourceFingerprint = null;
            if (postgresSource)
            {
                var previewExit = await ProcessStreamer.RunAsync(
                    chaptarrConsole,
                    new[] { "-preview-bookshelf-migration", $"-data={dataDirectory}" },
                    stagingDirectory,
                    log,
                    ct,
                    migrationEnvironment).ConfigureAwait(false);
                if (previewExit != 0)
                {
                    throw new InvalidOperationException($"The read-only BookshelfNG migration preview exited with code {previewExit}.");
                }

                ui.SetStep("Review the migration preview...", "Read the row report and warnings in the log before continuing.");
                if (!ui.ConfirmMigrationApply(providerMode.Value, backup.Path))
                {
                    throw new OperationCanceledException("Migration cancelled at the preview review step.", ct);
                }

                var idleCheckExit = await ProcessStreamer.RunAsync(
                    chaptarrConsole,
                    new[]
                    {
                        "-check-bookshelf-postgres-backup",
                        "-confirm-bookshelf-service-stopped",
                        $"-data={dataDirectory}"
                    },
                    stagingDirectory,
                    log,
                    CancellationToken.None,
                    migrationEnvironment).ConfigureAwait(false);
                if (idleCheckExit != 0)
                {
                    throw new InvalidOperationException($"PostgreSQL connection preflight exited with code {idleCheckExit}; the source database was not migrated.");
                }
            }
            else
            {
                sourceFingerprint = SqliteDatabaseFingerprint.Capture(dataDirectory);
                using var sourceVersionGuard = new SqliteDatabaseVersionGuard(dataDirectory);
                var previewArguments = new List<string> { "-preview-bookshelf-migration", $"-data={dataDirectory}" };
                if (mergeSqliteSources)
                {
                    previewArguments.Add("-merge-bookshelf-databases");
                }
                var previewExit = await ProcessStreamer.RunAsync(
                    chaptarrConsole,
                    previewArguments,
                    stagingDirectory,
                    log,
                    ct,
                    migrationEnvironment).ConfigureAwait(false);
                if (previewExit != 0)
                {
                    throw new InvalidOperationException($"The read-only BookshelfNG migration preview exited with code {previewExit}.");
                }

                EnsureSourceDatabaseUnchanged(sourceVersionGuard, "during preview");

                ui.SetStep("Review the migration preview...", "Read the row report and warnings in the log before continuing.");
                if (!ui.ConfirmMigrationApply(providerMode.Value, backup.Path))
                {
                    throw new OperationCanceledException("Migration cancelled at the preview review step.", ct);
                }

                EnsureSourceDatabaseUnchanged(sourceVersionGuard, "while the preview awaited confirmation");
            }

            if (sourceFingerprint is not null)
            {
                var afterReviewFingerprint = SqliteDatabaseFingerprint.Capture(dataDirectory);
                LogSourceDatabaseRepresentationChange(sourceFingerprint, afterReviewFingerprint, "during preview and operator review", ui);
            }

            ui.SetStep("Applying BookshelfNG data migration...", "This modifies the database after the separate, verified backup.");
            var applyArguments = new List<string>
            {
                "-migrate-bookshelf",
                "-confirm-bookshelf-service-stopped",
                $"-data={dataDirectory}"
            };
            if (mergeSqliteSources)
            {
                applyArguments.Add("-merge-bookshelf-databases");
            }
            var applyExit = await ProcessStreamer.RunAsync(
                chaptarrConsole,
                applyArguments,
                stagingDirectory,
                log,
                ct,
                migrationEnvironment,
                () => postgresDatabaseMutationMayHaveStarted = postgresSource).ConfigureAwait(false);
            if (applyExit != 0)
            {
                throw new InvalidOperationException($"The BookshelfNG data migration exited with code {applyExit}.");
            }

            ui.SetStep("Switching the Windows service to ChaptarrNG...");
            previousBinDirectory = PayloadExtractor.ReplaceBinDirectory(dataDirectory, stagingDirectory, log);
            stagingDirectory = null;

            var installedConsole = Path.Combine(dataDirectory, "bin", "Chaptarr.Console.exe");
            if (!File.Exists(installedConsole))
            {
                throw new FileNotFoundException("The ChaptarrNG service executable is missing after the binary switch.", installedConsole);
            }

            ui.SetStep("Registering the ChaptarrNG service...", "The Readarr data directory is explicitly preserved in the service command.");
            var installExit = await ProcessStreamer.RunAsync(
                installedConsole,
                new[] { "/i", $"-data={dataDirectory}", "/nobrowser", "/nostart" },
                Path.GetDirectoryName(installedConsole),
                log,
                ct).ConfigureAwait(false);
            if (installExit != 0)
            {
                throw new InvalidOperationException($"Chaptarr.Console.exe service registration exited with code {installExit}.");
            }

            ServiceLifecycle.SetServiceEnvironment(
                ServiceLifecycle.ChaptarrServiceName,
                BookshelfServiceEnvironment.CreateTargetServiceEnvironment(providerMode.Value));
            ui.Log("[pipeline] BookshelfNG service settings and the selected provider mode were applied to ChaptarrNG before startup.");

            var installedServiceAccount = ServiceLifecycle.GetServiceAccount(ServiceLifecycle.ChaptarrServiceName);
            if (!ServiceAccountPolicy.AreEquivalent(installedServiceAccount, bookshelfServiceAccount))
            {
                if (ServiceLifecycle.TryGetStatus(ServiceLifecycle.ChaptarrServiceName) == ServiceControllerStatus.Running &&
                    !await ServiceLifecycle.StopAsync(
                            ServiceLifecycle.ChaptarrServiceName,
                            log,
                            StopServiceTimeout,
                            CancellationToken.None).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The default ChaptarrNG service could not be stopped to restore the BookshelfNG service identity.");
                }

                ServiceLifecycle.SetServiceAccount(
                    ServiceLifecycle.ChaptarrServiceName,
                    bookshelfServiceAccount,
                    serviceAccountPassword);
                ui.Log($"[pipeline] ChaptarrNG service identity preserved as '{bookshelfServiceAccount}'.");
            }

            if (!await ServiceLifecycle.SetStartTypeAsync(
                    ServiceLifecycle.ChaptarrServiceName,
                    "demand",
                    log,
                    ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The ChaptarrNG service could not be placed in a startable state for its local health check.");
            }

            ui.SetStep("Verifying ChaptarrNG...", "Waiting for the service and local health endpoint.");
            if (!await ServiceLifecycle.StartAsync(
                    ServiceLifecycle.ChaptarrServiceName,
                    log,
                    VerifyRunningTimeout,
                    ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The ChaptarrNG service did not reach RUNNING.");
            }

            var chaptarrImagePath = ServiceLifecycle.GetImagePath(ServiceLifecycle.ChaptarrServiceName);
            if (string.IsNullOrWhiteSpace(chaptarrImagePath) ||
                !chaptarrImagePath.Contains(dataDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The ChaptarrNG service command does not preserve the BookshelfNG data directory.");
            }

            if (!await WaitForHealthAsync(
                    dataDirectory,
                    installedConsole,
                    migrationEnvironment,
                    VerifyHealthTimeout,
                    log,
                    ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The ChaptarrNG service did not pass its local HTTP and migrated-database health checks.");
            }

            if (!bookshelfWasRunning &&
                !await ServiceLifecycle.StopAsync(
                        ServiceLifecycle.ChaptarrServiceName,
                        log,
                        StopServiceTimeout,
                        CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException("ChaptarrNG passed health but could not be returned to the stopped state BookshelfNG had before migration.");
            }

            var targetStartType = bookshelfServiceStartType switch
            {
                2 => "auto",
                3 => "demand",
                4 => "disabled",
                _ => throw new InvalidOperationException($"BookshelfNG uses unsupported service start type {bookshelfServiceStartType}.")
            };
            if (!await ServiceLifecycle.SetStartTypeAsync(
                    ServiceLifecycle.ChaptarrServiceName,
                    targetStartType,
                    log,
                    ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The ChaptarrNG service startup policy could not be matched to BookshelfNG.");
            }
            ServiceLifecycle.SetServiceDelayedAutoStart(
                ServiceLifecycle.ChaptarrServiceName,
                bookshelfServiceDelayedAutoStart ?? false);

            var newVersion = ReadInstalledChaptarrVersion(dataDirectory);
            if (!string.IsNullOrWhiteSpace(newVersion))
            {
                try
                {
                    RegistryService.WriteCurrentVersion(newVersion);
                }
                catch (Exception ex)
                {
                    ui.LogError($"[pipeline] Could not record ChaptarrNG version in the installer registry: {ex.Message}");
                }
            }

            var serviceCleanupScheduled = ServiceCleanupRunner.ScheduleLegacyServiceCleanup(dataDirectory, bookshelfWasRunning, ui);
            if (!serviceCleanupScheduled)
            {
                ui.LogError("[installer] The stopped Readarr service remains disabled so it cannot start beside ChaptarrNG. The installer could not schedule its removal after this window closes; restore remains available.");
            }

            PayloadExtractor.TryDeleteDirectory(previousBinDirectory);
            serviceStopped = false;
            ui.SetProgressPercent(100);
            ui.SetStep(
                "Migration complete.",
                $"ChaptarrNG is installed with the BookshelfNG data directory and its original service state. The verified rollback backup is retained at {backup.Path}. " +
                (serviceCleanupScheduled
                    ? "The disabled Readarr service will be removed after this installer closes."
                    : "Readarr remains disabled; use Restore BookshelfNG if you need to revert."));
            return new InstallOutcome(true, backup.Path, null);
        }
        catch (OperationCanceledException)
        {
            return await RestoreAfterFailureAsync(ui, log, dataDirectory, backupPath, previousBinDirectory, stagingDirectory, serviceStopped, bookshelfWasRunning, bookshelfServiceStartType, bookshelfServiceDelayedAutoStart, postgresDatabaseMutationMayHaveStarted, bookshelfServiceAccount, serviceAccountPassword, "Migration cancelled.")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ui.LogError($"[pipeline] {ex.GetType().Name}: {ex.Message}");
            return await RestoreAfterFailureAsync(ui, log, dataDirectory, backupPath, previousBinDirectory, stagingDirectory, serviceStopped, bookshelfWasRunning, bookshelfServiceStartType, bookshelfServiceDelayedAutoStart, postgresDatabaseMutationMayHaveStarted, bookshelfServiceAccount, serviceAccountPassword, ex.Message)
                .ConfigureAwait(false);
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                PayloadExtractor.TryDeleteDirectory(stagingDirectory);
            }
            serviceAccountPassword?.Dispose();
        }
    }

    public static async Task<InstallOutcome> RunRestoreAsync(IUiReporter ui, CancellationToken ct = default)
    {
        var log = new Progress<LogLine>(line => ui.Log(line));
        try
        {
            var record = RegistryService.Read();
            if (record?.BackupPath is null ||
                string.IsNullOrWhiteSpace(record.DataDirectory) ||
                !RegistryService.HasRecoverableUpgrade())
            {
                return Failure("No complete BookshelfNG migration backup was found in the installer registry record.", null);
            }
            if (ct.IsCancellationRequested)
            {
                return Failure("Restore was cancelled before it started.", record.BackupPath);
            }

            var bookshelfServiceAccount = record.BookshelfServiceAccount ?? ServiceAccountPolicy.LocalService;
            SecureString? serviceAccountPassword = null;
            var readarrServiceExists = ServiceLifecycle.IsInstalled(ServiceLifecycle.BookshelfServiceName);
            var readarrAccountMatches = readarrServiceExists && ServiceAccountPolicy.AreEquivalent(
                ServiceLifecycle.GetServiceAccount(ServiceLifecycle.BookshelfServiceName),
                bookshelfServiceAccount);
            if (ServiceAccountPolicy.RequiresPassword(bookshelfServiceAccount) && !readarrAccountMatches)
            {
                serviceAccountPassword = ui.RequestServiceAccountPassword(bookshelfServiceAccount);
                if (serviceAccountPassword is null || serviceAccountPassword.Length == 0)
                {
                    serviceAccountPassword?.Dispose();
                    serviceAccountPassword = null;
                    return Failure("Restore was cancelled because the original Windows service-account password was not provided.", record.BackupPath);
                }
            }

            try
            {
                ui.SetStep("Restoring BookshelfNG from the migration backup...", record.BackupPath);
                ui.SetProgressIndeterminate();
                var exit = await RollbackRunner.RunAsync(
                    record.BackupPath,
                    record.DataDirectory,
                    log,
                    CancellationToken.None,
                    record.BookshelfServiceStartType,
                    record.BookshelfServiceDelayedAutoStart,
                    record.BookshelfServiceWasRunning,
                    Path.Combine(record.DataDirectory, "bin", "Chaptarr.Console.exe"),
                    restorePostgresDatabase: File.Exists(Path.Combine(record.BackupPath, "postgres-main.dump")),
                    bookshelfServiceAccount: bookshelfServiceAccount,
                    serviceAccountPassword: serviceAccountPassword).ConfigureAwait(false);
                if (exit != 0)
                {
                    return Failure($"Rollback failed with exit code {exit}. The backup is still available at {record.BackupPath}.", record.BackupPath);
                }

                RecordRollbackTimestamp(ui);
                ui.SetProgressPercent(100);
                ui.SetStep("BookshelfNG restored.", "The original Readarr service and database backup are in use again.");
                return new InstallOutcome(true, record.BackupPath, null);
            }
            finally
            {
                serviceAccountPassword?.Dispose();
            }
        }
        catch (Exception ex)
        {
            ui.LogError($"[rollback] {ex.GetType().Name}: {ex.Message}");
            return Failure(ex.Message, null);
        }
    }

    private static async Task<InstallOutcome> RestoreAfterFailureAsync(
        IUiReporter ui,
        IProgress<LogLine> log,
        string? dataDirectory,
        string? backupPath,
        string? previousBinDirectory,
        string? stagingDirectory,
        bool serviceStopped,
        bool bookshelfWasRunning,
        int? bookshelfServiceStartType,
        bool? bookshelfServiceDelayedAutoStart,
        bool restorePostgresDatabase,
        string? bookshelfServiceAccount,
        SecureString? serviceAccountPassword,
        string reason)
    {
        ui.LogError($"[pipeline] {reason}");
        if (dataDirectory is null || !serviceStopped)
        {
            return Failure(reason, backupPath);
        }

        ui.SetStep("Restoring BookshelfNG...", backupPath is null
            ? "No complete migration backup exists; restarting the original service."
            : $"Restoring binaries, database, and service from {backupPath}");

        if (backupPath is null)
        {
            var started = !bookshelfWasRunning || await ServiceLifecycle.StartAsync(
                    ServiceLifecycle.BookshelfServiceName,
                    log,
                    VerifyRunningTimeout,
                    CancellationToken.None).ConfigureAwait(false);
            return Failure(
                started
                    ? reason + (bookshelfWasRunning
                        ? " The original BookshelfNG service was restarted."
                        : " BookshelfNG was already stopped and remains stopped.")
                    : reason + " BookshelfNG did not restart; start Readarr manually and check the Windows Event Viewer.",
                null);
        }

        try
        {
            var rollbackPassword = serviceAccountPassword;
            if (!string.IsNullOrWhiteSpace(bookshelfServiceAccount) &&
                ServiceLifecycle.IsInstalled(ServiceLifecycle.BookshelfServiceName) &&
                ServiceAccountPolicy.AreEquivalent(
                    ServiceLifecycle.GetServiceAccount(ServiceLifecycle.BookshelfServiceName),
                    bookshelfServiceAccount))
            {
                rollbackPassword = null;
            }

            var exit = await RollbackRunner.RunAsync(
                backupPath,
                dataDirectory,
                log,
                CancellationToken.None,
                bookshelfServiceStartType,
                bookshelfServiceDelayedAutoStart,
                bookshelfWasRunning,
                ResolveMigrationConsole(dataDirectory, stagingDirectory),
                restorePostgresDatabase,
                bookshelfServiceAccount,
                rollbackPassword).ConfigureAwait(false);
            if (exit == 0)
            {
                RecordRollbackTimestamp(ui);
                if (previousBinDirectory is not null)
                {
                    PayloadExtractor.TryDeleteDirectory(previousBinDirectory);
                }

                return Failure(
                    reason + $"\n\nBookshelfNG was restored from the verified backup at:\n{backupPath}",
                    backupPath);
            }

            return Failure(
                reason + $"\n\nAutomatic rollback exited with code {exit}. The verified backup is preserved at:\n{backupPath}\nUse the installer’s Restore BookshelfNG action or the manual recovery steps.",
                backupPath);
        }
        catch (Exception rollbackException)
        {
            ui.LogError($"[rollback] {rollbackException.GetType().Name}: {rollbackException.Message}");
            return Failure(
                reason + $"\n\nAutomatic rollback failed. The verified backup is preserved at:\n{backupPath}\nRestore BookshelfNG manually before starting either service.",
                backupPath);
        }
    }

    private static async Task<bool> WaitForHealthAsync(
        string dataDirectory,
        string consolePath,
        IReadOnlyDictionary<string, string?> migrationEnvironment,
        TimeSpan timeout,
        IProgress<LogLine> log,
        CancellationToken ct)
    {
        var configPath = Path.Combine(dataDirectory, "config.xml");
        var config = XDocument.Load(configPath);
        string? GetValue(string name) => config
            .Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        var environment = BookshelfServiceEnvironment.GetEffectiveConfigurationEnvironment(ServiceLifecycle.ChaptarrServiceName);
        string? GetEnvironmentValue(string name) => environment.TryGetValue("CHAPTARR__SERVER__" + name, out var value)
            ? value
            : null;

        var portText = GetEnvironmentValue("PORT") ?? GetValue("Port") ??
            BookshelfServiceEnvironment.GetEffectiveDefaultPort(ServiceLifecycle.ChaptarrServiceName);
        var port = int.Parse(BookshelfServiceEnvironment.DefaultBookshelfPort, System.Globalization.CultureInfo.InvariantCulture);
        if (portText is not null)
        {
            if (!int.TryParse(portText, out var configuredPort) || configuredPort is < 1 or > 65535)
            {
                log.Report(new LogLine(LogStream.Stderr, $"[health] The configured ChaptarrNG HTTP port '{portText}' is invalid."));
                return false;
            }

            port = configuredPort;
        }

        var bindAddress = GetEnvironmentValue("BINDADDRESS") ?? GetValue("BindAddress");
        var address = ResolveLocalHealthAddress(bindAddress);
        var urlBase = (GetEnvironmentValue("URLBASE") ?? GetValue("UrlBase"))?.Trim('/');
        var pingPath = string.IsNullOrWhiteSpace(urlBase) ? "/ping" : $"/{urlBase}/ping";
        var uri = new UriBuilder("http", address.ToString(), port, pingPath).Uri;
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        log.Report(new LogLine(LogStream.Stdout, $"[health] Checking local HTTP endpoint {uri}..."));
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(uri, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    log.Report(new LogLine(LogStream.Stdout, "[health] ChaptarrNG /ping returned success; checking the migrated database and compatibility marker."));
                    var databaseExit = await ProcessStreamer.RunAsync(
                        consolePath,
                        new[] { "-check-bookshelf-migration", $"-data={dataDirectory}" },
                        Path.GetDirectoryName(consolePath),
                        log,
                        ct,
                        migrationEnvironment).ConfigureAwait(false);
                    if (databaseExit == 0)
                    {
                        log.Report(new LogLine(LogStream.Stdout, "[health] The ChaptarrNG database is readable and the BookshelfNG compatibility profile is present."));
                        return true;
                    }

                    log.Report(new LogLine(LogStream.Stderr, $"[health] The migrated-database check exited with code {databaseExit}; waiting for the service database to become ready."));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // The service can report RUNNING before its HTTP listener is ready.
            }

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        log.Report(new LogLine(LogStream.Stderr, $"[health] {uri} did not return success within {timeout.TotalSeconds:F0}s."));
        return false;
    }

    private static IPAddress ResolveLocalHealthAddress(string? bindAddress)
    {
        if (bindAddress is not null && bindAddress.StartsWith("[", StringComparison.Ordinal) && bindAddress.EndsWith("]", StringComparison.Ordinal))
        {
            bindAddress = bindAddress[1..^1];
        }

        if (string.IsNullOrWhiteSpace(bindAddress) || bindAddress is "*" or "+" or "0.0.0.0")
        {
            return IPAddress.Loopback;
        }

        if (bindAddress == "::")
        {
            return IPAddress.IPv6Loopback;
        }

        if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        var addresses = IPAddress.TryParse(bindAddress, out var parsed)
            ? new[] { parsed }
            : Dns.GetHostAddresses(bindAddress);
        var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .ToHashSet();
        var selected = addresses
            .Where(address => IPAddress.IsLoopback(address) || localAddresses.Contains(address))
            .OrderByDescending(IPAddress.IsLoopback)
            .FirstOrDefault();
        return selected ?? throw new InvalidOperationException(
            $"The configured BookshelfNG bind address '{bindAddress}' does not resolve to a local interface; the installer will not probe a remote host.");
    }

    private static InstallOutcome Failure(string message, string? backupPath) =>
        new(false, backupPath, message);

    private static void RecordRollbackTimestamp(IUiReporter ui)
    {
        try
        {
            RegistryService.WriteRollbackTimestamp();
        }
        catch (Exception ex)
        {
            ui.LogError($"[rollback] Could not record the rollback timestamp: {ex.Message}");
        }
    }

    private static void EnsureSourceDatabaseUnchanged(
        SqliteDatabaseVersionGuard sourceVersionGuard,
        string phase)
    {
        if (sourceVersionGuard.HasChanged)
        {
            throw new InvalidDataException($"BookshelfNG's SQLite database received a committed change {phase}. The installer stopped before apply and will restore the verified backup.");
        }
    }

    private static void LogSourceDatabaseRepresentationChange(
        SqliteDatabaseFingerprint.Snapshot initialFingerprint,
        SqliteDatabaseFingerprint.Snapshot currentFingerprint,
        string phase,
        IUiReporter ui)
    {
        var changedFiles = SqliteDatabaseFingerprint.GetChangedFiles(initialFingerprint, currentFingerprint);
        if (!string.Equals(initialFingerprint.Hash, currentFingerprint.Hash, StringComparison.Ordinal))
        {
            ui.Log($"[pipeline] SQLite file representation changed {phase} ({string.Join(", ", changedFiles)}), but PRAGMA data_version did not change; no committed source database change was detected.");
        }
    }

    private static string? ReadInstalledChaptarrVersion(string dataDirectory)
    {
        var exe = Path.Combine(dataDirectory, "bin", "Chaptarr.exe");
        if (!File.Exists(exe))
        {
            exe = Path.Combine(dataDirectory, "bin", "Chaptarr.Console.exe");
        }

        if (!File.Exists(exe))
        {
            return null;
        }

        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveMigrationConsole(string dataDirectory, string? stagingDirectory)
    {
        var installed = Path.Combine(dataDirectory, "bin", "Chaptarr.Console.exe");
        if (File.Exists(installed))
        {
            return installed;
        }

        var staged = stagingDirectory is null ? null : Path.Combine(stagingDirectory, "Chaptarr.Console.exe");
        return staged is not null && File.Exists(staged) ? staged : null;
    }
}

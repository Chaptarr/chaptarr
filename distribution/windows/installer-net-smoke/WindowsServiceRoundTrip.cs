using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Net.Sockets;
using System.Xml.Linq;
using ChaptarrNG.Installer.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

internal static class WindowsServiceRoundTrip
{
    private enum FixtureTable
    {
        Authors,
        Books,
        Editions,
        BookFiles
    }

    private const string BookshelfArchiveUrl =
        "https://github.com/snapetech-org/bookshelfng/releases/download/main-v0.4.21.71/BookshelfNG-main-v0.4.21.71-win-x64.zip";
    private const string BookshelfArchiveSha256 =
        "54200ccd62c3f20d71c52144af0bbb38117150ab04c657263ffb9c7cf1391762";
    private const string BookshelfRegistryKey = @"SOFTWARE\ChaptarrNG\BookshelfMigration";
    private const string TestServiceAccount = @"NT AUTHORITY\NetworkService";
    private const int MaxArchiveDownloadAttempts = 3;

    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("FAIL: Windows service round-trip smoke can only run on Windows.");
            return 1;
        }

        var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        var cleanupDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            string.IsNullOrWhiteSpace(runnerTemp) ? Path.GetTempPath() : runnerTemp));
        var configuredCleanupManifest = Environment.GetEnvironmentVariable("CHAPTARR_SERVICE_ROUNDTRIP_CLEANUP_MANIFEST");
        var cleanupManifestPath = string.IsNullOrWhiteSpace(configuredCleanupManifest)
            ? Path.Combine(cleanupDirectory, "chaptarr-bookshelf-service-roundtrip-cleanup.json")
            : Path.GetFullPath(configuredCleanupManifest);
        if (!string.Equals(Path.GetDirectoryName(cleanupManifestPath), cleanupDirectory, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("FAIL: the deferred cleanup manifest must be a direct child of RUNNER_TEMP.");
            return 1;
        }

        if (File.Exists(cleanupManifestPath))
        {
            Console.Error.WriteLine($"FAIL: refusing to replace an existing cleanup manifest at {cleanupManifestPath}.");
            return 1;
        }

        var deferCleanupToWorkflow = !string.IsNullOrWhiteSpace(configuredCleanupManifest);
        var failed = false;
        CleanupManifest? manifest = null;
        try
        {
            Console.WriteLine("[smoke] Starting the isolated BookshelfNG migration worker...");
            var workerExitCode = await RunMigrationWorkerProcessAsync(cleanupManifestPath).ConfigureAwait(false);
            if (workerExitCode != 0)
            {
                throw new InvalidOperationException($"The migration worker exited with code {workerExitCode}.");
            }

            manifest = ReadCleanupManifest(cleanupManifestPath);
            await WaitForDeferredBookshelfServiceRemovalAsync().ConfigureAwait(false);
            if (!ServiceLifecycle.IsInstalled(ServiceLifecycle.ChaptarrServiceName))
            {
                throw new InvalidOperationException("Chaptarr was not registered after deferred BookshelfNG service cleanup.");
            }

            await WaitForServiceAsync(
                ServiceLifecycle.ChaptarrServiceName,
                ServiceControllerStatus.Running,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await WaitForHealthAsync(manifest.DataDirectory, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            await RestoreAndValidateAsync(manifest.DataDirectory).ConfigureAwait(false);
            Console.WriteLine("PASS: BookshelfNG upgraded, deferred service removal completed after installer exit, and explicit restore passed.");
        }
        catch (Exception exception)
        {
            failed = true;
            Console.Error.WriteLine($"FAIL: {exception.GetType().Name}: {exception.Message}");
        }

        if (deferCleanupToWorkflow)
        {
            if (File.Exists(cleanupManifestPath))
            {
                Console.WriteLine($"[smoke] Fixture cleanup is deferred to the workflow: {cleanupManifestPath}");
            }
        }
        else if (File.Exists(cleanupManifestPath))
        {
            try
            {
                manifest ??= ReadCleanupManifest(cleanupManifestPath);
                var cleanupOk = await CleanupAsync(
                    manifest.ScratchRoot,
                    manifest.InstallerTempDirectory,
                    manifest.RemoveInstallerTempDirectory).ConfigureAwait(false);
                if (!cleanupOk)
                {
                    failed = true;
                }
                else
                {
                    File.Delete(cleanupManifestPath);
                }
            }
            catch (Exception exception)
            {
                failed = true;
                Console.Error.WriteLine($"Could not safely clean up the smoke-test fixture: {exception.Message}");
            }
        }

        return failed ? 1 : 0;
    }

    public static async Task<int> RunWorkerAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("FAIL: Windows service round-trip smoke can only run on Windows.");
            return 1;
        }

        var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.Error.WriteLine("FAIL: Windows service round-trip smoke requires an elevated process.");
            return 1;
        }

        if (ServiceLifecycle.IsInstalled(ServiceLifecycle.BookshelfServiceName) ||
            ServiceLifecycle.IsInstalled(ServiceLifecycle.ChaptarrServiceName))
        {
            Console.Error.WriteLine("FAIL: refusing to run while a Readarr or Chaptarr service already exists on this runner.");
            return 1;
        }

        if (RegistryService.Read() is not null)
        {
            Console.Error.WriteLine("FAIL: refusing to replace an existing Bookshelf migration registry record.");
            return 1;
        }

        var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        var configuredCleanupManifest = Environment.GetEnvironmentVariable("CHAPTARR_SERVICE_ROUNDTRIP_CLEANUP_MANIFEST");
        var cleanupManifestPath = string.IsNullOrWhiteSpace(configuredCleanupManifest)
            ? null
            : Path.GetFullPath(configuredCleanupManifest);
        if (cleanupManifestPath is not null)
        {
            var runnerTempDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(string.IsNullOrWhiteSpace(runnerTemp) ? Path.GetTempPath() : runnerTemp));
            if (!string.Equals(Path.GetDirectoryName(cleanupManifestPath), runnerTempDirectory, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("FAIL: the deferred cleanup manifest must be a direct child of RUNNER_TEMP.");
                return 1;
            }

            if (File.Exists(cleanupManifestPath))
            {
                Console.Error.WriteLine($"FAIL: refusing to replace an existing cleanup manifest at {cleanupManifestPath}.");
                return 1;
            }
        }

        var scratchRoot = Path.Combine(
            string.IsNullOrWhiteSpace(runnerTemp) ? Path.GetTempPath() : runnerTemp,
            "chaptarr-bookshelf-service-roundtrip-" + Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(scratchRoot, "Readarr Data With Spaces");
        var archivePath = Path.Combine(scratchRoot, "bookshelfng-win-x64.zip");
        var extractionDirectory = Path.Combine(scratchRoot, "bookshelfng");
        var installerTempDirectory = Path.Combine(Path.GetTempPath(), "ChaptarrNG.Installer");
        var installerTempDirectoryExisted = Directory.Exists(installerTempDirectory);
        var failed = false;
        var preserveScratch = false;

        try
        {
            if (cleanupManifestPath is not null)
            {
                using var manifestStream = new FileStream(cleanupManifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(manifestStream, new CleanupManifest(
                    scratchRoot,
                    installerTempDirectory,
                    dataDirectory,
                    RemoveInstallerTempDirectory: !installerTempDirectoryExisted));
            }

            Directory.CreateDirectory(dataDirectory);
            Directory.CreateDirectory(extractionDirectory);

            await DownloadBookshelfArchiveAsync(archivePath).ConfigureAwait(false);
            ExtractBookshelfArchive(archivePath, extractionDirectory);
            var bookshelfConsole = Directory
                .EnumerateFiles(extractionDirectory, "Readarr.Console.exe", SearchOption.AllDirectories)
                .SingleOrDefault()
                ?? throw new FileNotFoundException("The pinned BookshelfNG archive did not contain Readarr.Console.exe.");

            var sourceBin = Path.GetDirectoryName(bookshelfConsole)
                ?? throw new InvalidDataException("Could not determine the BookshelfNG binary directory.");
            var activeBin = Path.Combine(dataDirectory, "bin");
            CopyDirectory(sourceBin, activeBin);
            SeedBookshelfDatabase(dataDirectory);
            await GrantServicePermissionsAsync(activeBin, dataDirectory).ConfigureAwait(false);

            Console.WriteLine("[smoke] Registering and starting the pinned BookshelfNG 0.4.21.71 service...");
            await RunProcessAsync(
                Path.Combine(activeBin, "Readarr.Console.exe"),
                new[] { "/i", $"-data={dataDirectory}", "/nobrowser" }).ConfigureAwait(false);
            await EnsureTestServiceAccountAsync(ServiceLifecycle.BookshelfServiceName).ConfigureAwait(false);
            await StartServiceIfStoppedAsync(ServiceLifecycle.BookshelfServiceName).ConfigureAwait(false);
            await WaitForServiceAsync(ServiceLifecycle.BookshelfServiceName, ServiceControllerStatus.Running, TimeSpan.FromSeconds(90))
                .ConfigureAwait(false);
            await WaitForHealthAsync(dataDirectory, TimeSpan.FromSeconds(90)).ConfigureAwait(false);

            var sourceDatabase = Path.Combine(dataDirectory, "readarr.db");
            AssertLineage(sourceDatabase, DatabaseLineageKind.BookshelfNG, "BookshelfNG source startup");
            AssertRowCount(sourceDatabase, FixtureTable.Books, expectedAtLeast: 1, "BookshelfNG source data");

            Console.WriteLine("[smoke] Declining the explicit migration preview to exercise the No choice...");
            var declinedUpgrade = await InstallPipeline.RunUpgradeAsync(
                new SmokeUiReporter(approveMigration: false)).ConfigureAwait(false);
            if (declinedUpgrade.Success ||
                string.IsNullOrWhiteSpace(declinedUpgrade.BackupPath) ||
                string.IsNullOrWhiteSpace(declinedUpgrade.ErrorMessage) ||
                !declinedUpgrade.ErrorMessage.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(RegistryService.Read()?.LastRollbackTime))
            {
                throw new InvalidOperationException(
                    "Declining the BookshelfNG migration preview did not restore the original installation.");
            }

            if (ServiceLifecycle.IsInstalled(ServiceLifecycle.ChaptarrServiceName))
            {
                throw new InvalidOperationException("Declining the migration preview unexpectedly installed a Chaptarr service.");
            }

            await WaitForServiceAsync(
                ServiceLifecycle.BookshelfServiceName,
                ServiceControllerStatus.Running,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await WaitForHealthAsync(dataDirectory, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            AssertServicePath(ServiceLifecycle.BookshelfServiceName, dataDirectory);
            AssertServiceAccount(ServiceLifecycle.BookshelfServiceName, "declined migration preview");
            AssertServiceStartType(ServiceLifecycle.BookshelfServiceName, expected: 2, "declined migration preview");
            AssertLineage(sourceDatabase, DatabaseLineageKind.BookshelfNG, "declined migration preview");
            AssertRowCount(sourceDatabase, FixtureTable.Books, expectedAtLeast: 1, "BookshelfNG data after No");
            AssertRowCount(sourceDatabase, FixtureTable.BookFiles, expectedAtLeast: 2, "BookshelfNG files after No");
            AssertDatabaseIntegrity(sourceDatabase, "BookshelfNG database after No");
            AssertBookshelfWasRunningAtUpgrade(expected: true, "declined migration preview");
            Console.WriteLine("PASS: No restored and restarted BookshelfNG with its original service and schema intact.");

            Console.WriteLine("[smoke] Injecting an HTTP port conflict to exercise automatic installer rollback...");
            using (var blocker = new TcpListener(IPAddress.Loopback, GetConfiguredHttpPort(dataDirectory)))
            using (var releaseCancellation = new CancellationTokenSource())
            {
                var blockerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var startBlocker = StartPortBlockerAfterBookshelfStopsAsync(
                    blocker,
                    blockerStarted,
                    releaseCancellation.Token);
                var releaseBlocker = ReleasePortBlockerAfterChaptarrStopsAsync(
                    blocker,
                    blockerStarted.Task,
                    releaseCancellation.Token);
                InstallOutcome failedUpgrade;
                try
                {
                    failedUpgrade = await InstallPipeline.RunUpgradeAsync(new SmokeUiReporter()).ConfigureAwait(false);
                }
                finally
                {
                    releaseCancellation.Cancel();
                    blocker.Stop();
                    try { await startBlocker.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    try { await releaseBlocker.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }

                if (failedUpgrade.Success ||
                    string.IsNullOrWhiteSpace(failedUpgrade.BackupPath) ||
                    string.IsNullOrWhiteSpace(RegistryService.Read()?.LastRollbackTime))
                {
                    throw new InvalidOperationException(
                        "The injected Chaptarr health-check failure did not complete automatic BookshelfNG rollback.");
                }

                var automaticRollbackBackup = Path.Combine(failedUpgrade.BackupPath, "readarr.db");
                AssertLineage(automaticRollbackBackup, DatabaseLineageKind.BookshelfNG, "automatic rollback backup");
                await WaitForServiceAsync(ServiceLifecycle.BookshelfServiceName, ServiceControllerStatus.Running, TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);
                await WaitForHealthAsync(dataDirectory, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                AssertServicePath(ServiceLifecycle.BookshelfServiceName, dataDirectory);
                AssertServiceAccount(ServiceLifecycle.BookshelfServiceName, "automatic rollback");
                AssertServiceStartType(ServiceLifecycle.BookshelfServiceName, expected: 2, "automatic rollback");
                AssertBookshelfWasRunningAtUpgrade(expected: true, "automatic rollback");
                AssertLineage(sourceDatabase, DatabaseLineageKind.BookshelfNG, "automatic rollback database");
                AssertRowCount(sourceDatabase, FixtureTable.Books, expectedAtLeast: 1, "automatically restored BookshelfNG books");
                AssertDatabaseIntegrity(sourceDatabase, "automatically restored BookshelfNG database");
            }

            Console.WriteLine("[smoke] Running the real installer upgrade pipeline with synthetic populated migration data...");
            var ui = new SmokeUiReporter();
            var upgrade = await InstallPipeline.RunUpgradeAsync(ui).ConfigureAwait(false);
            if (!upgrade.Success || string.IsNullOrWhiteSpace(upgrade.BackupPath))
            {
                throw new InvalidOperationException("The installer upgrade pipeline failed: " + upgrade.ErrorMessage);
            }
            AssertBookshelfWasRunningAtUpgrade(expected: true, "successful migration handoff");

            var migrationBackupDatabase = Path.Combine(upgrade.BackupPath, "readarr.db");
            AssertLineage(migrationBackupDatabase, DatabaseLineageKind.BookshelfNG, "retained migration backup");
            AssertRowCount(migrationBackupDatabase, FixtureTable.Books, expectedAtLeast: 1, "retained BookshelfNG backup");
            AssertLineage(sourceDatabase, DatabaseLineageKind.Chaptarr, "migrated Chaptarr database");
            AssertRowCount(sourceDatabase, FixtureTable.Authors, expectedAtLeast: 1, "migrated authors");
            AssertRowCount(sourceDatabase, FixtureTable.Books, expectedAtLeast: 1, "migrated books");
            AssertRowCount(sourceDatabase, FixtureTable.Editions, expectedAtLeast: 2, "migrated editions");
            AssertRowCount(sourceDatabase, FixtureTable.BookFiles, expectedAtLeast: 2, "migrated audiobook and ebook files");
            AssertDatabaseIntegrity(sourceDatabase, "migrated Chaptarr database");
            AssertServicePath(ServiceLifecycle.ChaptarrServiceName, dataDirectory);
            await WaitForServiceAsync(ServiceLifecycle.ChaptarrServiceName, ServiceControllerStatus.Running, TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);
            AssertServiceAccount(ServiceLifecycle.ChaptarrServiceName, "successful migration handoff");
            if (!ServiceLifecycle.IsInstalled(ServiceLifecycle.BookshelfServiceName) ||
                ServiceLifecycle.TryGetStatus(ServiceLifecycle.BookshelfServiceName) != ServiceControllerStatus.Stopped)
            {
                throw new InvalidOperationException("Readarr must remain registered and stopped until the installer exits.");
            }
            AssertServiceStartType(ServiceLifecycle.BookshelfServiceName, expected: 4, "successful migration handoff");

            Console.WriteLine("[smoke] Scheduling deferred handoff cleanup for after this installer worker exits...");
            await ScheduleDeferredHandoffCleanupAsync(dataDirectory, scratchRoot).ConfigureAwait(false);
            Console.WriteLine("PASS: BookshelfNG 0.4.21.71 upgraded through the installer and Chaptarr passed health and migration checks.");
        }
        catch (Exception exception)
        {
            failed = true;
            Console.Error.WriteLine($"FAIL: {exception.GetType().Name}: {exception.Message}");
        }

        if (cleanupManifestPath is not null)
        {
            Console.WriteLine($"[smoke] Fixture cleanup is deferred until this process exits: {cleanupManifestPath}");
        }
        else
        {
            var cleanupOk = await CleanupAsync(
                scratchRoot,
                installerTempDirectory,
                removeInstallerTempDirectory: !installerTempDirectoryExisted).ConfigureAwait(false);
            if (!cleanupOk)
            {
                preserveScratch = true;
                failed = true;
            }

            if (preserveScratch)
            {
                Console.Error.WriteLine($"Test services could not be removed safely. Keeping their files at {scratchRoot}.");
            }
        }

        return failed ? 1 : 0;
    }

    private static async Task DownloadBookshelfArchiveAsync(string destination)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ChaptarrNG-Windows-Smoke/1.0");

        for (var attempt = 1; attempt <= MaxArchiveDownloadAttempts; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(
                    BookshelfArchiveUrl,
                    HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode &&
                    attempt < MaxArchiveDownloadAttempts &&
                    IsTransientArchiveStatus(response.StatusCode))
                {
                    Console.WriteLine($"[smoke] BookshelfNG archive download returned HTTP {(int)response.StatusCode}; retrying.");
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2)).ConfigureAwait(false);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                await using (var output = File.Create(destination))
                {
                    await input.CopyToAsync(output).ConfigureAwait(false);
                }

                await using var archive = File.OpenRead(destination);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(archive).ConfigureAwait(false)).ToLowerInvariant();
                if (!string.Equals(actual, BookshelfArchiveSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The downloaded BookshelfNG archive checksum did not match the pinned release asset ({actual}).");
                }

                return;
            }
            catch (HttpRequestException exception) when (
                attempt < MaxArchiveDownloadAttempts &&
                (!exception.StatusCode.HasValue || IsTransientArchiveStatus(exception.StatusCode.Value)))
            {
                Console.WriteLine($"[smoke] BookshelfNG archive download failed ({exception.StatusCode?.ToString() ?? "network error"}); retrying.");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2)).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (attempt < MaxArchiveDownloadAttempts)
            {
                Console.WriteLine("[smoke] BookshelfNG archive download timed out; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2)).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("The pinned BookshelfNG archive download exhausted its retry attempts.");
    }

    private static bool IsTransientArchiveStatus(HttpStatusCode statusCode)
    {
        var status = (int)statusCode;
        return statusCode is HttpStatusCode.Forbidden or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || status >= 500;
    }

    private static void ExtractBookshelfArchive(string archivePath, string destination)
    {
        ZipFile.ExtractToDirectory(archivePath, destination);
    }

    private static void SeedBookshelfDatabase(string dataDirectory)
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var schema = File.ReadAllText(Path.Combine(fixtureDirectory, "BookshelfNG.Schema.158.sql"));
        var populated = File.ReadAllText(Path.Combine(fixtureDirectory, "BookshelfNG.PopulatedMigration158.sql"));
        var databasePath = Path.Combine(dataDirectory, "readarr.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ConnectionString;

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = schema;
            schemaCommand.ExecuteNonQuery();
        }

        using (var populateCommand = connection.CreateCommand())
        {
            populateCommand.CommandText = populated + @"
                UPDATE Indexers SET EnableRss = 0, EnableAutomaticSearch = 0, EnableInteractiveSearch = 0;
                UPDATE ImportLists SET EnableAutomaticAdd = 0, ShouldSearch = 0, ShouldMonitorExisting = 0;
                UPDATE Authors SET Monitored = 0, LastInfoSync = CURRENT_TIMESTAMP;
                UPDATE Books SET Monitored = 0, LastInfoSync = CURRENT_TIMESTAMP;
            ";
            populateCommand.ExecuteNonQuery();
        }
    }

    private static async Task GrantServicePermissionsAsync(string binaryDirectory, string dataDirectory)
    {
        await RunProcessAsync("icacls.exe", new[]
        {
            binaryDirectory, "/grant", TestServiceAccount + ":(OI)(CI)RX", "/T", "/C"
        }).ConfigureAwait(false);
        await RunProcessAsync("icacls.exe", new[]
        {
            dataDirectory, "/grant", TestServiceAccount + ":(OI)(CI)M", "/T", "/C"
        }).ConfigureAwait(false);
    }

    private static async Task EnsureTestServiceAccountAsync(string serviceName)
    {
        var account = ServiceLifecycle.GetServiceAccount(serviceName);
        if (ServiceAccountPolicy.AreEquivalent(account, TestServiceAccount))
        {
            return;
        }

        if (ServiceLifecycle.TryGetStatus(serviceName) == ServiceControllerStatus.Running)
        {
            await StopServiceAsync(serviceName).ConfigureAwait(false);
        }

        ServiceLifecycle.SetServiceAccount(serviceName, TestServiceAccount, null);
    }

    private static void AssertServiceAccount(string serviceName, string phase)
    {
        if (!ServiceAccountPolicy.AreEquivalent(ServiceLifecycle.GetServiceAccount(serviceName), TestServiceAccount))
        {
            throw new InvalidOperationException($"The {phase} did not preserve the original NetworkService account.");
        }
    }

    private static string? ReadServiceAccount(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: false);
        return key?.GetValue("ObjectName") as string;
    }

    private static async Task WaitForServiceAsync(
        string serviceName,
        ServiceControllerStatus expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = ServiceLifecycle.TryGetStatus(serviceName);
            if (status == expected)
            {
                return;
            }

            await Task.Delay(1000).ConfigureAwait(false);
        }

        throw new System.TimeoutException($"Windows service {serviceName} did not reach {expected} within {timeout.TotalSeconds:0} seconds. Current status: {ServiceLifecycle.TryGetStatus(serviceName)?.ToString() ?? "not installed"}.");
    }

    private static async Task StopServiceAsync(string serviceName)
    {
        var stopped = await ServiceLifecycle.StopAsync(
            serviceName,
            new Progress<LogLine>(line => Console.WriteLine(line.Text)),
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!stopped)
        {
            throw new InvalidOperationException($"The {serviceName} service and its process did not stop cleanly.");
        }
    }

    private static Task StartServiceIfStoppedAsync(string serviceName)
    {
        using var service = new ServiceController(serviceName);
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped)
        {
            service.Start();
        }

        return Task.CompletedTask;
    }

    private static async Task WaitForHealthAsync(string dataDirectory, TimeSpan timeout)
    {
        var configPath = Path.Combine(dataDirectory, "config.xml");
        var deadline = DateTime.UtcNow + timeout;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var config = XDocument.Load(configPath);
                var portText = config.Descendants().FirstOrDefault(e => e.Name.LocalName == "Port")?.Value;
                var port = int.TryParse(portText, out var parsed) ? parsed : 8787;
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/ping").ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                // Service startup may report RUNNING before its HTTP listener is ready.
            }

            await Task.Delay(1000).ConfigureAwait(false);
        }

        throw new System.TimeoutException($"The service in {dataDirectory} did not pass its local /ping check.");
    }

    private static int GetConfiguredHttpPort(string dataDirectory)
    {
        var config = XDocument.Load(Path.Combine(dataDirectory, "config.xml"));
        var portText = config.Descendants().FirstOrDefault(e => e.Name.LocalName == "Port")?.Value;
        return int.TryParse(portText, out var port) && port is > 0 and <= 65535 ? port : 8787;
    }

    private static async Task StartPortBlockerAfterBookshelfStopsAsync(
        TcpListener blocker,
        TaskCompletionSource<bool> blockerStarted,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (ServiceLifecycle.TryGetStatus(ServiceLifecycle.BookshelfServiceName) == ServiceControllerStatus.Stopped)
            {
                try
                {
                    blocker.Start();
                    blockerStarted.TrySetResult(true);
                    Console.WriteLine("[smoke] BookshelfNG has stopped; holding its HTTP port to force Chaptarr health-check rollback.");
                    return;
                }
                catch (SocketException)
                {
                    // The service can report STOPPED just before Windows releases its listener.
                    blocker.Stop();
                }
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReleasePortBlockerAfterChaptarrStopsAsync(
        TcpListener blocker,
        Task blockerStarted,
        CancellationToken cancellationToken)
    {
        await blockerStarted.WaitAsync(cancellationToken).ConfigureAwait(false);
        var sawChaptarrRunning = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var chaptarrStatus = ServiceLifecycle.TryGetStatus(ServiceLifecycle.ChaptarrServiceName);
            if (chaptarrStatus == ServiceControllerStatus.Running)
            {
                sawChaptarrRunning = true;
            }

            if (sawChaptarrRunning &&
                (chaptarrStatus is null || chaptarrStatus == ServiceControllerStatus.Stopped))
            {
                Console.WriteLine("[smoke] Chaptarr has stopped; releasing the injected port conflict before BookshelfNG restore.");
                blocker.Stop();
                return;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AssertLineage(string databasePath, DatabaseLineageKind expected, string label)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ConnectionString;
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            using var reader = tableCommand.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        if (!tables.Contains("Authors"))
        {
            throw new InvalidDataException($"Expected {label} to contain Authors; found no supported database lineage.");
        }

        var authorColumns = GetColumns(connection, FixtureTable.Authors);
        var actual = DatabaseLineageKind.Unsupported;
        if (authorColumns.Contains("AudiobookRootFolderPath") && authorColumns.Contains("EbookRootFolderPath"))
        {
            actual = DatabaseLineageKind.Chaptarr;
        }
        else if (tables.Contains("AuthorMetadata") && tables.Contains("Books") &&
                 authorColumns.Contains("AuthorMetadataId") &&
                 GetColumns(connection, FixtureTable.Books).Contains("ForeignBookId"))
        {
            actual = DatabaseLineageKind.BookshelfNG;
        }

        if (actual != expected)
        {
            throw new InvalidDataException($"Expected {label} to use {expected} lineage, found {actual}.");
        }
    }

    private static HashSet<string> GetColumns(SqliteConnection connection, FixtureTable table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = table switch
        {
            FixtureTable.Authors => "PRAGMA table_info(\"Authors\");",
            FixtureTable.Books => "PRAGMA table_info(\"Books\");",
            _ => throw new InvalidDataException($"Unexpected SQLite fixture table '{table}'.")
        };
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static void AssertRowCount(string databasePath, FixtureTable table, long expectedAtLeast, string label)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ConnectionString;
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = table switch
        {
            FixtureTable.Authors => "SELECT COUNT(*) FROM \"Authors\";",
            FixtureTable.Books => "SELECT COUNT(*) FROM \"Books\";",
            FixtureTable.Editions => "SELECT COUNT(*) FROM \"Editions\";",
            FixtureTable.BookFiles => "SELECT COUNT(*) FROM \"BookFiles\";",
            _ => throw new InvalidDataException($"Unexpected SQLite fixture table '{table}'.")
        };
        var count = Convert.ToInt64(command.ExecuteScalar());
        if (count < expectedAtLeast)
        {
            throw new InvalidDataException($"Expected {label} to contain at least {expectedAtLeast} rows, found {count}.");
        }
    }

    private static void AssertDatabaseIntegrity(string databasePath, string label)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ConnectionString;
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = integrity.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SQLite integrity_check failed for {label}: {result ?? "no result"}.");
            }
        }

        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            using var reader = foreignKeys.ExecuteReader();
            if (reader.Read())
            {
                throw new InvalidDataException($"SQLite foreign_key_check found a violation in {label}.");
            }
        }
    }

    private static void AssertServicePath(string serviceName, string dataDirectory)
    {
        var imagePath = ServiceLifecycle.GetImagePath(serviceName);
        if (string.IsNullOrWhiteSpace(imagePath) ||
            !imagePath.Contains(dataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The {serviceName} service command did not retain {dataDirectory}.");
        }
    }

    private static void AssertServiceExecutablePath(string serviceName, string executablePath)
    {
        var imagePath = ServiceLifecycle.GetImagePath(serviceName);
        var expectedExecutable = "\"" + Path.GetFullPath(executablePath) + "\"";
        if (string.IsNullOrWhiteSpace(imagePath) ||
            !imagePath.StartsWith(expectedExecutable, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The {serviceName} service command must start with its quoted executable path {expectedExecutable}.");
        }
    }

    private static void AssertServiceStartType(string serviceName, int expected, string label)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: false);
        var actual = key?.GetValue("Start") is int startType ? startType : -1;
        if (actual != expected)
        {
            throw new InvalidOperationException($"The {serviceName} service had startup type {actual} after {label}; expected {expected}.");
        }
    }

    private static void AssertBookshelfWasRunningAtUpgrade(bool expected, string label)
    {
        var actual = RegistryService.Read()?.BookshelfServiceWasRunning;
        if (actual != expected)
        {
            throw new InvalidOperationException($"The installer recorded BookshelfNG running state '{actual}' after {label}; expected '{expected}'.");
        }
    }

    private static async Task ScheduleDeferredHandoffCleanupAsync(string dataDirectory, string scratchRoot)
    {
        var scriptPath = Path.Combine(scratchRoot, "finalize-service-handoff-smoke.ps1");
        await using (var source = typeof(ServiceCleanupRunner).Assembly.GetManifestResourceStream("finalize-service-handoff.ps1")
            ?? throw new InvalidOperationException("The installer is missing its deferred service handoff script."))
        await using (var destination = File.Create(scriptPath))
        {
            await source.CopyToAsync(destination).ConfigureAwait(false);
        }

        var parent = Process.GetCurrentProcess();
        var parentStartTicks = parent.StartTime.ToUniversalTime().Ticks;
        parent.Dispose();

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = scratchRoot
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
            "-ParentProcessId", Environment.ProcessId.ToString(),
            "-ParentProcessStartTicks", parentStartTicks.ToString(),
            "-DataDirectory", Path.GetFullPath(dataDirectory)
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var cleanup = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start deferred BookshelfNG service cleanup.");
        Console.WriteLine($"[smoke] Deferred cleanup process {cleanup.Id} is waiting for worker {Environment.ProcessId} to exit.");
    }

    private static async Task WaitForDeferredBookshelfServiceRemovalAsync()
    {
        var serviceKey = $@"SYSTEM\CurrentControlSet\Services\{ServiceLifecycle.BookshelfServiceName}";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            using var key = Registry.LocalMachine.OpenSubKey(serviceKey, writable: false);
            if (key is null)
            {
                return;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        throw new InvalidOperationException("The deferred cleanup script did not remove the disabled Readarr service after the migration worker exited.");
    }

    private static async Task RestoreAndValidateAsync(string dataDirectory)
    {
        var sourceDatabase = Path.Combine(dataDirectory, "readarr.db");
        Console.WriteLine("[smoke] Running the installer restore action after deferred service cleanup...");
        var restore = await InstallPipeline.RunRestoreAsync(new SmokeUiReporter()).ConfigureAwait(false);
        if (!restore.Success)
        {
            throw new InvalidOperationException("The installer restore pipeline failed: " + restore.ErrorMessage);
        }

        if (ServiceLifecycle.IsInstalled(ServiceLifecycle.ChaptarrServiceName))
        {
            throw new InvalidOperationException("The Chaptarr service remained registered after BookshelfNG restore.");
        }

        await WaitForServiceAsync(ServiceLifecycle.BookshelfServiceName, ServiceControllerStatus.Running, TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
        await WaitForHealthAsync(dataDirectory, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        AssertServicePath(ServiceLifecycle.BookshelfServiceName, dataDirectory);
        AssertServiceExecutablePath(
            ServiceLifecycle.BookshelfServiceName,
            Path.Combine(dataDirectory, "bin", "Readarr.Console.exe"));
        if (!ServiceAccountPolicy.AreEquivalent(ReadServiceAccount(ServiceLifecycle.BookshelfServiceName), TestServiceAccount))
        {
            throw new InvalidOperationException("The restored Readarr service did not preserve the original NetworkService account.");
        }
        AssertServiceStartType(ServiceLifecycle.BookshelfServiceName, expected: 2, "manual restore after handoff cleanup");
        AssertLineage(sourceDatabase, DatabaseLineageKind.BookshelfNG, "restored BookshelfNG database");
        AssertRowCount(sourceDatabase, FixtureTable.Books, expectedAtLeast: 1, "restored BookshelfNG books");
        AssertRowCount(sourceDatabase, FixtureTable.BookFiles, expectedAtLeast: 2, "restored BookshelfNG files");
        AssertDatabaseIntegrity(sourceDatabase, "restored BookshelfNG database");

        var registry = RegistryService.Read();
        if (string.IsNullOrWhiteSpace(registry?.LastRollbackTime) ||
            !string.Equals(Path.GetFullPath(registry.DataDirectory ?? ""), Path.GetFullPath(dataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The installer did not record the completed rollback against the selected data directory.");
        }
    }

    private static async Task<int> RunMigrationWorkerProcessAsync(string cleanupManifestPath)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the Windows service smoke executable path.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = typeof(WindowsServiceRoundTrip).Assembly.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                throw new InvalidOperationException("Could not locate the smoke assembly to start the migration worker.");
            }

            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("--windows-service-round-trip-worker");
        startInfo.Environment["CHAPTARR_SERVICE_ROUNDTRIP_CLEANUP_MANIFEST"] = cleanupManifestPath;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the isolated Windows service migration worker.");
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                Console.Out.WriteLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                Console.Error.WriteLine(eventArgs.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            process.WaitForExit();
            throw new System.TimeoutException("The Windows service migration worker did not finish within twenty minutes.");
        }

        return process.ExitCode;
    }

    private static CleanupManifest ReadCleanupManifest(string cleanupManifestPath)
    {
        using var stream = new FileStream(cleanupManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var manifest = JsonSerializer.Deserialize<CleanupManifest>(stream)
            ?? throw new InvalidDataException("The Windows service smoke cleanup manifest was empty.");
        var scratchRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.ScratchRoot));
        var dataDirectory = Path.GetFullPath(manifest.DataDirectory);
        var scratchPrefix = scratchRoot + Path.DirectorySeparatorChar;
        if (!dataDirectory.StartsWith(scratchPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Windows service smoke data directory is outside its scratch root.");
        }

        return manifest;
    }

    private static async Task RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var executableDirectory = Path.GetDirectoryName(fileName);
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            startInfo.WorkingDirectory = executableDirectory;
        }
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new System.TimeoutException($"{fileName} did not finish within three minutes.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Console.WriteLine(stdout.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Console.Error.WriteLine(stderr.TrimEnd());
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}.");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static async Task<bool> CleanupAsync(
        string scratchRoot,
        string installerTempDirectory,
        bool removeInstallerTempDirectory)
    {
        var allServicesStopped = true;
        foreach (var serviceName in new[] { ServiceLifecycle.ChaptarrServiceName, ServiceLifecycle.BookshelfServiceName })
        {
            if (!ServiceLifecycle.IsInstalled(serviceName))
            {
                continue;
            }

            var imagePath = ServiceLifecycle.GetImagePath(serviceName);
            if (string.IsNullOrWhiteSpace(imagePath) ||
                !imagePath.Contains(scratchRoot, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Refusing to clean up {serviceName}; its command does not point inside the smoke-test directory.");
                allServicesStopped = false;
                continue;
            }

            try
            {
                await StopServiceAsync(serviceName).ConfigureAwait(false);
                var deleted = await ServiceLifecycle.DeleteAsync(
                    serviceName,
                    new Progress<LogLine>(line => Console.WriteLine(line.Text))).ConfigureAwait(false);
                if (!deleted)
                {
                    var remainingStatus = "unknown";
                    try
                    {
                        remainingStatus = ServiceLifecycle.TryGetStatus(serviceName)?.ToString() ?? "not queryable";
                    }
                    catch (Exception exception)
                    {
                        remainingStatus = $"query failed ({exception.GetType().Name})";
                    }

                    Console.Error.WriteLine($"Service cleanup did not remove {serviceName}; final SCM status: {remainingStatus}.");
                }

                allServicesStopped &= deleted;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Could not safely remove {serviceName}: {exception.Message}");
                allServicesStopped = false;
            }
        }

        if (!allServicesStopped)
        {
            return false;
        }

        var record = RegistryService.Read();
        if (record is not null &&
            !string.IsNullOrWhiteSpace(record.DataDirectory) &&
            Path.GetFullPath(record.DataDirectory).StartsWith(Path.GetFullPath(scratchRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            baseKey.DeleteSubKeyTree(BookshelfRegistryKey, throwOnMissingSubKey: false);
        }

        try
        {
            if (removeInstallerTempDirectory &&
                Directory.Exists(installerTempDirectory) &&
                !Directory.EnumerateFileSystemEntries(installerTempDirectory).Any())
            {
                Directory.Delete(installerTempDirectory);
            }

            if (Directory.Exists(scratchRoot))
            {
                await DeleteDirectoryWithRetryAsync(scratchRoot).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not remove smoke-test directory {scratchRoot}: {exception.Message}");
            return false;
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        const int attempts = 12;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt + 1 < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt + 1 < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed class SmokeUiReporter : IUiReporter
    {
        private readonly bool _approveMigration;

        public SmokeUiReporter(bool approveMigration = true)
        {
            _approveMigration = approveMigration;
        }

        public void SetStep(string step, string detail = "") =>
            Console.WriteLine($"[installer] {step}{(string.IsNullOrWhiteSpace(detail) ? "" : " " + detail)}");

        public void SetProgressPercent(double percent) { }
        public void SetProgressIndeterminate() { }
        public ProviderIdentityMode? ChooseProviderIdentityMode() => ProviderIdentityMode.NativeHardcover;
        public System.Security.SecureString? RequestServiceAccountPassword(string serviceAccount) =>
            throw new InvalidOperationException($"The smoke test unexpectedly requested a password for '{serviceAccount}'.");
        public bool ConfirmMigrationApply(ProviderIdentityMode mode, string backupPath) => _approveMigration;
        public void Log(LogLine line) => Console.WriteLine($"[{line.Stream}] {line.Text}");
        public void Log(string line) => Console.WriteLine(line);
        public void LogError(string line) => Console.Error.WriteLine(line);
    }

    private enum DatabaseLineageKind
    {
        Unsupported,
        BookshelfNG,
        Chaptarr
    }

    private sealed record CleanupManifest(
        string ScratchRoot,
        string InstallerTempDirectory,
        string DataDirectory,
        bool RemoveInstallerTempDirectory);
}

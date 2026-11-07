using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ChaptarrNG.Installer.Pipeline;

public static class ServiceLifecycle
{
    public const string BookshelfServiceName = "Readarr";
    public const string ChaptarrServiceName = "Chaptarr";
    private const string DefaultBookshelfDataDirectory = @"C:\ProgramData\Readarr";
    private static readonly TimeSpan ServiceProcessExitTimeout = TimeSpan.FromSeconds(15);
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(
        IntPtr serviceHandle,
        int infoLevel,
        out ServiceStatusProcess status,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceControlManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig(
        IntPtr service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string serviceStartName,
        IntPtr password,
        string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public static void SetServiceAccount(string serviceName, string serviceAccount, SecureString? password)
    {
        if (ServiceAccountPolicy.RequiresPassword(serviceAccount) && (password is null || password.Length == 0))
        {
            throw new ArgumentException("A password is required for this Windows service account.", nameof(password));
        }

        if (password is not null && !ServiceAccountPolicy.RequiresPassword(serviceAccount) && password.Length > 0)
        {
            throw new ArgumentException("A password must not be supplied for this Windows service account.", nameof(password));
        }

        var manager = OpenServiceControlManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not connect to the Windows Service Control Manager.");
        }

        IntPtr service = IntPtr.Zero;
        IntPtr passwordPointer = IntPtr.Zero;
        SecureString? emptyPassword = null;
        try
        {
            service = OpenService(manager, serviceName, ServiceChangeConfig);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open the {serviceName} service for account configuration.");
            }

            if (password is not null)
            {
                passwordPointer = Marshal.SecureStringToGlobalAllocUnicode(password);
            }
            else if (ServiceAccountPolicy.UsesEmptyPassword(serviceAccount))
            {
                emptyPassword = new SecureString();
                passwordPointer = Marshal.SecureStringToGlobalAllocUnicode(emptyPassword);
            }

            if (!ChangeServiceConfig(
                    service,
                    ServiceNoChange,
                    ServiceNoChange,
                    ServiceNoChange,
                    null,
                    null,
                    IntPtr.Zero,
                    null,
                    serviceAccount,
                    passwordPointer,
                    null))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not set the {serviceName} service account to '{serviceAccount}'.");
            }
        }
        finally
        {
            if (passwordPointer != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(passwordPointer);
            }
            emptyPassword?.Dispose();
            if (service != IntPtr.Zero)
            {
                CloseServiceHandle(service);
            }
            CloseServiceHandle(manager);
        }
    }

    public static string? GetImagePath(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}",
            writable: false);
        return key?.GetValue("ImagePath") as string;
    }

    private static bool IsServiceRegistrationPresent(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}",
            writable: false);
        return key is not null;
    }

    public static string GetBookshelfServiceAccount()
    {
        return GetServiceAccount(BookshelfServiceName);
    }

    public static string GetServiceAccount(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}",
            writable: false);
        return key?.GetValue("ObjectName") as string ?? "LocalSystem";
    }

    public static IReadOnlyDictionary<string, string> GetBookshelfServiceEnvironment()
    {
        return GetServiceEnvironment(BookshelfServiceName);
    }

    public static IReadOnlyDictionary<string, string> GetServiceEnvironment(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}",
            writable: false);
        var values = key?.GetValue("Environment") as string[];
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values is null)
        {
            return environment;
        }

        foreach (var value in values)
        {
            var separator = value.IndexOf('=');
            if (separator > 0)
            {
                environment[value[..separator]] = value[(separator + 1)..];
            }
        }

        return environment;
    }

    public static void SetServiceEnvironment(string serviceName, IReadOnlyDictionary<string, string> environment)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}",
            writable: true)
            ?? throw new InvalidOperationException($"The {serviceName} service registry key was not found.");

        var values = new List<string>(environment.Count);
        foreach (var item in environment)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Contains('=') ||
                item.Key.Contains('\0') || item.Value.Contains('\0'))
            {
                throw new InvalidOperationException($"The {serviceName} service environment contains an invalid variable name or value.");
            }

            values.Add(item.Key + "=" + item.Value);
        }

        values.Sort(StringComparer.OrdinalIgnoreCase);
        key.SetValue("Environment", values.ToArray(), RegistryValueKind.MultiString);
        key.Flush();
    }

    public static IReadOnlyDictionary<string, string> GetBookshelfServerCommandLineOverrides()
    {
        var imagePath = GetImagePath(BookshelfServiceName);
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return overrides;
        }

        foreach (Match match in Regex.Matches(
                     imagePath,
                     @"(?i)(?:^|\s)(?:--?|/)(?<name>port|urlbase|bindaddress|sslport|sslcertpath|sslcertpassword)(?:\s*=\s*|\s+)(?:""(?<quoted>[^""]*)""|(?<bare>[^\s]+))"))
        {
            var value = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["bare"].Value;
            var key = match.Groups["name"].Value.ToUpperInvariant() switch
            {
                "PORT" => "CHAPTARR__SERVER__PORT",
                "URLBASE" => "CHAPTARR__SERVER__URLBASE",
                "BINDADDRESS" => "CHAPTARR__SERVER__BINDADDRESS",
                "SSLPORT" => "CHAPTARR__SERVER__SSLPORT",
                "SSLCERTPATH" => "CHAPTARR__SERVER__SSLCERTPATH",
                "SSLCERTPASSWORD" => "CHAPTARR__SERVER__SSLCERTPASSWORD",
                _ => throw new InvalidOperationException("The BookshelfNG service command line contains an unsupported server override.")
            };
            overrides[key] = value;
        }

        var enableSsl = Regex.Match(
            imagePath,
            @"(?i)(?:^|\s)(?:--?|/)enablessl(?:\s*=\s*(?:""(?<quoted>true|false)""|(?<value>true|false))|\s+(?:""(?<quoted>true|false)""|(?<value>true|false)))?(?=\s|$)");
        if (enableSsl.Success)
        {
            overrides["CHAPTARR__SERVER__ENABLESSL"] = enableSsl.Groups["quoted"].Success
                ? enableSsl.Groups["quoted"].Value
                : enableSsl.Groups["value"].Success
                    ? enableSsl.Groups["value"].Value
                    : "true";
        }

        return overrides;
    }

    public static int GetBookshelfServiceStartType()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{BookshelfServiceName}",
            writable: false);
        return key?.GetValue("Start") is int startType ? startType : 2;
    }

    public static bool GetBookshelfServiceDelayedAutoStart()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{BookshelfServiceName}",
            writable: false);
        return key?.GetValue("DelayedAutoStart") is int delayedAutoStart && delayedAutoStart != 0;
    }

    public static void SetServiceDelayedAutoStart(string serviceName, bool enabled)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}",
            writable: true)
            ?? throw new InvalidOperationException($"The {serviceName} service registry key was not found.");
        key.SetValue("DelayedAutoStart", enabled ? 1 : 0, RegistryValueKind.DWord);
        key.Flush();
    }

    public static string GetBookshelfDataDirectory()
    {
        var imagePath = GetImagePath(BookshelfServiceName);
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return DefaultBookshelfDataDirectory;
        }
        imagePath = Environment.ExpandEnvironmentVariables(imagePath);

        var dataMatches = Regex.Matches(
            imagePath,
            @"(?i)(?:^|\s)(?:--?|/)data(?:\s*=\s*|\s+)(?:""(?<quoted>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s]+))");
        if (dataMatches.Count > 0)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match dataMatch in dataMatches)
            {
                var value = dataMatch.Groups["quoted"].Success
                    ? dataMatch.Groups["quoted"].Value
                    : dataMatch.Groups["single"].Success
                        ? dataMatch.Groups["single"].Value
                        : dataMatch.Groups["bare"].Value;
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException("The BookshelfNG service has an empty -data path. Set an absolute path before migrating.");
                }

                if (LooksLikeBookshelfOption(value))
                {
                    throw new InvalidOperationException("The BookshelfNG service has a -data option without a path before another switch. Set an absolute path before migrating.");
                }

                value = Environment.ExpandEnvironmentVariables(value);
                if (!Path.IsPathFullyQualified(value))
                {
                    throw new InvalidOperationException(
                        $"The BookshelfNG service uses a relative or incomplete data directory ('{value}'). Set a fully qualified path before migrating.");
                }

                paths.Add(Path.GetFullPath(value));
            }

            if (paths.Count != 1)
            {
                throw new InvalidOperationException("The BookshelfNG service has conflicting -data paths. Resolve the service command before migrating.");
            }

            foreach (var path in paths)
            {
                return path;
            }

            throw new InvalidOperationException("The BookshelfNG service data directory could not be resolved safely.");
        }

        if (Regex.IsMatch(imagePath, @"(?i)(?:^|\s)(?:--?|/)data(?=\s|$)"))
        {
            throw new InvalidOperationException("The BookshelfNG service has a -data option without a value. Set an absolute path before migrating.");
        }

        if (Regex.IsMatch(imagePath, @"(?i)(?:^|\s)(?:--?|/)data\s*="))
        {
            throw new InvalidOperationException("The BookshelfNG service has an empty -data path. Set an absolute path before migrating.");
        }

        var executable = GetExecutablePath(imagePath);
        if (executable is not null)
        {
            var binDirectory = Path.GetDirectoryName(executable);
            if (binDirectory is not null &&
                string.Equals(Path.GetFileName(binDirectory), "bin", StringComparison.OrdinalIgnoreCase))
            {
                var dataDirectory = Directory.GetParent(binDirectory)?.FullName;
                if (dataDirectory is not null)
                {
                    return dataDirectory;
                }
            }
        }

        return DefaultBookshelfDataDirectory;
    }

    private static bool LooksLikeBookshelfOption(string value)
    {
        return value.StartsWith("-", StringComparison.Ordinal) ||
            (value.StartsWith("/", StringComparison.Ordinal) && !value.StartsWith("//", StringComparison.Ordinal));
    }

    public static bool IsInstalled(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            _ = sc.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static ServiceControllerStatus? TryGetStatus(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static async Task<bool> StopAsync(
        string serviceName,
        IProgress<LogLine> log,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (!IsInstalled(serviceName))
        {
            log.Report(new LogLine(LogStream.Stdout, $"[service] {serviceName} is not installed; nothing to stop."));
            return true;
        }

        using var sc = new ServiceController(serviceName);
        try
        {
            sc.Refresh();
            var processId = GetServiceProcessId(sc);
            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                log.Report(new LogLine(LogStream.Stdout, $"[service] {serviceName} already stopped."));
            }
            else
            {
                log.Report(new LogLine(LogStream.Stdout, $"[service] Stopping {serviceName}..."));
                if (sc.Status != ServiceControllerStatus.StopPending)
                {
                    sc.Stop();
                }

                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        log.Report(new LogLine(LogStream.Stdout, $"[service] {serviceName} reported STOPPED."));
                        break;
                    }

                    await Task.Delay(500, ct).ConfigureAwait(false);
                }

                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    log.Report(new LogLine(LogStream.Stderr, $"[service] Timed out waiting for {serviceName} to stop."));
                    return false;
                }
            }

            if (processId.HasValue && !await WaitForProcessExitAsync(serviceName, processId.Value, log, ct).ConfigureAwait(false))
            {
                return false;
            }

            log.Report(new LogLine(LogStream.Stdout, $"[service] {serviceName} process has exited."));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            log.Report(new LogLine(LogStream.Stderr, $"[service] Could not stop {serviceName}: {ex.Message}"));
            return false;
        }
    }

    public static async Task<bool> StartAsync(
        string serviceName,
        IProgress<LogLine> log,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (!IsInstalled(serviceName))
        {
            log.Report(new LogLine(LogStream.Stderr, $"[service] {serviceName} is not installed; it cannot be started."));
            return false;
        }

        using var sc = new ServiceController(serviceName);
        try
        {
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                log.Report(new LogLine(LogStream.Stdout, $"[service] Starting {serviceName}..."));
                sc.Start();
            }

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    log.Report(new LogLine(LogStream.Stdout, $"[service] {serviceName} is RUNNING."));
                    return true;
                }

                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            log.Report(new LogLine(LogStream.Stderr, $"[service] Timed out waiting for {serviceName} to start."));
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.Report(new LogLine(LogStream.Stderr, $"[service] Could not start {serviceName}: {ex.Message}"));
            return false;
        }
    }

    public static async Task<bool> DeleteAsync(
        string serviceName,
        IProgress<LogLine> log,
        CancellationToken ct = default)
    {
        if (!IsInstalled(serviceName))
        {
            log.Report(new LogLine(LogStream.Stdout, $"[service] {serviceName} is not installed; skipping delete."));
            return true;
        }

        log.Report(new LogLine(LogStream.Stdout, $"[service] Deleting service {serviceName}..."));
        var exit = await ProcessStreamer.RunAsync(
            "sc.exe",
            new[] { "delete", serviceName },
            null,
            log,
            ct).ConfigureAwait(false);
        if (exit != 0)
        {
            log.Report(new LogLine(LogStream.Stderr, $"[service] sc.exe delete {serviceName} exited {exit}."));
            return false;
        }

        // Windows may keep a stopped service marked for deletion until every
        // outstanding SCM handle closes. Give service-host and management
        // handles enough time to drain before reporting that removal failed.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            // Do not poll through ServiceController after DeleteService. Opening
            // the service while it is marked for deletion can keep its SCM
            // registration alive and delay the very removal we are waiting for.
            if (!IsServiceRegistrationPresent(serviceName))
            {
                return true;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return !IsServiceRegistrationPresent(serviceName);
    }

    public static async Task<bool> SetStartTypeAsync(
        string serviceName,
        string startType,
        IProgress<LogLine> log,
        CancellationToken ct = default)
    {
        var exit = await ProcessStreamer.RunAsync(
            "sc.exe",
            new[] { "config", serviceName, "start=", startType },
            null,
            log,
            ct).ConfigureAwait(false);
        if (exit != 0)
        {
            log.Report(new LogLine(LogStream.Stderr, $"[service] sc.exe config {serviceName} start= {startType} exited {exit}."));
            return false;
        }

        return true;
    }

    public static async Task<bool> PollForRunningAsync(
        string serviceName,
        IProgress<LogLine> log,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        log.Report(new LogLine(LogStream.Stdout, $"[service] Waiting for {serviceName} to reach RUNNING ({timeout.TotalSeconds:F0}s timeout)..."));
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var status = TryGetStatus(serviceName);
            if (status == ServiceControllerStatus.Running)
            {
                log.Report(new LogLine(LogStream.Stdout, $"[service] {serviceName} is RUNNING."));
                return true;
            }

            await Task.Delay(2000, ct).ConfigureAwait(false);
        }

        log.Report(new LogLine(LogStream.Stderr, $"[service] {serviceName} did not reach RUNNING within {timeout.TotalSeconds:F0}s."));
        return false;
    }

    private static string? GetExecutablePath(string imagePath)
    {
        var value = imagePath.TrimStart();
        if (value.StartsWith('"'))
        {
            var endQuote = value.IndexOf('"', 1);
            return endQuote > 1 ? value[1..endQuote] : null;
        }

        var exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd >= 0 ? value[..(exeEnd + 4)].Trim() : null;
    }

    private static int? GetServiceProcessId(ServiceController service)
    {
        if (!QueryServiceStatusEx(
                service.ServiceHandle.DangerousGetHandle(),
                0,
                out var status,
                (uint)Marshal.SizeOf<ServiceStatusProcess>(),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not query the process ID for service {service.ServiceName}.");
        }

        return status.ProcessId == 0 ? null : checked((int)status.ProcessId);
    }

    private static async Task<bool> WaitForProcessExitAsync(
        string serviceName,
        int processId,
        IProgress<LogLine> log,
        CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            if (process.HasExited)
            {
                return true;
            }

            log.Report(new LogLine(
                LogStream.Stdout,
                $"[service] Waiting for {serviceName} process {processId} to exit after the service reported STOPPED..."));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ServiceProcessExitTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (process.HasExited)
                {
                    return true;
                }

                log.Report(new LogLine(
                    LogStream.Stderr,
                    $"[service] {serviceName} process {processId} did not exit within {ServiceProcessExitTimeout.TotalSeconds:0} seconds."));
                return false;
            }
        }
    }
}

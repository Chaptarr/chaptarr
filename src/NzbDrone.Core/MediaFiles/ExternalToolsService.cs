using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.MediaFiles
{
    public interface IExternalToolsService
    {
        string GetFFprobePath();
        string GetFFmpegPath();
        string GetM4bToolPath();
        bool IsFFprobeAvailable();
        bool IsFFmpegAvailable();
        bool IsM4bToolAvailable();
        [Obsolete("Use ExecuteFFprobe(IReadOnlyList<string>) to avoid argument injection and quoting issues.")]
        string ExecuteFFprobe(string arguments);
        string ExecuteFFprobe(IReadOnlyList<string> arguments, int timeoutMs = 10000);
        [Obsolete("Use ExecuteFFmpeg(IReadOnlyList<string>) to avoid argument injection and quoting issues.")]
        string ExecuteFFmpeg(string arguments);
        string ExecuteFFmpeg(IReadOnlyList<string> arguments, int timeoutMs = 10000, bool preferStderrOnEmpty = false);
        string ExecuteM4bTool(IReadOnlyList<string> arguments, int timeoutMs = 3600000, Action<string> outputHandler = null, CancellationToken cancellationToken = default);
        ExternalToolResult ExecuteM4bToolDetailed(IReadOnlyList<string> arguments, int timeoutMs = 3600000, Action<string> outputHandler = null, CancellationToken cancellationToken = default);
    }

    public class ExternalToolResult
    {
        public int ExitCode { get; set; } = -1;
        public bool TimedOut { get; set; }
        public bool Cancelled { get; set; }
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        public string ErrorMessage { get; set; }
        public int TimeoutMs { get; set; }

        public bool Succeeded => !TimedOut && !Cancelled && ExitCode == 0 && string.IsNullOrWhiteSpace(ErrorMessage);

        public string CombinedOutput
        {
            get
            {
                if (string.IsNullOrEmpty(StandardOutput))
                {
                    return StandardError ?? string.Empty;
                }

                if (string.IsNullOrEmpty(StandardError))
                {
                    return StandardOutput ?? string.Empty;
                }

                return StandardOutput + Environment.NewLine + StandardError;
            }
        }

        public string GetPreferredOutput(bool preferStderrOnEmpty = false)
        {
            if (string.IsNullOrEmpty(StandardOutput) && preferStderrOnEmpty && !string.IsNullOrEmpty(StandardError))
            {
                return StandardError;
            }

            return StandardOutput;
        }
    }

    public class ExternalToolsService : IExternalToolsService
    {
        private readonly IConfigService _configService;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;
        private readonly string _appFolder;
        private readonly string _startupFolder;

        public ExternalToolsService(IConfigService configService,
                                  IDiskProvider diskProvider,
                                  IAppFolderInfo appFolderInfo,
                                  Logger logger)
        {
            _configService = configService;
            _diskProvider = diskProvider;
            _appFolder = appFolderInfo.AppDataFolder;
            _startupFolder = appFolderInfo.StartUpFolder;
            _logger = logger;
            _logger.Debug("ExternalToolsService initialized with AppDataFolder: {0}, StartupFolder: {1}", _appFolder, _startupFolder);
        }

        public string GetFFprobePath()
        {
            // TODO: Add custom path support when config service supports it

            // Check bundled path
            var bundledPath = GetBundledToolPath("ffprobe");
            _logger.Debug("Checking bundled FFprobe path: {0}", bundledPath);
            if (_diskProvider.FileExists(bundledPath))
            {
                _logger.Debug("Found bundled FFprobe at: {0}", bundledPath);
                return bundledPath;
            }

            // Check system PATH
            var systemPath = FindInPath("ffprobe");
            _logger.Debug("Checking system PATH for FFprobe, found: {0}", systemPath ?? "null");
            if (!string.IsNullOrEmpty(systemPath))
            {
                _logger.Debug("Found FFprobe in system PATH at: {0}", systemPath);
                return systemPath;
            }

            _logger.Warn("FFprobe not found in any location");
            return "ffprobe"; // Fallback to system command
        }

        public string GetFFmpegPath()
        {
            // TODO: Add custom path support when config service supports it

            // Check bundled path
            var bundledPath = GetBundledToolPath("ffmpeg");
            if (_diskProvider.FileExists(bundledPath))
            {
                return bundledPath;
            }

            // Check system PATH
            var systemPath = FindInPath("ffmpeg");
            if (!string.IsNullOrEmpty(systemPath))
            {
                return systemPath;
            }

            _logger.Warn("FFmpeg not found in any location");
            return "ffmpeg"; // Fallback to system command
        }

        public string GetM4bToolPath()
        {
            // TODO: Add custom path support when config service supports it

            // For Docker/Linux, we install it in /usr/local/bin
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var systemPath = "/usr/local/bin/m4b-tool";
                if (_diskProvider.FileExists(systemPath))
                {
                    return systemPath;
                }
            }

            // Check bundled path
            var bundledPath = GetBundledToolPath("m4b-tool");
            if (_diskProvider.FileExists(bundledPath))
            {
                return bundledPath;
            }

            // Check system PATH
            var pathResult = FindInPath("m4b-tool");
            if (!string.IsNullOrEmpty(pathResult))
            {
                return pathResult;
            }

            _logger.Warn("m4b-tool not found in any location");
            return null;
        }

        public bool IsFFprobeAvailable()
        {
            try
            {
                var path = GetFFprobePath();
                _logger.Debug("IsFFprobeAvailable checking path: {0}", path);
                if (string.IsNullOrEmpty(path))
                {
                    _logger.Warn("FFprobe path is null or empty - ffprobe not found in system");
                    return false;
                }

                var result = ExecuteCommand(path, "-version", preferStderrOnEmpty: true);
                _logger.Debug("FFprobe -version output: {0}", result ?? "null");
                var isAvailable = !string.IsNullOrEmpty(result) && result.Contains("ffprobe");

                if (!isAvailable)
                {
                    _logger.Warn("FFprobe executable found at {0} but -version check failed", path);
                }
                else
                {
                    _logger.Debug("FFprobe is available at: {0}", path);
                }

                return isAvailable;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to check FFprobe availability");
                return false;
            }
        }

        public bool IsFFmpegAvailable()
        {
            try
            {
                var path = GetFFmpegPath();
                if (string.IsNullOrEmpty(path))
                {
                    return false;
                }

                var result = ExecuteCommand(path, "-version", preferStderrOnEmpty: true);
                return !string.IsNullOrEmpty(result) && result.Contains("ffmpeg");
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to check FFmpeg availability");
                return false;
            }
        }

        public bool IsM4bToolAvailable()
        {
            try
            {
                var path = GetM4bToolPath();
                if (string.IsNullOrEmpty(path))
                {
                    return false;
                }

                // m4b-tool is a PHP script, so we need to run it with PHP
                var result = ExecuteCommand("php", new[] { path, "--version" }, preferStderrOnEmpty: true);
                return !string.IsNullOrEmpty(result) && result.Contains("m4b-tool");
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to check m4b-tool availability");
                return false;
            }
        }

        [Obsolete("Use ExecuteFFprobe(IReadOnlyList<string>) to avoid argument injection and quoting issues.")]
        public string ExecuteFFprobe(string arguments)
        {
            var path = GetFFprobePath();
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException("FFprobe is not available");
            }

            return ExecuteCommand(path, arguments);
        }

        public string ExecuteFFprobe(IReadOnlyList<string> arguments, int timeoutMs = 10000)
        {
            var path = GetFFprobePath();
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException("FFprobe is not available");
            }

            return ExecuteCommand(path, arguments, timeoutMs: timeoutMs);
        }

        [Obsolete("Use ExecuteFFmpeg(IReadOnlyList<string>) to avoid argument injection and quoting issues.")]
        public string ExecuteFFmpeg(string arguments)
        {
            var path = GetFFmpegPath();
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException("FFmpeg is not available");
            }

            return ExecuteCommand(path, arguments);
        }

        public string ExecuteFFmpeg(IReadOnlyList<string> arguments, int timeoutMs = 10000, bool preferStderrOnEmpty = false)
        {
            var path = GetFFmpegPath();
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException("FFmpeg is not available");
            }

            return ExecuteCommand(path, arguments, preferStderrOnEmpty: preferStderrOnEmpty, timeoutMs: timeoutMs);
        }

        public string ExecuteM4bTool(IReadOnlyList<string> arguments, int timeoutMs = 3600000, Action<string> outputHandler = null, CancellationToken cancellationToken = default)
        {
            return ExecuteM4bToolDetailed(arguments, timeoutMs, outputHandler, cancellationToken).GetPreferredOutput(preferStderrOnEmpty: true);
        }

        public ExternalToolResult ExecuteM4bToolDetailed(IReadOnlyList<string> arguments, int timeoutMs = 3600000, Action<string> outputHandler = null, CancellationToken cancellationToken = default)
        {
            var path = GetM4bToolPath();
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException("m4b-tool is not available");
            }

            // m4b-tool is a PHP script
            var args = new List<string> { path };

            if (arguments != null)
            {
                args.AddRange(arguments);
            }

            return ExecuteCommandDetailed("php", args, timeoutMs: timeoutMs, outputHandler: outputHandler, cancellationToken: cancellationToken);
        }

        private string GetBundledToolPath(string toolName)
        {
            var platform = GetPlatform();
            var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLower();

            // Check in startup folder first (where binaries are)
            var toolPath = Path.Combine(_startupFolder, "Tools", toolName, $"{platform}-{architecture}", toolName);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                toolPath += ".exe";
            }

            _logger.Debug("Checking bundled tool at startup location: {0}", toolPath);
            if (_diskProvider.FileExists(toolPath))
            {
                _logger.Debug("Found bundled tool at: {0}", toolPath);
                return toolPath;
            }

            // Fall back to app data folder
            toolPath = Path.Combine(_appFolder, "Tools", toolName, $"{platform}-{architecture}", toolName);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                toolPath = toolPath.Replace(".exe", "") + ".exe";
            }

            _logger.Debug("Checking bundled tool at app data location: {0}", toolPath);
            return toolPath;
        }

        private string GetPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "win";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "linux";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "osx";
            }

            throw new PlatformNotSupportedException($"Platform {RuntimeInformation.OSDescription} is not supported");
        }

        private string FindInPath(string executable)
        {
            try
            {
                // For Linux containers, try known locations first
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var knownPaths = new[]
                    {
                        $"/usr/bin/{executable}",
                        $"/usr/local/bin/{executable}",
                        $"/bin/{executable}"
                    };

                    foreach (var knownPath in knownPaths)
                    {
                        _logger.Debug("Checking known location: {0}", knownPath);

                        // Try executing directly even if FileExists fails (container issue workaround)
                        try
                        {
                            var testResult = ExecuteCommand(knownPath, "-version");
                            if (!string.IsNullOrEmpty(testResult))
                            {
                                _logger.Debug("Found {0} at known location: {1}", executable, knownPath);
                                return knownPath;
                            }
                        }
                        catch
                        {
                            // Continue to next path
                        }
                    }
                }

                var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
                _logger.Debug("Searching for {0} in PATH: {1}", executable, string.Join(":", paths));

                foreach (var path in paths)
                {
                    var fullPath = Path.Combine(path, executable);

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        fullPath += ".exe";
                    }

                    _logger.Debug("Checking if file exists: {0}", fullPath);
                    if (_diskProvider.FileExists(fullPath))
                    {
                        _logger.Debug("Found {0} at: {1}", executable, fullPath);
                        return fullPath;
                    }
                }

                // Also try using 'which' command on Unix-like systems
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    _logger.Debug("Trying 'which' command for {0}", executable);
                    var result = ExecuteCommand("which", executable);
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        _logger.Debug("'which' command found {0} at: {1}", executable, result.Trim());
                        return result.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Error searching for {0} in PATH", executable);
            }

            return null;
        }

        private string ExecuteCommand(string fileName, IReadOnlyList<string> arguments, bool preferStderrOnEmpty = false, int timeoutMs = 10000, Action<string> outputHandler = null)
        {
            return ExecuteCommandDetailed(fileName, arguments, timeoutMs, outputHandler).GetPreferredOutput(preferStderrOnEmpty);
        }

        private ExternalToolResult ExecuteCommandDetailed(string fileName, IReadOnlyList<string> arguments, int timeoutMs = 10000, Action<string> outputHandler = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (arguments == null)
                {
                    arguments = Array.Empty<string>();
                }

                var commandForLog = fileName + " " + string.Join(" ", arguments.Select(QuoteArgumentForLog));
                _logger.Debug("Executing command: {0}", commandForLog);
                var result = new ExternalToolResult
                {
                    TimeoutMs = timeoutMs
                };

                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (var arg in arguments)
                {
                    startInfo.ArgumentList.Add(arg ?? string.Empty);
                }

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        _logger.Debug("Process.Start returned null for: {0}", fileName);
                        result.ErrorMessage = "Process failed to start";
                        return result;
                    }

                    // Read raw chunks so tools that update progress with carriage returns still stream live.
                    var outputTask = ReadStreamAsync(process.StandardOutput, outputHandler, "stdout");
                    var errorTask = ReadStreamAsync(process.StandardError, outputHandler, "stderr");

                    var stopwatch = Stopwatch.StartNew();
                    var exited = false;
                    while (!exited)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            result.Cancelled = true;
                            result.ErrorMessage = "Process cancelled";
                            _logger.Info("Command cancellation requested, stopping process: {0}", commandForLog);
                            KillProcessTree(process, commandForLog, "cancelled");
                            try { process.WaitForExit(1000); } catch { }
                            break;
                        }

                        var remaining = timeoutMs - (int)Math.Min(timeoutMs, stopwatch.ElapsedMilliseconds);
                        if (remaining <= 0)
                        {
                            break;
                        }

                        exited = process.WaitForExit(Math.Min(250, remaining));
                    }

                    if (!exited)
                    {
                        if (!result.Cancelled)
                        {
                            result.TimedOut = true;
                            _logger.Warn("Command timed out after {0}ms: {1}", timeoutMs, commandForLog);
                            KillProcessTree(process, commandForLog, "timed-out");
                            try { process.WaitForExit(1000); } catch { }
                        }
                    }
                    else
                    {
                        // Ensure async stream readers receive the final EOF.
                        try { process.WaitForExit(); } catch { }
                    }

                    // Best-effort wait for output tasks after process exit/kill; never block indefinitely.
                    try { Task.WaitAll(new Task[] { outputTask, errorTask }, millisecondsTimeout: 1000); } catch { }

                    var output = outputTask.IsCompletedSuccessfully ? outputTask.Result : string.Empty;
                    var error = errorTask.IsCompletedSuccessfully ? errorTask.Result : string.Empty;
                    result.StandardOutput = output ?? string.Empty;
                    result.StandardError = error ?? string.Empty;

                    try
                    {
                        result.ExitCode = process.ExitCode;
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Failed to read command exit code: {0}", commandForLog);
                        result.ExitCode = -1;
                    }

                    _logger.Debug("Command exit code: {0}", result.ExitCode);
                    if (!string.IsNullOrEmpty(output))
                    {
                        _logger.Debug("Command output (first 200 chars): {0}", output.Length > 200 ? output.Substring(0, 200) + "..." : output);
                    }
                    if (!string.IsNullOrEmpty(error))
                    {
                        _logger.Debug("Command error output: {0}", TruncateForLog(error, 4000));
                    }

                    if (result.ExitCode != 0 && !string.IsNullOrEmpty(error))
                    {
                        _logger.Debug("Command {0} failed with: {1}", commandForLog, TruncateForLog(error, 4000));
                    }

                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to execute command: {0} {1}", fileName, arguments);
                return new ExternalToolResult
                {
                    ExitCode = -1,
                    TimeoutMs = timeoutMs,
                    ErrorMessage = ex.Message,
                    StandardError = ex.ToString()
                };
            }
        }

        private void KillProcessTree(Process process, string commandForLog, string reason)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to kill {0} process: {1}", reason, commandForLog);
            }
        }

        private async Task<string> ReadStreamAsync(StreamReader reader, Action<string> outputHandler, string streamName)
        {
            var builder = new StringBuilder();
            var buffer = new char[1024];

            while (true)
            {
                var read = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                var chunk = new string(buffer, 0, read);
                builder.Append(chunk);

                if (outputHandler == null)
                {
                    continue;
                }

                try
                {
                    outputHandler(chunk);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Output handler failed while reading {0}", streamName);
                }
            }

            return builder.ToString();
        }

        private string ExecuteCommand(string fileName, string arguments, bool preferStderrOnEmpty = false)
        {
            try
            {
                _logger.Debug("Executing command: {0} {1}", fileName, arguments);
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        _logger.Debug("Process.Start returned null for: {0}", fileName);
                        return null;
                    }

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    var exited = process.WaitForExit(10000);
                    if (!exited)
                    {
                        _logger.Warn("Command timed out after {0}ms: {1} {2}", 10000, fileName, arguments);
                        try { process.Kill(entireProcessTree: true); } catch { }
                        try { process.WaitForExit(1000); } catch { }
                    }

                    try { Task.WaitAll(new Task[] { outputTask, errorTask }, millisecondsTimeout: 1000); } catch { }

                    var output = outputTask.IsCompletedSuccessfully ? outputTask.Result : string.Empty;
                    var error = errorTask.IsCompletedSuccessfully ? errorTask.Result : string.Empty;

                    _logger.Debug("Command exit code: {0}", process.ExitCode);
                    if (!string.IsNullOrEmpty(output))
                    {
                        _logger.Debug("Command output (first 200 chars): {0}", output.Length > 200 ? output.Substring(0, 200) + "..." : output);
                    }
                    if (!string.IsNullOrEmpty(error))
                    {
                        _logger.Debug("Command error output: {0}", error);
                    }

                    if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
                    {
                        _logger.Debug("Command {0} {1} failed with: {2}", fileName, arguments, error);
                    }

                    // Handle tools like FFprobe that output version info to stderr
                    if (string.IsNullOrEmpty(output) && preferStderrOnEmpty && !string.IsNullOrEmpty(error))
                    {
                        _logger.Debug("Output was empty, returning stderr content for {0}", fileName);
                        return error;
                    }

                    return output;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to execute command: {0} {1}", fileName, arguments);
                return null;
            }
        }

        private static string QuoteArgumentForLog(string argument)
        {
            if (string.IsNullOrEmpty(argument))
            {
                return "\"\"";
            }

            if (argument.Any(char.IsWhiteSpace))
            {
                return "\"" + argument.Replace("\"", "\\\"") + "\"";
            }

            return argument;
        }

        private static string TruncateForLog(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength) + "...";
        }
    }
}

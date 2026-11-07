using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace ChaptarrNG.Installer.Pipeline;

public static class ServiceCleanupRunner
{
    private const string ResourceName = "finalize-service-handoff.ps1";

    public static bool ScheduleLegacyServiceCleanup(
        string dataDirectory,
        bool chaptarrServiceWasRunning,
        IUiReporter ui)
    {
        string? scriptPath = null;
        try
        {
            scriptPath = ExtractToTemp();
            using var currentProcess = Process.GetCurrentProcess();
            var processStartTicks = currentProcess.StartTime.ToUniversalTime().Ticks;
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-ParentProcessId");
            startInfo.ArgumentList.Add(currentProcess.Id.ToString());
            startInfo.ArgumentList.Add("-ParentProcessStartTicks");
            startInfo.ArgumentList.Add(processStartTicks.ToString());
            startInfo.ArgumentList.Add("-DataDirectory");
            startInfo.ArgumentList.Add(Path.GetFullPath(dataDirectory));
            startInfo.ArgumentList.Add("-ChaptarrServiceWasRunning");
            startInfo.ArgumentList.Add(chaptarrServiceWasRunning ? "1" : "0");

            using var cleanupProcess = Process.Start(startInfo);
            if (cleanupProcess is null)
            {
                throw new InvalidOperationException("PowerShell did not start the deferred service cleanup process.");
            }

            ui.Log("[installer] Readarr is disabled and will be removed after this installer closes, once ChaptarrNG remains in its verified service state.");
            scriptPath = null;
            return true;
        }
        catch (Exception exception)
        {
            if (scriptPath is not null)
            {
                try { File.Delete(scriptPath); }
                catch { /* Keep cleanup best-effort; the verified migration remains usable. */ }
            }

            ui.LogError($"[installer] Could not schedule deferred Readarr service cleanup: {exception.Message}");
            return false;
        }
    }

    private static string ExtractToTemp()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found in the installer.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "ChaptarrNG.Installer");
        Directory.CreateDirectory(tempDirectory);
        var scriptPath = Path.Combine(tempDirectory, $"handoff-{Guid.NewGuid():N}.ps1");
        using var destination = File.Create(scriptPath);
        stream.CopyTo(destination);
        return scriptPath;
    }
}

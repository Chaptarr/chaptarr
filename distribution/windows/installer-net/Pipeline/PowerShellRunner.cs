using System;
using System.Collections.Generic;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace ChaptarrNG.Installer.Pipeline;

public static class PowerShellRunner
{
    public static Task<int> RunScriptAsync(
        string scriptPath,
        IReadOnlyList<string> scriptArguments,
        IProgress<LogLine> log,
        CancellationToken ct = default,
        SecureString? standardInputSecret = null)
    {
        var arguments = new List<string>
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath
        };
        arguments.AddRange(scriptArguments);

        return ProcessStreamer.RunAsync(
            fileName: "powershell.exe",
            arguments: arguments,
            workingDirectory: null,
            log: log,
            ct: ct,
            standardInputSecret: standardInputSecret);
    }
}

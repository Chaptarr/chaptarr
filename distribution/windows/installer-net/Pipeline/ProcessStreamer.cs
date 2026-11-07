using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ChaptarrNG.Installer.Pipeline;

public enum LogStream
{
    Stdout,
    Stderr
}

public readonly record struct LogLine(LogStream Stream, string Text);

public static class ProcessStreamer
{
    public static async Task<int> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        IProgress<LogLine> log,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        if (environment != null)
        {
            foreach (var pair in environment)
            {
                if (pair.Value == null)
                {
                    psi.Environment.Remove(pair.Key);
                }
                else
                {
                    psi.Environment[pair.Key] = pair.Value;
                }
            }
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                log.Report(new LogLine(LogStream.Stdout, e.Data));
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                log.Report(new LogLine(LogStream.Stderr, e.Data));
            }
        };

        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using (ct.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort — process may have already exited between the check and Kill.
            }
        }))
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        return proc.ExitCode;
    }

    public static async Task<int> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IProgress<LogLine> log,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action? onStarted = null,
        SecureString? standardInputSecret = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInputSecret is not null,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        if (environment != null)
        {
            foreach (var pair in environment)
            {
                if (pair.Value == null)
                {
                    psi.Environment.Remove(pair.Key);
                }
                else
                {
                    psi.Environment[pair.Key] = pair.Value;
                }
            }
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) log.Report(new LogLine(LogStream.Stdout, e.Data));
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) log.Report(new LogLine(LogStream.Stderr, e.Data));
        };

        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        onStarted?.Invoke();

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using (ct.Register(() =>
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort — the process may have exited between the check and Kill.
            }
        }))
        {
            if (standardInputSecret is not null)
            {
                await WriteSecureStringFrameAsync(proc, standardInputSecret, ct).ConfigureAwait(false);
            }

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        return proc.ExitCode;
    }

    private static async Task WriteSecureStringFrameAsync(
        Process process,
        SecureString secret,
        CancellationToken ct)
    {
        var byteCount = checked(secret.Length * sizeof(char));
        var frame = new byte[sizeof(int) + byteCount];
        var secretPointer = Marshal.SecureStringToGlobalAllocUnicode(secret);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, sizeof(int)), secret.Length);
            if (byteCount > 0)
            {
                Marshal.Copy(secretPointer, frame, sizeof(int), byteCount);
            }

            var input = process.StandardInput.BaseStream;
            await input.WriteAsync(frame.AsMemory(), ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
            if (secretPointer != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(secretPointer);
            }
        }
    }
}

using System;
using System.IO;
using Microsoft.Win32;

namespace ChaptarrNG.Installer.Pipeline;

public sealed record UpgradeRecord(
    string? LastUpgradeTime,
    string? BackupPath,
    string? DataDirectory,
    string? BookshelfServiceAccount,
    string? ProviderMode,
    string? PreviousVersion,
    int? BookshelfServiceStartType,
    bool? BookshelfServiceDelayedAutoStart,
    bool? BookshelfServiceWasRunning,
    string? CurrentVersion,
    string? LastRollbackTime);

public static class RegistryService
{
    private const string UpgradeSubKey = @"SOFTWARE\ChaptarrNG\BookshelfMigration";

    public static UpgradeRecord? Read()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(UpgradeSubKey, writable: false);
        if (key is null)
        {
            return null;
        }

        return new UpgradeRecord(
            LastUpgradeTime: key.GetValue("LastUpgradeTime") as string,
            BackupPath: key.GetValue("BackupPath") as string,
            DataDirectory: key.GetValue("DataDirectory") as string,
            BookshelfServiceAccount: key.GetValue("BookshelfServiceAccount") as string,
            ProviderMode: key.GetValue("ProviderMode") as string,
            PreviousVersion: key.GetValue("PreviousVersion") as string,
            BookshelfServiceStartType: key.GetValue("BookshelfServiceStartType") is int startType ? startType : null,
            BookshelfServiceDelayedAutoStart: key.GetValue("BookshelfServiceDelayedAutoStart") is int delayedAutoStart
                ? delayedAutoStart != 0
                : null,
            BookshelfServiceWasRunning: key.GetValue("BookshelfServiceWasRunning") is int wasRunning
                ? wasRunning != 0
                : null,
            CurrentVersion: key.GetValue("CurrentVersion") as string,
            LastRollbackTime: key.GetValue("LastRollbackTime") as string);
    }

    public static bool HasRecoverableUpgrade()
    {
        var record = Read();
        return !string.IsNullOrWhiteSpace(record?.BackupPath) &&
            Directory.Exists(record.BackupPath) &&
            Directory.Exists(Path.Combine(record.BackupPath, "bin"));
    }

    public static void WriteUpgradeStart(
        string backupPath,
        string dataDirectory,
        string bookshelfServiceAccount,
        string providerMode,
        string? previousVersion,
        int bookshelfServiceStartType,
        bool bookshelfServiceDelayedAutoStart,
        bool bookshelfServiceWasRunning)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(UpgradeSubKey, writable: true);
        if (key is null)
        {
            throw new InvalidOperationException($"Unable to open or create registry key: HKLM\\{UpgradeSubKey}");
        }

        key.SetValue("LastUpgradeTime", DateTime.UtcNow.ToString("O"));
        key.SetValue("BackupPath", backupPath);
        key.SetValue("DataDirectory", dataDirectory);
        key.SetValue("BookshelfServiceAccount", bookshelfServiceAccount);
        key.SetValue("ProviderMode", providerMode);
        key.SetValue("BookshelfServiceStartType", bookshelfServiceStartType, RegistryValueKind.DWord);
        key.SetValue("BookshelfServiceDelayedAutoStart", bookshelfServiceDelayedAutoStart ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("BookshelfServiceWasRunning", bookshelfServiceWasRunning ? 1 : 0, RegistryValueKind.DWord);
        if (!string.IsNullOrWhiteSpace(previousVersion))
        {
            key.SetValue("PreviousVersion", previousVersion);
        }
    }

    public static void WriteCurrentVersion(string version)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(UpgradeSubKey, writable: true);
        key?.SetValue("CurrentVersion", version);
    }

    public static void WriteRollbackTimestamp()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(UpgradeSubKey, writable: true);
        key?.SetValue("LastRollbackTime", DateTime.UtcNow.ToString("O"));
    }
}

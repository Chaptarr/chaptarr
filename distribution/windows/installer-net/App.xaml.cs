using System;
using System.Threading;
using System.Windows;
using ChaptarrNG.Installer.Pipeline;

namespace ChaptarrNG.Installer;

public partial class App : Application
{
    private Mutex? _migrationMutex;
    private bool _ownsMigrationMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (Array.Exists(e.Args, arg => string.Equals(arg, "/verify-payload", StringComparison.OrdinalIgnoreCase)))
        {
            var valid = PayloadExtractor.HasEmbeddedPayload();
            if (!valid)
            {
                System.Diagnostics.Trace.WriteLine("The installer is missing its embedded ChaptarrNG payload or rollback script.");
            }

            Shutdown(valid ? 0 : 1);
            return;
        }

        _migrationMutex = new Mutex(false, @"Global\ChaptarrNG.BookshelfMigration");
        try
        {
            _ownsMigrationMutex = _migrationMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous installer exited unexpectedly. Its OS mutex was
            // released, so this instance owns the recovery path now.
            _ownsMigrationMutex = true;
        }

        if (!_ownsMigrationMutex)
        {
            _migrationMutex.Dispose();
            _migrationMutex = null;
            MessageBox.Show(
                "Another BookshelfNG migration or restore window is already open. Close it before starting another one.",
                "ChaptarrNG Installer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(2);
            return;
        }

        var restoreMode = false;
        foreach (var arg in e.Args)
        {
            if (string.Equals(arg, "/restore", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--restore", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-restore", StringComparison.OrdinalIgnoreCase))
            {
                restoreMode = true;
            }
        }

        var window = new MainWindow(restoreMode);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMigrationMutex)
        {
            _migrationMutex?.ReleaseMutex();
        }

        _migrationMutex?.Dispose();
        _migrationMutex = null;
        _ownsMigrationMutex = false;
        base.OnExit(e);
    }
}

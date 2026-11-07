using System;
using System.Threading;
using System.Threading.Tasks;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ChaptarrNG.Installer.Pipeline;

namespace ChaptarrNG.Installer;

public partial class MainWindow : IUiReporter
{
    private bool _autoScroll = true;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private readonly bool _restoreMode;

    public MainWindow() : this(restoreMode: false) { }

    public MainWindow(bool restoreMode)
    {
        _restoreMode = restoreMode;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_restoreMode)
        {
            Title = "ChaptarrNG BookshelfNG Migration — Restore";
            InstallButton.Visibility = Visibility.Collapsed;
            RestoreButton.Visibility = Visibility.Collapsed;
            var confirm = MessageBox.Show(
                this,
                "This replaces the migrated database and binaries with the retained BookshelfNG backup, removes the Chaptarr service, and starts Readarr. Chaptarr changes made after migration will be lost. Continue?",
                "Confirm BookshelfNG restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                Close();
                return;
            }

            AppendLog("[installer] /restore mode — restoring the retained BookshelfNG migration backup.");
            await RunRestoreAsync().ConfigureAwait(true);
            return;
        }

        if (RegistryService.HasRecoverableUpgrade())
        {
            var record = RegistryService.Read();
            RestoreButton.Visibility = Visibility.Visible;
            AppendLog("[installer] A verified BookshelfNG migration backup was found — Restore BookshelfNG is available.");
            if (!string.IsNullOrWhiteSpace(record?.PreviousVersion))
            {
                AppendLog($"[installer]   PreviousVersion: {record.PreviousVersion}");
            }
            if (!string.IsNullOrWhiteSpace(record?.BackupPath))
            {
                AppendLog($"[installer]   BackupPath: {record.BackupPath}");
            }
        }
    }

    private async Task RunRestoreAsync()
    {
        _isRunning = true;
        _cts = new CancellationTokenSource();
        SetButtonsForRunning();
        CancelButton.IsEnabled = false;

        try
        {
            var outcome = await Task.Run(() => InstallPipeline.RunRestoreAsync(this, _cts.Token))
                .ConfigureAwait(true);

            if (!outcome.Success)
            {
                AppendLogColored($"[installer] Restore failed: {outcome.ErrorMessage}", Color.FromRgb(0xE8, 0x4A, 0x4A));
                MessageBox.Show(this,
                    outcome.ErrorMessage ?? "Restore failed.",
                    "ChaptarrNG Installer",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _isRunning = false;
            _cts?.Dispose();
            _cts = null;
            SetButtonsForDone();
        }
    }

    // --- IUiReporter (always marshal to UI thread) ---

    public void SetStep(string step, string detail = "")
    {
        Dispatcher.Invoke(() =>
        {
            StepLabel.Text = step;
            DetailLabel.Text = detail;
        });
    }

    public void SetProgressPercent(double percent)
    {
        Dispatcher.Invoke(() =>
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Clamp(percent, 0, 100);
        });
    }

    public void SetProgressIndeterminate()
    {
        Dispatcher.Invoke(() => Progress.IsIndeterminate = true);
    }

    public ProviderIdentityMode? ChooseProviderIdentityMode()
    {
        return Dispatcher.Invoke(() =>
        {
            var result = MessageBox.Show(
                this,
                "BookshelfNG can store bare numeric provider IDs for either native Hardcover or Goodreads records.\n\n" +
                "Choose Yes if the primary Bookshelf catalog uses native Hardcover IDs.\n" +
                "Choose No if the primary catalog uses Goodreads IDs.\n" +
                "If you are unsure, choose Cancel and inspect the BookshelfNG provider settings first.",
                "Choose BookshelfNG provider identity",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            return result switch
            {
                MessageBoxResult.Yes => ProviderIdentityMode.NativeHardcover,
                MessageBoxResult.No => ProviderIdentityMode.Goodreads,
                _ => (ProviderIdentityMode?)null
            };
        });
    }

    public SecureString? RequestServiceAccountPassword(string serviceAccount)
    {
        return Dispatcher.Invoke(() =>
        {
            var passwordBox = new PasswordBox { MinWidth = 360, MaxLength = 4096 };
            SecureString? capturedPassword = null;
            var dialog = new Window
            {
                Title = "BookshelfNG service account",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };

            var content = new StackPanel { Margin = new Thickness(22), MinWidth = 400 };
            content.Children.Add(new TextBlock
            {
                Text = $"Enter the password for the BookshelfNG Windows service account:\n{serviceAccount}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            content.Children.Add(passwordBox);
            content.Children.Add(new TextBlock
            {
                Text = "The password is held in memory for this migration and is not saved or written to logs.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 16)
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancelButton = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
            var continueButton = new Button { Content = "Continue", IsDefault = true, MinWidth = 88 };
            continueButton.Click += (_, _) =>
            {
                if (passwordBox.SecurePassword.Length == 0)
                {
                    MessageBox.Show(dialog, "Enter the service-account password to continue.", "Password required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    passwordBox.Focus();
                    return;
                }

                capturedPassword = passwordBox.SecurePassword.Copy();
                dialog.DialogResult = true;
            };
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(continueButton);
            content.Children.Add(buttons);
            dialog.Content = content;
            dialog.Loaded += (_, _) => passwordBox.Focus();
            dialog.Closed += (_, _) => passwordBox.Clear();

            if (dialog.ShowDialog() != true)
            {
                capturedPassword?.Dispose();
                return null;
            }

            return capturedPassword;
        });
    }

    public bool ConfirmMigrationApply(ProviderIdentityMode mode, string backupPath)
    {
        return Dispatcher.Invoke(() =>
        {
            var providerName = mode == ProviderIdentityMode.NativeHardcover
                ? "native Hardcover IDs"
                : "Goodreads IDs";
            var result = MessageBox.Show(
                this,
                "The read-only preview completed. Review its row counts, mappings, and warnings in the installer log before continuing.\n\n" +
                $"The apply step will modify the BookshelfNG database using {providerName}. The original binaries, config, database, and user backups are saved at:\n\n" +
                backupPath +
                "\n\nAfter apply, the installer switches the Windows service to ChaptarrNG. Continue?",
                "Confirm BookshelfNG migration",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            return result == MessageBoxResult.Yes;
        });
    }

    public void Log(LogLine line)
    {
        if (line.Stream == LogStream.Stderr)
        {
            LogError(line.Text);
        }
        else
        {
            Log(line.Text);
        }
    }

    public void Log(string line) => Dispatcher.Invoke(() => AppendLog(line));
    public void LogError(string line) => Dispatcher.Invoke(() => AppendLogColored(line, Color.FromRgb(0xE8, 0x4A, 0x4A)));

    // --- Internal log rendering ---

    private void AppendLog(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        LogText.Inlines.Add(new Run(line));
        LogText.Inlines.Add(new LineBreak());

        if (_autoScroll)
        {
            LogScroller.ScrollToEnd();
        }
    }

    private void AppendLogColored(string line, Color color)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        LogText.Inlines.Add(new Run(line) { Foreground = new SolidColorBrush(color) });
        LogText.Inlines.Add(new LineBreak());

        if (_autoScroll)
        {
            LogScroller.ScrollToEnd();
        }
    }

    private void LogScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.ExtentHeightChange) < 0.5)
        {
            // User-initiated scroll — update auto-scroll state based on whether they're at the bottom.
            _autoScroll = e.VerticalOffset >= LogScroller.ScrollableHeight - 2;
        }
    }

    // --- Button state machine ---

    private void SetButtonsForIdle()
    {
        InstallButton.IsEnabled = true;
        CancelButton.IsEnabled = false;
        CloseButton.IsEnabled = true;
    }

    private void SetButtonsForRunning()
    {
        InstallButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        CloseButton.IsEnabled = false;
    }

    private void SetButtonsForDone()
    {
        InstallButton.IsEnabled = false;
        if (!_restoreMode && RegistryService.HasRecoverableUpgrade())
        {
            RestoreButton.Visibility = Visibility.Visible;
            RestoreButton.IsEnabled = true;
        }
        else
        {
            RestoreButton.IsEnabled = false;
        }
        CancelButton.IsEnabled = false;
        CloseButton.IsEnabled = true;
    }

    // --- Button handlers ---

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        _isRunning = true;
        _cts = new CancellationTokenSource();
        SetButtonsForRunning();

        try
        {
            var outcome = await Task.Run(() => InstallPipeline.RunUpgradeAsync(this, _cts.Token))
                .ConfigureAwait(true);

            if (!outcome.Success)
            {
                AppendLogColored($"[installer] FAILED: {outcome.ErrorMessage}", Color.FromRgb(0xE8, 0x4A, 0x4A));
                if (!string.IsNullOrWhiteSpace(outcome.BackupPath))
                {
                    AppendLog($"[installer] Backup preserved at: {outcome.BackupPath}");
                }
                MessageBox.Show(this,
                    outcome.ErrorMessage ?? "Installation failed.",
                    "ChaptarrNG Installer",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _isRunning = false;
            _cts?.Dispose();
            _cts = null;
            SetButtonsForDone();
        }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        var confirm = MessageBox.Show(this,
            "Restore BookshelfNG from the verified migration backup? This returns the database to its pre-migration state and removes the Chaptarr service. Chaptarr changes made after migration will be lost.",
            "Confirm BookshelfNG restore",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        await RunRestoreAsync().ConfigureAwait(true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null) return;
        AppendLogColored("[installer] Cancel requested — stopping the current step. Rollback will run if a complete backup exists.", Colors.Goldenrod);
        _cts.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

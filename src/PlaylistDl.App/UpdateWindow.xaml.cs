using System.IO;
using System.Net.Http;
using System.Windows;
using PlaylistDl.App.Services;

namespace PlaylistDl.App;

public partial class UpdateWindow : Window
{
    private readonly UpdateResult _update;
    private readonly UpdateInstaller _installer;
    private readonly string _currentExecutable;
    private readonly string _downloadDirectory;
    private PreparedUpdate? _prepared;
    private CancellationTokenSource? _download;

    public UpdateWindow(
        UpdateResult update,
        Version currentVersion,
        string currentExecutable,
        UpdateInstaller? installer = null)
    {
        InitializeComponent();
        _update = update;
        _installer = installer ?? new UpdateInstaller();
        _currentExecutable = currentExecutable;
        // The tag comes from the release feed, so it never becomes a path of its own.
        var folder = string.Concat(update.Tag.Split(Path.GetInvalidFileNameChars()));
        _downloadDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlaylistDL",
            "updates",
            folder);

        HeadlineText.Text = $"Playlist DL {update.Tag} is available";
        DetailText.Text =
            $"You are running {currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}. " +
            "The download is checked against the checksums published with the release before anything is replaced.";
        if (!update.CanInstall)
        {
            DownloadButton.IsEnabled = false;
            StatusText.Text =
                "This release does not publish a verifiable executable. Use the release page to update manually.";
        }
    }

    /// <summary>Set when the app should close so the freshly installed version can start.</summary>
    public bool RestartRequested { get; private set; }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        DownloadButton.IsEnabled = false;
        InstallButton.IsEnabled = false;
        VerificationPanel.Visibility = Visibility.Collapsed;
        DownloadProgress.Value = 0;
        StatusText.Text = "Downloading…";
        _download?.Dispose();
        _download = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(percent => DownloadProgress.Value = percent);
            _prepared = await _installer.PrepareAsync(
                _update,
                _downloadDirectory,
                progress,
                _download.Token);
            DownloadProgress.Value = 100;
            StatusText.Text =
                $"Downloaded and verified {FormatSize(_prepared.Size)}. " +
                "Installing keeps the current version alongside it until the new one is in place.";
            ChecksumText.Text = _prepared.Sha256.ToLowerInvariant();
            VerificationPanel.Visibility = Visibility.Visible;
            InstallButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // The window is closing; nothing to report.
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or
            InvalidDataException or UnauthorizedAccessException)
        {
            StatusText.Text = exception.Message;
            DownloadButton.IsEnabled = true;
        }
    }

    /// <summary>Stops an in-flight download so its partial file is released with the window.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _download?.Cancel();
        _download?.Dispose();
        _download = null;
        base.OnClosed(e);
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_prepared is null)
        {
            return;
        }

        InstallButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        StatusText.Text = "Installing…";
        try
        {
            UpdateInstaller.Swap(_currentExecutable, _prepared.ExecutablePath);
        }
        catch (UpdateRollbackException rollback)
        {
            // The old binary could not be put back; the message names the file to rename.
            StatusText.Text = rollback.Message;
            return;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text =
                $"The update could not be installed and the current version was kept: {exception.Message}";
            DownloadButton.IsEnabled = true;
            return;
        }

        RestartRequested = true;
        DialogResult = true;
    }

    private void ReleasePageButton_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _update.ReleasePage.AbsoluteUri,
            UseShellExecute = true,
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:F1} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PlaylistDl.App.Models;
using PlaylistDl.App.Services;

namespace PlaylistDl.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CredentialStore _credentials;

    public SettingsWindow(AppSettings settings, CredentialStore? credentials = null)
    {
        InitializeComponent();
        _settings = settings;
        _credentials = credentials ?? new CredentialStore();
        SelectByTag(FormatBox, settings.Format);
        SelectByTag(BitrateBox, settings.Bitrate);
        SelectByTag(ThreadsBox, settings.Threads.ToString());
        CookieFileBox.Text = settings.CookieFile ?? string.Empty;
        BackendExecutableBox.Text = settings.BackendExecutable ?? string.Empty;
        WriteM3uBox.IsChecked = settings.WriteM3u;
        SelectByTag(NamingPresetBox, settings.NamingPreset);
        SelectByTag(DuplicatePolicyBox, settings.DuplicatePolicy);
        CreateSourceFolderBox.IsChecked = settings.CreateSourceFolder;
        SelectByTag(ThrottleBox, settings.ThrottleSeconds.ToString());
        SelectByTag(AutoSyncBox, settings.AutoSyncMinutes.ToString());
        YtDlpArgsBox.Text = settings.YtDlpArgs ?? string.Empty;
        EmbedLyricsBox.IsChecked = settings.EmbedLyrics;
        NormalizeLoudnessBox.IsChecked = settings.NormalizeLoudness;
        VerifyDownloadsBox.IsChecked = settings.VerifyDownloads;
        AutoUpdateCheckBox.IsChecked = settings.AutoUpdateCheck;
        UseOfficialSpotifyBox.IsChecked = settings.UseOfficialSpotifyApi;
        LoadSpotifyCredentials();
        UpdateBitrateAvailability();
    }

    private string SelectedFormat() =>
        (FormatBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mp3";

    private void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateBitrateAvailability();

    private void UpdateBitrateAvailability()
    {
        if (BitrateBox is not null)
        {
            BitrateBox.IsEnabled = SelectedFormat() == "mp3";
        }
    }

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        var match = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
        if (match is not null)
        {
            comboBox.SelectedItem = match;
        }
    }

    private void LoadSpotifyCredentials()
    {
        var stored = _credentials.Load();
        if (stored is not null)
        {
            SpotifyClientIdBox.Text = stored.ClientId;
            SpotifyClientSecretBox.Password = stored.ClientSecret;
        }

        SpotifyCredentialStatus.Text = stored is null
            ? "No credentials stored. They are kept in Windows Credential Manager, never in settings."
            : "Credentials are stored in Windows Credential Manager for this Windows account.";
        UpdateSpotifyCredentialAvailability();
    }

    private void UseOfficialSpotifyBox_Toggled(object sender, RoutedEventArgs e) =>
        UpdateSpotifyCredentialAvailability();

    private void UpdateSpotifyCredentialAvailability()
    {
        var enabled = UseOfficialSpotifyBox.IsChecked == true;
        if (SpotifyClientIdBox is not null)
        {
            SpotifyClientIdBox.IsEnabled = enabled;
            SpotifyClientSecretBox.IsEnabled = enabled;
        }
    }

    private void ChooseCookieButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose Netscape-format YouTube cookie file",
            Filter = "Cookie files (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            CookieFileBox.Text = dialog.FileName;
        }
    }

    private void ClearCookieButton_Click(object sender, RoutedEventArgs e) => CookieFileBox.Clear();

    private void ChooseBackendButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose alternate Playlist DL backend",
            Filter = "Playlist DL backend (playlistdl-backend*.exe)|playlistdl-backend*.exe|Executables (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            BackendExecutableBox.Text = dialog.FileName;
        }
    }

    private void ClearBackendButton_Click(object sender, RoutedEventArgs e) => BackendExecutableBox.Clear();

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var backendExecutable = string.IsNullOrWhiteSpace(BackendExecutableBox.Text)
            ? null
            : BackendExecutableBox.Text.Trim();
        if (backendExecutable is not null && !File.Exists(backendExecutable))
        {
            MessageBox.Show(
                this,
                "The alternate backend executable does not exist. Choose a valid playlistdl-backend.exe or clear the path.",
                "Backend not found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            BackendExecutableBox.Focus();
            return;
        }

        // Everything is validated before a single setting is written, so an aborted save
        // cannot leave half of the dialog applied to the live settings.
        var useOfficial = UseOfficialSpotifyBox.IsChecked == true;
        var clientId = SpotifyClientIdBox.Text.Trim();
        var clientSecret = SpotifyClientSecretBox.Password.Trim();
        if (useOfficial && (clientId.Length == 0 || clientSecret.Length == 0))
        {
            MessageBox.Show(
                this,
                "Enter both the client ID and the client secret, or turn off the official Spotify API.",
                "Spotify credentials incomplete",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SpotifyClientIdBox.Focus();
            return;
        }

        if (useOfficial)
        {
            if (!_credentials.Save(new SpotifyCredentials(clientId, clientSecret)))
            {
                MessageBox.Show(
                    this,
                    "Windows Credential Manager refused to store the credentials.",
                    "Credentials not stored",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            _credentials.Delete();
        }

        _settings.UseOfficialSpotifyApi = useOfficial;
        _settings.Format = SelectedFormat();
        _settings.Bitrate = ((ComboBoxItem)BitrateBox.SelectedItem).Tag?.ToString() ?? "0";
        _settings.Threads = int.Parse(((ComboBoxItem)ThreadsBox.SelectedItem).Tag?.ToString() ?? "2");
        _settings.CookieFile = string.IsNullOrWhiteSpace(CookieFileBox.Text) ? null : CookieFileBox.Text;
        _settings.BackendExecutable = backendExecutable;
        _settings.WriteM3u = WriteM3uBox.IsChecked == true;
        _settings.NamingPreset = ((ComboBoxItem)NamingPresetBox.SelectedItem).Tag?.ToString()
            ?? "position_artist_title";
        _settings.CreateSourceFolder = CreateSourceFolderBox.IsChecked == true;
        _settings.DuplicatePolicy = ((ComboBoxItem)DuplicatePolicyBox.SelectedItem).Tag?.ToString()
            ?? "download";
        _settings.ThrottleSeconds = int.Parse(((ComboBoxItem)ThrottleBox.SelectedItem).Tag?.ToString() ?? "0");
        _settings.AutoSyncMinutes = int.Parse(((ComboBoxItem)AutoSyncBox.SelectedItem).Tag?.ToString() ?? "0");
        _settings.YtDlpArgs = string.IsNullOrWhiteSpace(YtDlpArgsBox.Text) ? null : YtDlpArgsBox.Text.Trim();
        _settings.EmbedLyrics = EmbedLyricsBox.IsChecked == true;
        _settings.NormalizeLoudness = NormalizeLoudnessBox.IsChecked == true;
        _settings.VerifyDownloads = VerifyDownloadsBox.IsChecked == true;
        _settings.AutoUpdateCheck = AutoUpdateCheckBox.IsChecked == true;
        DialogResult = true;
    }
}

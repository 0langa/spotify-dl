using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PlaylistDl.App.Models;

namespace PlaylistDl.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        SelectByTag(FormatBox, settings.Format);
        SelectByTag(BitrateBox, settings.Bitrate);
        SelectByTag(ThreadsBox, settings.Threads.ToString());
        CookieFileBox.Text = settings.CookieFile ?? string.Empty;
        WriteM3uBox.IsChecked = settings.WriteM3u;
        SelectByTag(NamingPresetBox, settings.NamingPreset);
        CreateSourceFolderBox.IsChecked = settings.CreateSourceFolder;
        SelectByTag(ThrottleBox, settings.ThrottleSeconds.ToString());
        YtDlpArgsBox.Text = settings.YtDlpArgs ?? string.Empty;
        EmbedLyricsBox.IsChecked = settings.EmbedLyrics;
        AutoUpdateCheckBox.IsChecked = settings.AutoUpdateCheck;
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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Format = SelectedFormat();
        _settings.Bitrate = ((ComboBoxItem)BitrateBox.SelectedItem).Tag?.ToString() ?? "0";
        _settings.Threads = int.Parse(((ComboBoxItem)ThreadsBox.SelectedItem).Tag?.ToString() ?? "2");
        _settings.CookieFile = string.IsNullOrWhiteSpace(CookieFileBox.Text) ? null : CookieFileBox.Text;
        _settings.WriteM3u = WriteM3uBox.IsChecked == true;
        _settings.NamingPreset = ((ComboBoxItem)NamingPresetBox.SelectedItem).Tag?.ToString()
            ?? "position_artist_title";
        _settings.CreateSourceFolder = CreateSourceFolderBox.IsChecked == true;
        _settings.ThrottleSeconds = int.Parse(((ComboBoxItem)ThrottleBox.SelectedItem).Tag?.ToString() ?? "0");
        _settings.YtDlpArgs = string.IsNullOrWhiteSpace(YtDlpArgsBox.Text) ? null : YtDlpArgsBox.Text.Trim();
        _settings.EmbedLyrics = EmbedLyricsBox.IsChecked == true;
        _settings.AutoUpdateCheck = AutoUpdateCheckBox.IsChecked == true;
        DialogResult = true;
    }
}

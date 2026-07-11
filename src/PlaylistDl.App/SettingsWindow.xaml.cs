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
        SelectByTag(BitrateBox, settings.Bitrate);
        SelectByTag(ThreadsBox, settings.Threads.ToString());
        CookieFileBox.Text = settings.CookieFile ?? string.Empty;
    }

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
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
        _settings.Bitrate = ((ComboBoxItem)BitrateBox.SelectedItem).Tag?.ToString() ?? "0";
        _settings.Threads = int.Parse(((ComboBoxItem)ThreadsBox.SelectedItem).Tag?.ToString() ?? "2");
        _settings.CookieFile = string.IsNullOrWhiteSpace(CookieFileBox.Text) ? null : CookieFileBox.Text;
        DialogResult = true;
    }
}

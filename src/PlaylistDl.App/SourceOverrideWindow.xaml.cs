using System.Windows;
using PlaylistDl.App.Models;

namespace PlaylistDl.App;

public partial class SourceOverrideWindow : Window
{
    public SourceOverrideWindow(TrackItem track)
    {
        InitializeComponent();
        TrackTitle.Text = $"{track.Title} — {track.ArtistText}";
        SourceUrlBox.Text = track.SourceOverride ?? string.Empty;
        SourceUrlBox.Focus();
        SourceUrlBox.SelectAll();
    }

    public string? SourceUrl { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var value = SourceUrlBox.Text.Trim();
        if (value.Length > 0 && !IsSupportedSource(value))
        {
            ValidationText.Text = "Use an https://youtube.com, https://music.youtube.com, or https://youtu.be URL.";
            return;
        }

        SourceUrl = value.Length == 0 ? null : value;
        DialogResult = true;
    }

    internal static bool IsSupportedSource(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
    }
}

using System.Windows;
using PlaylistDl.App.Services;

namespace PlaylistDl.App;

public partial class RunLogWindow : Window
{
    private readonly string _path;

    public RunLogWindow(string path)
    {
        InitializeComponent();
        _path = path;
        LogPathBox.Text = path;
        RefreshLog();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshLog();

    private void RefreshLog()
    {
        LogTextBox.Text = RunLogReader.Read(_path);
        LogTextBox.ScrollToEnd();
        StatusText.Text = $"Updated {DateTime.Now:T}";
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogTextBox.Text);
            StatusText.Text = "Copied to clipboard";
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = "Clipboard is busy. Try again.";
        }
    }
}

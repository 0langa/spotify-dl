using System.Windows;
using PlaylistDl.App.Models;
using PlaylistDl.App.Services;

namespace PlaylistDl.App;

public sealed record LibraryEntry(SavedJob Job, string Name, string Subtitle, string ProgressLabel, string UpdatedLabel)
{
    public static LibraryEntry From(SavedJob job)
    {
        var done = job.Tracks.Count(track => track.IsComplete);
        var typeLabel = job.SourceType switch
        {
            "album" => "Album",
            "track" => "Track",
            "import" => "Import",
            _ => "Playlist",
        };
        return new LibraryEntry(
            job,
            string.IsNullOrWhiteSpace(job.SourceName) ? job.SourceUrl : job.SourceName,
            $"{typeLabel} · {job.SourceUrl}",
            $"{done}/{job.Tracks.Count} done",
            job.UpdatedAt.LocalDateTime.ToString("g"));
    }
}

public partial class LibraryWindow : Window
{
    private readonly LibraryStore _library;

    public LibraryWindow(LibraryStore library)
    {
        InitializeComponent();
        _library = library;
        Reload();
    }

    /// <summary>Set when the caller should open a job; null means plain close.</summary>
    public SavedJob? SelectedJob { get; private set; }

    public bool SyncRequested { get; private set; }

    private void Reload()
    {
        JobsList.ItemsSource = _library.List().Select(LibraryEntry.From).ToList();
    }

    private LibraryEntry? Selected => JobsList.SelectedItem as LibraryEntry;

    private void OpenButton_Click(object sender, RoutedEventArgs e) => Choose(sync: false);

    private void SyncButton_Click(object sender, RoutedEventArgs e) => Choose(sync: true);

    private void JobsList_MouseDoubleClick(object sender, RoutedEventArgs e) => Choose(sync: false);

    private void Choose(bool sync)
    {
        if (Selected is null)
        {
            return;
        }

        SelectedJob = Selected.Job;
        SyncRequested = sync;
        DialogResult = true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            $"Remove \"{Selected.Name}\" from the library? Downloaded files stay on disk.",
            "Delete saved job",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmed == MessageBoxResult.Yes)
        {
            _library.Delete(Selected.Job.SourceUrl);
            Reload();
        }
    }
}

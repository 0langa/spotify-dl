using System.IO;
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
            "search" => "Search",
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

    /// <summary>Compares the selected job's downloaded files against the disk.</summary>
    private async void CheckFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null)
        {
            return;
        }

        var entry = Selected;
        await RunCheckAsync(async scanner =>
        {
            // A large library means thousands of file probes; keep the window responsive.
            var job = entry.Job;
            var report = await Task.Run(() => scanner.Scan(job, job.OutputDirectory));
            HealthText.Text = $"{entry.Name}: {report.Summary}";
            if (!report.IsHealthy)
            {
                await OfferRepairAsync(scanner, entry, report);
            }
        });
    }

    /// <summary>Checks every saved job at once and lists the ones that need attention.</summary>
    private async void CheckAllButton_Click(object sender, RoutedEventArgs e) =>
        await RunCheckAsync(async scanner =>
        {
            var reports = await Task.Run(() => scanner.ScanAll());
            var damaged = reports.Where(report => !report.IsHealthy).ToList();
            HealthText.Text = damaged.Count == 0
                ? $"All {reports.Count} saved jobs match what is on disk."
                : "Needs attention — select a job and press Check files to repair it:" +
                    Environment.NewLine +
                    string.Join(
                        Environment.NewLine,
                        damaged.Select(report =>
                            $"• {LibraryEntry.From(report.Job).Name}: {report.Summary}"));
        });

    /// <summary>Runs one check with the buttons disabled and the list refreshed afterwards.</summary>
    private async Task RunCheckAsync(Func<LibraryHealthScanner, Task> check)
    {
        CheckFilesButton.IsEnabled = false;
        CheckAllButton.IsEnabled = false;
        HealthText.Visibility = Visibility.Visible;
        HealthText.Text = "Checking files…";
        try
        {
            await check(new LibraryHealthScanner(_library));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            HealthText.Text = $"The files could not be checked: {exception.Message}";
        }
        finally
        {
            CheckFilesButton.IsEnabled = true;
            CheckAllButton.IsEnabled = true;
            // Repairs change the done counts, so the list is rebuilt around the same job.
            var selected = (JobsList.SelectedItem as LibraryEntry)?.Job.SourceUrl;
            Reload();
            if (selected is not null)
            {
                JobsList.SelectedItem = JobsList.Items
                    .OfType<LibraryEntry>()
                    .FirstOrDefault(item => item.Job.SourceUrl == selected);
            }
        }
    }

    private async Task OfferRepairAsync(
        LibraryHealthScanner scanner, LibraryEntry entry, LibraryHealthReport report)
    {
        if (report.Moved > 0)
        {
            var relocate = MessageBox.Show(
                this,
                $"{report.Moved} files were found in a new place under " +
                $"{report.Job.OutputDirectory}. Point the library at them?",
                "Files moved",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (relocate == MessageBoxResult.Yes)
            {
                var moved = await Task.Run(() => scanner.Relocate(report));
                HealthText.Text = $"{entry.Name}: {moved} files relocated.";
            }
        }

        if (report.Missing == 0)
        {
            return;
        }

        var forget = MessageBox.Show(
            this,
            $"{report.Missing} files are missing or empty. Mark those tracks unfinished so " +
            "they can be downloaded again? Nothing is downloaded yet.",
            "Files missing",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (forget != MessageBoxResult.Yes)
        {
            return;
        }

        var reopened = await Task.Run(() => scanner.ForgetMissing(report));
        HealthText.Text = $"{entry.Name}: {reopened} tracks reopened.";
        if (reopened == 0)
        {
            return;
        }

        var open = MessageBox.Show(
            this,
            $"Open \"{entry.Name}\" now with those {reopened} tracks selected?",
            "Download again",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (open == MessageBoxResult.Yes)
        {
            SelectedJob = _library.Load(report.Job.SourceUrl) ?? report.Job;
            SyncRequested = false;
            DialogResult = true;
        }
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

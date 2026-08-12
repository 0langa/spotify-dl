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
    private readonly HashSet<string> _repaired = new(StringComparer.OrdinalIgnoreCase);
    private bool _checking;
    private bool _closed;

    public LibraryWindow(LibraryStore library)
    {
        InitializeComponent();
        _library = library;
        Reload();
    }

    /// <summary>Set when the caller should open a job; null means plain close.</summary>
    public SavedJob? SelectedJob { get; private set; }

    public bool SyncRequested { get; private set; }

    /// <summary>Sources whose library entry this window changed, for the caller to refresh.</summary>
    public IReadOnlyCollection<string> RepairedSources => _repaired;

    protected override void OnClosed(EventArgs e)
    {
        // A check that is still running resumes on the dispatcher after this point and
        // must not touch the closed window.
        _closed = true;
        base.OnClosed(e);
    }

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
        if (Selected is null || _checking)
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
            var report = await Task.Run(() => scanner.Scan(entry.Job));
            if (_closed)
            {
                return;
            }

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
            var reports = await Task.Run(scanner.ScanAll);
            if (_closed)
            {
                return;
            }

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

    /// <summary>
    /// Runs one check with the window locked, because a repair writes the job the other
    /// buttons act on.
    /// </summary>
    private async Task RunCheckAsync(Func<LibraryHealthScanner, Task> check)
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        SetButtonsEnabled(false);
        HealthText.Visibility = Visibility.Visible;
        HealthText.Text = "Checking files…";
        try
        {
            await check(new LibraryHealthScanner(_library));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            if (!_closed)
            {
                HealthText.Text = $"The files could not be checked: {exception.Message}";
            }
        }
        finally
        {
            _checking = false;
            if (!_closed)
            {
                SetButtonsEnabled(true);
                RefreshKeepingSelection();
            }
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        CheckFilesButton.IsEnabled = enabled;
        CheckAllButton.IsEnabled = enabled;
        DeleteButton.IsEnabled = enabled;
        OpenButton.IsEnabled = enabled;
        SyncButton.IsEnabled = enabled;
    }

    /// <summary>Rebuilds the list after a repair changed the counts, keeping the same job.</summary>
    private void RefreshKeepingSelection()
    {
        var selected = (JobsList.SelectedItem as LibraryEntry)?.Job.SourceUrl;
        Reload();
        if (selected is not null)
        {
            JobsList.SelectedItem = JobsList.Items
                .OfType<LibraryEntry>()
                .FirstOrDefault(item => item.Job.SourceUrl == selected);
        }
    }

    private async Task OfferRepairAsync(
        LibraryHealthScanner scanner, LibraryEntry entry, LibraryHealthReport report)
    {
        if (report.Moved > 0)
        {
            var relocate = MessageBox.Show(
                this,
                $"{report.Moved} files were found somewhere else under " +
                $"{report.Job.OutputDirectory}. Point the library at them? Only files whose " +
                "name occurs once and that no other saved job uses are matched.",
                "Files moved",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (relocate == MessageBoxResult.Yes)
            {
                var moved = await Repair(
                    report.Job.SourceUrl,
                    () => scanner.Relocate(report),
                    count => $"{entry.Name}: {count} files relocated.");
                if (_closed || moved is null)
                {
                    return;
                }
            }
        }

        if (report.Missing == 0)
        {
            return;
        }

        if (!report.CanReopenMissing)
        {
            // Everything the scan could not see looks missing, so offering to reopen those
            // tracks would throw away paths to files that are still there.
            HealthText.Text =
                $"{entry.Name}: {report.Summary}. Reconnect the folder and check again — " +
                "nothing was changed.";
            return;
        }

        var forget = MessageBox.Show(
            this,
            $"{report.Missing} files are missing or empty. Mark those tracks unfinished so " +
            "they can be downloaded again? Empty leftover files are deleted; nothing else " +
            "on disk is touched and nothing is downloaded yet.",
            "Files missing",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (forget != MessageBoxResult.Yes)
        {
            return;
        }

        var reopened = await Repair(
            report.Job.SourceUrl,
            () => scanner.ForgetMissing(report),
            count => count == 0
                ? $"{entry.Name}: nothing was reopened — the saved job changed while the check ran."
                : $"{entry.Name}: {count} tracks reopened.");
        if (_closed || reopened is null or 0)
        {
            return;
        }

        var open = MessageBox.Show(
            this,
            $"Open \"{entry.Name}\" now so those {reopened} tracks can be downloaded again?",
            "Download again",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (open == MessageBoxResult.Yes && !_closed)
        {
            SelectedJob = _library.Load(report.Job.SourceUrl) ?? report.Job;
            SyncRequested = false;
            DialogResult = true;
        }
    }

    /// <summary>
    /// Runs one repair off the UI thread. A failed write is reported as a failed repair,
    /// not as a failed check, because the two leave the library in different states.
    /// </summary>
    private async Task<int?> Repair(
        string sourceUrl, Func<int> repair, Func<int, string> describe)
    {
        try
        {
            var count = await Task.Run(repair);
            if (count > 0)
            {
                // The main window holds its own copy of this job and would write it back.
                _repaired.Add(sourceUrl);
            }

            if (!_closed)
            {
                HealthText.Text = describe(count);
            }

            return count;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            if (!_closed)
            {
                HealthText.Text = $"The library could not be written: {exception.Message}";
            }

            return null;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null || _checking)
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

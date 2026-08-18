using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
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
        var subtitle = $"{typeLabel} · {job.SourceUrl}";
        if (job.AutoSync)
        {
            subtitle += job.LastAutoSyncUtc is { } checkedAt
                ? $" · keeps in sync, last checked {checkedAt.LocalDateTime:g}"
                : " · keeps in sync";
        }

        return new LibraryEntry(
            job,
            string.IsNullOrWhiteSpace(job.SourceName) ? job.SourceUrl : job.SourceName,
            subtitle,
            $"{done}/{job.Tracks.Count} done",
            job.UpdatedAt.LocalDateTime.ToString("g"));
    }
}

public partial class LibraryWindow : Window
{
    private readonly LibraryStore _library;
    private readonly int _autoSyncMinutes;
    private readonly HashSet<string> _repaired = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);
    private bool _checking;
    private bool _closed;
    private bool _repairing;
    private bool _closeRequested;

    public LibraryWindow(LibraryStore library, int autoSyncMinutes = 0)
    {
        InitializeComponent();
        _library = library;
        _autoSyncMinutes = autoSyncMinutes;
        Reload();
    }

    /// <summary>Set when the caller should open a job; null means plain close.</summary>
    public SavedJob? SelectedJob { get; private set; }

    public bool SyncRequested { get; private set; }

    /// <summary>Sources whose library entry this window changed, for the caller to refresh.</summary>
    public IReadOnlyCollection<string> RepairedSources => _repaired;

    /// <summary>Sources this window removed, so their queued work can be dropped too.</summary>
    public IReadOnlyCollection<string> DeletedSources => _deleted;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The caller reads RepairedSources as soon as this window closes and then reloads
        // the library, so a repair that is still being written has to land first.
        if (_repairing)
        {
            e.Cancel = true;
            _closeRequested = true;
            HealthText.Text = "Finishing the repair…";
            return;
        }

        base.OnClosing(e);
    }

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

    private void JobsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowAutoSyncState();

    /// <summary>Mirrors the selected job's auto-sync flag onto the toggle.</summary>
    private void ShowAutoSyncState()
    {
        AutoSyncToggle.IsEnabled = Selected is not null && !_checking;
        AutoSyncToggle.IsChecked = Selected?.Job.AutoSync == true;
    }

    /// <summary>Turns unattended syncing on or off for the selected source.</summary>
    private void AutoSyncToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null || _checking)
        {
            ShowAutoSyncState();
            return;
        }

        var entry = Selected;
        var wanted = AutoSyncToggle.IsChecked == true;
        if (wanted && !AutoSyncScheduler.CanAutoSync(entry.Job))
        {
            AutoSyncToggle.IsChecked = false;
            HealthPanel.Visibility = Visibility.Visible;
            HealthText.Text = string.Equals(
                entry.Job.SourceType, "import", StringComparison.OrdinalIgnoreCase)
                ? $"{entry.Name}: an imported manifest cannot be synced on its own, " +
                    "because its source is a file on this machine."
                : $"{entry.Name}: its output folder {entry.Job.OutputDirectory} is not " +
                    "available, so it cannot be synced on its own.";
            return;
        }

        try
        {
            // Written on the entry as it is on disk: this window may have been open for a
            // while, and only this one field is being changed.
            var job = _library.Load(entry.Job.SourceUrl) ?? entry.Job;
            job.AutoSync = wanted;
            _library.Save(job);
            entry.Job.AutoSync = wanted;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            HealthPanel.Visibility = Visibility.Visible;
            HealthText.Text = $"The library could not be written: {exception.Message}";
            ShowAutoSyncState();
            return;
        }

        HealthPanel.Visibility = Visibility.Visible;
        HealthText.Text = wanted
            ? _autoSyncMinutes > 0
                ? $"{entry.Name}: checked for new tracks every " +
                    $"{IntervalText(_autoSyncMinutes)} while the app is open."
                : $"{entry.Name}: marked to keep in sync. Auto-sync is off in Settings, " +
                    "so nothing is checked until an interval is chosen there."
            : $"{entry.Name}: no longer checked on its own.";
    }

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

            report = await OfferToLocateFolderAsync(scanner, entry, report);
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

            if (reports.Count == 0)
            {
                // An unreadable library reads as an empty one, so this cannot claim health.
                HealthText.Text = "No saved jobs were found to check.";
                return;
            }

            var damaged = reports
                .Where(report => !report.IsHealthy || !report.ScanComplete)
                .ToList();
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
        HealthPanel.Visibility = Visibility.Visible;
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
        AutoSyncToggle.IsEnabled = enabled && Selected is not null;
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

        ShowAutoSyncState();
    }

    /// <summary>
    /// Offers to search a folder the user picks when the job's own output folder is gone.
    /// </summary>
    /// <remarks>
    /// Moving the music folder is the most common way a library goes stale, and from the
    /// old path nothing can be found: every file reads as unreachable and no repair is
    /// possible until the check is pointed at the new folder.
    /// </remarks>
    private async Task<LibraryHealthReport> OfferToLocateFolderAsync(
        LibraryHealthScanner scanner, LibraryEntry entry, LibraryHealthReport report)
    {
        if (report.RootAvailable || report.Unreachable == 0)
        {
            return report;
        }

        HealthText.Text = $"{entry.Name}: {report.Summary}";
        var locate = MessageBox.Show(
            this,
            $"{report.Job.OutputDirectory} is not available, so {report.Unreachable} files " +
            "cannot be checked. Look for them in another folder?",
            "Folder not available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (locate != MessageBoxResult.Yes || _closed)
        {
            return report;
        }

        var dialog = new OpenFolderDialog
        {
            Title = $"Where are the files of {entry.Name} now?",
            Multiselect = false,
        };
        try
        {
            if (dialog.ShowDialog(this) != true)
            {
                return report;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or COMException)
        {
            HealthText.Text = "The folder picker could not open.";
            return report;
        }

        var chosen = dialog.FolderName;
        HealthText.Text = $"Checking {chosen}…";
        return await Task.Run(() => scanner.Scan(entry.Job, chosen));
    }

    private async Task OfferRepairAsync(
        LibraryHealthScanner scanner, LibraryEntry entry, LibraryHealthReport report)
    {
        if (report.Moved > 0)
        {
            var relocate = MessageBox.Show(
                this,
                $"{LibraryHealthReport.Files(report.Moved)} were found somewhere else under " +
                $"{report.Root ?? report.Job.OutputDirectory}. Point the library at them? " +
                "Only files whose name occurs once and that no other saved job uses are " +
                "matched.",
                "Files moved",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (relocate == MessageBoxResult.Yes)
            {
                var moved = await Repair(
                    report.Job.SourceUrl,
                    () => scanner.Relocate(report),
                    count => $"{entry.Name}: {LibraryHealthReport.Files(count)} relocated.");
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
                $"{entry.Name}: {report.Summary}. The missing files were left alone — " +
                "make the folder readable and check again.";
            return;
        }

        var forget = MessageBox.Show(
            this,
            $"{LibraryHealthReport.Files(report.Missing)} are missing or empty. Mark those " +
            "tracks unfinished so " +
            "they can be downloaded again? Empty leftover files are deleted and the saved " +
            "job, the resume point, and queued jobs for it are updated; no other file on " +
            "disk is touched and nothing is downloaded yet.",
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
                : $"{entry.Name}: {LibraryHealthReport.Tracked(count)} reopened.");
        if (_closed || reopened is null or 0)
        {
            return;
        }

        var open = MessageBox.Show(
            this,
            $"Open \"{entry.Name}\" now so those {LibraryHealthReport.Tracked(reopened.Value)} " +
            "can be downloaded again?",
            "Download again",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (open != MessageBoxResult.Yes || _closed)
        {
            return;
        }

        // Never the pre-repair snapshot the scan started from: restoring that would write
        // the reopened tracks back as complete.
        if (_library.Load(report.Job.SourceUrl) is { } repaired)
        {
            SelectedJob = repaired;
            SyncRequested = false;
            DialogResult = true;
        }
        else
        {
            HealthText.Text = $"{entry.Name}: the repaired job could not be read back.";
        }
    }

    /// <summary>
    /// Runs one repair off the UI thread. A failed write is reported as a failed repair,
    /// not as a failed check, because the two leave the library in different states.
    /// </summary>
    private async Task<int?> Repair(
        string sourceUrl, Func<int> repair, Func<int, string> describe)
    {
        _repairing = true;
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
        finally
        {
            _repairing = false;
            if (_closeRequested)
            {
                Close();
            }
        }
    }

    private static string IntervalText(int minutes) => minutes switch
    {
        < 60 => $"{minutes} minutes",
        60 => "hour",
        < 1440 => $"{minutes / 60} hours",
        1440 => "day",
        _ => $"{minutes / 1440} days",
    };

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
            _deleted.Add(Selected.Job.SourceUrl);
            _library.Delete(Selected.Job.SourceUrl);
            Reload();
        }
    }
}

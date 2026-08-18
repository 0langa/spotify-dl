using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using PlaylistDl.App.Models;
using PlaylistDl.App.Services;

namespace PlaylistDl.App;

public partial class MainWindow : Window
{
    private readonly BackendClient _backend;
    private readonly SettingsService _settingsService = new();
    private readonly JobStore _jobStore = new();
    private readonly LibraryStore _library = new();
    private readonly QueueStore _queueStore = new();
    private readonly CredentialStore _credentialStore = new();
    private readonly List<QueueJobSummary> _queueReport = [];
    private const int PreparationFailureLimit = 3;
    private readonly UpdateService _updateService = new();
    private readonly AppSettings _settings;
    private readonly ICollectionView _tracksView;
    private PlaylistInfo? _playlist;
    private bool _jobRunning;
    private bool _shuttingDown;
    private bool _closeApproved;
    private string? _relaunchAfterClose;
    private bool _bulkSelectionUpdate;
    private bool _syncingSelectAll;
    private bool _syncingQuickFormat;
    private bool _uiReady;
    private HashSet<string> _activeTrackIds = [];
    private readonly Dictionary<string, TrackItem> _tracksById = [];
    private readonly List<TrackItem> _failedTracks = [];
    private readonly DownloadQueue _queue = new();
    private bool _queueRunning;

    /// <summary>Checks once a minute whether a source marked "keep in sync" is due.</summary>
    private readonly DispatcherTimer _autoSyncTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private QueuedJob? _activeQueuedJob;
    private CancellationTokenSource? _sourceOperationCts;
    private SavedJob? _savedJob;
    private UpdateResult? _availableUpdate;
    private string? _jobProgressText;

    public RangeObservableCollection<TrackItem> Tracks { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsService.Load();
        _backend = new BackendClient(() => _settings.BackendExecutable);
        DataContext = this;
        OutputDirectoryBox.Text = _settings.OutputDirectory;
        SyncQuickFormat();
        _tracksView = CollectionViewSource.GetDefaultView(Tracks);
        _tracksView.Filter = TrackPassesFilter;
        _backend.EventReceived += Backend_EventReceived;
        _backend.DiagnosticReceived += (_, _) => Dispatcher.Invoke(() =>
        {
            if (!_jobRunning)
            {
                StatusText.Text = "Backend warning — open Run log for exact details";
            }
        });
        _backend.OutdatedBackendRejected += (_, rejectedPath) => Dispatcher.Invoke(() =>
        {
            if (string.Equals(
                _settings.BackendExecutable,
                rejectedPath,
                StringComparison.OrdinalIgnoreCase))
            {
                _settings.BackendExecutable = null;
                try
                {
                    _settingsService.Save(_settings);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    StatusText.Text = $"Outdated backend replaced, but settings could not be repaired: {exception.Message}";
                    return;
                }
            }
            StatusText.Text = "Outdated alternate backend replaced with bundled backend";
        });
        _uiReady = true;
        if (Environment.ProcessPath is { Length: > 0 } runningExecutable)
        {
            UpdateInstaller.CleanupRetired(runningExecutable);
            UpdateInstaller.CleanupDownloads(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PlaylistDL",
                "updates"));
        }
        // Subscribed after the restore so a queue.json that could not be read is never
        // overwritten with an empty queue before the user changes anything.
        _queue.Replace(_queueStore.Load());
        _queue.Changed += (_, _) => PersistQueue();
        UpdateQueueUi();
        _savedJob = _jobStore.Load();
        _library.MigrateFromLastJob(_savedJob);
        ResumeButton.Visibility = _savedJob is null ? Visibility.Collapsed : Visibility.Visible;
        if (_savedJob is not null)
        {
            ResumeButton.ToolTip = $"Resume {_savedJob.SourceName} from {_savedJob.UpdatedAt.LocalDateTime:g}";
        }
        if (_settingsService.LastLoadFailed)
        {
            StatusText.Text =
                "Saved settings could not be read and defaults are in use — " +
                "the unreadable file is kept as settings.json.unreadable when settings are saved.";
        }
        Closing += MainWindow_Closing;
        Loaded += async (_, _) => await AutoCheckForUpdatesAsync();
        _autoSyncTimer.Tick += (_, _) => AutoSyncTick();
        _autoSyncTimer.Start();
    }

    /// <summary>Stops the backend before the window really closes.</summary>
    /// <remarks>
    /// Shutdown work started in <c>Closed</c> can be cut off by dispatcher shutdown and
    /// leave the backend and its converter children running, so the close is deferred
    /// until the backend session has ended.
    /// </remarks>
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _sourceOperationCts?.Cancel();
        if (_closeApproved)
        {
            return;
        }

        e.Cancel = true;
        if (_shuttingDown)
        {
            // Clicking close again must not abandon the shutdown already in flight.
            return;
        }

        _shuttingDown = true;
        // No further input while the session is being torn down: a click here would
        // otherwise relaunch the backend that was just stopped.
        IsEnabled = false;
        StatusText.Text = "Closing — stopping backend…";
        SaveCurrentJob();
        try
        {
            await _backend.DisposeAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The window must close even if the backend refuses to stop cleanly.
        }

        if (_relaunchAfterClose is { Length: > 0 } updated && !UpdateInstaller.Relaunch(updated))
        {
            MessageBox.Show(
                this,
                $"The update is installed. Start it again from {updated}.",
                "Update installed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        _closeApproved = true;
        Close();
    }

    private async Task AutoCheckForUpdatesAsync()
    {
        if (!UpdateService.ShouldAutoCheck(_settings.AutoUpdateCheck, _settings.LastUpdateCheckUtc, DateTimeOffset.UtcNow))
        {
            return;
        }

        try
        {
            var current = CurrentVersion();
            _availableUpdate = await _updateService.CheckAsync(current);
            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            TrySaveSettings();
            if (_availableUpdate is not null)
            {
                UpdateButton.Content = $"Get {_availableUpdate.Tag}";
                UpdateButton.ToolTip = "A newer release is available — click to open it";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException or InvalidDataException)
        {
            // Startup check stays silent; the manual button still reports problems.
        }
    }

    private bool TrackPassesFilter(object item)
    {
        var query = FilterBox.Text.Trim();
        if (query.Length == 0 || item is not TrackItem track)
        {
            return true;
        }

        return track.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || track.ArtistText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || track.Album.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _tracksView.Refresh();
        // Select all describes the filtered rows, so a new filter changes its state.
        UpdateSelectionUi();
    }

    private void SelectAllBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _syncingSelectAll)
        {
            return;
        }

        var selected = SelectAllBox.IsChecked == true;
        // One selection pass instead of one full recount per track keeps large
        // playlists responsive; the UI is refreshed once at the end.
        _bulkSelectionUpdate = true;
        try
        {
            foreach (var track in _tracksView.Cast<TrackItem>().ToList())
            {
                track.IsSelected = selected;
            }
        }
        finally
        {
            _bulkSelectionUpdate = false;
        }

        UpdateSelectionUi();
    }

    private void Track_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackItem.IsSelected) && !_bulkSelectionUpdate)
        {
            UpdateSelectionUi();
        }
    }

    private void UpdateSelectionUi()
    {
        var selected = 0;
        var visible = 0;
        var visibleSelected = 0;
        foreach (var track in Tracks)
        {
            if (track.IsSelected)
            {
                selected++;
            }
        }

        // Select all applies to the filtered rows, so its state must describe them too.
        foreach (var track in _tracksView.Cast<TrackItem>())
        {
            visible++;
            if (track.IsSelected)
            {
                visibleSelected++;
            }
        }

        _syncingSelectAll = true;
        SelectAllBox.IsChecked = visible > 0 && visibleSelected == visible
            ? true
            : visibleSelected == 0 ? false : null;
        _syncingSelectAll = false;
        var idle = !_jobRunning && !_queueRunning && _sourceOperationCts is null;
        DownloadButton.IsEnabled = idle && ((selected > 0 && _playlist is not null) || !_queue.IsEmpty);
        AddToQueueButton.IsEnabled = idle && selected > 0 && _playlist is not null;
        if (_playlist is not null)
        {
            PlaylistSummary.Text = $"{SourceLabel()} · {selected}/{Tracks.Count} tracks selected · {_playlist.Owner}";
        }
    }

    /// <summary>Retry stays available only when a job is not already using the backend.</summary>
    private void UpdateRetryUi()
    {
        RetryFailedButton.Visibility = _failedTracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetryFailedButton.IsEnabled = _failedTracks.Count > 0 && !_jobRunning;
        RetryFailedButton.Content = $"Retry {_failedTracks.Count} failed";
    }

    /// <summary>Source intake stays disabled while a job owns the backend session.</summary>
    private void UpdateSourceIntakeAvailability()
    {
        // A queue run owns the backend session from its first re-resolve onwards.
        var idle = !_jobRunning && !_queueRunning && _sourceOperationCts is null;
        AnalyzeButton.IsEnabled = idle;
        ImportManifestButton.IsEnabled = idle;
        LibraryButton.IsEnabled = idle;
        ResumeButton.IsEnabled = idle;
        UpdateRetryUi();
    }

    private string SourceLabel() => _playlist?.SourceType switch
    {
        "album" => "album",
        "track" => "track",
        "import" => "import",
        "search" => "search",
        _ => "playlist",
    };

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        var input = PlaylistUrlBox.Text.Trim();
        var isSpotifyUrl = SpotifyInput.TryNormalize(input, out var url);
        if (!isSpotifyUrl)
        {
            if (input.Length == 0 || input.Contains("://", StringComparison.Ordinal))
            {
                MessageBox.Show(
                    this,
                    "Paste a Spotify playlist, album, or track URL — or type an artist and title to search.",
                    "Invalid input",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            PlaylistUrlBox.Text = url;
        }

        using var operation = BeginSourceOperation(isSpotifyUrl ? "Resolving playlist…" : "Searching YouTube Music…");
        if (operation is null)
        {
            return;
        }
        try
        {
            if (isSpotifyUrl)
            {
                await ResolveAsync(url, cancellationToken: operation.Token);
            }
            else
            {
                await SearchAsync(input.StartsWith("search:", StringComparison.OrdinalIgnoreCase)
                    ? input["search:".Length..]
                    : input, cancellationToken: operation.Token);
            }
            SaveCurrentJob();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Source operation cancelled";
        }
        catch (Exception ex)
        {
            var userMessage = UserErrorMessages.ForSourceResolution(ex, isSpotifyUrl);
            MessageBox.Show(this, userMessage, isSpotifyUrl ? "Playlist failed" : "Search failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = isSpotifyUrl ? "Playlist resolution failed" : "Search failed";
            // The banner carries the Diagnose button, so it must also appear for the
            // search path — a blocked backend fails both intakes the same way.
            ShowFailureBanner(isSpotifyUrl
                ? userMessage + " You can also type an artist and title to search, or import a CSV/JSON manifest."
                : userMessage + " You can also paste a Spotify link or import a CSV/JSON manifest.");
        }
        finally
        {
            EndSourceOperation(operation);
        }
    }

    private async Task SearchAsync(
        string query,
        SavedJob? restore = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _backend.RequestAsync("resolve_search", new { query, limit = 12 }, cancellationToken);
        ApplyResolvedPlaylist(response, restore);
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_queue.IsEmpty)
        {
            if (!_queueRunning)
            {
                // Each run reports on itself; the previous run's report is replaced.
                _queueReport.Clear();
            }
            await RunQueueAsync();
            return;
        }

        var selectedTracks = Tracks.Where(track => track.IsSelected).ToList();
        if (_playlist is null || selectedTracks.Count == 0)
        {
            return;
        }

        foreach (var track in selectedTracks)
        {
            track.Progress = 0;
            track.Status = "Queued";
            track.ErrorText = null;
        }

        await StartJobAsync(selectedTracks);
    }

    private void AddToQueueButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedTracks = Tracks.Where(track => track.IsSelected).ToList();
        if (_playlist is null || selectedTracks.Count == 0)
        {
            return;
        }

        _queue.Enqueue(new QueuedJob(
            _playlist.Name,
            _playlist.SourceUrl,
            _playlist.SourceType,
            OutputDirectoryBox.Text,
            QueuedJobSettings.From(_settings),
            SavedJobSnapshot.Create(
                _playlist.SourceUrl,
                _playlist.Name,
                _playlist.SourceType,
                OutputDirectoryBox.Text,
                Tracks))
        {
            PlaylistId = _playlist.Id,
            AllTracks = [.. Tracks],
            Tracks = selectedTracks,
        });
        foreach (var track in selectedTracks)
        {
            track.Progress = 0;
            track.Status = "Queued";
            track.ErrorText = null;
        }

        UpdateQueueUi();
        StatusText.Text = $"Added \"{_playlist.Name}\" to the queue — resolve another source or start the queue";
    }

    /// <summary>
    /// Enqueues the sources that are due for an unattended sync, and starts the queue.
    /// </summary>
    /// <remarks>
    /// Auto-sync is the ordinary queue with the ordinary sync semantics: each job is
    /// re-resolved when it runs and only new or unfinished tracks are selected. It only
    /// runs while nothing else does, and it only runs while the app is open.
    /// </remarks>
    private void AutoSyncTick()
    {
        if (_settings.AutoSyncMinutes <= 0 || _jobRunning || _queueRunning ||
            _sourceOperationCts is not null || !_uiReady)
        {
            return;
        }

        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var queued in _queue.Items)
        {
            busy.Add(queued.SourceUrl);
        }
        if (_playlist?.SourceUrl is { Length: > 0 } loaded)
        {
            // The grid owns that source; syncing it in the background would fight the user.
            busy.Add(loaded);
        }

        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<SavedJob> due;
        try
        {
            due = AutoSyncScheduler.Due(
                _library.List(), TimeSpan.FromMinutes(_settings.AutoSyncMinutes), now, busy);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var added = 0;
        foreach (var job in due)
        {
            try
            {
                AutoSyncScheduler.MarkChecked(_library, job.SourceUrl, now);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Without the stamp the source would be picked again on the next tick.
                continue;
            }

            _queue.Enqueue(new QueuedJob(
                string.IsNullOrWhiteSpace(job.SourceName) ? job.SourceUrl : job.SourceName,
                job.SourceUrl,
                job.SourceType,
                string.IsNullOrWhiteSpace(job.OutputDirectory)
                    ? OutputDirectoryBox.Text
                    : job.OutputDirectory,
                QueuedJobSettings.From(_settings),
                job));
            added++;
        }

        if (added == 0)
        {
            return;
        }

        UpdateQueueUi();
        StatusText.Text = added == 1
            ? "Auto-sync: checking 1 source for new tracks"
            : $"Auto-sync: checking {added} sources for new tracks";
        _ = RunQueueAsync();
    }

    private void UpdateQueueUi()
    {
        DownloadButton.Content = _queue.IsEmpty
            ? "Download audio"
            : $"Start queue ({_queue.Count})";
        QueueButton.Content = _queue.IsEmpty ? "Queue" : $"Queue ({_queue.Count})";
        QueueButton.IsEnabled = !_queue.IsEmpty || _queueReport.Count > 0;
        if (!_queue.IsEmpty && !_jobRunning && !_queueRunning && _sourceOperationCts is null)
        {
            DownloadButton.IsEnabled = true;
        }
    }

    /// <summary>Opens the verified download and install flow for an available release.</summary>
    private void OfferUpdate(UpdateResult update)
    {
        if (_jobRunning || _queueRunning || _sourceOperationCts is not null)
        {
            StatusText.Text = "Finish or cancel the running download before installing an update";
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = update.ReleasePage.AbsoluteUri,
                UseShellExecute = true,
            });
            return;
        }

        var dialog = new UpdateWindow(update, CurrentVersion(), executable) { Owner = this };
        dialog.ShowDialog();
        if (!dialog.RestartRequested)
        {
            return;
        }

        // The new binary is in place; start it only once this instance has released the
        // backend session, so the two versions never share the extracted helper folder.
        _relaunchAfterClose = executable;
        Close();
    }

    private static Version CurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);

    /// <summary>Accepts a dropped Spotify link or CSV/JSON manifest.</summary>
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedSource(e.Data) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        var dropped = DroppedSource(e.Data);
        if (dropped is null || _jobRunning || _queueRunning || _sourceOperationCts is not null)
        {
            return;
        }

        e.Handled = true;
        var (path, url) = dropped.Value;
        if (path is not null)
        {
            using var operation = BeginSourceOperation("Importing dropped manifest…");
            if (operation is null)
            {
                return;
            }
            try
            {
                PlaylistUrlBox.Text = path;
                await ImportManifestAsync(path, cancellationToken: operation.Token);
                SaveCurrentJob();
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Track manifest import cancelled";
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                StatusText.Text = $"Dropped manifest could not be imported: {exception.Message}";
            }
            finally
            {
                EndSourceOperation(operation);
            }

            return;
        }

        PlaylistUrlBox.Text = url;
        AnalyzeButton_Click(sender, e);
    }

    /// <summary>Returns the manifest path or Spotify URL a drop carries, if any.</summary>
    private static (string? Path, string? Url)? DroppedSource(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) &&
            data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var manifest = files.FirstOrDefault(file =>
                file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            return manifest is null ? null : (manifest, null);
        }

        if (data.GetDataPresent(DataFormats.UnicodeText) &&
            data.GetData(DataFormats.UnicodeText) is string text &&
            SpotifyInput.TryNormalize(text, out var url))
        {
            return (null, url);
        }

        return null;
    }

    private void QueueButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new QueueWindow(_queue, _queueReport, _queueRunning) { Owner = this };
        dialog.ShowDialog();
        UpdateQueueUi();
        UpdateSelectionUi();
    }

    /// <summary>Counts only the tracks this job asked for, not everything on screen.</summary>
    private int CompletedTracksOfThisJob() =>
        Tracks.Count(track => track.Status == "Done" && _activeTrackIds.Contains(track.Id));

    private void PersistQueue()
    {
        try
        {
            _queueStore.Save(_queue.Items);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Queue could not be saved: {exception.Message}";
        }
    }

    private async Task RunQueueAsync()
    {
        QueuedJob prepared;
        _queueRunning = true;
        // Claim the session before the first await: a second click, or an analyze while a
        // job is being prepared, would otherwise run two jobs against one backend.
        UpdateSourceIntakeAvailability();
        UpdateSelectionUi();
        CancelButton.IsEnabled = true;
        var preparationFailures = 0;
        while (true)
        {
            var job = _queue.DequeueNext();
            if (job is null)
            {
                FinishQueueRun();
                return;
            }

            _activeQueuedJob = job;
            StatusText.Text = _queue.IsEmpty
                ? $"Queue: preparing \"{job.Name}\""
                : $"Queue: preparing \"{job.Name}\" — {_queue.Count} more waiting";

            try
            {
                prepared = await PrepareQueuedJobAsync(job);
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One unresolvable source must not discard the jobs behind it.
                _queueReport.Add(new QueueJobSummary(job.Name, 0, 0, null, exception.Message));
                _activeQueuedJob = null;
                if (++preparationFailures >= PreparationFailureLimit && !_queue.IsEmpty)
                {
                    _queue.Requeue(job);
                    // Repeated failures mean the backend or the network is the problem,
                    // not the sources; stop before the whole queue is consumed by it.
                    FinishQueueRun();
                    StatusText.Text =
                        $"Queue paused after {preparationFailures} sources could not be prepared — " +
                        $"{_queue.Count} jobs kept. Open Queue for details.";
                    return;
                }
            }
        }

        _activeQueuedJob = prepared;
        // The queued job becomes the current source: saving, retrying, and the track
        // grid must all describe the job that is actually running.
        _playlist = new PlaylistInfo
        {
            Id = prepared.PlaylistId ?? string.Empty,
            Name = prepared.Name,
            SourceUrl = prepared.SourceUrl,
            SourceType = prepared.SourceType,
            Owner = "queued job",
        };
        PlaylistUrlBox.Text = prepared.SourceUrl;
        OutputDirectoryBox.Text = prepared.OutputDirectory;
        SetTracks(prepared.AllTracks);
        PlaylistTitle.Text = prepared.Name;
        _tracksView.Refresh();
        UpdateQueueUi();
        UpdateSelectionUi();
        StatusText.Text = _queue.IsEmpty
            ? $"Queue: downloading \"{prepared.Name}\""
            : $"Queue: downloading \"{prepared.Name}\" — {_queue.Count} more waiting";
        await StartJobCoreAsync(
            prepared.PlaylistId ?? string.Empty,
            prepared.OutputDirectory,
            prepared.Tracks,
            prepared.Settings);
    }

    /// <summary>Gives a queued job a live backend playlist, re-resolving it when needed.</summary>
    /// <remarks>
    /// A job restored from disk, or one whose backend session was replaced, has no live
    /// playlist id left, so the source is resolved again and the saved per-track state
    /// decides what still needs downloading.
    /// </remarks>
    private async Task<QueuedJob> PrepareQueuedJobAsync(QueuedJob job)
    {
        if (!job.NeedsResolve)
        {
            return job;
        }

        var response = job.SourceType switch
        {
            "import" => await _backend.RequestAsync("import_manifest", new { path = job.SourceUrl }),
            "search" => await _backend.RequestAsync(
                "resolve_search",
                new
                {
                    query = job.SourceUrl.StartsWith("search:", StringComparison.OrdinalIgnoreCase)
                        ? job.SourceUrl["search:".Length..]
                        : job.SourceUrl,
                    limit = 12,
                }),
            _ => await _backend.RequestAsync("resolve", ResolvePayload(job.SourceUrl)),
        };

        var playlist = response.GetProperty("playlist")
            .Deserialize<PlaylistInfo>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The queued source could not be read.");
        var restored = RestoreTracks(playlist, job.Snapshot);
        var selected = restored.Where(track => track.IsSelected).ToList();
        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                $"Every track of \"{job.Name}\" is already downloaded.");
        }

        return job with
        {
            PlaylistId = playlist.Id,
            AllTracks = restored,
            Tracks = selected,
        };
    }

    private void FinishQueueRun()
    {
        _queueRunning = false;
        _activeQueuedJob = null;
        UpdateSourceIntakeAvailability();
        UpdateSelectionUi();
        if (!_jobRunning)
        {
            CancelButton.IsEnabled = false;
        }
        UpdateQueueUi();
        PersistQueue();
        if (_queueReport.Count > 0)
        {
            var saved = _queueReport.Sum(summary => summary.Succeeded);
            var failed = _queueReport.Sum(summary => summary.Failed);
            StatusText.Text =
                $"Queue finished — {_queueReport.Count} jobs, {saved} saved, {failed} failed. " +
                "Open Queue for the per-job report.";
        }
    }

    private Task StartJobAsync(IReadOnlyList<TrackItem> jobTracks)
    {
        if (_playlist is null)
        {
            return Task.CompletedTask;
        }

        return StartJobCoreAsync(
            _playlist.Id,
            OutputDirectoryBox.Text,
            jobTracks,
            QueuedJobSettings.From(_settings));
    }

    private async Task StartJobCoreAsync(
        string playlistId,
        string outputDirectory,
        IReadOnlyList<TrackItem> jobTracks,
        QueuedJobSettings snapshot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("Choose a valid output folder before downloading.");
            }

            Directory.CreateDirectory(outputDirectory);
            // Reading every library entry can take a moment on a large library.
            var existingFiles = snapshot.DuplicatePolicy == "download"
                ? null
                : await Task.Run(() => _library.DownloadedFiles(_playlist?.SourceUrl));
            _activeTrackIds = jobTracks.Select(track => track.Id).ToHashSet();
            // This job supersedes the previous failure set; results repopulate it.
            _failedTracks.Clear();
            _jobRunning = true;
            _jobProgressText = null;
            UpdateSourceIntakeAvailability();
            DownloadButton.IsEnabled = false;
            AddToQueueButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            StatusText.Text = "Starting downloads…";
            HideFailureBanner();
            await _backend.RequestAsync(
                "start",
                new
                {
                    playlist_id = playlistId,
                    output_dir = outputDirectory,
                    format = snapshot.Format,
                    bitrate = snapshot.Bitrate,
                    threads = snapshot.Threads,
                    cookie_file = snapshot.CookieFile,
                    track_ids = jobTracks.Select(track => track.Id).ToList(),
                    write_m3u = snapshot.WriteM3u,
                    source_overrides = jobTracks
                        .Where(track => !string.IsNullOrWhiteSpace(track.SourceOverride))
                        .GroupBy(track => track.Id)
                        .ToDictionary(group => group.Key, group => group.First().SourceOverride),
                    naming_preset = snapshot.NamingPreset,
                    create_source_folder = snapshot.CreateSourceFolder,
                    throttle_seconds = snapshot.ThrottleSeconds,
                    retries = 1,
                    ytdlp_args = snapshot.YtDlpArgs,
                    embed_lyrics = snapshot.EmbedLyrics,
                    normalize_loudness = snapshot.NormalizeLoudness,
                    verify_downloads = snapshot.VerifyDownloads,
                    duplicate_policy = snapshot.DuplicatePolicy,
                    existing_files = existingFiles,
                });
            SaveCurrentJob();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var failedJobName = _activeQueuedJob?.Name;
            _jobRunning = false;
            _jobProgressText = null;
            _activeQueuedJob = null;
            UpdateSourceIntakeAvailability();
            CancelButton.IsEnabled = false;
            UpdateSelectionUi();
            StatusText.Text = $"Download could not start: {exception.Message}";
            SaveCurrentJob();
            if (failedJobName is not null)
            {
                // Keep the jobs behind this one; the user restarts the queue when ready.
                _queueReport.Add(new QueueJobSummary(failedJobName, 0, 0, null, exception.Message));
            }
            _queueRunning = false;
            UpdateQueueUi();
        }
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sourceOperationCts is not null)
            {
                _sourceOperationCts.Cancel();
                StatusText.Text = "Cancelling source operation…";
                SaveCurrentJob();
                await _backend.RestartAsync();
                InvalidateBackendSession("Source operation cancelled — analyze or resume a source to continue");
                return;
            }

            await _backend.RequestAsync("cancel", new { });
            StatusText.Text = "Cancellation requested…";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _jobRunning = false;
            _queueRunning = false;
            _activeQueuedJob = null;
            UpdateSourceIntakeAvailability();
            CancelButton.IsEnabled = false;
            UpdateQueueUi();
            UpdateSelectionUi();
            StatusText.Text = $"Backend stopped before cancellation completed: {exception.Message}";
            SaveCurrentJob();
        }
    }

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = FolderPickerPath.ResolveInitialDirectory(OutputDirectoryBox.Text);
        var attempts = initialDirectory is null ? new string?[] { null } : new string?[] { initialDirectory, null };

        foreach (var attempt in attempts)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose playlist output folder",
                Multiselect = false,
                InitialDirectory = attempt,
            };

            try
            {
                if (dialog.ShowDialog(this) == true)
                {
                    OutputDirectoryBox.Text = dialog.FolderName;
                    _settings.OutputDirectory = dialog.FolderName;
                    TrySaveSettings();
                }

                return;
            }
            catch (Exception exception) when (exception is ArgumentException or COMException)
            {
                // Windows Shell can reject otherwise valid saved paths. Retry from its default location.
            }
        }

        StatusText.Text = "Folder picker could not open. Enter the output path manually.";
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = OutputDirectoryBox.Text;
        if (!Directory.Exists(directory))
        {
            StatusText.Text = "Output folder does not exist yet";
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    private void OpenRunLogButton_Click(object sender, RoutedEventArgs e)
    {
        var logWindow = new RunLogWindow(_backend.LogPath) { Owner = this };
        logWindow.ShowDialog();
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_jobRunning || _queueRunning)
        {
            // Settings are snapshotted per job; changing the resolver mid-run would make
            // the jobs of one queue run disagree about how their source was resolved.
            StatusText.Text = "Finish or cancel the running download before changing settings";
            return;
        }

        var previousBackend = _settings.BackendExecutable;
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                _settingsService.Save(_settings);
                if (!string.Equals(previousBackend, _settings.BackendExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    SaveCurrentJob();
                    await _backend.RestartAsync();
                    InvalidateBackendSession(
                        string.IsNullOrWhiteSpace(_settings.BackendExecutable)
                            ? "Bundled backend restored — analyze or resume a source to continue"
                            : "Alternate backend activated — analyze or resume a source to continue");
                }
                else
                {
                    StatusText.Text = "Settings saved";
                }
                SyncQuickFormat();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                StatusText.Text = $"Settings could not be applied: {exception.Message}";
            }
        }
    }

    private void InvalidateBackendSession(string status)
    {
        _playlist = null;
        _activeTrackIds.Clear();
        _failedTracks.Clear();
        // Queued jobs outlive the backend session; they are resolved again when they run.
        _queue.InvalidateSessions();
        // The restarted backend no longer owns the previous job, so the job state must
        // not stay armed — otherwise Cancel and Analyze remain locked with no job left.
        _jobRunning = false;
        _jobProgressText = null;
        _queueRunning = false;
        _activeQueuedJob = null;
        SetTracks([]);
        PlaylistTitle.Text = "No source loaded";
        PlaylistSummary.Text = "Analyze a source or resume a saved job";
        RetryFailedButton.Visibility = Visibility.Collapsed;
        OverallProgress.Value = 0;
        CancelButton.IsEnabled = false;
        UpdateQueueUi();
        UpdateSelectionUi();
        UpdateSourceIntakeAvailability();
        StatusText.Text = status;
    }

    private void SyncQuickFormat()
    {
        var match = QuickFormatBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _settings.Format, StringComparison.Ordinal));
        if (match is not null)
        {
            _syncingQuickFormat = true;
            QuickFormatBox.SelectedItem = match;
            _syncingQuickFormat = false;
        }
    }

    private void QuickFormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _syncingQuickFormat ||
            QuickFormatBox.SelectedItem is not ComboBoxItem { Tag: string format })
        {
            return;
        }

        _settings.Format = format;
        if (TrySaveSettings())
        {
            StatusText.Text = $"Audio format set to {format.ToUpperInvariant()}";
        }
    }

    /// <summary>Persist settings without letting a locked file take the app down.</summary>
    private bool TrySaveSettings()
    {
        try
        {
            _settingsService.Save(_settings);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Settings could not be saved: {exception.Message}";
            return false;
        }
    }

    private void NotifyJobFinished()
    {
        if (IsActive)
        {
            return;
        }

        System.Media.SystemSounds.Asterisk.Play();
        FlashWindow();
    }

    private void FlashWindow()
    {
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        if (helper.Handle != nint.Zero)
        {
            _ = NativeMethods.FlashWindow(helper.Handle, true);
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool FlashWindow(nint hwnd, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool invert);
    }

    private void Backend_EventReceived(object? sender, JsonElement message)
    {
        Dispatcher.InvokeAsync(() => HandleBackendEvent(message));
    }

    private async void HandleBackendEvent(JsonElement message)
    {
        try
        {
            var type = message.GetProperty("type").GetString();
            if (type == "track_progress")
            {
                var trackId = message.GetProperty("track_id").GetString();
                var track = trackId is null ? null : FindTrack(trackId);
                if (track is not null)
                {
                    track.Progress = message.GetProperty("progress").GetInt32();
                    track.Status = message.GetProperty("status").GetString() ?? "Working";
                    if (message.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                    {
                        track.OutputPath = path.GetString();
                    }
                }

                UpdateOverallProgress();
            }
            else if (type == "track_result")
            {
                var result = JobResults.ParseSingle(message);
                if (result is not null)
                {
                    ApplyLiveTrackResult(result);
                }
            }
            else if (type == "job_progress")
            {
                var processed = message.GetProperty("processed").GetInt32();
                var total = message.GetProperty("total").GetInt32();
                var succeeded = message.GetProperty("succeeded").GetInt32();
                var failed = message.GetProperty("failed").GetInt32();
                var rate = message.GetProperty("tracks_per_minute").GetDouble();
                var eta = message.TryGetProperty("eta_seconds", out var etaElement) &&
                    etaElement.ValueKind == JsonValueKind.Number
                    ? etaElement.GetInt32()
                    : 0;
                OverallProgress.Value = total == 0 ? 0 : processed * 100d / total;
                _jobProgressText = $"{processed}/{total} processed · {succeeded} saved · {failed} failed · " +
                    $"{rate:F1} tracks/min · ETA {FormatDuration(eta)}";
                StatusText.Text = _jobProgressText;
                // One durable checkpoint per completed rolling window avoids rewriting
                // the full large-playlist snapshot for every provider callback.
                SaveCurrentJob();
            }
            else if (type == "job_warning")
            {
                var hint = JobResults.ParseFailure(message).FailureHint;
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    ShowFailureBanner(hint);
                }
            }
            else if (type is "job_completed" or "job_cancelled")
            {
                _jobRunning = false;
                _jobProgressText = null;
                UpdateSourceIntakeAvailability();
                CancelButton.IsEnabled = false;
                UpdateSelectionUi();
                if (type == "job_completed")
                {
                    var m3uPath = message.TryGetProperty("m3u_path", out var m3u) && m3u.ValueKind == JsonValueKind.String
                        ? m3u.GetString()
                        : null;
                    var results = JobResults.Parse(message);
                    var failure = JobResults.ParseFailure(message);
                    ApplyJobResults(results, m3uPath, failure);
                    if (_activeQueuedJob is not null)
                    {
                        var succeeded = results.Count(result => result.Success);
                        _queueReport.Add(new QueueJobSummary(
                            _activeQueuedJob.Name,
                            succeeded,
                            results.Count - succeeded,
                            failure?.FailureHint,
                            null));
                    }
                    if (_queueRunning && !_queue.IsEmpty)
                    {
                        await RunQueueAsync();
                        return;
                    }
                    if (_queueRunning)
                    {
                        FinishQueueRun();
                    }
                    NotifyJobFinished();
                }
                else
                {
                    foreach (var result in JobResults.Parse(message))
                    {
                        ApplyLiveTrackResult(result);
                    }
                    if (_activeQueuedJob is not null)
                    {
                        _queueReport.Add(new QueueJobSummary(
                            _activeQueuedJob.Name,
                            CompletedTracksOfThisJob(),
                            _failedTracks.Count,
                            null,
                            "Cancelled"));
                    }
                    // Cancelling stops the run; the jobs behind it stay queued.
                    _queueRunning = false;
                    _activeQueuedJob = null;
                    UpdateQueueUi();
                    NotifyJobFinished();
                    StatusText.Text = _queue.IsEmpty
                        ? "Downloads cancelled"
                        : $"Downloads cancelled — {_queue.Count} queued jobs kept";
                    SaveCurrentJob();
                }
            }
            else if (type == "error" && _jobRunning)
            {
                _jobRunning = false;
                _jobProgressText = null;
                if (_activeQueuedJob is not null)
                {
                    _queueReport.Add(new QueueJobSummary(
                        _activeQueuedJob.Name,
                        CompletedTracksOfThisJob(),
                        _failedTracks.Count,
                        null,
                        "Backend stopped during this job"));
                }
                _queueRunning = false;
                UpdateQueueUi();
                UpdateSourceIntakeAvailability();
                CancelButton.IsEnabled = false;
                UpdateSelectionUi();
                StatusText.Text = "Download stopped — progress saved. Open Run log for exact details.";
                SaveCurrentJob();
                _activeQueuedJob = null;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A UI-side failure while reacting to a backend event must never
            // silently stall the queue or hide the job state.
            _jobRunning = false;
            _queueRunning = false;
            UpdateQueueUi();
            UpdateSourceIntakeAvailability();
            CancelButton.IsEnabled = false;
            StatusText.Text = $"Internal error while processing downloads: {ex.Message}";
            _activeQueuedJob = null;
        }
    }

    private void ApplyJobResults(
        IReadOnlyList<DownloadResult> results,
        string? m3uPath = null,
        JobFailureSummary? failure = null)
    {
        _failedTracks.Clear();
        foreach (var result in results)
        {
            var track = FindTrack(result.TrackId);
            if (track is null)
            {
                continue;
            }

            if (result.Success)
            {
                track.Progress = 100;
                track.Status = "Done";
                track.OutputPath = result.Path;
                track.ErrorText = null;
            }
            else
            {
                track.Status = "Failed";
                track.ErrorText = result.Error;
                _failedTracks.Add(track);
            }
        }

        if (_failedTracks.Count > 0 && !string.IsNullOrWhiteSpace(failure?.FailureHint))
        {
            ShowFailureBanner(failure.FailureHint);
        }
        else
        {
            HideFailureBanner();
        }

        UpdateRetryUi();
        var succeeded = results.Count(result => result.Success);
        var summary = _failedTracks.Count == 0
            ? $"Downloads complete — {succeeded} done"
            : $"Downloads finished — {succeeded} done, {_failedTracks.Count} failed";
        if (m3uPath is not null)
        {
            summary += $" · playlist saved as {Path.GetFileName(m3uPath)}";
        }

        StatusText.Text = summary;
        SaveCurrentJob();
    }

    private void ApplyLiveTrackResult(DownloadResult result)
    {
        var track = FindTrack(result.TrackId);
        if (track is null)
        {
            return;
        }
        track.Progress = 100;
        if (result.Success)
        {
            track.Status = "Done";
            track.OutputPath = result.Path;
            track.ErrorText = null;
            _failedTracks.Remove(track);
        }
        else
        {
            track.Status = "Failed";
            track.ErrorText = result.Error ?? "Provider returned no error detail. See Run log.";
            if (!_failedTracks.Contains(track))
            {
                _failedTracks.Add(track);
            }
        }
        UpdateRetryUi();
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds <= 0)
        {
            return "calculating";
        }
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{Math.Max(1, duration.Minutes)}m";
    }

    /// <summary>Replaces the visible tracks and the lookup used by backend events.</summary>
    private void SetTracks(IReadOnlyList<TrackItem> tracks)
    {
        Tracks.ReplaceAll(tracks);
        _tracksById.Clear();
        foreach (var track in tracks)
        {
            if (!string.IsNullOrEmpty(track.Id))
            {
                _tracksById[track.Id] = track;
            }
            if (!string.IsNullOrEmpty(track.SpotifyUrl))
            {
                _tracksById.TryAdd(track.SpotifyUrl, track);
            }
        }
    }

    // Progress events arrive per track and per provider callback, so this lookup must
    // not scan a thousand-track collection every time.
    private TrackItem? FindTrack(string trackId) =>
        _tracksById.TryGetValue(trackId, out var track) ? track : null;

    private void ShowFailureBanner(string message)
    {
        FailureBannerText.Text = message;
        FailureBanner.Visibility = Visibility.Visible;
    }

    private void HideFailureBanner() => FailureBanner.Visibility = Visibility.Collapsed;

    private async void DiagnoseButton_Click(object sender, RoutedEventArgs e)
    {
        DiagnoseButton.IsEnabled = false;
        StatusText.Text = "Running network diagnosis…";
        try
        {
            var report = await _backend.RequestAsync("diagnose", new { });
            var lines = new List<string>();
            if (report.TryGetProperty("backend_path", out var backendPath))
            {
                lines.Add($"Backend: {backendPath.GetString()}");
                lines.Add(string.Empty);
            }
            if (report.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
            {
                foreach (var check in checks.EnumerateArray())
                {
                    var ok = check.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
                    var url = check.GetProperty("url").GetString();
                    var detail = check.TryGetProperty("detail", out var detailElement) ? detailElement.GetString() : null;
                    var elapsed = check.TryGetProperty("elapsed_ms", out var elapsedElement) ? elapsedElement.GetInt32() : 0;
                    lines.Add($"{(ok ? "OK " : "BLOCKED")}  {url}  ({elapsed} ms)");
                    if (!ok && !string.IsNullOrWhiteSpace(detail))
                    {
                        lines.Add($"    {detail}");
                    }
                }
            }
            lines.Add(string.Empty);
            lines.Add("If an endpoint is BLOCKED here but reachable in your browser, allow the backend path above in your antivirus or firewall.");
            MessageBox.Show(this, string.Join('\n', lines), "Network diagnosis", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "Network diagnosis finished";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Diagnosis failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Network diagnosis failed";
        }
        finally
        {
            DiagnoseButton.IsEnabled = true;
        }
    }

    private async void RetryFailedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_failedTracks.Count == 0)
        {
            return;
        }

        var retryTracks = _failedTracks.ToList();
        foreach (var track in retryTracks)
        {
            track.Progress = 0;
            track.Status = "Queued";
            track.ErrorText = null;
        }

        RetryFailedButton.Visibility = Visibility.Collapsed;
        await StartJobAsync(retryTracks);
    }

    private void UpdateOverallProgress()
    {
        // One pass: this runs for every provider progress callback of every track.
        var count = 0;
        var total = 0L;
        var complete = 0;
        var restrictToJob = _activeTrackIds.Count > 0;
        foreach (var track in Tracks)
        {
            if (restrictToJob && !_activeTrackIds.Contains(track.Id))
            {
                continue;
            }
            count++;
            total += track.Progress;
            if (track.Progress >= 100)
            {
                complete++;
            }
        }

        if (count == 0)
        {
            foreach (var track in Tracks)
            {
                count++;
                total += track.Progress;
                if (track.Progress >= 100)
                {
                    complete++;
                }
            }
        }

        OverallProgress.Value = count == 0 ? 0 : (double)total / count;
        if (_jobProgressText is not null)
        {
            StatusText.Text = _jobProgressText;
            return;
        }
        StatusText.Text = $"{complete}/{count} complete";
    }

    private CancellationTokenSource? BeginSourceOperation(string status)
    {
        if (_sourceOperationCts is not null)
        {
            // A second source request would leave the first one's result to overwrite
            // whatever the user sees next; keep exactly one in flight.
            return null;
        }

        _sourceOperationCts = new CancellationTokenSource();
        UpdateSourceIntakeAvailability();
        UpdateSelectionUi();
        CancelButton.IsEnabled = true;
        StatusText.Text = status;
        return _sourceOperationCts;
    }

    private void EndSourceOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_sourceOperationCts, operation))
        {
            _sourceOperationCts = null;
        }
        operation.Dispose();
        UpdateSourceIntakeAvailability();
        UpdateSelectionUi();
        if (!_jobRunning)
        {
            CancelButton.IsEnabled = false;
        }
    }

    private async Task ResolveAsync(
        string url,
        SavedJob? restore = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _backend.RequestAsync("resolve", ResolvePayload(url), cancellationToken);
        ApplyResolvedPlaylist(response, restore);
    }

    /// <summary>Builds the resolve request, adding official credentials when opted in.</summary>
    /// <remarks>
    /// The credentials are read from Windows Credential Manager for each request and are
    /// never written to settings, the saved job, or the run log.
    /// </remarks>
    private object ResolvePayload(string url)
    {
        SpotifyCredentials? credentials = null;
        if (_settings.UseOfficialSpotifyApi)
        {
            credentials = _credentialStore.Load();
            if (credentials is null)
            {
                // Falling back silently would look like the official API is in use.
                StatusText.Text =
                    "Stored Spotify credentials could not be read — using the public resolver. " +
                    "Re-enter them under Settings.";
            }
        }

        return new
        {
            url,
            spotify_client_id = credentials?.ClientId,
            spotify_client_secret = credentials?.ClientSecret,
        };
    }

    private async Task ImportManifestAsync(
        string path,
        SavedJob? restore = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _backend.RequestAsync("import_manifest", new { path }, cancellationToken);
        ApplyResolvedPlaylist(response, restore);
    }

    /// <summary>Applies a saved snapshot onto freshly resolved tracks.</summary>
    private List<TrackItem> RestoreTracks(PlaylistInfo? playlist, SavedJob? restore)
    {
        var savedById = new Dictionary<string, SavedTrack>();
        foreach (var item in restore?.Tracks ?? [])
        {
            if (!string.IsNullOrEmpty(item.SpotifyUrl))
            {
                savedById.TryAdd(item.SpotifyUrl, item);
            }
            if (!string.IsNullOrEmpty(item.Id))
            {
                savedById.TryAdd(item.Id, item);
            }
        }

        var resolvedTracks = new List<TrackItem>();
        foreach (var track in playlist?.Tracks ?? [])
        {
            SavedTrack? saved = null;
            if (!string.IsNullOrEmpty(track.SpotifyUrl))
            {
                savedById.TryGetValue(track.SpotifyUrl, out saved);
            }
            if (saved is null && !string.IsNullOrEmpty(track.Id))
            {
                savedById.TryGetValue(track.Id, out saved);
            }
            if (saved is not null)
            {
                track.IsSelected = saved.IsSelected && !saved.IsComplete;
                track.Progress = saved.IsComplete ? 100 : 0;
                track.Status = saved.IsComplete ? "Done" :
                    string.IsNullOrWhiteSpace(saved.LastError) ? "Ready" : "Failed";
                track.OutputPath = saved.OutputPath;
                track.SourceOverride = saved.SourceOverride;
                track.ErrorText = saved.IsComplete ? null : saved.LastError;
            }
            track.PropertyChanged += Track_PropertyChanged;
            resolvedTracks.Add(track);
        }

        return resolvedTracks;
    }

    private void ApplyResolvedPlaylist(JsonElement response, SavedJob? restore)
    {
        _playlist = response.GetProperty("playlist").Deserialize<PlaylistInfo>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        SetTracks(RestoreTracks(_playlist, restore));

        PlaylistTitle.Text = _playlist?.Name ?? "Playlist";
        FilterBox.Clear();
        _failedTracks.Clear();
        _failedTracks.AddRange(Tracks.Where(track => track.HasError));
        UpdateRetryUi();
        HideFailureBanner();
        UpdateSelectionUi();
        var sourceLabel = SourceLabel();
        StatusText.Text = restore is null
            ? $"{char.ToUpperInvariant(sourceLabel[0])}{sourceLabel[1..]} ready"
            : $"Job restored — {Tracks.Count(track => track.Status == "Done")} complete, {Tracks.Count(track => track.IsSelected)} remaining";
    }

    private async void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        var saved = _jobStore.Load();
        if (saved is null || string.IsNullOrWhiteSpace(saved.SourceUrl))
        {
            ResumeButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "No resumable job found";
            return;
        }

        await RestoreJobAsync(saved, sync: false);
    }

    private async void LibraryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LibraryWindow(_library) { Owner = this };
        var opened = dialog.ShowDialog() == true && dialog.SelectedJob is not null;
        ReconcileRepairedJobs(dialog.RepairedSources);
        if (opened)
        {
            await RestoreJobAsync(dialog.SelectedJob!, dialog.SyncRequested);
        }
    }

    /// <summary>
    /// Brings everything that mirrors the library back in line after a repair.
    /// </summary>
    /// <remarks>
    /// The library window writes only the library entry, but the same job is mirrored in
    /// the grid, in the resume file, and in every queued job's snapshot. Any of those
    /// would write the pre-repair state back over the repair, so the stale copies are
    /// dropped or refreshed here.
    /// </remarks>
    private void ReconcileRepairedJobs(IReadOnlyCollection<string> repairedSources)
    {
        if (repairedSources.Count == 0)
        {
            return;
        }

        var resume = _jobStore.Load();
        if (resume is not null &&
            repairedSources.Contains(resume.SourceUrl, StringComparer.OrdinalIgnoreCase) &&
            _library.Load(resume.SourceUrl) is { } refreshed)
        {
            try
            {
                _jobStore.Save(refreshed);
                _savedJob = refreshed;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The library entry is repaired either way; Resume just stays stale.
            }
        }

        _queue.RefreshSnapshots(source =>
            repairedSources.Contains(source, StringComparer.OrdinalIgnoreCase)
                ? _library.Load(source)
                : null);

        var current = _playlist?.SourceUrl;
        if (string.IsNullOrWhiteSpace(current) ||
            !repairedSources.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        // The grid holds the pre-repair rows and the next save would put them back. The
        // repaired entry is applied to them in place: the resolved backend session is
        // still valid, so tearing it down and re-resolving the source would only cost the
        // user their session for nothing.
        if (_library.Load(current) is not { } repaired)
        {
            InvalidateBackendSession("Saved job repaired — reload it from the library");
            return;
        }

        ApplyRepairedTracks(repaired);
    }

    /// <summary>Brings the loaded track list in line with a repaired library entry.</summary>
    private void ApplyRepairedTracks(SavedJob repaired)
    {
        var byKey = new Dictionary<string, SavedTrack>(StringComparer.Ordinal);
        foreach (var track in repaired.Tracks)
        {
            if (!string.IsNullOrEmpty(track.SpotifyUrl))
            {
                byKey.TryAdd(track.SpotifyUrl, track);
            }
            if (!string.IsNullOrEmpty(track.Id))
            {
                byKey.TryAdd(track.Id, track);
            }
        }

        var reopened = 0;
        foreach (var track in Tracks)
        {
            SavedTrack? saved = null;
            if (!string.IsNullOrEmpty(track.SpotifyUrl))
            {
                byKey.TryGetValue(track.SpotifyUrl, out saved);
            }
            if (saved is null && !string.IsNullOrEmpty(track.Id))
            {
                byKey.TryGetValue(track.Id, out saved);
            }
            if (saved is null)
            {
                continue;
            }

            track.OutputPath = saved.OutputPath;
            if (saved.IsComplete || track.Status != "Done")
            {
                continue;
            }

            track.Status = "Ready";
            track.Progress = 0;
            track.IsSelected = true;
            reopened++;
        }

        if (!string.IsNullOrWhiteSpace(repaired.OutputDirectory))
        {
            // A relocation can move the job to the folder its files were found in, and the
            // next save and the next download both read this box.
            OutputDirectoryBox.Text = repaired.OutputDirectory;
        }

        _savedJob = repaired;
        UpdateSelectionUi();
        UpdateOverallProgress();
        StatusText.Text = reopened == 0
            ? "Saved job updated from the library check"
            : $"{reopened} tracks are ready to download again";
    }

    private async Task RestoreJobAsync(SavedJob saved, bool sync)
    {
        using var operation = BeginSourceOperation(sync ? "Syncing with the source…" : "Restoring saved job…");
        if (operation is null)
        {
            return;
        }
        try
        {
            PlaylistUrlBox.Text = saved.SourceUrl;
            if (!string.IsNullOrWhiteSpace(saved.OutputDirectory))
            {
                OutputDirectoryBox.Text = saved.OutputDirectory;
            }
            if (saved.SourceType == "import")
            {
                await ImportManifestAsync(saved.SourceUrl, saved, operation.Token);
            }
            else if (saved.SourceType == "search")
            {
                var query = saved.SourceUrl.StartsWith("search:", StringComparison.OrdinalIgnoreCase)
                    ? saved.SourceUrl["search:".Length..]
                    : saved.SourceUrl;
                await SearchAsync(query, saved, operation.Token);
            }
            else
            {
                await ResolveAsync(saved.SourceUrl, saved, operation.Token);
            }
            _savedJob = saved;
            if (sync)
            {
                var newTracks = Tracks.Count(track => !saved.Tracks.Any(item =>
                    (!string.IsNullOrEmpty(item.SpotifyUrl) && item.SpotifyUrl == track.SpotifyUrl) ||
                    item.Id == track.Id));
                var remaining = Tracks.Count(track => track.IsSelected);
                StatusText.Text = newTracks > 0
                    ? $"Sync found {newTracks} new tracks — {remaining} selected for download"
                    : $"Sync: no new tracks — {remaining} unfinished selected";
                SaveCurrentJob();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = sync ? "Sync cancelled" : "Restore cancelled";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, sync ? "Sync failed" : "Resume failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = sync ? "Could not sync the saved job" : "Could not restore the saved job";
        }
        finally
        {
            EndSourceOperation(operation);
        }
    }

    private void SaveCurrentJob()
    {
        SavedJob? snapshot;
        if (_activeQueuedJob is { AllTracks.Count: 0 })
        {
            // Still preparing: the durable snapshot is the queue's, not an empty grid.
            return;
        }
        if (_activeQueuedJob is not null)
        {
            snapshot = SavedJobSnapshot.Create(
                _activeQueuedJob.SourceUrl,
                _activeQueuedJob.Name,
                _activeQueuedJob.SourceType,
                _activeQueuedJob.OutputDirectory,
                _activeQueuedJob.AllTracks);
        }
        else if (_playlist is not null && !string.IsNullOrWhiteSpace(_playlist.SourceUrl))
        {
            snapshot = SavedJobSnapshot.Create(
                _playlist.SourceUrl,
                _playlist.Name,
                _playlist.SourceType,
                OutputDirectoryBox.Text,
                Tracks);
        }
        else
        {
            return;
        }

        _savedJob = snapshot;
        try
        {
            _jobStore.Save(_savedJob);
            _library.Save(_savedJob);
            ResumeButton.Visibility = Visibility.Visible;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Downloads remain usable if local job persistence is unavailable.
        }
    }

    private async void ImportManifestButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import track manifest",
            Filter = "Track manifests (*.csv;*.json)|*.csv;*.json|CSV files (*.csv)|*.csv|JSON files (*.json)|*.json",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        using var operation = BeginSourceOperation("Importing track manifest…");
        if (operation is null)
        {
            return;
        }
        try
        {
            PlaylistUrlBox.Text = dialog.FileName;
            await ImportManifestAsync(dialog.FileName, cancellationToken: operation.Token);
            SaveCurrentJob();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Track manifest import cancelled";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Track manifest import failed";
        }
        finally
        {
            EndSourceOperation(operation);
        }
    }

    private void SourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TrackItem track })
        {
            return;
        }

        var dialog = new SourceOverrideWindow(
            track,
            () => _backend.RequestAsync(
                "search_sources",
                new
                {
                    title = track.Title,
                    artist = track.Artists.FirstOrDefault() ?? string.Empty,
                    duration_seconds = track.DurationSeconds,
                    limit = 8,
                }))
        { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            track.SourceOverride = dialog.SourceUrl;
            StatusText.Text = dialog.SourceUrl is null
                ? $"{track.Title} will use automatic matching"
                : $"Manual source saved for {track.Title}";
            SaveCurrentJob();
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not null)
        {
            OfferUpdate(_availableUpdate);
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Checking…";
        try
        {
            var current = CurrentVersion();
            _availableUpdate = await _updateService.CheckAsync(current);
            if (_availableUpdate is null)
            {
                UpdateButton.Content = "Up to date";
                StatusText.Text = $"Playlist DL {current.Major}.{current.Minor}.{current.Build} is up to date";
            }
            else
            {
                UpdateButton.Content = $"Get {_availableUpdate.Tag}";
                UpdateButton.ToolTip = "Download, verify, and install the latest release";
                StatusText.Text = $"Playlist DL {_availableUpdate.Tag} is available";
                OfferUpdate(_availableUpdate);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException or InvalidDataException)
        {
            UpdateButton.Content = "Check for updates";
            StatusText.Text = "Update check unavailable — try again later";
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }
}

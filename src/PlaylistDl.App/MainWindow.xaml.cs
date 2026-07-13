using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    private readonly UpdateService _updateService = new();
    private readonly AppSettings _settings;
    private readonly ICollectionView _tracksView;
    private PlaylistInfo? _playlist;
    private bool _jobRunning;
    private bool _syncingSelectAll;
    private bool _syncingQuickFormat;
    private bool _uiReady;
    private HashSet<string> _activeTrackIds = [];
    private readonly List<TrackItem> _failedTracks = [];
    private readonly DownloadQueue _queue = new();
    private bool _queueRunning;
    private QueuedJob? _activeQueuedJob;
    private CancellationTokenSource? _sourceOperationCts;
    private SavedJob? _savedJob;
    private UpdateResult? _availableUpdate;

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
        _backend.DiagnosticReceived += (_, message) => Dispatcher.Invoke(() => StatusText.Text = message);
        _uiReady = true;
        _savedJob = _jobStore.Load();
        _library.MigrateFromLastJob(_savedJob);
        ResumeButton.Visibility = _savedJob is null ? Visibility.Collapsed : Visibility.Visible;
        if (_savedJob is not null)
        {
            ResumeButton.ToolTip = $"Resume {_savedJob.SourceName} from {_savedJob.UpdatedAt.LocalDateTime:g}";
        }
        Closing += (_, _) => _sourceOperationCts?.Cancel();
        Closed += async (_, _) =>
        {
            SaveCurrentJob();
            await _backend.DisposeAsync();
        };
        Loaded += async (_, _) => await AutoCheckForUpdatesAsync();
    }

    private async Task AutoCheckForUpdatesAsync()
    {
        if (!UpdateService.ShouldAutoCheck(_settings.AutoUpdateCheck, _settings.LastUpdateCheckUtc, DateTimeOffset.UtcNow))
        {
            return;
        }

        try
        {
            var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);
            _availableUpdate = await _updateService.CheckAsync(current);
            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            _settingsService.Save(_settings);
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

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => _tracksView.Refresh();

    private void SelectAllBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _syncingSelectAll)
        {
            return;
        }

        var selected = SelectAllBox.IsChecked == true;
        foreach (var track in _tracksView.Cast<TrackItem>().ToList())
        {
            track.IsSelected = selected;
        }
    }

    private void Track_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackItem.IsSelected))
        {
            UpdateSelectionUi();
        }
    }

    private void UpdateSelectionUi()
    {
        var selected = Tracks.Count(track => track.IsSelected);
        _syncingSelectAll = true;
        SelectAllBox.IsChecked = selected == Tracks.Count ? true : selected == 0 ? false : null;
        _syncingSelectAll = false;
        DownloadButton.IsEnabled = (selected > 0 && !_jobRunning && _playlist is not null) ||
            (!_queue.IsEmpty && !_jobRunning);
        AddToQueueButton.IsEnabled = selected > 0 && !_jobRunning && _playlist is not null;
        if (_playlist is not null)
        {
            PlaylistSummary.Text = $"{SourceLabel()} · {selected}/{Tracks.Count} tracks selected · {_playlist.Owner}";
        }
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
            if (isSpotifyUrl)
            {
                ShowFailureBanner(
                    userMessage + " You can also type an artist and title to search, or import a CSV/JSON manifest.");
            }
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
            _playlist.Id,
            _playlist.Name,
            _playlist.SourceUrl,
            _playlist.SourceType,
            OutputDirectoryBox.Text,
            [.. Tracks],
            selectedTracks,
            QueuedJobSettings.From(_settings)));
        foreach (var track in selectedTracks)
        {
            track.Progress = 0;
            track.Status = "Queued";
            track.ErrorText = null;
        }

        UpdateQueueUi();
        StatusText.Text = $"Added \"{_playlist.Name}\" to the queue — resolve another source or start the queue";
    }

    private void UpdateQueueUi()
    {
        DownloadButton.Content = _queue.IsEmpty
            ? "Download audio"
            : $"Start queue ({_queue.Count})";
        if (!_queue.IsEmpty && !_jobRunning)
        {
            DownloadButton.IsEnabled = true;
        }
    }

    private async Task RunQueueAsync()
    {
        var job = _queue.DequeueNext();
        if (job is null)
        {
            _queueRunning = false;
            _activeQueuedJob = null;
            UpdateQueueUi();
            return;
        }

        _queueRunning = true;
        _activeQueuedJob = job;
        Tracks.ReplaceAll(job.Tracks);
        PlaylistTitle.Text = job.Name;
        _tracksView.Refresh();
        UpdateQueueUi();
        StatusText.Text = _queue.IsEmpty
            ? $"Queue: downloading \"{job.Name}\""
            : $"Queue: downloading \"{job.Name}\" — {_queue.Count} more waiting";
        await StartJobCoreAsync(job.PlaylistId, job.OutputDirectory, job.Tracks, job.Settings);
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
        Directory.CreateDirectory(outputDirectory);
        _activeTrackIds = jobTracks.Select(track => track.Id).ToHashSet();
        RetryFailedButton.Visibility = Visibility.Collapsed;
        _jobRunning = true;
        AnalyzeButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusText.Text = "Starting downloads…";
        HideFailureBanner();
        await _backend.SendCommandAsync(
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
                    .ToDictionary(track => track.Id, track => track.SourceOverride),
                naming_preset = snapshot.NamingPreset,
                create_source_folder = snapshot.CreateSourceFolder,
                throttle_seconds = snapshot.ThrottleSeconds,
                retries = 1,
                ytdlp_args = snapshot.YtDlpArgs,
                embed_lyrics = snapshot.EmbedLyrics,
            });
        SaveCurrentJob();
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceOperationCts is not null)
        {
            _sourceOperationCts.Cancel();
            StatusText.Text = "Cancelling source operation…";
            await _backend.RestartAsync();
            return;
        }

        await _backend.SendCommandAsync("cancel", new { });
        StatusText.Text = "Cancellation requested…";
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
                    _settingsService.Save(_settings);
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

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var previousBackend = _settings.BackendExecutable;
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _settingsService.Save(_settings);
            if (!string.Equals(previousBackend, _settings.BackendExecutable, StringComparison.OrdinalIgnoreCase))
            {
                await _backend.RestartAsync();
                StatusText.Text = string.IsNullOrWhiteSpace(_settings.BackendExecutable)
                    ? "Bundled backend restored"
                    : "Alternate backend activated";
            }
            else
            {
                StatusText.Text = "Settings saved";
            }
            SyncQuickFormat();
        }
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
        _settingsService.Save(_settings);
        StatusText.Text = $"Audio format set to {format.ToUpperInvariant()}";
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
                    if (track.Progress >= 100)
                    {
                        SaveCurrentJob();
                    }
                }

                UpdateOverallProgress();
            }
            else if (type is "job_completed" or "job_cancelled")
            {
                _jobRunning = false;
                AnalyzeButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                UpdateSelectionUi();
                if (type == "job_completed")
                {
                    var m3uPath = message.TryGetProperty("m3u_path", out var m3u) && m3u.ValueKind == JsonValueKind.String
                        ? m3u.GetString()
                        : null;
                    ApplyJobResults(JobResults.Parse(message), m3uPath, JobResults.ParseFailure(message));
                    if (_queueRunning && !_queue.IsEmpty)
                    {
                        await RunQueueAsync();
                        return;
                    }
                    if (_queueRunning)
                    {
                        _queueRunning = false;
                        _activeQueuedJob = null;
                        StatusText.Text = "Queue finished — " + StatusText.Text;
                    }
                    NotifyJobFinished();
                }
                else
                {
                    _queueRunning = false;
                    _queue.Clear();
                    UpdateQueueUi();
                    NotifyJobFinished();
                    StatusText.Text = "Downloads cancelled";
                    SaveCurrentJob();
                    _activeQueuedJob = null;
                }
            }
            else if (type == "error" && _jobRunning)
            {
                _jobRunning = false;
                _queueRunning = false;
                _queue.Clear();
                UpdateQueueUi();
                AnalyzeButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                UpdateSelectionUi();
                StatusText.Text = message.GetProperty("message").GetString() ?? "Download failed";
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
            _queue.Clear();
            UpdateQueueUi();
            AnalyzeButton.IsEnabled = true;
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

        RetryFailedButton.Visibility = _failedTracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetryFailedButton.IsEnabled = _failedTracks.Count > 0;
        RetryFailedButton.Content = $"Retry {_failedTracks.Count} failed";
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

    private TrackItem? FindTrack(string trackId) =>
        Tracks.FirstOrDefault(item => item.Id == trackId || item.SpotifyUrl == trackId);

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
        var jobTracks = Tracks.Where(track => _activeTrackIds.Contains(track.Id)).ToList();
        if (jobTracks.Count == 0)
        {
            jobTracks = [.. Tracks];
        }

        OverallProgress.Value = jobTracks.Count == 0 ? 0 : jobTracks.Average(track => track.Progress);
        var complete = jobTracks.Count(track => track.Progress >= 100);
        StatusText.Text = $"{complete}/{jobTracks.Count} complete";
    }

    private CancellationTokenSource BeginSourceOperation(string status)
    {
        _sourceOperationCts?.Dispose();
        _sourceOperationCts = new CancellationTokenSource();
        AnalyzeButton.IsEnabled = false;
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
        AnalyzeButton.IsEnabled = true;
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
        var response = await _backend.RequestAsync("resolve", new { url }, cancellationToken);
        ApplyResolvedPlaylist(response, restore);
    }

    private async Task ImportManifestAsync(
        string path,
        SavedJob? restore = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _backend.RequestAsync("import_manifest", new { path }, cancellationToken);
        ApplyResolvedPlaylist(response, restore);
    }

    private void ApplyResolvedPlaylist(JsonElement response, SavedJob? restore)
    {
        _playlist = response.GetProperty("playlist").Deserialize<PlaylistInfo>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var resolvedTracks = new List<TrackItem>();
        foreach (var track in _playlist?.Tracks ?? [])
        {
            var saved = restore?.Tracks.FirstOrDefault(item =>
                (!string.IsNullOrEmpty(item.SpotifyUrl) && item.SpotifyUrl == track.SpotifyUrl) || item.Id == track.Id);
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
        Tracks.ReplaceAll(resolvedTracks);

        PlaylistTitle.Text = _playlist?.Name ?? "Playlist";
        FilterBox.Clear();
        _failedTracks.Clear();
        _failedTracks.AddRange(Tracks.Where(track => track.HasError));
        RetryFailedButton.Visibility = _failedTracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetryFailedButton.IsEnabled = _failedTracks.Count > 0;
        RetryFailedButton.Content = $"Retry {_failedTracks.Count} failed";
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
        if (dialog.ShowDialog() == true && dialog.SelectedJob is not null)
        {
            await RestoreJobAsync(dialog.SelectedJob, dialog.SyncRequested);
        }
    }

    private async Task RestoreJobAsync(SavedJob saved, bool sync)
    {
        using var operation = BeginSourceOperation(sync ? "Syncing with the source…" : "Restoring saved job…");
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
        catch (IOException)
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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _availableUpdate.ReleasePage.AbsoluteUri,
                UseShellExecute = true,
            });
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Checking…";
        try
        {
            var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);
            _availableUpdate = await _updateService.CheckAsync(current);
            if (_availableUpdate is null)
            {
                UpdateButton.Content = "Up to date";
                StatusText.Text = $"Playlist DL {current.Major}.{current.Minor}.{current.Build} is up to date";
            }
            else
            {
                UpdateButton.Content = $"Get {_availableUpdate.Tag}";
                UpdateButton.ToolTip = "Open the latest verified GitHub release";
                StatusText.Text = $"Playlist DL {_availableUpdate.Tag} is available";
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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    private readonly BackendClient _backend = new();
    private readonly SettingsService _settingsService = new();
    private readonly AppSettings _settings;
    private readonly ICollectionView _tracksView;
    private PlaylistInfo? _playlist;
    private bool _jobRunning;
    private bool _syncingSelectAll;
    private HashSet<string> _activeTrackIds = [];

    public ObservableCollection<TrackItem> Tracks { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsService.Load();
        DataContext = this;
        OutputDirectoryBox.Text = _settings.OutputDirectory;
        _tracksView = CollectionViewSource.GetDefaultView(Tracks);
        _tracksView.Filter = TrackPassesFilter;
        _backend.EventReceived += Backend_EventReceived;
        _backend.DiagnosticReceived += (_, message) => Dispatcher.Invoke(() => StatusText.Text = message);
        Closed += async (_, _) => await _backend.DisposeAsync();
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
        if (_syncingSelectAll)
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
        DownloadButton.IsEnabled = selected > 0 && !_jobRunning && _playlist is not null;
        if (_playlist is not null)
        {
            PlaylistSummary.Text = $"{SourceLabel()} · {selected}/{Tracks.Count} tracks selected · {_playlist.Owner}";
        }
    }

    private string SourceLabel() => _playlist?.SourceType switch
    {
        "album" => "album",
        "track" => "track",
        _ => "playlist",
    };

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        var url = PlaylistUrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Paste a valid Spotify playlist, album, or track URL.", "Invalid link", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "Resolving playlist…");
        try
        {
            var response = await _backend.RequestAsync("resolve", new { url });
            _playlist = response.GetProperty("playlist").Deserialize<PlaylistInfo>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Tracks.Clear();
            foreach (var track in _playlist?.Tracks ?? [])
            {
                track.PropertyChanged += Track_PropertyChanged;
                Tracks.Add(track);
            }

            PlaylistTitle.Text = _playlist?.Name ?? "Playlist";
            FilterBox.Clear();
            UpdateSelectionUi();
            var sourceLabel = SourceLabel();
            StatusText.Text = $"{char.ToUpperInvariant(sourceLabel[0])}{sourceLabel[1..]} ready";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Playlist failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Playlist resolution failed";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedTracks = Tracks.Where(track => track.IsSelected).ToList();
        if (_playlist is null || selectedTracks.Count == 0)
        {
            return;
        }

        foreach (var track in selectedTracks)
        {
            track.Progress = 0;
            track.Status = "Queued";
        }

        await StartJobAsync(selectedTracks);
    }

    private async Task StartJobAsync(IReadOnlyList<TrackItem> jobTracks)
    {
        if (_playlist is null)
        {
            return;
        }

        Directory.CreateDirectory(OutputDirectoryBox.Text);
        _activeTrackIds = jobTracks.Select(track => track.Id).ToHashSet();
        _jobRunning = true;
        AnalyzeButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusText.Text = "Starting downloads…";
        await _backend.SendCommandAsync(
            "start",
            new
            {
                playlist_id = _playlist.Id,
                output_dir = OutputDirectoryBox.Text,
                bitrate = _settings.Bitrate,
                threads = _settings.Threads,
                cookie_file = _settings.CookieFile,
                track_ids = jobTracks.Select(track => track.Id).ToList(),
            });
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        await _backend.SendCommandAsync("cancel", new { });
        StatusText.Text = "Cancellation requested…";
    }

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose playlist output folder",
            Multiselect = false,
            InitialDirectory = Directory.Exists(OutputDirectoryBox.Text) ? OutputDirectoryBox.Text : null,
        };
        if (dialog.ShowDialog(this) == true)
        {
            OutputDirectoryBox.Text = dialog.FolderName;
            _settings.OutputDirectory = dialog.FolderName;
            _settingsService.Save(_settings);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _settingsService.Save(_settings);
            StatusText.Text = "Settings saved";
        }
    }

    private void Backend_EventReceived(object? sender, JsonElement message)
    {
        Dispatcher.Invoke(() =>
        {
            var type = message.GetProperty("type").GetString();
            if (type == "track_progress")
            {
                var trackId = message.GetProperty("track_id").GetString();
                var track = Tracks.FirstOrDefault(item => item.Id == trackId || item.SpotifyUrl == trackId);
                if (track is not null)
                {
                    track.Progress = message.GetProperty("progress").GetInt32();
                    track.Status = message.GetProperty("status").GetString() ?? "Working";
                }

                UpdateOverallProgress();
            }
            else if (type is "job_completed" or "job_cancelled")
            {
                _jobRunning = false;
                AnalyzeButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                UpdateSelectionUi();
                StatusText.Text = type == "job_completed" ? "Downloads complete" : "Downloads cancelled";
            }
            else if (type == "error" && _jobRunning)
            {
                _jobRunning = false;
                AnalyzeButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                UpdateSelectionUi();
                StatusText.Text = message.GetProperty("message").GetString() ?? "Download failed";
            }
        });
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

    private void SetBusy(bool busy, string? status = null)
    {
        AnalyzeButton.IsEnabled = !busy;
        if (status is not null)
        {
            StatusText.Text = status;
        }
    }
}

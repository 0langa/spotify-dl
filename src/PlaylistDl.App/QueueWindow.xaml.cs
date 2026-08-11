using System.Windows;
using PlaylistDl.App.Services;

namespace PlaylistDl.App;

public sealed record QueueEntry(int Position, string Name, string TypeLabel, string TrackLabel, string OutputDirectory)
{
    public static QueueEntry From(QueuedJob job, int index) => new(
        index + 1,
        job.Name,
        job.SourceType switch
        {
            "album" => "Album",
            "track" => "Track",
            "import" => "Import",
            "search" => "Search",
            _ => "Playlist",
        },
        job.SelectedCount.ToString(),
        job.OutputDirectory);
}

public sealed record QueueReportEntry(string Name, string Outcome, string Detail)
{
    public static QueueReportEntry From(QueueJobSummary summary) => new(
        summary.Name,
        summary.Outcome,
        summary.Error ?? summary.FailureHint ?? string.Empty);
}

public partial class QueueWindow : Window
{
    private readonly DownloadQueue _queue;
    private readonly bool _queueRunning;
    private bool _reloading;

    public QueueWindow(DownloadQueue queue, IReadOnlyList<QueueJobSummary> report, bool queueRunning)
    {
        InitializeComponent();
        _queue = queue;
        _queueRunning = queueRunning;
        if (report.Count > 0)
        {
            ReportTitle.Visibility = Visibility.Visible;
            ReportList.Visibility = Visibility.Visible;
            ReportList.ItemsSource = report.Select(QueueReportEntry.From).ToList();
        }

        // A running queue advances while this dialog is open, so the rows must follow it;
        // acting on a stale index would move or remove the wrong job.
        _queue.Changed += QueueChanged;
        Closed += (_, _) => _queue.Changed -= QueueChanged;
        Reload(selectIndex: 0);
    }

    private void QueueChanged(object? sender, EventArgs e)
    {
        if (!_reloading)
        {
            Dispatcher.Invoke(() => Reload(JobsList.SelectedIndex));
        }
    }

    private void Reload(int selectIndex)
    {
        _reloading = true;
        try
        {
            ReloadCore(selectIndex);
        }
        finally
        {
            _reloading = false;
        }
    }

    private void ReloadCore(int selectIndex)
    {
        JobsList.ItemsSource = _queue.Items.Select(QueueEntry.From).ToList();
        if (_queue.Count > 0)
        {
            JobsList.SelectedIndex = Math.Clamp(selectIndex, 0, _queue.Count - 1);
        }

        // Reordering the job that is already downloading would not change anything.
        var editable = _queue.Count > 0;
        MoveUpButton.IsEnabled = editable;
        MoveDownButton.IsEnabled = editable;
        RemoveButton.IsEnabled = editable;
        ClearButton.IsEnabled = editable;
        StatusText.Text = _queue.Count == 0
            ? "No jobs waiting."
            : _queueRunning
                ? $"{_queue.Count} waiting — the running job is not listed."
                : $"{_queue.Count} waiting.";
    }

    private void MoveUpButton_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void MoveDownButton_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int offset)
    {
        var index = JobsList.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        Reload(_queue.Move(index, offset));
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var index = JobsList.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        _queue.RemoveAt(index);
        Reload(index);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_queue.Count == 0)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            $"Remove all {_queue.Count} waiting jobs? Downloaded files stay on disk.",
            "Clear queue",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmed == MessageBoxResult.Yes)
        {
            _queue.Clear();
            Reload(0);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

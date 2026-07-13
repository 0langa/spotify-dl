using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PlaylistDl.App.Services;

public sealed class BackendClient : IAsyncDisposable
{
    private readonly Func<string?> _configuredBackendPath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly RunLog _runLog;
    private Process? _process;

    public BackendClient(Func<string?>? configuredBackendPath = null, RunLog? runLog = null)
    {
        _configuredBackendPath = configuredBackendPath ?? (() => null);
        _runLog = runLog ?? new RunLog();
    }

    public string LogPath => _runLog.Path;

    public event EventHandler<JsonElement>? EventReceived;
    public event EventHandler<string>? DiagnosticReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        var startInfo = CreateStartInfo();
        _runLog.Write("app", $"Starting backend: {startInfo.FileName}");
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!_process.Start())
        {
            throw new InvalidOperationException("Backend process failed to start.");
        }

        _ = Task.Run(() => ReadEventsAsync(_process.StandardOutput, cancellationToken), cancellationToken);
        _ = Task.Run(() => ReadDiagnosticsAsync(_process.StandardError, cancellationToken), cancellationToken);
        await Task.Delay(100, cancellationToken);
        if (_process.HasExited)
        {
            throw new InvalidOperationException("Backend exited during startup.");
        }
    }

    public async Task<JsonElement> RequestAsync(string type, object payload, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            _pending[id] = completion;
        }

        var message = JsonSerializer.Serialize(new { id, type, payload }, _jsonOptions);
        using var document = JsonDocument.Parse(message);
        var values = new Dictionary<string, object?> { ["id"] = id, ["type"] = type };
        foreach (var property in document.RootElement.GetProperty("payload").EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        await SendAsync(values, cancellationToken);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task;
    }

    public Task SendCommandAsync(string type, object payload, CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString("N"), ["type"] = type };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, _jsonOptions));
        foreach (var property in document.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        return SendAsync(values, cancellationToken);
    }

    private async Task SendAsync(object value, CancellationToken cancellationToken)
    {
        if (_process is null)
        {
            throw new InvalidOperationException("Backend is not running.");
        }

        var line = JsonSerializer.Serialize(value, _jsonOptions);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadEventsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            await ReadEventsCoreAsync(reader, cancellationToken);
        }
        finally
        {
            FailPending("Backend exited before responding.");
        }
    }

    private void FailPending(string message)
    {
        List<TaskCompletionSource<JsonElement>> orphaned;
        lock (_pending)
        {
            orphaned = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var completion in orphaned)
        {
            completion.TrySetException(new InvalidOperationException(message));
        }
    }

    private async Task ReadEventsCoreAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();
            LogEvent(root, line);
            if (root.TryGetProperty("request_id", out var requestIdElement))
            {
                var requestId = requestIdElement.GetString();
                TaskCompletionSource<JsonElement>? completion = null;
                if (requestId is not null)
                {
                    lock (_pending)
                    {
                        if (_pending.Remove(requestId, out var found))
                        {
                            completion = found;
                        }
                    }
                }

                if (completion is not null)
                {
                    if (root.GetProperty("type").GetString() == "error")
                    {
                        completion.TrySetException(new InvalidOperationException(root.GetProperty("message").GetString()));
                    }
                    else
                    {
                        completion.TrySetResult(root);
                    }
                }
            }

            EventReceived?.Invoke(this, root);
        }
    }

    private async Task ReadDiagnosticsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            _runLog.Write("backend", line);
            DiagnosticReceived?.Invoke(this, line);
        }
    }

    private void LogEvent(JsonElement root, string rawLine)
    {
        var type = root.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;
        if (type == "track_progress")
        {
            return;
        }
        if (type == "track_result")
        {
            var trackId = ReadString(root, "track_id") ?? "unknown";
            var success = root.TryGetProperty("success", out var successElement) &&
                successElement.ValueKind == JsonValueKind.True;
            var errorClass = ReadString(root, "error_class") ?? "unknown";
            var detail = success
                ? ReadString(root, "path") ?? "saved"
                : ReadString(root, "error") ?? "No provider detail";
            _runLog.Write("track", $"{(success ? "DONE" : "FAILED")} {trackId} [{errorClass}] {detail}");
            return;
        }
        _runLog.Write("event", rawLine);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private ProcessStartInfo CreateStartInfo()
    {
        var overridePath = SelectBackendOverride(
            _configuredBackendPath(),
            Environment.GetEnvironmentVariable("PLAYLISTDL_BACKEND_PATH"));
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return ToolStartInfo(overridePath);
        }

        var extracted = ToolBundleService.TryResolveBackend();
        if (extracted is not null)
        {
            return ToolStartInfo(extracted);
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "playlistdl-backend.exe");
        if (File.Exists(bundled))
        {
            return ToolStartInfo(bundled);
        }

        var repository = FindRepositoryRoot();
        var startInfo = BaseStartInfo("uv");
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(repository, "backend"));
        startInfo.ArgumentList.Add("playlistdl-backend");
        startInfo.WorkingDirectory = repository;
        return startInfo;
    }

    public static string? SelectBackendOverride(string? configuredPath, string? environmentPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath.Trim();
        }

        return string.IsNullOrWhiteSpace(environmentPath) ? null : environmentPath.Trim();
    }

    private static ProcessStartInfo BaseStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    private static ProcessStartInfo ToolStartInfo(string backendPath)
    {
        backendPath = Path.GetFullPath(backendPath);
        if (!File.Exists(backendPath))
        {
            throw new FileNotFoundException(
                "Configured backend executable was not found. Open Settings and choose a valid playlistdl-backend.exe, or clear the override.",
                backendPath);
        }

        var startInfo = BaseStartInfo(backendPath);
        var toolDirectory = Path.GetDirectoryName(backendPath)
            ?? throw new InvalidOperationException("Backend tool directory is unavailable.");
        startInfo.WorkingDirectory = toolDirectory;
        var siblingFfmpeg = Path.Combine(toolDirectory, "ffmpeg.exe");
        if (File.Exists(siblingFfmpeg))
        {
            startInfo.Environment["PLAYLISTDL_FFMPEG"] = siblingFfmpeg;
        }
        startInfo.Environment["PATH"] = toolDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        return startInfo;
    }

    public async Task RestartAsync()
    {
        await StopBackendAsync();
    }

    private static string FindRepositoryRoot()
    {
        foreach (var startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var current = new DirectoryInfo(startPath);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "backend")) &&
                    File.Exists(Path.Combine(current.FullName, "PlaylistDl.slnx")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found. Set PLAYLISTDL_BACKEND_PATH.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopBackendAsync();
        _writeLock.Dispose();
    }

    private async Task StopBackendAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                await SendCommandAsync("shutdown", new { });
                // Closing stdin gives the backend a second, EOF-based exit path.
                _process.StandardInput.Close();
            }
            catch
            {
                // Best-effort shutdown during app exit.
            }

            if (!_process.WaitForExit(1500))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }

        _process?.Dispose();
        _process = null;
    }
}

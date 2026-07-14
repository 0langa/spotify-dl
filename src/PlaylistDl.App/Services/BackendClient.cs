using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PlaylistDl.App.Services;

public sealed class BackendClient : IAsyncDisposable
{
    private const int SupportedProtocol = 1;
    private readonly Func<string?> _configuredBackendPath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _startLock = new(1, 1);
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
    public event EventHandler<string>? OutdatedBackendRejected;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false })
            {
                return;
            }
            _process?.Dispose();
            _process = null;

            var (startInfo, overridePath) = CreateStartInfo(allowOverride: true);
            var launchedVersion = await StartProcessAsync(startInfo, cancellationToken);
            var bundledVersion = overridePath is null
                ? null
                : ToolBundleService.TryResolveBackendVersion();
            if (overridePath is null || !IsBackendVersionOutdated(launchedVersion, bundledVersion))
            {
                return;
            }

            _runLog.Write(
                "app",
                $"Rejecting outdated alternate backend {launchedVersion}; bundled backend is {bundledVersion}.");
            await StopBackendAsync();
            var (bundledStartInfo, _) = CreateStartInfo(allowOverride: false);
            await StartProcessAsync(bundledStartInfo, cancellationToken);
            OutdatedBackendRejected?.Invoke(this, overridePath);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<string?> StartProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void ReadyHandler(object? sender, JsonElement message)
        {
            if (message.TryGetProperty("type", out var type) && type.GetString() == "ready")
            {
                var protocol = message.TryGetProperty("protocol", out var protocolElement) &&
                    protocolElement.TryGetInt32(out var value)
                    ? value
                    : (int?)null;
                if (!IsSupportedProtocol(protocol))
                {
                    ready.TrySetException(new InvalidDataException(
                        $"Backend protocol {protocol?.ToString() ?? "missing"} is incompatible with client protocol {SupportedProtocol}."));
                    return;
                }

                ready.TrySetResult(
                    message.TryGetProperty("version", out var version) ? version.GetString() : null);
            }
        }

        EventReceived += ReadyHandler;
        _runLog.Write("app", $"Starting backend: {startInfo.FileName}");
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process = process;
        var started = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Backend process failed to start.");
            }
            started = true;

            // The readers belong to the backend lifetime, not to the request that happened
            // to start it. A cancelled request must not silently disconnect a healthy backend.
            _ = Task.Run(() => ReadEventsAsync(process.StandardOutput, CancellationToken.None));
            _ = Task.Run(() => ReadDiagnosticsAsync(process.StandardError, CancellationToken.None));
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch
        {
            if (started && ReferenceEquals(_process, process))
            {
                await StopBackendAsync();
            }
            else
            {
                process.Dispose();
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }
            throw;
        }
        finally
        {
            EventReceived -= ReadyHandler;
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

        try
        {
            await SendAsync(values, cancellationToken);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task;
        }
        finally
        {
            lock (_pending)
            {
                _pending.Remove(id);
            }
        }
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
        if (_process is not { HasExited: false })
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
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement.Clone();
                LogEvent(root, line);
                var eventType = root.TryGetProperty("type", out var typeElement) &&
                    typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : throw new InvalidDataException("Backend event has no string type.");
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
                        if (eventType == "error")
                        {
                            var message = root.TryGetProperty("message", out var messageElement) &&
                                messageElement.ValueKind == JsonValueKind.String
                                ? messageElement.GetString()
                                : "Backend returned an error without a message.";
                            completion.TrySetException(new InvalidOperationException(message));
                        }
                        else
                        {
                            completion.TrySetResult(root);
                        }
                    }
                }

                EventReceived?.Invoke(this, root);
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or InvalidOperationException or
                KeyNotFoundException)
            {
                _runLog.Write("protocol", $"Ignored malformed backend event: {exception.Message}");
            }
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

    private (ProcessStartInfo StartInfo, string? OverridePath) CreateStartInfo(bool allowOverride)
    {
        var overridePath = allowOverride
            ? SelectBackendOverride(
                _configuredBackendPath(),
                Environment.GetEnvironmentVariable("PLAYLISTDL_BACKEND_PATH"))
            : null;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return (ToolStartInfo(overridePath), overridePath);
        }

        var extracted = ToolBundleService.TryResolveBackend();
        if (extracted is not null)
        {
            return (ToolStartInfo(extracted), null);
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "playlistdl-backend.exe");
        if (File.Exists(bundled))
        {
            return (ToolStartInfo(bundled), null);
        }

        var repository = FindRepositoryRoot();
        var startInfo = BaseStartInfo("uv");
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(repository, "backend"));
        startInfo.ArgumentList.Add("playlistdl-backend");
        startInfo.WorkingDirectory = repository;
        return (startInfo, null);
    }

    public static bool IsBackendVersionOutdated(string? launchedVersion, string? bundledVersion) =>
        Version.TryParse(launchedVersion, out var launched) &&
        Version.TryParse(bundledVersion, out var bundled) &&
        launched < bundled;

    public static bool IsSupportedProtocol(int? protocol) => protocol == SupportedProtocol;

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
        _startLock.Dispose();
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

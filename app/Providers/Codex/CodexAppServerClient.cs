using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuotaScope.Providers.Codex;

internal sealed class CodexAppServerClient : IDisposable
{
    private const string RateLimitsReadMethod = "account/rateLimits/read";

    private readonly ProviderSettings _settings;
    private readonly ConcurrentDictionary<int, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly object _latestReadLock = new();
    private Process? _process;
    private int _nextId;
    private bool _initialized;
    private CancellationTokenSource? _readerCts;
    private string? _lastError;
    // The last account/rateLimits/read result with every rolling update since
    // merged in. The read loop writes it and a reconnect clears it from another
    // thread, hence the lock.
    private JsonElement? _latestRead;

    private sealed record PendingRequest(string Method, TaskCompletionSource<JsonElement> Completion);

    public string ResolvedCommandText => CodexCommandResolver.Resolve(_settings.Command).DisplayText;

    public event Action<ProviderUsage>? RateLimitsUpdated;

    public CodexAppServerClient(ProviderSettings settings)
    {
        _settings = settings;
    }

    public async Task<ProviderUsage> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRateLimitsWithoutRestartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderUsage> RestartAsync(CancellationToken cancellationToken)
    {
        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisposeProcessOnly();
            await StartProcessAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startLock.Release();
        }

        return await ReadRateLimitsWithoutRestartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderUsage> ReadRateLimitsWithoutRestartAsync(CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync(RateLimitsReadMethod, null, cancellationToken).ConfigureAwait(false);
        return RateLimitMapper.FromJsonResult(result, _settings.CreditsFullAmount);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _initialized) return;

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _initialized) return;

            DisposeProcessOnly();
            await StartProcessAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task StartProcessAsync(CancellationToken cancellationToken)
    {
        _lastError = null;
        var command = CodexCommandResolver.Resolve(_settings.Command);
        var psi = new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start codex app-server.");
        _readerCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_readerCts.Token));
        _ = Task.Run(() => DrainErrorsAsync(_readerCts.Token));

        var initializeParams = new
        {
            clientInfo = new { name = "quota-scope", title = "QuotaScope", version = "0.1.0" },
            capabilities = new
            {
                experimentalApi = true,
                optOutNotificationMethods = Array.Empty<string>()
            }
        };
        await SendRequestAsync("initialize", initializeParams, cancellationToken).ConfigureAwait(false);

        // codex app-server (>= 0.146) gates every subsequent request until the client
        // acknowledges the handshake with an `initialized` notification. Without it the
        // server silently drops `account/rateLimits/read`, which surfaces as a timeout /
        // "Codex connection required" in the popup.
        await SendNotificationAsync("initialized", null, cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    private async Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (_process?.StandardInput is null) throw new InvalidOperationException("Codex app-server is not running.");

        var notification = parameters is null
            ? JsonSerializer.Serialize(new { method })
            : JsonSerializer.Serialize(new { method, @params = parameters });

        try
        {
            await _process.StandardInput.WriteLineAsync(notification.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            throw CreateProcessFailure(method, ex);
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (_process?.StandardInput is null) throw new InvalidOperationException("Codex app-server is not running.");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new PendingRequest(method, tcs);

        var request = parameters is null
            ? JsonSerializer.Serialize(new { id, method, @params = (object?)null })
            : JsonSerializer.Serialize(new { id, method, @params = parameters });

        try
        {
            await _process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _pending.TryRemove(id, out _);
            throw CreateProcessFailure(method, ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        await using (timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token)))
        {
            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_process?.HasExited == true || !string.IsNullOrWhiteSpace(_lastError))
            {
                throw CreateProcessFailure(method, null);
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _process is { HasExited: false })
            {
                var line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement.Clone();
                if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
                {
                    if (_pending.TryRemove(id, out var pending))
                    {
                        if (root.TryGetProperty("error", out var error))
                        {
                            pending.Completion.TrySetException(new InvalidOperationException(error.ToString()));
                        }
                        else if (root.TryGetProperty("result", out var result))
                        {
                            var response = result.Clone();
                            // Cached here rather than by the awaiting caller, so an
                            // update right behind this response merges into it
                            // instead of into the read before it.
                            if (pending.Method == RateLimitsReadMethod) RememberRead(response, cancellationToken);
                            pending.Completion.TrySetResult(response);
                        }
                    }
                    continue;
                }

                if (root.TryGetProperty("method", out var methodElement)
                    && methodElement.GetString() == "account/rateLimits/updated"
                    && root.TryGetProperty("params", out var parameters)
                    && MergeIntoLatestRead(parameters, cancellationToken) is { } usage)
                {
                    RateLimitsUpdated?.Invoke(usage);
                }
            }
        }
        catch
        {
            _initialized = false;
        }
    }

    // Both run on the read loop and check its token under the lock: a reconnect
    // cancels the old loop before clearing the cache, so a line the old process
    // left behind cannot seed or update the new process's cache.
    private void RememberRead(JsonElement result, CancellationToken cancellationToken)
    {
        lock (_latestReadLock)
        {
            if (!cancellationToken.IsCancellationRequested) _latestRead = result;
        }
    }

    // A rolling update only carries what changed, so it is merged into the
    // latest read instead of being mapped on its own. Before the first read there
    // is nothing to merge into, and the regular poll delivers the full snapshot.
    // A malformed update is dropped here: an exception would end the read loop,
    // which is also what completes pending requests.
    private ProviderUsage? MergeIntoLatestRead(JsonElement parameters, CancellationToken cancellationToken)
    {
        lock (_latestReadLock)
        {
            if (cancellationToken.IsCancellationRequested || _latestRead is not { } latest) return null;
            try
            {
                var merged = RateLimitMapper.MergeRollingUpdate(latest, parameters);
                var usage = RateLimitMapper.FromJsonResult(merged, _settings.CreditsFullAmount);
                _latestRead = merged;
                return usage;
            }
            catch
            {
                return null;
            }
        }
    }

    private async Task DrainErrorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _process is { HasExited: false })
            {
                var line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _lastError = line.Trim();
                }
            }
        }
        catch
        {
        }
    }

    private InvalidOperationException CreateProcessFailure(string method, Exception? inner)
    {
        var detail = !string.IsNullOrWhiteSpace(_lastError)
            ? _lastError
            : _process?.HasExited == true
                ? $"process exited with code {_process.ExitCode}"
                : "no response from codex app-server";
        return new InvalidOperationException($"Codex app-server failed during {method}: {detail}", inner);
    }

    private void DisposeProcessOnly()
    {
        _initialized = false;
        _readerCts?.Cancel();
        _readerCts?.Dispose();
        _readerCts = null;
        // The next process may be signed in to another account; its updates
        // must wait for its own first read.
        lock (_latestReadLock)
        {
            _latestRead = null;
        }
        foreach (var pending in _pending)
        {
            pending.Value.Completion.TrySetCanceled();
        }
        _pending.Clear();

        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        _process.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        DisposeProcessOnly();
        _startLock.Dispose();
    }
}





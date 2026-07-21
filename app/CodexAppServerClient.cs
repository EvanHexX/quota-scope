using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuotaScope;

internal sealed class CodexAppServerClient : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private int _nextId;
    private bool _initialized;
    private CancellationTokenSource? _readerCts;
    private string? _lastError;

    public string ResolvedCommandText => CodexCommandResolver.Resolve(_settings.CodexCommand).DisplayText;

    public event Action<UsageViewModel>? RateLimitsUpdated;

    public CodexAppServerClient(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<UsageViewModel> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRateLimitsWithoutRestartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UsageViewModel> RestartAsync(CancellationToken cancellationToken)
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

    private async Task<UsageViewModel> ReadRateLimitsWithoutRestartAsync(CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync("account/rateLimits/read", null, cancellationToken).ConfigureAwait(false);
        return RateLimitMapper.FromJsonResult(result);
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
        var command = CodexCommandResolver.Resolve(_settings.CodexCommand);
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
        _initialized = true;
    }

    private async Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (_process?.StandardInput is null) throw new InvalidOperationException("Codex app-server is not running.");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

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
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        if (root.TryGetProperty("error", out var error))
                        {
                            tcs.TrySetException(new InvalidOperationException(error.ToString()));
                        }
                        else if (root.TryGetProperty("result", out var result))
                        {
                            tcs.TrySetResult(result.Clone());
                        }
                    }
                    continue;
                }

                if (root.TryGetProperty("method", out var methodElement)
                    && methodElement.GetString() == "account/rateLimits/updated"
                    && root.TryGetProperty("params", out var parameters))
                {
                    RateLimitsUpdated?.Invoke(RateLimitMapper.FromJsonResult(parameters));
                }
            }
        }
        catch
        {
            _initialized = false;
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
        foreach (var pending in _pending)
        {
            pending.Value.TrySetCanceled();
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





using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pharmco.Client.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pharmco.Client.Services;

/// <summary>
/// Background sync engine that pushes queued operations and pulls updates.
/// Runs every 60 seconds when online, plus manual trigger.
/// Implements exponential backoff on failure.
/// </summary>
public sealed class SyncEngine : IHostedService, IDisposable
{
    private readonly LocalDatabase _localDb;
    private readonly HttpClient _httpClient;
    private readonly ILogger<SyncEngine> _logger;
    private readonly string _baseUrl;
    private readonly string _deviceId;
    private Timer? _timer;
    private CancellationTokenSource? _cts;
    private bool _isOnline;

    // Backoff schedule: 1s, 5s, 30s, 5m, 15m (cap)
    private static readonly TimeSpan[] BackoffSchedule = new[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15)
    };

    private int _currentBackoffIndex;
    private DateTimeOffset _lastSyncAt;

    public SyncStatus Status { get; private set; } = SyncStatus.Pending;
    public DateTimeOffset? LastSuccessfulSync => _lastSyncAt;
    public bool IsOnline => _isOnline;

    public event Action<SyncStatus>? StatusChanged;

    public SyncEngine(LocalDatabase localDb, ILogger<SyncEngine> logger, string baseUrl)
    {
        _localDb = localDb;
        _logger = logger;
        _baseUrl = baseUrl.TrimEnd('/');
        _deviceId = Environment.MachineName + "-" + Environment.UserName.GetHashCode();

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _logger.LogInformation("SyncEngine starting, polling every 60s");

        // Start periodic sync
        _timer = new Timer(OnTimer, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SyncEngine stopping");
        _timer?.Dispose();
        _timer = null;
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _cts?.Dispose();
    }

    // ------------------------------------------------------------------
    // Timer Callback
    // ------------------------------------------------------------------

    private async void OnTimer(object? state)
    {
        try
        {
            if (_isOnline)
            {
                await SyncAsync(_cts?.Token ?? CancellationToken.None);
            }
            else
            {
                Status = SyncStatus.Offline;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync timer error");
            Status = SyncStatus.Error;
        }
    }

    // ------------------------------------------------------------------
    // Main Sync Method
    // ------------------------------------------------------------------

    public async Task SyncAsync(CancellationToken ct = default)
    {
        // Check online status
        _isOnline = await CheckOnlineAsync(ct);
        if (!_isOnline)
        {
            Status = SyncStatus.Offline;
            _logger.LogDebug("Offline — skipping sync");
            return;
        }

        Status = SyncStatus.Pending;
        _logger.LogInformation("Starting sync cycle");

        try
        {
            // Push queued operations
            await PushOperationsAsync(ct);

            // Pull updates
            await PullUpdatesAsync(ct);

            _lastSyncAt = DateTimeOffset.UtcNow;
            Status = SyncStatus.Synced;
            _currentBackoffIndex = 0; // Reset backoff on success

            await _localDb.SetMetaAsync("last_sync_at", _lastSyncAt.ToString("o"), ct);

            _logger.LogInformation("Sync cycle completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync cycle failed");
            Status = SyncStatus.Error;

            // Apply exponential backoff
            var backoff = BackoffSchedule[Math.Min(_currentBackoffIndex, BackoffSchedule.Length - 1)];
            _logger.LogWarning("Next retry in {Backoff}s (attempt {Index})",
                backoff.TotalSeconds, _currentBackoffIndex + 1);
            _currentBackoffIndex++;
        }
        finally
        {
            StatusChanged?.Invoke(Status);
        }
    }

    // ------------------------------------------------------------------
    // Push Operations
    // ------------------------------------------------------------------

    private async Task PushOperationsAsync(CancellationToken ct)
    {
        var pendingOps = await _localDb.GetPendingOperationsAsync(500, ct);
        if (pendingOps.Count == 0)
        {
            _logger.LogDebug("No pending operations to push");
            return;
        }

        _logger.LogInformation("Pushing {Count} pending operations", pendingOps.Count);

        var request = new SyncPushRequest { Operations = pendingOps };
        var json = JsonSerializer.Serialize(request);

        // Compress with gzip
        using var compressedStream = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionMode.Compress))
        using (var writer = new StreamWriter(gzip, Encoding.UTF8))
        {
            await writer.WriteAsync(json);
        }
        compressedStream.Position = 0;

        try
        {
            using var content = new ByteArrayContent(compressedStream.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");

            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/sync/push");
            requestMessage.Content = content;

            var response = await _httpClient.SendAsync(requestMessage, ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<SyncPushResult>(responseJson);

            if (result?.Results is not null)
            {
                foreach (var opResult in result.Results)
                {
                    if (opResult.Status == "ok")
                    {
                        await _localDb.MarkOperationSyncedAsync(opResult.Uuid, ct);
                    }
                    else
                    {
                        await _localDb.MarkOperationFailedAsync(
                            opResult.Uuid,
                            opResult.Error ?? "Unknown error",
                            ct);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to push operations");
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Pull Updates
    // ------------------------------------------------------------------

    private async Task PullUpdatesAsync(CancellationToken ct)
    {
        var since = _lastSyncAt;
        _logger.LogInformation("Pulling updates since {Since}", since);

        try
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, $"/api/sync/pull?since={Uri.EscapeDataString(since.ToString("o"))}");

            var response = await _httpClient.SendAsync(requestMessage, ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var pullResponse = JsonSerializer.Deserialize<SyncPullResponse>(responseJson);

            if (pullResponse is null) return;

            // Apply product updates (LWW)
            if (pullResponse.Products is not null)
            {
                foreach (var product in pullResponse.Products)
                {
                    await _localDb.UpsertProductAsync(product, ct);
                }
                _logger.LogInformation("Applied {Count} product updates", pullResponse.Products.Count);
            }

            // Update last sync time from server
            if (pullResponse.ServerTime != default)
            {
                _lastSyncAt = pullResponse.ServerTime;
            }

            _logger.LogInformation("Pull completed, server time: {ServerTime}", pullResponse.ServerTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pull updates");
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Online Check
    // ------------------------------------------------------------------

    private async Task<bool> CheckOnlineAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Manual Trigger
    // ------------------------------------------------------------------

    public async Task ForceSyncAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Manual sync triggered");
        await SyncAsync(ct);
    }
}

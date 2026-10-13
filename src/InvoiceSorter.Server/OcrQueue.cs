using InvoiceSorter.Core.Central;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Processing;

namespace InvoiceSorter.Server;

/// <summary>
/// The Surya queue: one Surya model on the GPU reads the pages one after the other, first come first served.
/// Clients send one page at a time and wait for it, so the pages of several computers alternate fairly.
/// </summary>
public sealed class OcrQueue(ServerConfig config, ServerPaths paths) : BackgroundService
{
    private sealed class Job
    {
        public required OcrJobInfo Info { get; init; }
        public required byte[] Image { get; set; }
        public required string Extension { get; init; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public CancellationTokenSource Cancel { get; } = new();
    }

    private readonly object _sync = new();
    private readonly LinkedList<Job> _waiting = new();
    private readonly Dictionary<string, Job> _jobs = new();
    private readonly SemaphoreSlim _signal = new(0);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Job? _running;
    private SuryaOcr? _surya;
    private readonly SuryaState _state = new() { State = config.SuryaEnabled ? "starting" : "off" };
    private int _processed, _failed;
    private double _busySeconds;
    private CancellationToken _stopping;

    public SuryaState Surya
    {
        get { lock (_sync) return new SuryaState { State = _state.State, Device = _state.Device, Version = _state.Version, Message = _state.Message }; }
    }

    public QueueState Queue
    {
        get
        {
            lock (_sync)
                return new QueueState
                {
                    Waiting = _waiting.Count, Running = _running != null ? 1 : 0, Processed = _processed, Failed = _failed,
                    AverageSeconds = _processed > 0 ? Math.Round(_busySeconds / _processed, 1) : null,
                };
        }
    }

    // ------------------------------------------------------------------------------------------ API

    /// <summary>Adds a page to the queue. Throws when Surya is off or the queue is full.</summary>
    public OcrJobInfo Submit(byte[] image, string contentType, string client)
    {
        lock (_sync)
        {
            if (_state.State is "off" or "error")
                throw new QueueUnavailableException(_state.State == "off"
                    ? "Ezen a szerveren nincs bekapcsolva a Surya OCR."
                    : $"A szerver Surya OCR-je hibás: {_state.Message}");
            if (_waiting.Count >= config.MaxQueue)
                throw new QueueFullException($"A sor megtelt ({config.MaxQueue} oldal vár). Próbáld újra később.");
            var job = new Job
            {
                Info = new OcrJobInfo { Id = Guid.NewGuid().ToString("N"), Status = "queued", Client = client, QueuedAt = DateTime.Now },
                Image = image,
                Extension = contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg",
            };
            _jobs[job.Info.Id] = job;
            _waiting.AddLast(job);
            job.Info.Position = _waiting.Count;
            _signal.Release();
            return Snapshot(job, withResult: false);
        }
    }

    /// <summary>The job's state; waits up to <paramref name="wait"/> for it to finish or move in the queue.</summary>
    public async Task<OcrJobInfo?> GetAsync(string id, TimeSpan wait, CancellationToken ct)
    {
        Job? job;
        Task changed;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out job)) return null;
            job.LastSeen = DateTime.UtcNow;
            if (job.Info.IsFinished) return Snapshot(job, withResult: true);
            changed = _changed.Task;
        }
        if (wait > TimeSpan.Zero)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping);
            await Task.WhenAny(job.Done.Task, changed, Task.Delay(wait, cts.Token)).ConfigureAwait(false);
        }
        lock (_sync)
        {
            job.LastSeen = DateTime.UtcNow;
            return Snapshot(job, withResult: true);
        }
    }

    public bool Cancel(string id)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out var job) || job.Info.IsFinished) return false;
            if (_waiting.Remove(job))
            {
                Finish(job, "cancelled", null);
            }
            else
            {
                job.Cancel.Cancel();   // running: the worker stops waiting for it
            }
            return true;
        }
    }

    /// <summary>All jobs (without images and results) – for the server manager.</summary>
    public List<OcrJobInfo> List()
    {
        lock (_sync)
        {
            UpdatePositions();
            return _jobs.Values.OrderBy(j => j.Info.QueuedAt).Select(j => Snapshot(j, withResult: false)).ToList();
        }
    }

    /// <summary>Stops and starts the Surya worker again (e.g. after installing Surya).</summary>
    public async Task RestartSuryaAsync()
    {
        SuryaOcr? old;
        lock (_sync)
        {
            old = _surya;
            _surya = null;
            _state.State = config.SuryaEnabled ? "starting" : "off";
            _state.Message = null;
        }
        old?.Dispose();
        if (config.SuryaEnabled) await StartSuryaAsync(_stopping);
    }

    // ------------------------------------------------------------------------------------------ worker

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        Directory.CreateDirectory(paths.TempDir);
        foreach (var f in Directory.GetFiles(paths.TempDir)) { try { File.Delete(f); } catch { } }
        if (config.SuryaEnabled) _ = StartSuryaAsync(stoppingToken);
        else AppLog.Info("A Surya OCR ki van kapcsolva ezen a szerveren (szerverkezelő › Szerver beállításai)");

        while (!stoppingToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            CleanUp();
            while (!stoppingToken.IsCancellationRequested)
            {
                Job? job;
                SuryaOcr? surya;
                lock (_sync)
                {
                    if (_state.State != "ready" || _waiting.First == null) break;
                    job = _waiting.First.Value;
                    _waiting.RemoveFirst();
                    _running = job;
                    job.Info.Status = "running";
                    job.Info.Position = 0;
                    job.Info.StartedAt = DateTime.Now;
                    surya = _surya;
                    Changed();
                }
                await RunAsync(job, surya!, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunAsync(Job job, SuryaOcr surya, CancellationToken stoppingToken)
    {
        var file = Path.Combine(paths.TempDir, job.Info.Id + job.Extension);
        var started = DateTime.UtcNow;
        try
        {
            await File.WriteAllBytesAsync(file, job.Image, stoppingToken);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancel.Token);
            cts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, config.JobTimeoutMinutes)));
            var result = await surya.RecognizeRawAsync(file, cts.Token).ConfigureAwait(false);
            lock (_sync)
            {
                _processed++;
                _busySeconds += (DateTime.UtcNow - started).TotalSeconds;
                job.Info.Result = result;
                Finish(job, "done", null);
            }
            AppLog.Info($"OCR kész: {job.Info.Client} – {result.Lines?.Count ?? 0} sor, {(DateTime.UtcNow - started).TotalSeconds:0.0} mp");
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            var cancelled = job.Cancel.IsCancellationRequested;
            lock (_sync)
            {
                if (!cancelled) _failed++;
                Finish(job, cancelled ? "cancelled" : "failed", cancelled ? null : ex.Message);
            }
            if (!cancelled) AppLog.Warn($"OCR sikertelen ({job.Info.Client}): {ex.Message}");
            // A stopped or hanging (timed out) worker is started again for the next page. A page cancelled by its
            // client needs no restart: the worker skips the late answer by its request id.
            if (!cancelled && (!surya.IsAlive || ex is OperationCanceledException)) await RestartSuryaAsync();
        }
        finally
        {
            try { File.Delete(file); } catch { }
            lock (_sync) { if (_running == job) _running = null; Changed(); }
        }
    }

    private async Task StartSuryaAsync(CancellationToken ct)
    {
        try
        {
            lock (_sync) { _state.State = "starting"; _state.Message = null; Changed(); }
            var surya = await SuryaOcr.StartLocalAsync(config.SuryaPython, ct).ConfigureAwait(false);
            lock (_sync)
            {
                _surya = surya;
                _state.State = "ready";
                _state.Device = surya.Device;
                _state.Version = surya.Version;
                Changed();
            }
            _signal.Release();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            lock (_sync)
            {
                _state.State = "error";
                _state.Message = ex.Message;
                // Waiting pages cannot be read: tell their clients (they continue with Windows OCR).
                foreach (var job in _waiting.ToList()) Finish(job, "failed", "A szerver Surya OCR-je nem indult el: " + ex.Message);
                _waiting.Clear();
                Changed();
            }
            AppLog.Error("A Surya OCR nem indult el", ex);
        }
    }

    // ------------------------------------------------------------------------------------------ helpers

    private void Finish(Job job, string status, string? error)
    {
        job.Info.Status = status;
        job.Info.Error = error;
        job.Info.FinishedAt = DateTime.Now;
        job.Info.Position = 0;
        job.Image = Array.Empty<byte>();   // the page image is not kept once the page is done
        job.Done.TrySetResult();
        Changed();
    }

    /// <summary>Wakes every client waiting for news (a page started or finished: the positions moved).</summary>
    private void Changed()
    {
        UpdatePositions();
        var old = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }

    private void UpdatePositions()
    {
        int i = 1;
        foreach (var job in _waiting) job.Info.Position = i++;
    }

    /// <summary>Forgets finished jobs after 10 minutes and drops waiting pages whose computer stopped asking.</summary>
    private void CleanUp()
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            foreach (var job in _waiting.Where(j => now - j.LastSeen > TimeSpan.FromMinutes(3)).ToList())
            {
                _waiting.Remove(job);
                Finish(job, "cancelled", "A kliens nem jelentkezett – törölve a sorból.");
            }
            foreach (var id in _jobs.Where(kv => kv.Value.Info.FinishedAt is { } f && DateTime.Now - f > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList())
                _jobs.Remove(id);
        }
    }

    private static OcrJobInfo Snapshot(Job job, bool withResult) => new()
    {
        Id = job.Info.Id, Status = job.Info.Status, Position = job.Info.Position, Client = job.Info.Client,
        QueuedAt = job.Info.QueuedAt, StartedAt = job.Info.StartedAt, FinishedAt = job.Info.FinishedAt, Error = job.Info.Error,
        Result = withResult ? job.Info.Result : null,
    };

    public override void Dispose()
    {
        _surya?.Dispose();
        base.Dispose();
    }
}

public sealed class QueueFullException(string message) : Exception(message);
public sealed class QueueUnavailableException(string message) : Exception(message);

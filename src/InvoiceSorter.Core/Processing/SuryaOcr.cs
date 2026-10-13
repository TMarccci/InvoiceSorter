using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using InvoiceSorter.Core.Central;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Core.Models;

namespace InvoiceSorter.Core.Processing;

/// <summary>
/// Optional OCR with Surya (https://github.com/datalab-to/surya), a deep-learning OCR model that is usually more
/// accurate than the built-in Windows OCR on faded or small print. It runs either
/// <list type="bullet">
/// <item>on this computer, in a separate Python process (surya_worker.py) that loads the models once and then answers
/// requests: one JSON line per image in, one JSON line with the recognized lines out; or</item>
/// <item>on an Invoice Sorter server (central configuration, or a server set for Surya only): every page is a job in
/// the server's queue – other computers' pages are read before or after it, one at a time.</item>
/// </list>
/// Locally nothing is sent anywhere; with a server the page images go to that server only.
/// </summary>
public sealed class SuryaOcr : IDisposable
{
    private readonly Process? _process;
    private readonly CentralClient? _server;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _requestId;

    /// <summary>"cuda" / "cpu"; for a server "cuda – 192.168.1.20:8770".</summary>
    public string Device { get; }

    /// <summary>True when the pages are read by a server.</summary>
    public bool IsServer => _server != null;

    /// <summary>
    /// Waiting in the server's queue: "Sorban áll a szerveren: 3. hely" (empty when the page is being read).
    /// Raised from a background thread.
    /// </summary>
    public static event Action<string>? QueueStatusChanged;

    private SuryaOcr(Process process, string device)
    {
        _process = process;
        Device = device;
    }

    private SuryaOcr(CentralClient server, string device)
    {
        _server = server;
        Device = device;
    }

    /// <summary>Folder of the Surya environment (the app's own, or the server's in its data folder).</summary>
    public static string HomeDir => Path.Combine(AppPaths.LocalDir, "surya");

    /// <summary>The python.exe to use: the configured one, otherwise the own environment if it exists.</summary>
    public static string? FindPython(AppSettings settings) => FindPython(settings.SuryaPython);

    public static string? FindPython(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var own = Path.Combine(HomeDir, "venv", "Scripts", "python.exe");
        return File.Exists(own) ? own : null;
    }

    /// <summary>Where Surya reads pages with these settings: a server (central mode, or "Surya server") or this computer.</summary>
    public static (string Url, string Key)? ServerFor(AppSettings settings) =>
        settings.ConfigMode == ConfigMode.Central ? (settings.CentralServerUrl, settings.CentralClientKey)
        : settings.SuryaLocation == SuryaLocation.Server ? (settings.SuryaServerUrl, settings.SuryaApiKey)
        : null;

    /// <summary>
    /// Starts the local worker and waits until the models are loaded (the first start downloads them) – or checks
    /// that the server's Surya is ready.
    /// </summary>
    public static Task<SuryaOcr> StartAsync(AppSettings settings, CancellationToken ct = default) =>
        ServerFor(settings) is { } server
            ? ConnectAsync(server.Url, server.Key, settings.SuryaServerTimeoutSeconds, ct)
            : StartLocalAsync(settings.SuryaPython, ct);

    // ------------------------------------------------------------------------------------------ local worker

    /// <summary>Starts the Surya worker on this computer (also used by the server).</summary>
    public static async Task<SuryaOcr> StartLocalAsync(string? python, CancellationToken ct = default)
    {
        var exe = FindPython(python)
                  ?? throw new InvalidOperationException("A Surya nincs telepítve ezen a gépen. Telepítsd (kliensben: Beállítások › Surya OCR › Telepítés; szerveren: szerverkezelő › Szerver › Surya telepítése), vagy add meg a Python elérési útját.");
        Directory.CreateDirectory(HomeDir);
        var script = Path.Combine(HomeDir, "surya_worker.py");
        await File.WriteAllTextAsync(script, LoadWorkerScript(), new UTF8Encoding(false), ct);

        var psi = new ProcessStartInfo(exe, $"-u \"{script}\"")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = HomeDir,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        // The installer downloads the models next to the environment (needed for the server's service account).
        var models = Path.Combine(HomeDir, "models");
        if (Directory.Exists(models)) psi.Environment["MODEL_CACHE_DIR"] = models;

        AppLog.Info($"Surya OCR indítása ({exe})…");
        var process = Process.Start(psi) ?? throw new InvalidOperationException("A Python nem indítható el.");
        var stderr = new Queue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            AppLog.Debug("Surya: " + e.Data);
            lock (stderr)
            {
                // Progress bars ("Recognizing Text: 40%|…") are not worth keeping.
                if (e.Data.Contains("it/s]") || e.Data.Contains("s/it]")) return;
                stderr.Enqueue(e.Data.Length > 300 ? e.Data[..300] : e.Data);
                while (stderr.Count > 8) stderr.Dequeue();
            }
        };
        process.BeginErrorReadLine();

        // The first start may download ~1 GB of model weights.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token)
                       ?? throw new InvalidOperationException("A Surya betöltés közben leállt." + LastErrors(stderr));
            var msg = JsonSerializer.Deserialize<WorkerMessage>(line, Json);
            if (msg?.Error != null) throw new InvalidOperationException("Surya: " + msg.Error);
            if (msg?.Ready == true)
            {
                AppLog.Info($"A Surya OCR kész ({msg.Device}, surya {msg.Version})");
                return new SuryaOcr(process, msg.Device ?? "?") { Version = msg.Version, _stderr = stderr };
            }
        }
    }

    private Queue<string>? _stderr;

    /// <summary>The last error lines of the worker (Python traceback), for the error message.</summary>
    private static string LastErrors(Queue<string>? stderr)
    {
        if (stderr == null) return "";
        lock (stderr) return stderr.Count == 0 ? "" : " Python: " + string.Join(" | ", stderr);
    }

    /// <summary>The surya-ocr version of the local worker.</summary>
    public string? Version { get; private init; }

    /// <summary>True while the local worker process runs.</summary>
    public bool IsAlive => _process is { HasExited: false } || _server != null;

    /// <summary>The worker's answer for one image (pixel coordinates) – what the server sends to its clients.</summary>
    public async Task<WorkerMessage> RecognizeRawAsync(string imagePath, CancellationToken ct = default)
    {
        if (_process == null) throw new InvalidOperationException("Csak a helyi Surya ad nyers eredményt.");
        await _lock.WaitAsync(ct);
        try
        {
            if (_process.HasExited) throw new InvalidOperationException($"A Surya folyamat leállt (kilépési kód {_process.ExitCode})." + LastErrors(_stderr));
            int id = Interlocked.Increment(ref _requestId);
            var request = JsonSerializer.Serialize(new { id, image = imagePath });
            await _process.StandardInput.WriteLineAsync(request.AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);

            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(ct)
                           ?? throw new InvalidOperationException("A Surya folyamat leállt." + LastErrors(_stderr));
                var msg = JsonSerializer.Deserialize<WorkerMessage>(line, Json);
                if (msg == null || msg.Id != id) continue;
                if (msg.Error != null) throw new InvalidOperationException("Surya: " + msg.Error);
                return msg;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // ------------------------------------------------------------------------------------------ server

    /// <summary>Connects to an Invoice Sorter server and checks that its Surya is ready (waits up to 3 minutes while it starts).</summary>
    private static async Task<SuryaOcr> ConnectAsync(string url, string key, int timeoutSeconds, CancellationToken ct)
    {
        var client = new CentralClient(url, key, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 30, 3600)));
        try
        {
            var waitUntil = DateTime.UtcNow.AddMinutes(3);
            while (true)
            {
                var health = await client.GetHealthAsync(ct);
                switch (health.Surya.State)
                {
                    case "ready":
                        AppLog.Info($"A szerver Surya OCR-je kész ({client.Where}): surya {health.Surya.Version}, {health.Surya.Device}; " +
                                    $"várakozó oldalak: {health.Queue.Waiting}");
                        return new SuryaOcr(client, $"{health.Surya.Device ?? "?"} – {client.Where}");
                    case "off":
                        throw new InvalidOperationException($"A szerveren ({client.Where}) nincs bekapcsolva a Surya OCR (szerverkezelő › Szerver beállításai).");
                    case "error":
                        throw new InvalidOperationException($"A szerver Surya OCR-je hibás ({client.Where}): {health.Surya.Message}");
                }
                if (DateTime.UtcNow > waitUntil)
                    throw new InvalidOperationException($"A szerver ({client.Where}) még tölti a Surya modelljeit – próbáld újra néhány perc múlva.");
                AppLog.Info($"A szerver ({client.Where}) tölti a Surya modelljeit – várakozás…");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Sends the page to the server's queue and waits for its turn and the result.</summary>
    private async Task<List<OcrTextLine>> RecognizeOnServerAsync(string imagePath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, ct);
        var type = Path.GetExtension(imagePath).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        var job = await _server!.SubmitOcrJobAsync(bytes, type, ct);
        try
        {
            int lastPosition = -1;
            while (!job.IsFinished)
            {
                if (job.Status == "queued" && job.Position != lastPosition)
                {
                    lastPosition = job.Position;
                    QueueStatusChanged?.Invoke($"Sorban áll a szerveren: {job.Position}. hely");
                }
                else if (job.Status == "running" && lastPosition != 0)
                {
                    lastPosition = 0;
                    QueueStatusChanged?.Invoke("");
                }
                job = await _server.GetOcrJobAsync(job.Id, 20, ct);
            }
        }
        catch (OperationCanceledException)
        {
            await _server.CancelOcrJobAsync(job.Id);   // free the place in the queue
            throw;
        }
        QueueStatusChanged?.Invoke("");
        if (job.Status != "done" || job.Result == null)
            throw new InvalidOperationException("Szerver: " + (job.Error ?? "a Surya nem olvasta el az oldalt"));
        return ToLines(job.Result);
    }

    // ------------------------------------------------------------------------------------------ reading

    /// <summary>Recognizes the text of an image; coordinates are normalized like the Windows OCR result.</summary>
    public async Task<List<OcrTextLine>> RecognizeAsync(string imagePath, CancellationToken ct = default) =>
        _server != null ? await RecognizeOnServerAsync(imagePath, ct) : ToLines(await RecognizeRawAsync(imagePath, ct));

    /// <summary>Converts the worker / server answer: pixel boxes → normalized boxes; words get boxes from the line (by characters) if Surya gives none.</summary>
    public static List<OcrTextLine> ToLines(WorkerMessage msg)
    {
        double w = Math.Max(1, msg.Width), h = Math.Max(1, msg.Height);
        var lines = new List<OcrTextLine>();
        foreach (var l in msg.Lines ?? new())
        {
            if (string.IsNullOrWhiteSpace(l.Text) || l.Bbox is not { Length: 4 } b) continue;
            var line = new OcrTextLine();
            if (l.Words is { Count: > 0 })
            {
                foreach (var word in l.Words.Where(x => !string.IsNullOrWhiteSpace(x.Text) && x.Bbox is { Length: 4 }))
                    line.Words.Add(Box(word.Text!.Trim(), word.Bbox!, w, h));
            }
            else
            {
                // Spread the words over the line box in proportion to their length.
                var text = l.Text.Trim();
                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                double charW = (b[2] - b[0]) / Math.Max(1, text.Length);
                double x = b[0];
                foreach (var part in parts)
                {
                    line.Words.Add(Box(part, new[] { x, b[1], x + part.Length * charW, b[3] }, w, h));
                    x += (part.Length + 1) * charW;
                }
            }
            if (line.Words.Count > 0) lines.Add(line);
        }
        return lines;
    }

    private static OcrWordBox Box(string text, double[] b, double w, double h) => new()
    {
        Text = text,
        X = b[0] / w, Y = b[1] / h,
        W = Math.Max(0, b[2] - b[0]) / w, H = Math.Max(0, b[3] - b[1]) / h,
    };

    private static string LoadWorkerScript() => LoadResource("surya_worker.py");

    public static string LoadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("InvoiceSorter.Core." + name)
                           ?? throw new InvalidOperationException(name + " erőforrás hiányzik");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Opens a console window that installs Surya into <see cref="HomeDir"/> (see setup_surya.ps1): the app's own
    /// environment, or the server's in its data folder. The window stays open until Enter is pressed.
    /// </summary>
    public static Process StartInstaller()
    {
        Directory.CreateDirectory(HomeDir);
        var script = Path.Combine(HomeDir, "setup_surya.ps1");
        File.WriteAllText(script, LoadResource("setup_surya.ps1"), new UTF8Encoding(true));
        return Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -HomeDir \"{HomeDir}\"")
        {
            UseShellExecute = true,
            WorkingDirectory = HomeDir,
        }) ?? throw new InvalidOperationException("A PowerShell nem indítható el.");
    }

    /// <summary>Starts the worker (or connects to the server), reads one page and reports device and speed ("Surya tesztelése").</summary>
    public static async Task<string> TestAsync(AppSettings settings, string? imagePath, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var surya = await StartAsync(settings, ct);
        var load = sw.Elapsed;
        var what = surya.IsServer
            ? $"A szerver Surya OCR-je működik ({surya.Device}, kapcsolódás {load.TotalSeconds:0.0} mp)"
            : $"A Surya működik: {surya.Device.ToUpperInvariant()} (modellek betöltve {load.TotalSeconds:0} mp alatt)";
        if (imagePath == null || !File.Exists(imagePath)) return what + ".";
        sw.Restart();
        var lines = await surya.RecognizeAsync(imagePath, ct);
        return $"{what}: egy oldal {sw.Elapsed.TotalSeconds:0.0} mp alatt ({lines.Count} sor). Első sor: \"{lines.FirstOrDefault()?.Text}\"";
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>One answer of the Surya worker (also the server's OCR result): pixel size and recognized lines.</summary>
    public sealed class WorkerMessage
    {
        public int Id { get; set; }
        public bool Ready { get; set; }
        public string? Device { get; set; }
        public string? Version { get; set; }
        public string? Error { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public List<WorkerLine>? Lines { get; set; }
    }

    public sealed class WorkerLine
    {
        public string? Text { get; set; }
        public double[]? Bbox { get; set; }
        public double Confidence { get; set; }
        public List<WorkerLine>? Words { get; set; }
    }

    public void Dispose()
    {
        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.Close(); // the worker exits at end of input
                    if (!_process.WaitForExit(5000)) _process.Kill(entireProcessTree: true);
                }
            }
            catch { /* already gone */ }
            _process.Dispose();
        }
        _server?.Dispose();
        _lock.Dispose();
    }
}

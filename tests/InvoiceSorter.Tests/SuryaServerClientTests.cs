using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using InvoiceSorter.Core;
using InvoiceSorter.Core.Central;
using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;
using InvoiceSorter.Core.Storage;

namespace InvoiceSorter.Tests;

/// <summary>
/// The client side of the Invoice Sorter server (Surya queue and central configuration) against a small fake server
/// with the same HTTP API as src/InvoiceSorter.Server.
/// </summary>
public sealed class SuryaServerClientTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _image = Path.Combine(Path.GetTempPath(), $"surya-test-{Guid.NewGuid():N}.jpg");
    private readonly int _port;
    private int _jobs, _polls, _configDownloads;

    private const string Key = "secret-key";
    private string _suryaState = "ready";
    private string _server = ServerHealth.ServerName;
    private int _revision = 7;
    private long _receivedBytes;
    private string _receivedType = "";

    public SuryaServerClientTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
        File.WriteAllBytes(_image, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            var path = ctx.Request.Url!.AbsolutePath;
            object body;
            int status = 200;
            if (ctx.Request.Headers["Authorization"] != $"Bearer {Key}")
            {
                status = 401;
                body = new { error = "Hiányzó vagy hibás kulcs." };
            }
            else if (path == "/api/health")
            {
                body = new
                {
                    status = "ok", server = _server, api = 2, version = "1.1.0", machineName = "GPU-PC", revision = _revision,
                    surya = new { state = _suryaState, device = "cuda", version = "0.17.1", message = _suryaState == "error" ? "nincs python" : null },
                    queue = new { waiting = 0, running = 0, processed = _jobs, failed = 0 },
                };
            }
            else if (path == "/api/ocr/jobs" && ctx.Request.HttpMethod == "POST")
            {
                Interlocked.Increment(ref _jobs);
                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                _receivedBytes = ms.Length;
                _receivedType = ctx.Request.ContentType ?? "";
                status = 202;
                body = new { id = "job1", status = "queued", position = 2, client = "teszt" };
            }
            else if (path == "/api/ocr/jobs/job1" && ctx.Request.HttpMethod == "GET")
            {
                // First answer: still waiting (1st in line), then done.
                body = Interlocked.Increment(ref _polls) == 1
                    ? new { id = "job1", status = "queued", position = 1, client = "teszt" }
                    : new
                    {
                        id = "job1", status = "done", position = 0, client = "teszt",
                        result = new
                        {
                            width = 1000, height = 2000,
                            lines = new object[]
                            {
                                new { text = "Számlaszám: A0001210", bbox = new[] { 100.0, 200, 500, 240 }, confidence = 0.98, words = Array.Empty<object>() },
                                new { text = $"{_receivedBytes} bytes {_receivedType}", bbox = new[] { 100.0, 300, 400, 340 }, confidence = 0.9,
                                      words = new object[] { new { text = $"{_receivedBytes}", bbox = new[] { 100.0, 300, 150, 340 } } } },
                            },
                        },
                    };
            }
            else if (path == "/api/config/revision")
            {
                body = new { revision = _revision };
            }
            else if (path == "/api/config")
            {
                Interlocked.Increment(ref _configDownloads);
                var settings = AppSettings.CreateDefault();
                settings.ReceiverCompanies.Add(new ReceiverCompany { Name = "Minta Vevő 1926 Kft.", VatNumber = "99991018-2-41" });
                body = new CentralConfig
                {
                    Revision = _revision, UpdatedBy = "admin",
                    Styles = new List<InvoiceStyle> { new() { Id = "central-1", Name = "Központi sablon" } },
                    Settings = settings,
                };
            }
            else
            {
                status = 404;
                body = new { error = "Ismeretlen útvonal" };
            }
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, JsonStore.Options));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private AppSettings Settings(string key = Key) => new()
    {
        SuryaMode = SuryaMode.UnclearOnly,
        SuryaLocation = SuryaLocation.Server,
        SuryaServerUrl = $"127.0.0.1:{_port}",
        SuryaApiKey = key,
    };

    [Fact]
    public async Task ReadsAPageThroughTheServerQueue()
    {
        var positions = new List<string>();
        void OnQueue(string text) { lock (positions) positions.Add(text); }
        SuryaOcr.QueueStatusChanged += OnQueue;
        try
        {
            using var surya = await SuryaOcr.StartAsync(Settings());
            Assert.True(surya.IsServer);
            Assert.Equal($"cuda – 127.0.0.1:{_port}", surya.Device);

            var lines = await surya.RecognizeAsync(_image);
            Assert.Equal(2, lines.Count);
            Assert.Equal(new[] { "Számlaszám:", "A0001210" }, lines[0].Words.Select(w => w.Text));
            Assert.Equal(0.1, lines[0].Words[0].X, 3);
            Assert.Equal("4", lines[1].Words[0].Text);   // the image bytes arrived (4 bytes)
            Assert.StartsWith("image/jpeg", _receivedType);
            Assert.Equal(1, _jobs);
        }
        finally
        {
            SuryaOcr.QueueStatusChanged -= OnQueue;
        }
        Assert.Contains(positions, p => p.Contains("2. hely"));
        Assert.Contains(positions, p => p.Contains("1. hely"));
    }

    [Fact]
    public async Task CentralModeUsesTheConfigurationServerForSurya()
    {
        var settings = new AppSettings
        {
            SuryaMode = SuryaMode.UnclearOnly,
            SuryaLocation = SuryaLocation.Local,   // ignored in central mode
            ConfigMode = ConfigMode.Central,
            CentralServerUrl = $"127.0.0.1:{_port}",
            CentralClientKey = Key,
        };
        using var surya = await SuryaOcr.StartAsync(settings);
        Assert.True(surya.IsServer);
    }

    [Fact]
    public async Task WrongKeyWrongServerAndStoppedSuryaGiveClearMessages()
    {
        var wrongKey = await Assert.ThrowsAsync<InvalidOperationException>(() => SuryaOcr.StartAsync(Settings("nope")));
        Assert.Contains("elutasította a kulcsot", wrongKey.Message);

        _suryaState = "off";
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => SuryaOcr.StartAsync(Settings()));
        Assert.Contains("nincs bekapcsolva", off.Message);

        _suryaState = "error";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SuryaOcr.StartAsync(Settings()));
        Assert.Contains("nincs python", error.Message);

        _server = "something-else";
        var notOurs = await Assert.ThrowsAsync<InvalidOperationException>(() => SuryaOcr.StartAsync(Settings()));
        Assert.Contains("nem Invoice Sorter szerver", notOurs.Message);

        var settings = Settings();
        settings.SuryaServerUrl = "127.0.0.1:1";   // nothing listens there
        var unreachable = await Assert.ThrowsAsync<InvalidOperationException>(() => SuryaOcr.StartAsync(settings));
        Assert.Contains("nem érhető el", unreachable.Message);
    }

    [Fact]
    public async Task CentralConfigurationIsDownloadedOnlyWhenTheRevisionChanges()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"central-test-{Guid.NewGuid():N}");
        var oldDataDir = AppPaths.DataDir;
        AppPaths.DataDir = dataDir;
        try
        {
            var settings = new AppSettings { ConfigMode = ConfigMode.Central, CentralServerUrl = $"127.0.0.1:{_port}", CentralClientKey = Key };
            var first = await CentralSync.FetchIfChangedAsync(settings, -1, force: false);
            Assert.NotNull(first);
            Assert.Equal(7, first!.Revision);
            Assert.Equal("Központi sablon", first.Styles.Single().Name);
            Assert.Equal(7, CentralSync.LoadCache()!.Revision);   // kept for offline use

            Assert.Null(await CentralSync.FetchIfChangedAsync(settings, 7, force: false));
            Assert.Equal(1, _configDownloads);

            _revision = 8;
            Assert.Equal(8, (await CentralSync.FetchIfChangedAsync(settings, 7, force: false))!.Revision);

            // The machine-specific fields stay those of this computer.
            settings.SuryaPython = @"C:\py\python.exe";
            var merged = AppSettings.MergeCentral(settings, first.Settings);
            Assert.Equal(ConfigMode.Central, merged.ConfigMode);
            Assert.Equal(@"C:\py\python.exe", merged.SuryaPython);
            Assert.Equal("Minta Vevő 1926 Kft.", merged.ReceiverCompanies.Single().Name);
        }
        finally
        {
            AppPaths.DataDir = oldDataDir;
            try { Directory.Delete(dataDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("192.168.1.20:8770", "http://192.168.1.20:8770/")]
    [InlineData(" http://gpu-pc:8770/ ", "http://gpu-pc:8770/")]
    [InlineData("https://ocr.example.com/invoice", "https://ocr.example.com/invoice/")]
    public void ServerAddressesAreNormalized(string input, string expected) =>
        Assert.Equal(expected, CentralClient.ServerUri(input).ToString());

    [Fact]
    public void InvalidServerAddressesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => CentralClient.ServerUri(""));
        Assert.Throws<InvalidOperationException>(() => CentralClient.ServerUri("ftp://x"));
    }

    [Theory]
    [InlineData("v1.2.0", "v1.1.9", true)]
    [InlineData("1.2.0", "v1.2.0", false)]
    [InlineData("1.10", "1.9.3", true)]
    [InlineData("v1.0.2", "1.1.0", false)]
    [InlineData("nonsense", "1.0.0", false)]
    public void NewerVersionsAreRecognized(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(candidate, current));

    [Fact]
    public void OldSettingsKeepRunningSuryaLocally()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{\"SuryaMode\":\"UnclearOnly\",\"SuryaPython\":\"\"}", JsonStore.Options)!;
        Assert.Equal(SuryaLocation.Local, old.SuryaLocation);
        Assert.Equal(ConfigMode.Local, old.ConfigMode);
        Assert.True(old.CheckForUpdates);
        Assert.Equal(300, old.SuryaServerTimeoutSeconds);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        try { File.Delete(_image); } catch { }
    }
}

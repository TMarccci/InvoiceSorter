using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InvoiceSorter.Core.Storage;

namespace InvoiceSorter.Core.Central;

/// <summary>
/// HTTP client of the Invoice Sorter server: central configuration, learned issuers and the Surya queue.
/// Every call sends <c>Authorization: Bearer &lt;key&gt;</c> (client key, or the admin key for changes) and the computer
/// name, which is shown in the server's queue.
/// </summary>
public sealed class CentralClient : IDisposable
{
    private readonly HttpClient _http;

    public Uri BaseUri { get; }
    public string Where => BaseUri.Authority;

    public CentralClient(string url, string key, TimeSpan? timeout = null)
    {
        BaseUri = ServerUri(url);
        _http = new HttpClient { BaseAddress = BaseUri, Timeout = timeout ?? TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrWhiteSpace(key))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        _http.DefaultRequestHeaders.Add("X-Client-Name", $"{Environment.MachineName} ({Environment.UserName})");
    }

    /// <summary>"192.168.1.20:8770" → http://192.168.1.20:8770/ (the base address of the server).</summary>
    public static Uri ServerUri(string? url)
    {
        url = (url ?? "").Trim().TrimEnd('/');
        if (url.Length == 0) throw new InvalidOperationException("Nincs megadva szervercím (Beállítások › Konfiguráció).");
        if (!url.Contains("://")) url = "http://" + url;
        if (!Uri.TryCreate(url + "/", UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"'{url}' nem érvényes szervercím (példa: http://192.168.1.20:8770).");
        return uri;
    }

    public static readonly JsonSerializerOptions Json = new(JsonStore.Options) { PropertyNameCaseInsensitive = true };

    // ------------------------------------------------------------------------------------------ calls

    public async Task<ServerHealth> GetHealthAsync(CancellationToken ct = default)
    {
        var health = await SendAsync<ServerHealth>(HttpMethod.Get, "api/health", null, TimeSpan.FromSeconds(15), ct);
        if (health.Server != ServerHealth.ServerName)
            throw new InvalidOperationException($"A(z) {Where} válaszolt, de nem Invoice Sorter szerver. Ellenőrizd a címet és a portot.");
        return health;
    }

    public async Task<int> GetRevisionAsync(CancellationToken ct = default) =>
        (await SendAsync<RevisionInfo>(HttpMethod.Get, "api/config/revision", null, TimeSpan.FromSeconds(15), ct)).Revision;

    public Task<CentralConfig> GetConfigAsync(CancellationToken ct = default) =>
        SendAsync<CentralConfig>(HttpMethod.Get, "api/config", null, null, ct);

    /// <summary>Saves new central styles and settings (admin key). Returns the new configuration.</summary>
    public Task<CentralConfig> PutConfigAsync(ConfigUpdate update, CancellationToken ct = default) =>
        SendAsync<CentralConfig>(HttpMethod.Put, "api/config", JsonContent.Create(update, options: Json), null, ct);

    public Task LearnIssuerAsync(string name, string? taxNumber, CancellationToken ct = default) =>
        SendAsync<RevisionInfo>(HttpMethod.Post, "api/issuers/learn", JsonContent.Create(new LearnIssuerRequest { Name = name, TaxNumber = taxNumber }, options: Json), null, ct);

    public Task<OcrJobInfo> SubmitOcrJobAsync(byte[] image, string contentType, CancellationToken ct = default)
    {
        var content = new ByteArrayContent(image);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return SendAsync<OcrJobInfo>(HttpMethod.Post, "api/ocr/jobs", content, null, ct);
    }

    /// <summary>The state of a job; waits up to <paramref name="waitSeconds"/> on the server for it to finish (long poll).</summary>
    public Task<OcrJobInfo> GetOcrJobAsync(string id, int waitSeconds, CancellationToken ct = default) =>
        SendAsync<OcrJobInfo>(HttpMethod.Get, $"api/ocr/jobs/{Uri.EscapeDataString(id)}?wait={waitSeconds}", null, TimeSpan.FromSeconds(waitSeconds + 30), ct);

    public async Task CancelOcrJobAsync(string id)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var r = await _http.DeleteAsync($"api/ocr/jobs/{Uri.EscapeDataString(id)}", cts.Token);
        }
        catch { /* best effort */ }
    }

    /// <summary>All jobs of the queue (admin key) – shown in the server manager.</summary>
    public Task<List<OcrJobInfo>> GetQueueAsync(CancellationToken ct = default) =>
        SendAsync<List<OcrJobInfo>>(HttpMethod.Get, "api/ocr/queue", null, TimeSpan.FromSeconds(15), ct);

    /// <summary>Restarts the Surya worker of the server (admin key), e.g. after installing Surya.</summary>
    public Task<SuryaState> RestartSuryaAsync(CancellationToken ct = default) =>
        SendAsync<SuryaState>(HttpMethod.Post, "api/admin/surya/restart", null, TimeSpan.FromMinutes(25), ct);

    private sealed class RevisionInfo
    {
        public int Revision { get; set; }
    }

    private sealed class ErrorInfo
    {
        public string? Error { get; set; }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, TimeSpan? timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != null) cts.CancelAfter(timeout.Value);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"A szerver ({Where}) nem válaszol (időtúllépés). Fut a szolgáltatás, és nyitva van a {BaseUri.Port}-es port a tűzfalon?");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"A szerver ({Where}) nem érhető el: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException($"A szerver ({Where}) elutasította a kulcsot – ellenőrizd a kliens kulcsot (szerverkezelő › Áttekintés).");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Ehhez a művelethez a szerver adminisztrátori kulcsa kell.");
            if (!response.IsSuccessStatusCode)
            {
                string? error = null;
                try { error = JsonSerializer.Deserialize<ErrorInfo>(body, Json)?.Error; } catch (JsonException) { }
                throw new CentralServerException((int)response.StatusCode,
                    error ?? $"A szerver ({Where}) hibát jelzett: HTTP {(int)response.StatusCode}.");
            }
            try
            {
                return JsonSerializer.Deserialize<T>(body, Json) ?? throw new JsonException("empty");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"A(z) {Where} válaszolt, de nem Invoice Sorter szerver. Ellenőrizd a címet és a portot.");
            }
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>An error answer of the server (HTTP status + its message).</summary>
public sealed class CentralServerException(int status, string message) : InvalidOperationException(message)
{
    public int Status { get; } = status;
}

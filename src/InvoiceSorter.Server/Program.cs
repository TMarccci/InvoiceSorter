using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using InvoiceSorter.Core;
using InvoiceSorter.Core.Central;
using InvoiceSorter.Core.Logging;
using InvoiceSorter.Server;

// Invoice Sorter Server: central styles / settings for the clients and a shared Surya OCR queue.
//
//   InvoiceSorterServer.exe [--data <dir>]       console mode (Ctrl+C stops)
//   installed by the server installer as the Windows service "InvoiceSorterServer"
//
// Data folder (default C:\ProgramData\TMarccci\InvoiceSorter\Server): server.json (port, keys, Surya), central.json
// (central configuration), backups\, Logs\, surya\ (Surya environment), tmp\.

var dataDir = ServerConfig.ResolveDataDir(args);
AppPaths.LocalDir = dataDir;
AppPaths.DataDir = dataDir;
var config = ServerConfig.LoadOrCreate(dataDir);
var version = (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0").Split('+')[0];

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Host.UseWindowsService(o => o.ServiceName = ServerConfig.ServiceName);
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = (long)config.MaxImageMb * 1024 * 1024;
    var address = IPAddress.TryParse(config.BindAddress, out var ip) ? ip : IPAddress.Any;
    k.Listen(address, config.Port, listen =>
    {
        if (!string.IsNullOrWhiteSpace(config.CertificatePath))
            listen.UseHttps(X509CertificateLoader.LoadPkcs12FromFile(config.CertificatePath, config.CertificatePassword));
    });
});
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new ServerPaths(dataDir));
builder.Services.AddSingleton<CentralStore>();
builder.Services.AddSingleton<OcrQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OcrQueue>());

var app = builder.Build();
var store = app.Services.GetRequiredService<CentralStore>();
var queue = app.Services.GetRequiredService<OcrQueue>();

AppLog.Info($"=== Invoice Sorter szerver {version} indul – port {config.Port}, adatmappa {dataDir}");

// ---------------------------------------------------------------------------------------------- authentication

static bool Same(string a, string b) =>
    a.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

string Key(HttpContext ctx)
{
    var auth = ctx.Request.Headers.Authorization.ToString();
    return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : ctx.Request.Headers["X-API-Key"].ToString();
}

bool IsAdmin(HttpContext ctx) => Same(Key(ctx), config.AdminKey);
bool IsClient(HttpContext ctx) => IsAdmin(ctx) || Same(Key(ctx), config.ClientKey);

string Who(HttpContext ctx)
{
    var name = ctx.Request.Headers["X-Client-Name"].ToString();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    return string.IsNullOrWhiteSpace(name) ? ip : $"{name.Trim()} [{ip}]";
}

static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
static IResult Unauthorized() => Error(401, "Hiányzó vagy hibás kulcs.");

// ---------------------------------------------------------------------------------------------- endpoints

app.MapGet("/", () => Results.Text($"Invoice Sorter szerver {version} – fut. API: /api/health (kulccsal).", "text/plain; charset=utf-8"));

app.MapGet("/api/health", (HttpContext ctx) => !IsClient(ctx) ? Unauthorized() : Results.Json(new ServerHealth
{
    Version = version,
    MachineName = Environment.MachineName,
    Revision = store.Revision,
    Surya = queue.Surya,
    Queue = queue.Queue,
}));

app.MapGet("/api/config/revision", (HttpContext ctx) => !IsClient(ctx) ? Unauthorized() : Results.Json(new { revision = store.Revision }));

app.MapGet("/api/config", (HttpContext ctx) => !IsClient(ctx) ? Unauthorized() : Results.Json(store.Get()));

app.MapPut("/api/config", (HttpContext ctx, ConfigUpdate update) =>
{
    if (!IsClient(ctx)) return Unauthorized();
    if (!IsAdmin(ctx)) return Error(403, "A központi konfiguráció módosításához az adminisztrátori kulcs kell.");
    try
    {
        if (string.IsNullOrWhiteSpace(update.UpdatedBy)) update.UpdatedBy = Who(ctx);
        return Results.Json(store.Update(update));
    }
    catch (ConflictException ex)
    {
        return Error(409, ex.Message);
    }
});

app.MapPost("/api/issuers/learn", (HttpContext ctx, LearnIssuerRequest request) =>
{
    if (!IsClient(ctx)) return Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Name)) return Error(400, "Hiányzik a név.");
    store.LearnIssuer(request.Name, request.TaxNumber, Who(ctx));
    return Results.Json(new { revision = store.Revision });
});

app.MapPost("/api/ocr/jobs", async (HttpContext ctx) =>
{
    if (!IsClient(ctx)) return Unauthorized();
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
    if (ms.Length == 0) return Error(400, "Az oldal képét a kérés törzsében kell küldeni.");
    try
    {
        var job = queue.Submit(ms.ToArray(), ctx.Request.ContentType ?? "image/jpeg", Who(ctx));
        return Results.Json(job, statusCode: 202);
    }
    catch (QueueFullException ex) { return Error(429, ex.Message); }
    catch (QueueUnavailableException ex) { return Error(503, ex.Message); }
});

app.MapGet("/api/ocr/jobs/{id}", async (HttpContext ctx, string id, int? wait) =>
{
    if (!IsClient(ctx)) return Unauthorized();
    var job = await queue.GetAsync(id, TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, 30)), ctx.RequestAborted);
    return job == null ? Error(404, "Nincs ilyen feladat (lejárt vagy törölték).") : Results.Json(job);
});

app.MapDelete("/api/ocr/jobs/{id}", (HttpContext ctx, string id) =>
    !IsClient(ctx) ? Unauthorized() : Results.Json(new { cancelled = queue.Cancel(id) }));

app.MapGet("/api/ocr/queue", (HttpContext ctx) =>
    !IsClient(ctx) ? Unauthorized() : !IsAdmin(ctx) ? Error(403, "Adminisztrátori kulcs kell.") : Results.Json(queue.List()));

app.MapPost("/api/admin/surya/restart", async (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Unauthorized();
    await queue.RestartSuryaAsync();
    return Results.Json(queue.Surya);
});

app.Run();

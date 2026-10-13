using InvoiceSorter.Core.Models;
using InvoiceSorter.Core.Processing;

namespace InvoiceSorter.Core.Central;

/// <summary>
/// The configuration the Invoice Sorter server hands out to its clients: invoice styles and settings. Every change
/// increases <see cref="Revision"/>; clients compare it with the revision they applied last.
/// </summary>
public sealed class CentralConfig
{
    public int Revision { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string UpdatedBy { get; set; } = "";
    public List<InvoiceStyle> Styles { get; set; } = new();
    public AppSettings Settings { get; set; } = AppSettings.CreateDefault();
}

/// <summary>GET /api/health – what the server is and what it can do.</summary>
public sealed class ServerHealth
{
    public const string ServerName = "invoice-sorter-server";
    public const int CurrentApi = 2;

    public string Status { get; set; } = "ok";
    public string Server { get; set; } = ServerName;
    public int Api { get; set; } = CurrentApi;
    public string Version { get; set; } = "";
    public string MachineName { get; set; } = "";
    public int Revision { get; set; }
    public SuryaState Surya { get; set; } = new();
    public QueueState Queue { get; set; } = new();
}

public sealed class SuryaState
{
    /// <summary>off | starting | ready | error</summary>
    public string State { get; set; } = "off";
    public string? Device { get; set; }
    public string? Version { get; set; }
    public string? Message { get; set; }
}

public sealed class QueueState
{
    public int Waiting { get; set; }
    public int Running { get; set; }
    public int Processed { get; set; }
    public int Failed { get; set; }
    public double? AverageSeconds { get; set; }
}

/// <summary>One page to read with Surya on the server (POST /api/ocr/jobs, GET /api/ocr/jobs/{id}).</summary>
public sealed class OcrJobInfo
{
    public string Id { get; set; } = "";
    /// <summary>queued | running | done | failed | cancelled</summary>
    public string Status { get; set; } = "queued";
    /// <summary>1 = next in line; 0 when running or finished.</summary>
    public int Position { get; set; }
    public string Client { get; set; } = "";
    public DateTime QueuedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Error { get; set; }
    /// <summary>The recognized text (only when done and requested by its client).</summary>
    public SuryaOcr.WorkerMessage? Result { get; set; }

    public bool IsFinished => Status is "done" or "failed" or "cancelled";

    /// <summary>Hungarian status for the server manager.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusText => Status switch
    {
        "queued" => Position > 0 ? $"sorban ({Position}.)" : "sorban",
        "running" => "olvasás",
        "done" => "kész",
        "failed" => "sikertelen",
        "cancelled" => "megszakítva",
        _ => Status,
    };
}

/// <summary>POST /api/issuers/learn – an issuer name typed in a client's review screen.</summary>
public sealed class LearnIssuerRequest
{
    public string Name { get; set; } = "";
    public string? TaxNumber { get; set; }
}

/// <summary>PUT /api/config – the new styles and settings (admin key).</summary>
public sealed class ConfigUpdate
{
    public List<InvoiceStyle> Styles { get; set; } = new();
    public AppSettings Settings { get; set; } = AppSettings.CreateDefault();
    /// <summary>The revision the editor started from; a newer revision on the server means someone else saved in between.</summary>
    public int BasedOnRevision { get; set; }
    public string UpdatedBy { get; set; } = "";
}

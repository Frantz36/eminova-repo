// Eminova.Host — Data/Entities.cs
// Three tables are enough to host the POC: the dashboard state document per tenant/user,
// an audit trail, and a record of every dataset import batch.
namespace Eminova.Host.Data;

/// <summary>The dashboard's whole state object (the POC's <c>S</c>) stored as one JSON document.</summary>
public sealed class TenantState
{
    public string TenantId { get; set; } = "default";   // customer / organisation
    public string UserId { get; set; } = "poc-user";    // signed-in user (OIDC name claim) or "poc-user"
    public string Json { get; set; } = "{}";
    public int Version { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Server-side audit of saves, resets and imports (the dashboard keeps its own in-page audit list).</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime AtUtc { get; set; }
}

/// <summary>One uploaded customer dataset (CSV/JSON) merged into the state document.</summary>
public sealed class ImportBatch
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Entity { get; set; } = "";
    public string FileName { get; set; } = "";
    public int RowsRead { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public string Warnings { get; set; } = "";
    public DateTime AtUtc { get; set; }
}

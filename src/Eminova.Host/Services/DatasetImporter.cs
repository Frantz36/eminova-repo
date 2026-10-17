// Eminova.Host — Services/DatasetImporter.cs
// Merges customer datasets (CSV or JSON array) into the dashboard state document.
// Each supported entity maps the customer's column headings (many aliases accepted)
// onto the field names the EMINOVA Workbench already understands, so migrated records
// appear in the right workspace immediately — no dashboard code changes required.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eminova.Host.Data;

namespace Eminova.Host.Services;

public sealed record EntitySpec(
    string Entity,                                   // URL name:  POST /api/import/{entity}
    string StateKey,                                 // array in the Workbench state (S.<key>)
    string IdPrefix,                                 // used when the source has no id column
    IReadOnlyDictionary<string, string[]> Columns,   // target field -> accepted source headings
    string[] NumericFields,
    string[] DateFields,
    string[] BoolFields);

public sealed record ImportResult(string Entity, string StateKey, int RowsRead, int Added, int Updated, int Skipped, IReadOnlyList<string> Warnings, int StateVersion);

public sealed class DatasetImporter
{
    private readonly EminovaDbContext _db;
    public DatasetImporter(EminovaDbContext db) => _db = db;

    private static IReadOnlyDictionary<string, string[]> Map(params (string target, string aliases)[] pairs) =>
        pairs.ToDictionary(p => p.target, p => p.aliases.Split('|'), StringComparer.OrdinalIgnoreCase);

    private static readonly string[] None = Array.Empty<string>();

    /// <summary>Importable datasets for the EMINOVA HR & Payroll Workbench. Keys are the {entity} URL segment;
    /// target field names are exactly the ones the Workbench payroll engine reads (S.emps[].salary, .tax, .pension …).</summary>
    public static readonly IReadOnlyDictionary<string, EntitySpec> Specs = new Dictionary<string, EntitySpec>(StringComparer.OrdinalIgnoreCase)
    {
        ["employees"] = new("employees", "emps", "E",
            Map(("id", "id|employee no|employee number|emp no|staff no|personnel no"), ("name", "name|full name|employee name"),
                ("pos", "pos|position|job title|designation"), ("dept", "dept|department|cost centre|cost center"),
                ("grp", "grp|pay group|paygroup|frequency|pay frequency"), ("pay", "pay|pay basis|pay type"),
                ("salary", "salary|basic salary|monthly salary|basic"), ("rate", "rate|hourly rate|rate per hour"), ("hours", "hours|hours worked|ordinary hours"),
                ("dob", "dob|date of birth|birth date"), ("tax", "tax|tax number|income tax number|tax ref|sars tax number"),
                ("bank", "bank|bank name|bank display"), ("acct", "acct|account|account number|bank account"),
                ("pension", "pension|pension fund|retirement fund member"), ("medical", "medical|medical aid|medical aid contribution"),
                ("deps", "deps|dependants|medical dependants"), ("start", "start|start date|engagement date|date engaged"),
                ("end", "end|termination date|end date"), ("status", "status|employment status"),
                ("ee", "ee|occupational level|ee occupational level"), ("gender", "gender|sex"),
                ("ot", "ot|overtime hours|overtime"), ("comm", "comm|commission"), ("allow", "allow|allowance|allowance amount"),
                ("allowName", "allowname|allowance name|allowance type"), ("garn", "garn|garnishee|emolument attachment order|eao"),
                ("eti", "eti|eti eligible|employment tax incentive")),
            new[] { "salary", "rate", "hours", "medical", "deps", "ot", "comm", "allow", "garn" }, new[] { "dob", "start", "end" }, new[] { "pension", "eti" }),

        ["leave"] = new("leave", "leave", "LV",
            Map(("id", "id|leave id|request no"), ("emp", "emp|employee|employee no|employee number"), ("type", "type|leave type"),
                ("from", "from|start|start date|from date"), ("to", "to|end|end date|to date"), ("days", "days|days taken|duration"),
                ("status", "status|approval status"), ("note", "note|notes|reason|comment")),
            new[] { "days" }, new[] { "from", "to" }, None),

        ["cases"] = new("cases", "cases", "CS",
            Map(("id", "id|case no|ticket no|case id"), ("emp", "emp|employee|employee no"), ("cat", "cat|category|type"),
                ("subj", "subj|subject|summary|title"), ("status", "status|state"), ("sla", "sla|due|due date|sla date"),
                ("created", "created|opened|date logged"), ("owner", "owner|assigned to|agent")),
            None, new[] { "sla", "created" }, None),

        ["changes"] = new("changes", "changes", "CHG",
            Map(("id", "id|change id|change no"), ("type", "type|change type"), ("emp", "emp|employee|employee no"), ("detail", "detail|description"),
                ("by", "by|requested by|requester"), ("status", "status|state"), ("risk", "risk|risk note")),
            None, None, None),

        ["access"] = new("access", "access", "ACC",
            Map(("id", "id"), ("user", "user|user name|name"), ("role", "role|system role"), ("scope", "scope|data scope|pay groups"),
                ("last", "last|last login|last used"), ("flag", "flag|review flag|finding")),
            None, new[] { "last" }, None),

        ["projects"] = new("projects", "projects", "CTX",
            Map(("id", "id|project id|project code"), ("name", "name|project name|title"), ("owner", "owner|project manager|pm"),
                ("phase", "phase|stage"), ("start", "start|start date"), ("end", "end|end date|finish"),
                ("budget", "budget|budget (zar)|approved budget"), ("spent", "spent|actual|actual cost|spend to date")),
            new[] { "budget", "spent" }, new[] { "start", "end" }, None),

        ["tasks"] = new("tasks", "tasks", "TSK",
            Map(("id", "id|task id"), ("proj", "proj|project|project id"), ("title", "title|task|task name"), ("owner", "owner|assigned to|resource"),
                ("status", "status|state"), ("due", "due|due date|finish"), ("pri", "pri|priority")),
            None, new[] { "due" }, None),

        ["raid"] = new("raid", "raid", "RAID",
            Map(("id", "id|raid id|ref"), ("proj", "proj|project|project id"), ("type", "type|raid type|category"), ("text", "text|description|title"),
                ("owner", "owner"), ("rating", "rating|rag|severity"), ("status", "status|state")),
            None, None, None),
    };


    public async Task<ImportResult> ImportAsync(string tenant, string user, string entity, IFormFile file)
    {
        if (!Specs.TryGetValue(entity, out var spec))
            throw new ArgumentException($"Unknown entity '{entity}'. Supported: {string.Join(", ", Specs.Keys)}");

        var warnings = new List<string>();
        var rows = await ReadRowsAsync(file);
        if (rows.Count == 0) throw new ArgumentException("The file contains no data rows.");

        // Load (or start) the state document. A partial document is fine: the patched dashboard
        // overlays it on its seed data, so importing 'projects' alone still renders correctly.
        var row = await _db.TenantStates.FindAsync(tenant, user);
        var state = row is null ? new JsonObject() : (JsonNode.Parse(row.Json) as JsonObject ?? new JsonObject());
        state["v"] ??= 3;    // Workbench state schema version; the patched load() only accepts v === 3
        var target = state[spec.StateKey] as JsonArray;
        if (target is null) { target = new JsonArray(); state[spec.StateKey] = target; }

        // Index existing records by id for upsert.
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < target.Count; i++)
            if (target[i] is JsonObject o && o["id"] is JsonValue v) index[v.ToString()] = i;

        int added = 0, updated = 0, skipped = 0, autoId = target.Count;
        var headerMap = BuildHeaderMap(spec, rows[0].Keys, warnings);
        if (headerMap.Count == 0) throw new ArgumentException($"No column headings matched entity '{entity}'. Expected one of: {string.Join(", ", spec.Columns.Keys)}");

        foreach (var src in rows)
        {
            var obj = new JsonObject();
            foreach (var (sourceHeading, targetField) in headerMap)
            {
                var raw = src.TryGetValue(sourceHeading, out var v) ? v?.Trim() : null;
                if (string.IsNullOrEmpty(raw)) continue;
                if (spec.BoolFields.Contains(targetField, StringComparer.OrdinalIgnoreCase))
                    obj[targetField] = JsonValue.Create(raw.Trim().ToLowerInvariant() is "yes" or "y" or "true" or "1" or "ja");
                else if (spec.NumericFields.Contains(targetField, StringComparer.OrdinalIgnoreCase))
                    obj[targetField] = ParseNumber(raw, out var n) ? JsonValue.Create(n) : JsonValue.Create(raw);
                else if (spec.DateFields.Contains(targetField, StringComparer.OrdinalIgnoreCase))
                    obj[targetField] = JsonValue.Create(NormaliseDate(raw));
                else
                    obj[targetField] = JsonValue.Create(raw);
            }
            if (obj.Count == 0) { skipped++; continue; }

            var id = obj["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id))
            {
                id = $"{spec.IdPrefix}-{(++autoId):0000}";
                obj["id"] = id;
                warnings.Add($"Row without id assigned {id}");
            }

            if (index.TryGetValue(id, out var at))
            {
                if (target[at] is JsonObject existing)
                    foreach (var kv in obj) existing[kv.Key] = kv.Value?.DeepClone();   // merge in place: keep fields the source does not supply
                else
                    target[at] = obj;
                updated++;
            }
            else
            {
                target.Add(obj);
                index[id] = target.Count - 1;
                added++;
            }
        }

        var json = state.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        if (row is null) { row = new TenantState { TenantId = tenant, UserId = user }; _db.TenantStates.Add(row); }
        row.Json = json; row.Version += 1; row.UpdatedUtc = DateTime.UtcNow;

        var batch = new ImportBatch
        {
            TenantId = tenant, UserId = user, Entity = spec.Entity, FileName = file.FileName,
            RowsRead = rows.Count, Added = added, Updated = updated, Skipped = skipped,
            Warnings = string.Join(" | ", warnings.Take(20)), AtUtc = row.UpdatedUtc
        };
        _db.ImportBatches.Add(batch);
        _db.AuditEntries.Add(new AuditEntry { TenantId = tenant, UserId = user, Action = "import", Detail = $"{spec.Entity}: +{added} ~{updated} skip {skipped} from {file.FileName}", AtUtc = row.UpdatedUtc });
        await _db.SaveChangesAsync();

        return new ImportResult(spec.Entity, spec.StateKey, rows.Count, added, updated, skipped, warnings, row.Version);
    }

    // ------------------------------------------------------------ helpers
    private static List<(string source, string target)> BuildHeaderMap(EntitySpec spec, IEnumerable<string> headings, List<string> warnings)
    {
        var map = new List<(string, string)>();
        foreach (var h in headings)
        {
            var key = h.Trim();
            var hit = spec.Columns.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                                                      || c.Value.Any(a => a.Equals(key, StringComparison.OrdinalIgnoreCase)));
            if (hit.Key is not null) map.Add((h, hit.Key));
            else warnings.Add($"Column '{h}' not mapped (kept out of the import)");
        }
        return map;
    }

    private static async Task<List<Dictionary<string, string?>>> ReadRowsAsync(IFormFile file)
    {
        using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync();
        var isJson = file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith('[');
        return isJson ? ReadJsonRows(text) : ReadCsvRows(text);
    }

    private static List<Dictionary<string, string?>> ReadJsonRows(string text)
    {
        var rows = new List<Dictionary<string, string?>>();
        var arr = JsonNode.Parse(text) as JsonArray ?? throw new ArgumentException("JSON must be an array of objects.");
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            rows.Add(o.ToDictionary(kv => kv.Key, kv => kv.Value is JsonValue v ? v.ToString() : kv.Value?.ToJsonString()));
        }
        return rows;
    }

    /// <summary>RFC 4180-style CSV reader (quotes, embedded commas/newlines, CRLF). Delimiter auto-detected: comma or semicolon (SA locale).</summary>
    private static List<Dictionary<string, string?>> ReadCsvRows(string text)
    {
        var records = new List<List<string>>();
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        var firstLine = text.Split('\n').FirstOrDefault() ?? "";
        var delimiter = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delimiter) { record.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { /* ignore */ }
            else if (c == '\n') { record.Add(field.ToString()); field.Clear(); records.Add(record); record = new List<string>(); }
            else field.Append(c);
        }
        if (field.Length > 0 || record.Count > 0) { record.Add(field.ToString()); records.Add(record); }

        var rows = new List<Dictionary<string, string?>>();
        if (records.Count < 2) return rows;
        var headers = records[0].Select(h => h.Trim().TrimStart('﻿')).ToList();
        foreach (var r in records.Skip(1))
        {
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++) d[headers[i]] = i < r.Count ? r[i] : null;
            rows.Add(d);
        }
        return rows;
    }

    private static bool ParseNumber(string raw, out double value)
    {
        // Accepts "R 18 400 000", "18,400,000.00", "R18 400 000,50" (SA formatting) and plain numbers.
        var s = raw.Replace("R", "", StringComparison.OrdinalIgnoreCase).Replace("ZAR", "", StringComparison.OrdinalIgnoreCase)
                   .Replace(" ", "").Replace(" ", "");
        if (s.Count(ch => ch == ',') == 1 && !s.Contains('.')) s = s.Replace(',', '.');   // decimal comma
        else s = s.Replace(",", "");
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static string NormaliseDate(string raw)
    {
        // Dashboard expects ISO yyyy-MM-dd. Accept ISO, dd/MM/yyyy, yyyy/MM/dd, d MMM yyyy, Excel serials.
        string[] formats = { "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d MMM yyyy", "d MMMM yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm" };
        if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d.ToString("yyyy-MM-dd");
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20000 and < 80000)
            return DateTime.FromOADate(serial).ToString("yyyy-MM-dd");
        if (DateTime.TryParse(raw, new CultureInfo("en-ZA"), DateTimeStyles.None, out d)) return d.ToString("yyyy-MM-dd");
        return raw;   // leave as-is; the reconciliation step reports unparsed dates
    }
}

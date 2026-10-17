// Eminova.Migrate — migrates a customer's datasets into the hosted dashboard.
//
//   dotnet run --project tools/Eminova.Migrate -- <api base url> <folder with csv/json files> [tenant] [--dry-run]
//   e.g.  dotnet run --project tools/Eminova.Migrate -- https://eminova.<client>.co.za ./samples <CLIENT-CODE>
//
// File names select the entity: employees.csv, leave.csv, cases.csv, changes.csv, access.csv, projects.csv,
// tasks.csv, raid.csv (a prefix is allowed: "<client>-employees.csv"). Files are posted in dependency order and a reconciliation
// table (rows read / added / updated / skipped) is printed and written to migration-report.csv.
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Eminova.Migrate <api base url> <dataset folder> [tenant] [--dry-run]");
    return 1;
}
var baseUrl = args[0].TrimEnd('/');
var folder = args[1];
var tenant = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) ?? "default";
var dryRun = args.Contains("--dry-run");

// Dependency order: master data first so that references (project ids, people ids) resolve in the dashboard.
string[] order = { "employees", "access", "leave", "changes", "cases", "projects", "tasks", "raid" };

var files = Directory.GetFiles(folder).Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();
var plan = order
    .Select(e => (entity: e, file: files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).EndsWith(e, StringComparison.OrdinalIgnoreCase))))
    .Where(p => p.file is not null).ToList();

if (plan.Count == 0) { Console.Error.WriteLine("No recognised dataset files found in " + folder); return 1; }

using var http = new HttpClient { BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.Add("X-Eminova-Tenant", tenant);
var apiKey = Environment.GetEnvironmentVariable("EMINOVA_BEARER");          // optional: bearer token when the host is behind OIDC / API gateway
if (!string.IsNullOrEmpty(apiKey)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

var version = await http.GetStringAsync("api/version");
Console.WriteLine($"Target: {baseUrl}  tenant '{tenant}'  → {version}");
Console.WriteLine($"{"entity",-14} {"file",-36} {"rows",6} {"added",6} {"upd",6} {"skip",6}  warnings");

var report = new StringBuilder("entity,file,rows_read,added,updated,skipped,warnings\n");
var failed = false;
foreach (var (entity, file) in plan)
{
    var name = Path.GetFileName(file!);
    if (dryRun) { Console.WriteLine($"{entity,-14} {name,-36}   (dry run – not sent)"); continue; }

    using var form = new MultipartFormDataContent();
    var content = new ByteArrayContent(await File.ReadAllBytesAsync(file!));
    content.Headers.ContentType = new MediaTypeHeaderValue(name.EndsWith(".json") ? "application/json" : "text/csv");
    form.Add(content, "file", name);

    var resp = await http.PostAsync($"api/import/{entity}", form);
    var body = await resp.Content.ReadAsStringAsync();
    var isJson = resp.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
    if (!resp.IsSuccessStatusCode || !isJson)
    {
        if (!isJson && resp.IsSuccessStatusCode) body = "non-JSON response (redirected to a login page?) – run the migration from a signed-in session or an API-gateway token";
        failed = true;
        Console.WriteLine($"{entity,-14} {name,-36}  FAILED {(int)resp.StatusCode}: {body}");
        report.AppendLine($"{entity},{name},,,,,\"FAILED {(int)resp.StatusCode}: {body.Replace('"', '\'')}\"");
        continue;
    }
    using var doc = JsonDocument.Parse(body);
    var r = doc.RootElement;
    int rows = r.GetProperty("rowsRead").GetInt32(), added = r.GetProperty("added").GetInt32(),
        updated = r.GetProperty("updated").GetInt32(), skipped = r.GetProperty("skipped").GetInt32();
    var warnings = r.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
    Console.WriteLine($"{entity,-14} {name,-36} {rows,6} {added,6} {updated,6} {skipped,6}  {(warnings.Count == 0 ? "-" : warnings.Count + " (see report)")}");
    report.AppendLine($"{entity},{name},{rows},{added},{updated},{skipped},\"{string.Join(" | ", warnings).Replace('"', '\'')}\"");
}

var reportPath = Path.Combine(folder, "migration-report.csv");
await File.WriteAllTextAsync(reportPath, report.ToString());
Console.WriteLine($"\nReconciliation written to {reportPath}. Compare 'rows_read' with the customer's source counts before sign-off.");
return failed ? 2 : 0;

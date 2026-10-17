// Eminova.Patch — turns the standalone EMINOVA HR & Payroll Workbench POC into the hosted dashboard.
//
//   dotnet run --project tools/Eminova.Patch -- <EMINOVA_HR_Payroll_Workbench.html> <src/Eminova.Host/wwwroot/index.html> [tenant]
//
// Five anchored text replacements (the tool refuses to run if an anchor is missing or not unique,
// which means the Workbench build changed and the patch must be reviewed before deploying):
//   1. <meta eminova-api/tenant/sha> tags after the <title>
//   2. <script src="eminova-api.js"> loaded before the Workbench's own script
//   3. load()  → prefers the API state (v === 3), overlaid on seed(), then local cache
//   4. save()  → local cache + API save
//   5. boot    → awaits the API before the first render
//
// None of the Workbench's business logic (payroll engine, RULEPACK, workflows) is touched.
using System.Security.Cryptography;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Eminova.Patch <source-workbench.html> <output index.html> [tenant]");
    return 1;
}
var source = args[0];
var output = args[1];
var tenant = args.Length > 2 ? args[2] : "default";

var html = File.ReadAllText(source);
var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();

var edits = new (string name, string find, string replace)[]
{
    ("meta tags",
     "<title>EMINOVA HR &amp; Payroll Workbench</title>",
     $"<title>EMINOVA HR &amp; Payroll Workbench</title>\n<meta charset=\"utf-8\">\n<meta name=\"eminova-api\" content=\"/api\">\n<meta name=\"eminova-tenant\" content=\"{tenant}\">\n<meta name=\"eminova-source-sha256\" content=\"{sha}\">"),

    ("shim script",
     "<div id=\"toasts\" aria-live=\"polite\"></div><div id=\"modal\"></div>\n<script>",
     "<div id=\"toasts\" aria-live=\"polite\"></div><div id=\"modal\"></div>\n<script src=\"eminova-api.js\"></script>\n<script>"),

    ("load()",
     "function load() { try { const raw = localStorage.getItem(STORE_KEY); if (raw) { const o = JSON.parse(raw); if (o && o.v === 3) return o; } } catch (e) { } return null; }",
     "function load() { const api = window.EminovaApi; if (api && api.state && api.state.v === 3) { return Object.assign(seed(), api.state); } try { const raw = localStorage.getItem(STORE_KEY); if (raw) { const o = JSON.parse(raw); if (o && o.v === 3) return o; } } catch (e) { } return null; }"),

    ("save()",
     "function save() {\n  try { localStorage.setItem(STORE_KEY, JSON.stringify(S)); }",
     "function save() {\n  if (window.EminovaApi) window.EminovaApi.save(S);\n  try { localStorage.setItem(STORE_KEY, JSON.stringify(S)); }"),

    ("boot",
     "(function boot() {\n  S = load();",
     "(async function boot() {\n  if (window.EminovaApi) await window.EminovaApi.init();\n  S = load();\n  if (S && (!S.prior || !Object.keys(S.prior).length)) { initPrior(); audit(\"Hosted state initialised\", \"Prior period derived for migrated data\", S.company.name); save(); }"),
};

foreach (var (name, find, replace) in edits)
{
    var count = CountOf(html, find);
    if (count != 1)
    {
        Console.Error.WriteLine($"FAIL  anchor for '{name}' found {count} time(s) – expected exactly 1. The Workbench build has changed; review the patch before deploying.");
        return 2;
    }
    html = html.Replace(find, replace);
    Console.WriteLine($"ok    {name}");
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, html);
Console.WriteLine($"wrote {output}  ({html.Length:N0} chars)  source sha256 {sha[..12]}…  tenant '{tenant}'");
return 0;

static int CountOf(string text, string needle)
{
    int n = 0, i = 0;
    while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
    return n;
}

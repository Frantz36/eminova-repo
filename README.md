# EMINOVA HR & Payroll Workbench — hosted deployment (C# / ASP.NET Core 8)

Companion code to the Prevano LLC *Eminova Hosting Deployment Playbook* (Excel). Every file here is also reproduced in the
playbook's **Deployment Scripts (C#)** tab; the step numbers in the plan refer to the script blocks.

```
dashboard/EMINOVA_HR_Payroll_Workbench.html   ← the supplied Workbench file (unchanged)
src/Eminova.Host/                         ← ASP.NET Core 8 host: dashboard + /api/state + /api/import + /healthz
tools/Eminova.Patch/                      ← patches the POC into wwwroot/index.html (5 anchored edits)
tools/Eminova.Migrate/                    ← posts customer CSV/JSON datasets to /api/import/{entity}
deploy/docker | linux | iis | azure | render | sql ← hosting-specific scripts
deploy/ops/                               ← eminova-triage.sh (read-only diagnostics) + prometheus-alerts.yml (AI health & capacity rules)
samples/                                  ← example datasets showing accepted column headings
```

Quick start (any machine with the .NET 8 SDK):
```
dotnet restore && dotnet build          # Eminova.sln lists the three projects
dotnet run --project tools/Eminova.Patch -- dashboard/EMINOVA_HR_Payroll_Workbench.html src/Eminova.Host/wwwroot/index.html
dotnet run --project src/Eminova.Host          # https://localhost:5001  → dashboard;  /healthz ; /api/version
dotnet run --project tools/Eminova.Migrate -- https://localhost:5001 ./samples
```

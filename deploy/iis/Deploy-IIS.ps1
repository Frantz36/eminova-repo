# deploy/iis/Deploy-IIS.ps1 — Windows Server + IIS. Run as Administrator from the repo root.
param([string]$Tenant = "default", [string]$SiteName = "Eminova", [string]$SitePath = "C:\inetpub\eminova", [string]$HostHeader = "eminova.<CLIENT-DOMAIN>")
$ErrorActionPreference = "Stop"
Import-Module WebAdministration

Write-Host "[1/6] build + patch + publish"
dotnet restore; dotnet build -c Release --no-restore
dotnet run --project tools/Eminova.Patch -c Release -- dashboard/EMINOVA_HR_Payroll_Workbench.html src/Eminova.Host/wwwroot/index.html $Tenant
$release = "artifacts\eminova-host-$(Get-Date -Format yyyyMMdd-HHmm)"
dotnet publish src/Eminova.Host/Eminova.Host.csproj -c Release -o $release --no-self-contained

Write-Host "[2/6] app pool (No Managed Code, ApplicationPoolIdentity)"
if (-not (Test-Path "IIS:\AppPools\$SiteName")) { New-WebAppPool -Name $SiteName | Out-Null }
Set-ItemProperty "IIS:\AppPools\$SiteName" managedRuntimeVersion ""
Set-ItemProperty "IIS:\AppPools\$SiteName" startMode "AlwaysRunning"

Write-Host "[3/6] stop site, keep previous release for rollback"
if (Test-Path "IIS:\Sites\$SiteName") { Stop-Website $SiteName }
if (Test-Path $SitePath) { Remove-Item "$SitePath.prev" -Recurse -Force -ErrorAction SilentlyContinue; Rename-Item $SitePath "$SitePath.prev" }
New-Item -ItemType Directory -Path $SitePath | Out-Null
Copy-Item "$release\*" $SitePath -Recurse
New-Item -ItemType Directory -Path "$SitePath\logs","$SitePath\App_Data" -Force | Out-Null
icacls $SitePath /grant "IIS AppPool\$SiteName:(OI)(CI)M" | Out-Null

Write-Host "[4/6] site + HTTPS binding (certificate must already be in LocalMachine\My)"
if (-not (Test-Path "IIS:\Sites\$SiteName")) {
  New-Website -Name $SiteName -PhysicalPath $SitePath -ApplicationPool $SiteName -Port 80 -HostHeader $HostHeader | Out-Null
  New-WebBinding -Name $SiteName -Protocol https -Port 443 -HostHeader $HostHeader -SslFlags 1
}
Write-Host "[5/6] start"; Start-Website $SiteName
Write-Host "[6/6] health"; Invoke-WebRequest "https://$HostHeader/healthz" -UseBasicParsing | Select-Object StatusCode, Content
Write-Host "Deployed $release to $SitePath"

param([string]$SiteName = "Eminova", [string]$SitePath = "C:\inetpub\eminova")
Import-Module WebAdministration
Stop-Website $SiteName
Rename-Item $SitePath "$SitePath.failed" -Force
Rename-Item "$SitePath.prev" $SitePath
Start-Website $SiteName
Invoke-WebRequest "http://localhost/healthz" -UseBasicParsing | Select-Object StatusCode

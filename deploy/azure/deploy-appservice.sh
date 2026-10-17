#!/usr/bin/env bash
# deploy/azure/deploy-appservice.sh — Azure App Service (Linux) in South Africa North + Azure Database for PostgreSQL Flexible Server
# POPIA data residency: all resources in southafricanorth. Run from repo root after `az login`.
set -euo pipefail
RG="${RG:-rg-eminova-prod}"; LOC="southafricanorth"; APP="${APP:-eminova-<client>}"; PLAN="asp-eminova"; PG="pg-eminova-<client>"
TENANT="${1:-default}"

az group create -n "$RG" -l "$LOC"
az appservice plan create -g "$RG" -n "$PLAN" -l "$LOC" --is-linux --sku P1v3
az webapp create -g "$RG" -p "$PLAN" -n "$APP" --runtime "DOTNETCORE:8.0"
az webapp config set -g "$RG" -n "$APP" --always-on true --http20-enabled true --min-tls-version 1.2
az webapp update -g "$RG" -n "$APP" --https-only true

az postgres flexible-server create -g "$RG" -n "$PG" -l "$LOC" --tier Burstable --sku-name Standard_B2s --version 16 \
   --admin-user eminova_admin --admin-password "$DB_ADMIN_PASSWORD" --database-name eminova --public-access None
# (private endpoint / VNet integration per the Network Diagram tab; firewall rule for the App Service outbound IPs if public access is used)

az webapp config appsettings set -g "$RG" -n "$APP" --settings \
   Database__Provider=PostgreSQL \
   "ConnectionStrings__Eminova=Host=$PG.postgres.database.azure.com;Port=5432;Database=eminova;Username=eminova_admin;Password=$DB_ADMIN_PASSWORD;SSL Mode=Require" \
   Auth__Authority="${AUTH_AUTHORITY:-}" Auth__ClientId="${AUTH_CLIENT_ID:-eminova-dashboard}" Auth__ClientSecret="${AUTH_CLIENT_SECRET:-}" \
   Hosting__DefaultTenant="$TENANT" Hosting__HttpsRedirect=false
az webapp config set -g "$RG" -n "$APP" --generic-configurations '{"healthCheckPath": "/healthz"}'

dotnet run --project tools/Eminova.Patch -c Release -- dashboard/EMINOVA_HR_Payroll_Workbench.html src/Eminova.Host/wwwroot/index.html "$TENANT"
dotnet publish src/Eminova.Host/Eminova.Host.csproj -c Release -o artifacts/azure --no-self-contained
(cd artifacts/azure && zip -qr ../eminova-host.zip .)
az webapp deploy -g "$RG" -n "$APP" --src-path artifacts/eminova-host.zip --type zip
curl -fsS "https://$APP.azurewebsites.net/healthz" && echo && curl -fsS "https://$APP.azurewebsites.net/api/version"

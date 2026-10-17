#!/usr/bin/env bash
# deploy/linux/deploy.sh — build, patch, publish and install Eminova.Host on a Linux server (run from repo root)
set -euo pipefail
TENANT="${1:-default}"
RELEASE="artifacts/eminova-host-$(date +%Y%m%d-%H%M)"

echo "[1/6] restore + build";        dotnet restore && dotnet build -c Release --no-restore
echo "[2/6] patch dashboard";        dotnet run --project tools/Eminova.Patch -c Release -- dashboard/EMINOVA_HR_Payroll_Workbench.html src/Eminova.Host/wwwroot/index.html "$TENANT"
echo "[3/6] publish";                dotnet publish src/Eminova.Host/Eminova.Host.csproj -c Release -o "$RELEASE" --no-self-contained
echo "[4/6] install to /opt/eminova (previous release kept in /opt/eminova.prev for rollback)"
sudo systemctl stop eminova-host || true
sudo rm -rf /opt/eminova.prev && sudo mv /opt/eminova /opt/eminova.prev 2>/dev/null || true
sudo mkdir -p /opt/eminova && sudo cp -r "$RELEASE"/. /opt/eminova/ && sudo chown -R eminova:eminova /opt/eminova
echo "[5/6] service";                sudo cp deploy/linux/eminova-host.service /etc/systemd/system/ && sudo systemctl daemon-reload && sudo systemctl enable --now eminova-host
echo "[6/6] health check";           sleep 3 && curl -fsS http://127.0.0.1:5000/healthz && echo && curl -fsS http://127.0.0.1:5000/api/version && echo
echo "Deployed $RELEASE"

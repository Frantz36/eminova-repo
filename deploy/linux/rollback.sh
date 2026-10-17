#!/usr/bin/env bash
# deploy/linux/rollback.sh — restore the previous release
set -euo pipefail
sudo systemctl stop eminova-host
sudo rm -rf /opt/eminova.failed && sudo mv /opt/eminova /opt/eminova.failed
sudo mv /opt/eminova.prev /opt/eminova
sudo systemctl start eminova-host && sleep 3 && curl -fsS http://127.0.0.1:5000/healthz && echo " rollback ok"

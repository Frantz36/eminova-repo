#!/usr/bin/env bash
# deploy/sql/backup.sh — nightly logical backup (PostgreSQL). Keep 35 days; copy off-host to immutable storage.
set -euo pipefail
OUT="/var/backups/eminova/eminova-$(date +%Y%m%d-%H%M).dump"
mkdir -p "$(dirname "$OUT")"
pg_dump -h "${DB_HOST:-localhost}" -U eminova_app -d eminova -Fc -f "$OUT"
find /var/backups/eminova -name '*.dump' -mtime +35 -delete
echo "backup written: $OUT"
# expose success time to Prometheus node_exporter textfile collector (alert EminovaBackupStale)
mkdir -p /var/lib/node_exporter/textfile && echo "eminova_last_backup_timestamp_seconds $(date +%s)" > /var/lib/node_exporter/textfile/eminova_backup.prom
# restore:  pg_restore -h <host> -U eminova_app -d eminova --clean --if-exists <file>.dump

#!/usr/bin/env bash
# deploy/ops/eminova-triage.sh — first-response diagnostics for a frozen or failing EMINOVA HR & Payroll deployment.
# Safe to run by an AI agent or a human: READ-ONLY (collects evidence, changes nothing). Exit code = suspected cause class.
#   0 healthy   1 app down   2 database   3 disk/memory   4 proxy/TLS/DNS   5 identity   9 inconclusive
# Usage: sudo ./eminova-triage.sh [public url] [db host]     e.g. ./eminova-triage.sh https://eminova.<client>.co.za x.x.3.31
set -u
URL="${1:-https://localhost}"; DBH="${2:-localhost}"; OUT="/var/tmp/eminova-triage-$(date +%Y%m%d-%H%M%S).log"
R() { echo "== $*" | tee -a "$OUT"; }
C() { ( eval "$*" ) 2>&1 | tail -n 40 | tee -a "$OUT"; }
code=9
R "Eminova triage $(date -Is) host=$(hostname) url=$URL"
R "1. Local app health";        C "curl -s -o /dev/null -w 'healthz=%{http_code} t=%{time_total}s\n' -m 10 http://127.0.0.1:5000/healthz || curl -s -o /dev/null -w 'healthz(8080)=%{http_code}\n' -m 10 http://127.0.0.1:8080/healthz"
R "2. Public health via proxy"; C "curl -s -o /dev/null -w 'public=%{http_code} ssl_verify=%{ssl_verify_result} t=%{time_total}s\n' -m 15 $URL/healthz"
R "3. Service state";           C "systemctl is-active eminova-host nginx 2>/dev/null; docker ps --format '{{.Names}} {{.Status}}' 2>/dev/null"
R "4. Recent app errors";       C "journalctl -u eminova-host --since '-30 min' --no-pager | grep -iE 'fail|exception|error|timeout|refused' | tail -n 25; docker logs --since 30m eminova-host-host-1 2>&1 | grep -iE 'fail|exception|error' | tail -n 25"
R "5. Resources";               C "uptime; free -m; df -h / /opt /var 2>/dev/null; ps -o pid,rss,pcpu,etime,cmd -C dotnet"
R "6. Database";                C "pg_isready -h $DBH -p 5432 -t 5; PGCONNECT_TIMEOUT=5 psql -h $DBH -U eminova_app -d eminova -Atc \"select count(*) from pg_stat_activity; select pg_size_pretty(pg_database_size('eminova'));\" 2>&1"
R "7. TLS certificate";         C "echo | openssl s_client -servername ${URL#https://} -connect ${URL#https://}:443 2>/dev/null | openssl x509 -noout -dates -subject"
R "8. DNS";                     C "getent hosts ${URL#https://}; dig +short ${URL#https://} 2>/dev/null"
R "9. Identity provider";       C "AUTH=\$(grep -oE 'Auth__Authority=\S+' /etc/eminova/host.env 2>/dev/null | cut -d= -f2); [ -n \"\$AUTH\" ] && curl -s -o /dev/null -w \"idp=%{http_code}\n\" -m 10 \"\$AUTH/.well-known/openid-configuration\" || echo 'no Auth__Authority configured (POC anonymous mode)'"
R "10. Last deploy / release";  C "ls -ld /opt/eminova /opt/eminova.prev 2>/dev/null; curl -s -m 5 http://127.0.0.1:5000/api/version"

R "11. Backup freshness (RPO)";  C "ls -lt /var/backups/eminova 2>/dev/null | head -3; cat /var/lib/node_exporter/textfile/eminova_backup.prom 2>/dev/null"
R "12. Tenant state size (capacity)"; C "PGCONNECT_TIMEOUT=5 psql -h $DBH -U eminova_app -d eminova -Atc \"select \\\"TenantId\\\", \\\"Version\\\", pg_size_pretty(length(\\\"Json\\\")::bigint), \\\"UpdatedUtc\\\" from eminova_state order by length(\\\"Json\\\") desc limit 5\" 2>&1"

# --- classify (first matching rule wins) ---
if   grep -q 'healthz=200' "$OUT" && grep -q 'public=200' "$OUT"; then code=0
elif grep -qE 'df.*(9[0-9]|100)%' "$OUT" || grep -qiE 'out of memory|OOMKilled|No space left' "$OUT"; then code=3
elif grep -qiE 'pg_isready.*(no response|rejecting)|could not connect|password authentication failed|too many clients' "$OUT"; then code=2
elif grep -q 'healthz=200' "$OUT" && ! grep -q 'public=200' "$OUT"; then code=4
elif grep -qE 'idp=(000|4[0-9][0-9]|5[0-9][0-9])' "$OUT"; then code=5
elif ! grep -q 'healthz=200' "$OUT"; then code=1
fi
R "RESULT class=$code  (0 healthy, 1 app down, 2 database, 3 disk/memory, 4 proxy/TLS/DNS, 5 identity, 9 inconclusive)  evidence: $OUT"
exit $code

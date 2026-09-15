#!/usr/bin/env bash
# PHARMCO — database backup for the VPS (run from host cron, NOT inside compose).
#   usage: backup.sh                 # uses ./.env; backups -> BACKUP_DIR (default /backups)
#   env:   BACKUP_PASSPHRASE         # if set, backup is AES-256 encrypted
#          RETENTION_DAYS            # default 30
#
# cron example (daily 02:00, weekly purge):
#   0 2 * * *  cd /opt/pharmco/deploy && ./backup.sh >> /var/log/pharmco-backup.log 2>&1
#   0 5 * * 0  find /backups -name 'pharmco-*.sql.gz*' -mtime +30 -delete
set -euo pipefail

ENV_FILE="$(dirname "$0")/.env"
set -a; [ -f "$ENV_FILE" ] && . "$ENV_FILE"; set +a

DB_NAME="${DB_NAME:-pharmco}"
DB_USER="${DB_USER:-pharmco}"
BACKUP_DIR="${BACKUP_DIR:-/backups}"
RETENTION_DAYS="${RETENTION_DAYS:-30}"

mkdir -p "$BACKUP_DIR"
STAMP="$(date -u +%F-%H%M%S)"
SRC="$BACKUP_DIR/pharmco-$STAMP.sql.gz"
DST="$SRC"

docker exec db pg_dump -U "$DB_USER" "$DB_NAME" | gzip -9 > "$SRC"

if [ -n "${BACKUP_PASSPHRASE:-}" ]; then
    DST="$BACKUP_DIR/pharmco-$STAMP.sql.gz.enc"
    openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt \
        -pass "env:BACKUP_PASSPHRASE" -in "$SRC" -out "$DST"
    rm -f "$SRC"
fi

find "$BACKUP_DIR" -name 'pharmco-*.sql.gz*' -mtime "+$RETENTION_DAYS" -delete
echo "$(date -u +%FT%TZ) ok: $(du -h "$DST" | awk '{print $1}') $DST"
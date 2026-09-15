#!/usr/bin/env bash
# PHARMCO — restore a backup into a local file first, then pipe in.
#   usage: restore.sh <backup-file>                (gzip or openssl-encrypted)
#   env:   BACKUP_PASSPHRASE                       (required if backup is .enc)
# The restore TARGETS the running `db` container (docker exec).
set -euo pipefail

[ $# -eq 1 ] || { echo "usage: restore.sh <backup-file>" >&2; exit 1; }
BACKUP="$1"
[ -f "$BACKUP" ] || { echo "backup not found: $BACKUP" >&2; exit 1; }

ENV_FILE="$(dirname "$0")/.env"
set -a; [ -f "$ENV_FILE" ] && . "$ENV_FILE"; set +a
DB_NAME="${DB_NAME:-pharmco}"; DB_USER="${DB_USER:-pharmco}"

case "$BACKUP" in
    *.enc)
        [ -n "${BACKUP_PASSPHRASE:-}" ] || { echo "BACKUP_PASSPHRASE required for .enc backups" >&2; exit 1; }
        openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -salt \
            -pass "env:BACKUP_PASSPHRASE" -in "$BACKUP" \
        | gzip -dc \
        | docker exec -i db psql -U "$DB_USER" "$DB_NAME"
        ;;
    *) gzip -dc "$BACKUP" | docker exec -i db psql -U "$DB_USER" "$DB_NAME" ;;
esac

echo "ok: restored $BACKUP"
#!/usr/bin/env bash
# Fill a fresh .env with cryptographically random secrets (run once on the VPS).
#   usage: ./scripts/gen-env.sh
set -euo pipefail

ENV_FILE="$(dirname "$0")/../.env"
[ -f "$ENV_FILE" ] || { echo "missing $ENV_FILE — copy .env.example to .env first" >&2; exit 1; }

gen() { openssl rand -base64 48 | tr -d '\n='; }   # ~48 bytes of charset-safe entropy
gen32() { openssl rand -base64 32 | tr -d '\n'; }

# Preserve comments; only replace the four secret values.
sed -i \
    -e "s|^BASE_DOMAIN=.*|BASE_DOMAIN=$(grep '^BASE_DOMAIN=' "$ENV_FILE" | cut -d= -f2-)|" \
    -e "s|^DB_PASS=.*|DB_PASS=$(gen)|" \
    -e "s|^JWT_SECRET=.*|JWT_SECRET=$(gen)|" \
    -e "s|^JWT_ADMIN_KEY=.*|JWT_ADMIN_KEY=$(gen)|" \
    -e "s|^DARAJA_ENC_KEY=.*|DARAJA_ENC_KEY=$(gen32)|" \
    "$ENV_FILE"

chmod 600 "$ENV_FILE"
echo "✓ secrets written to $ENV_FILE (mode 600)"

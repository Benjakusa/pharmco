#!/usr/bin/env bash
# Apply the per-tenant schema chain to a target database.
#   usage: apply_tenant_schema.sh <postgres-url> <schema>
#   schema must match ^tenant_[a-z0-9_]+$ (mirrors master.tenants.schema_name CHECK)
#
# Applies db/tenant/002 → 004 → 005 → 007 in order (the same chain and
# placeholder substitution as Pharmco.Core TenantProvisioner). BEGIN/COMMIT
# statements inside the files are stripped and the whole chain runs in a
# single transaction, so a failure rolls everything back.
set -euo pipefail

DB_URL="${1:?usage: apply_tenant_schema.sh <postgres-url> <schema>}"
SCHEMA="${2:?usage: apply_tenant_schema.sh <postgres-url> <schema>}"

if [[ ! "$SCHEMA" =~ ^tenant_[a-z0-9_]+$ ]]; then
    echo "invalid schema name: '$SCHEMA' (must match ^tenant_[a-z0-9_]+\$)" >&2
    exit 1
fi

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TENANT_DIR="$HERE/../tenant"
CHAIN=(
    002_tenant_template.sql
    004_tenant_products.sql
    005_tenant_sales.sql
    007_sync_queue.sql
)

{
    echo "BEGIN;"
    for file in "${CHAIN[@]}"; do
        sed "s/{tenant_schema}/$SCHEMA/g" "$TENANT_DIR/$file" \
            | grep -vE '^[[:space:]]*(BEGIN|COMMIT);[[:space:]]*$' \
            || true
    done
    echo "COMMIT;"
} | psql "$DB_URL" -v ON_ERROR_STOP=1 -q -f -

echo "ok: schema '$SCHEMA' applied"
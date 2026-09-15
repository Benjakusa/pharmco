#!/usr/bin/env bash
# Apply the per-tenant schema template to a target database.
#   usage: apply_tenant_schema.sh <postgres-url> <schema>
#   schema must match ^tenant_[a-z0-9_]+$ (mirrors master.tenants.schema_name CHECK)
#
# The {tenant_schema} placeholder in the template is substituted, then the SQL
# is executed inside a single transaction (BEGIN/COMMIT in the template).
set -euo pipefail

DB_URL="${1:?usage: apply_tenant_schema.sh <postgres-url> <schema>}"
SCHEMA="${2:?usage: apply_tenant_schema.sh <postgres-url> <schema>}"

if [[ ! "$SCHEMA" =~ ^tenant_[a-z0-9_]+$ ]]; then
    echo "invalid schema name: '$SCHEMA' (must match ^tenant_[a-z0-9_]+\$)" >&2
    exit 1
fi

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE="$HERE/../tenant-template/001_tenant_tables.sql"

sed "s/{tenant_schema}/$SCHEMA/g" "$TEMPLATE" \
    | psql "$DB_URL" -v ON_ERROR_STOP=1 -q -f -

echo "ok: schema '$SCHEMA' applied"
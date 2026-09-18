-- ============================================================================
-- PHARMCO — 002_tenant_template.sql (per-tenant schema, applied at provision)
-- ----------------------------------------------------------------------------
-- The literal placeholder {tenant_schema} is replaced at runtime with a schema
-- name such as `tenant_test_001` (see Pharmco.Core TenantNaming + TenantProvisioner).
--
-- NOTE: this template deliberately has NO BEGIN/COMMIT — it executes INSIDE the
-- provisioning transaction (TenantProvisioner.ProvisionAsync). Standalone use:
--   wrap in BEGIN/COMMIT yourself (e.g. db/scripts/apply_tenant_schema.sh).
--
-- Defense-in-depth rule: EVERY table carries tenant_id + an index on it, so a
-- rogue query that somehow escapes the search_path still can't cross tenants
-- without hitting a guarded column. Sync invariants from docs/data-model.md:
--   * products.stock_qty is derived; stock_moves is the only writer.
--   * soft deletes via deleted_at, uuid PKs, sync_seq per row.
-- ============================================================================

CREATE SCHEMA IF NOT EXISTS {tenant_schema};

-- ---------------------------------------------------------------------------
-- products
-- ---------------------------------------------------------------------------
CREATE TABLE {tenant_schema}.products (
    id            uuid          PRIMARY KEY,
    tenant_id     uuid          NOT NULL,
    name          text          NOT NULL,
    barcode       text,
    category      text,
    unit          text          NOT NULL DEFAULT 'piece',
    buying_price  numeric(14,2) NOT NULL DEFAULT 0,
    selling_price numeric(14,2) NOT NULL CHECK (selling_price >= 0),
    stock_qty     numeric(14,3) NOT NULL DEFAULT 0,
    reorder_level numeric(14,3) NOT NULL DEFAULT 0,
    is_active     boolean       NOT NULL DEFAULT true,
    updated_at    timestamptz   NOT NULL DEFAULT now(),
    deleted_at    timestamptz,
    sync_seq      bigint        NOT NULL DEFAULT 0
);
CREATE INDEX products_tenant_id_idx ON {tenant_schema}.products (tenant_id);
CREATE INDEX products_barcode_idx   ON {tenant_schema}.products (barcode);
CREATE INDEX products_name_lower_idx ON {tenant_schema}.products (lower(name));

-- ---------------------------------------------------------------------------
-- sales
-- ---------------------------------------------------------------------------
CREATE TABLE {tenant_schema}.sales (
    id               uuid          PRIMARY KEY,
    tenant_id        uuid          NOT NULL,
    invoice_no       text          NOT NULL,
    device_id        uuid,
    cashier_user_id  uuid          NOT NULL,
    customer_phone   text,
    total            numeric(14,2) NOT NULL CHECK (total >= 0),
    payment_mode     varchar(10)   NOT NULL CHECK (payment_mode IN ('cash','mpesa')),
    mpesa_ref        text,
    status           varchar(24)   NOT NULL DEFAULT 'complete'
                                   CHECK (status IN ('complete','pending_verification','voided')),
    created_at       timestamptz   NOT NULL DEFAULT now(),
    updated_at       timestamptz   NOT NULL DEFAULT now(),
    deleted_at       timestamptz,
    sync_seq         bigint        NOT NULL DEFAULT 0,
    UNIQUE (tenant_id, invoice_no)
);
CREATE INDEX sales_tenant_id_idx ON {tenant_schema}.sales (tenant_id);
CREATE INDEX sales_created_at_idx ON {tenant_schema}.sales (created_at);
CREATE INDEX sales_status_idx    ON {tenant_schema}.sales (status);
CREATE INDEX sales_mpesa_ref_idx ON {tenant_schema}.sales (mpesa_ref);

-- ---------------------------------------------------------------------------
-- sale_items
-- ---------------------------------------------------------------------------
CREATE TABLE {tenant_schema}.sale_items (
    id         uuid          PRIMARY KEY,
    tenant_id  uuid          NOT NULL,
    sale_id    uuid          NOT NULL REFERENCES {tenant_schema}.sales(id) ON DELETE CASCADE,
    product_id uuid          NOT NULL REFERENCES {tenant_schema}.products(id),
    qty        numeric(14,3) NOT NULL CHECK (qty > 0),
    unit_price numeric(14,2) NOT NULL,
    subtotal   numeric(14,2) NOT NULL
);
CREATE INDEX sale_items_tenant_id_idx ON {tenant_schema}.sale_items (tenant_id);
CREATE INDEX sale_items_sale_idx      ON {tenant_schema}.sale_items (sale_id);

-- ---------------------------------------------------------------------------
-- stock_moves — only writer of products.stock_qty
-- ---------------------------------------------------------------------------
CREATE TABLE {tenant_schema}.stock_moves (
    id         uuid          PRIMARY KEY,
    tenant_id  uuid          NOT NULL,
    product_id uuid          NOT NULL REFERENCES {tenant_schema}.products(id),
    device_id  uuid,
    user_id    uuid          NOT NULL,
    qty_change numeric(14,3) NOT NULL CHECK (qty_change <> 0),
    reason     varchar(24)   NOT NULL
                CHECK (reason IN ('stock_in','stock_out','sale','void','adjustment')),
    ref_id     uuid,
    created_at timestamptz   NOT NULL DEFAULT now(),
    sync_seq   bigint        NOT NULL DEFAULT 0
);
CREATE INDEX stock_moves_tenant_id_idx ON {tenant_schema}.stock_moves (tenant_id);
CREATE INDEX stock_moves_product_idx   ON {tenant_schema}.stock_moves (product_id);
CREATE INDEX stock_moves_created_idx   ON {tenant_schema}.stock_moves (created_at);

-- ---------------------------------------------------------------------------
-- audit_logs — insert-only (fillfactor 70 keeps appended rows page-local)
-- ---------------------------------------------------------------------------
CREATE TABLE {tenant_schema}.audit_logs (
    id         uuid        PRIMARY KEY,
    tenant_id  uuid        NOT NULL,
    user_id    uuid,
    device_id  uuid,
    action     varchar(60) NOT NULL,
    entity     varchar(60),
    entity_id  uuid,
    detail     jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
) WITH (fillfactor = 70);
CREATE INDEX audit_logs_tenant_id_idx ON {tenant_schema}.audit_logs (tenant_id);
CREATE INDEX audit_logs_created_idx   ON {tenant_schema}.audit_logs (created_at);
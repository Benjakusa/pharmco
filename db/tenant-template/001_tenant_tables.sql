-- ============================================================================
-- PHARMCO — Per-tenant schema template (single source of truth for layout)
-- ----------------------------------------------------------------------------
-- Executed by the provisioning service at tenant onboarding. The literal
-- placeholder {tenant_schema} is substituted with e.g. `tenant_pharmco_001`
-- (see db/scripts/apply_tenant_schema.sh and docs/onboarding.md).
--
-- SYNC INVARIANTS (enforced by the app layer — see docs/data-model.md):
--   1. products.stock_qty is a DERIVED cache. Its only writer is stock_moves.
--   2. A sale writes sale_items AND one stock_moves row with qty_change = -qty.
--   3. Every local mutation bumps sync_seq on the row and appends the outbox;
--      the hub sync agent drains 'pending' -> ack only on verified cloud ack.
--   4. No hard row deletes for synced entities — soft delete via deleted_at.
-- ============================================================================

BEGIN;

CREATE SCHEMA IF NOT EXISTS {tenant_schema};

-- ---------------------------------------------------------------------------
-- devices — registered POS terminals (license.max_terminals counts active rows)
-- ---------------------------------------------------------------------------
CREATE TABLE {tenant_schema}.devices (
    id           uuid        PRIMARY KEY,
    name         text        NOT NULL,
    machine_id   text        NOT NULL UNIQUE,  -- stable hardware fingerprint (SMBIOS/WMI derived)
    is_active    boolean     NOT NULL DEFAULT true,
    last_seen_at timestamptz,
    created_at   timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- products — catalog. stock_qty is derived (invariant 1).
-- ---------------------------------------------------------------------------
CREATE TABLE {tenant_schema}.products (
    id            uuid          PRIMARY KEY,
    name          text          NOT NULL,
    barcode       text,
    category      text,
    unit          text          NOT NULL DEFAULT 'piece',  -- piece | strip | bottle | sachet | ...
    buying_price  numeric(14,2) NOT NULL DEFAULT 0,
    selling_price numeric(14,2) NOT NULL CHECK (selling_price >= 0),
    stock_qty     numeric(14,3) NOT NULL DEFAULT 0,
    reorder_level numeric(14,3) NOT NULL DEFAULT 0,
    is_active     boolean       NOT NULL DEFAULT true,
    updated_at    timestamptz   NOT NULL DEFAULT now(),
    deleted_at    timestamptz,
    sync_seq      bigint        NOT NULL DEFAULT 0,
    origin_device uuid          REFERENCES {tenant_schema}.devices(id)
);

CREATE INDEX products_barcode_idx      ON {tenant_schema}.products (barcode);
CREATE INDEX products_name_lower_idx   ON {tenant_schema}.products (lower(name));
CREATE INDEX products_updated_at_idx   ON {tenant_schema}.products (updated_at);

-- ---------------------------------------------------------------------------
-- sales — status 'pending_verification' = offline M-Pesa fallback awaiting
-- Daraja verification (docs/architecture.md § M-Pesa).
-- ---------------------------------------------------------------------------
CREATE TABLE {tenant_schema}.sales (
    id              uuid          PRIMARY KEY,
    invoice_no      text          NOT NULL UNIQUE,      -- INV-20260115-0001
    device_id       uuid          NOT NULL REFERENCES {tenant_schema}.devices(id),
    cashier_user_id uuid          NOT NULL,             -- logical ref to master.users(id)
    customer_phone  text,
    total           numeric(14,2) NOT NULL CHECK (total >= 0),
    payment_mode    text          NOT NULL CHECK (payment_mode IN ('cash','mpesa')),
    mpesa_ref       text,          -- Daraja CheckoutRequestID, or manual ref for offline fallback
    status          text          NOT NULL DEFAULT 'complete'
                                  CHECK (status IN ('complete','pending_verification','voided')),
    created_at      timestamptz   NOT NULL DEFAULT now(),
    updated_at      timestamptz   NOT NULL DEFAULT now(),
    deleted_at      timestamptz,
    sync_seq        bigint        NOT NULL DEFAULT 0,
    origin_device   uuid          REFERENCES {tenant_schema}.devices(id)
);

CREATE INDEX sales_created_at_idx ON {tenant_schema}.sales (created_at);
CREATE INDEX sales_status_idx     ON {tenant_schema}.sales (status);
CREATE INDEX sales_mpesa_ref_idx  ON {tenant_schema}.sales (mpesa_ref);

CREATE TABLE {tenant_schema}.sale_items (
    id         uuid          PRIMARY KEY,
    sale_id    uuid          NOT NULL REFERENCES {tenant_schema}.sales(id) ON DELETE CASCADE,
    product_id uuid          NOT NULL REFERENCES {tenant_schema}.products(id),
    qty        numeric(14,3) NOT NULL CHECK (qty > 0),
    unit_price numeric(14,2) NOT NULL,
    subtotal   numeric(14,2) NOT NULL
);

CREATE INDEX sale_items_sale_idx ON {tenant_schema}.sale_items (sale_id);

-- ---------------------------------------------------------------------------
-- stock_moves — THE ONLY table that changes products.stock_qty (invariant 1)
--   reason='sale'  -> ref_id = sales.id, qty_change = -qty (written atomically with sale)
--   reason='void'  -> ref_id = sales.id, qty_change = +qty (restores stock)
--   reason='stock_in' / 'stock_out' / 'adjustment' -> manual counts
-- ---------------------------------------------------------------------------
CREATE TABLE {tenant_schema}.stock_moves (
    id         uuid          PRIMARY KEY,
    product_id uuid          NOT NULL REFERENCES {tenant_schema}.products(id),
    device_id  uuid          NOT NULL REFERENCES {tenant_schema}.devices(id),
    user_id    uuid          NOT NULL,   -- logical ref to master.users(id)
    qty_change numeric(14,3) NOT NULL,   -- + in / - out
    reason     text          NOT NULL CHECK (reason IN ('stock_in','stock_out','sale','void','adjustment')),
    ref_id     uuid,                     -- sales.id when reason IN ('sale','void')
    created_at timestamptz   NOT NULL DEFAULT now(),
    sync_seq   bigint        NOT NULL DEFAULT 0
);

CREATE INDEX stock_moves_product_idx ON {tenant_schema}.stock_moves (product_id);
CREATE INDEX stock_moves_created_idx ON {tenant_schema}.stock_moves (created_at);
CREATE INDEX stock_moves_reason_idx  ON {tenant_schema}.stock_moves (reason);

-- ---------------------------------------------------------------------------
-- outbox — offline write queue. Hub sync agent drains to cloud, acks rows.
-- ---------------------------------------------------------------------------
CREATE TABLE {tenant_schema}.outbox (
    id         uuid         PRIMARY KEY,
    entity     text         NOT NULL,   -- product | sale | stock_moves | audit_log
    entity_id  uuid         NOT NULL,
    payload    jsonb        NOT NULL,
    seq        bigint       NOT NULL DEFAULT 0,
    status     text         NOT NULL DEFAULT 'pending'
                            CHECK (status IN ('pending','acked','failed')),
    error_note text,
    acked_at   timestamptz,
    created_at timestamptz  NOT NULL DEFAULT now()
);

CREATE INDEX outbox_drain_idx ON {tenant_schema}.outbox (status, seq);

-- ---------------------------------------------------------------------------
-- audit_logs — insert-only (fillfactor 70 fights page bloat on append)
-- ---------------------------------------------------------------------------
CREATE TABLE {tenant_schema}.audit_logs (
    id         uuid        PRIMARY KEY,
    user_id    uuid,                    -- logical ref to master.users(id)
    device_id  uuid        REFERENCES {tenant_schema}.devices(id),
    action     text        NOT NULL,
    entity     text,
    entity_id  uuid,
    detail     jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
) WITH (fillfactor = 70);

COMMIT;
-- ============================================================================
-- PHARMCO — 005_tenant_sales.sql  (POS sales loop — Week 3)
-- ----------------------------------------------------------------------------
-- Additive + idempotent changes to the per-tenant sales tables produced by
-- 002_tenant_template.sql. {tenant_schema} is substituted at apply time.
--
-- Adds:
--   * sales.client_sale_uuid + partial unique index → POST /api/sales
--     idempotency (same client_sale_uuid returns the existing sale, no dup)
--   * payment_mode widened to cash | mpesa_online | mpesa_manual
--     (002 stored varchar(10) 'cash'|'mpesa' — serialized POS needs both
--     'mpesa_online' and 'mpesa_manual'; existing 'mpesa' rows are normalized
--     to 'mpesa_online' BEFORE the new constraint is added)
--   * status widened to completed | pending_verification | voided (+legacy)
--   * sale_sequences — per-tenant, per-day invoice counter allocated inside
--     the (SERIALIZABLE) sale transaction, so PH-<TENANT>-<YYYYMMDD>-NNNN
--     stays sequential under concurrency
-- ============================================================================

-- ---------------------------------------------------------------------------
-- idempotency key
-- ---------------------------------------------------------------------------
ALTER TABLE {tenant_schema}.sales ADD COLUMN IF NOT EXISTS client_sale_uuid uuid;

CREATE UNIQUE INDEX IF NOT EXISTS uq_sales_client_uuid
    ON {tenant_schema}.sales (tenant_id, client_sale_uuid)
    WHERE client_sale_uuid IS NOT NULL;

-- ---------------------------------------------------------------------------
-- payment mode: cash | mpesa_online | mpesa_manual  (column is 24 chars wide)
-- ---------------------------------------------------------------------------
ALTER TABLE {tenant_schema}.sales ALTER COLUMN payment_mode TYPE varchar(24);

UPDATE {tenant_schema}.sales SET payment_mode = 'mpesa_online' WHERE payment_mode = 'mpesa';

DO $$
DECLARE cname text;
BEGIN
    SELECT conname INTO cname
      FROM pg_constraint
     WHERE conrelid = '{tenant_schema}.sales'::regclass AND contype = 'c'
       AND pg_get_constraintdef(oid) ILIKE '%payment_mode%';
    IF cname IS NOT NULL THEN
        EXECUTE format('ALTER TABLE {tenant_schema}.sales DROP CONSTRAINT %I', cname);
    END IF;
END
$$;

ALTER TABLE {tenant_schema}.sales ADD CONSTRAINT sales_payment_mode_check
    CHECK (payment_mode IN ('cash','mpesa_online','mpesa_manual'));

-- ---------------------------------------------------------------------------
-- status: 'completed' | 'pending_verification' | 'voided' (keep legacy 'complete')
-- ---------------------------------------------------------------------------
DO $$
DECLARE cname text;
BEGIN
    SELECT conname INTO cname
      FROM pg_constraint
     WHERE conrelid = '{tenant_schema}.sales'::regclass AND contype = 'c'
       AND pg_get_constraintdef(oid) ILIKE '%status%';
    IF cname IS NOT NULL THEN
        EXECUTE format('ALTER TABLE {tenant_schema}.sales DROP CONSTRAINT %I', cname);
    END IF;
END
$$;

ALTER TABLE {tenant_schema}.sales ADD CONSTRAINT sales_status_check
    CHECK (status IN ('complete','completed','pending_verification','voided'));

-- ---------------------------------------------------------------------------
-- per-tenant per-day invoice counter (allocated inside the sale transaction:
-- the upsert row-locks the counter so invoice numbers cannot collide)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS {tenant_schema}.sale_sequences (
    tenant_id uuid PRIMARY KEY,
    day       text NOT NULL,            -- YYYYMMDD (UTC)
    seq       bigint NOT NULL DEFAULT 0
);
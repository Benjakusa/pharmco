-- ============================================================================
-- PHARMCO — 004_tenant_products.sql  (Product catalog — Week 3)
-- ----------------------------------------------------------------------------
-- Additive + idempotent hardening of the per-tenant products table produced by
-- 002_tenant_template.sql. The literal placeholder {tenant_schema} is replaced
-- at runtime (db/scripts/apply_tenant_schema.sh / TenantProvisioner).
--
-- Adds:
--   * products.created_at (the template has updated_at but no created_at)
--   * DB-level non-negative price constraints (defense-in-depth; the API also
--     validates — CreateProduct_NegativePrice_Fails)
--   * index for the created_at sort / export ordering
-- ============================================================================

ALTER TABLE {tenant_schema}.products ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();

CREATE INDEX IF NOT EXISTS products_barcode_idx    ON {tenant_schema}.products (barcode);
CREATE INDEX IF NOT EXISTS products_created_at_idx ON {tenant_schema}.products (created_at);

-- Prices must never go negative even if a caller bypasses the API validation.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
                   WHERE conrelid = '{tenant_schema}.products'::regclass
                     AND conname = 'products_selling_price_nonneg') THEN
        ALTER TABLE {tenant_schema}.products
            ADD CONSTRAINT products_selling_price_nonneg CHECK (selling_price >= 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
                   WHERE conrelid = '{tenant_schema}.products'::regclass
                     AND conname = 'products_buying_price_nonneg') THEN
        ALTER TABLE {tenant_schema}.products
            ADD CONSTRAINT products_buying_price_nonneg CHECK (buying_price >= 0);
    END IF;
END
$$;
-- ============================================================================
-- PHARMCO — 006_daraja.sql  (M-Pesa Daraja integration tables + callback log)
-- ----------------------------------------------------------------------------
-- Idempotent + additive. Safe to run repeatedly.
-- Extends master.daraja_config (created by 001_master.sql) with:
--   * daraja_callback_log — append-only audit of every Safaricom callback
--   * daraja_stk_push_tracking — staged STK Push requests for idempotency
--   * Verified/processing metadata for the offline verification job
-- ============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- daraja_callback_log — every POST /api/mpesa/callback is recorded here
-- (master-level: Safaricom callbacks are not tenant-scoped at the HTTP layer)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS master.daraja_callback_log (
    id                  bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    checkout_request_id varchar(100) NOT NULL,   -- Safaricom's CheckoutRequestID
    merchant_request_id varchar(100) NOT NULL,   -- client-side request ID we sent
    result_code         integer      NOT NULL,
    result_desc         text         NOT NULL,
    mpesa_receipt_no    varchar(50),
    amount              integer,                  -- KSh (may be null on rejection)
    phone_number        varchar(15),
    transaction_date    text,                     -- Safaricom timestamp (yyyyMMddHHmmss)
    raw_payload         jsonb         NOT NULL DEFAULT '{}',  -- full callback JSON
    tenant_id           uuid         REFERENCES master.tenants(id),
    sale_invoice_no     varchar(30),             -- AccountReference we sent
    processed_at        timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT uq_callback_checkout UNIQUE (checkout_request_id)
);

CREATE INDEX IF NOT EXISTS ix_daraja_cb_tenant   ON master.daraja_callback_log (tenant_id, processed_at DESC);
CREATE INDEX IF NOT EXISTS ix_daraja_cb_invoice  ON master.daraja_callback_log (sale_invoice_no)
    WHERE sale_invoice_no IS NOT NULL;

-- ---------------------------------------------------------------------------
-- daraja_stk_push_tracking — lightweight idempotency for outbound STK Pushes
-- (tracks in-flight + completed requests so we never double-deduct or
-- double-credit a customer).  Cleared periodically by admin/cleanup job.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS master.daraja_stk_push_tracking (
    id                     uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id              uuid        NOT NULL REFERENCES master.tenants(id),
    checkout_request_id    varchar(100) NOT NULL UNIQUE,
    merchant_request_id    varchar(100) NOT NULL,
    invoice_no             varchar(30)  NOT NULL,
    customer_phone         varchar(15)  NOT NULL,
    amount_cents           integer      NOT NULL,
    status                 varchar(20)  NOT NULL DEFAULT 'pending'
                                CHECK (status IN ('pending','sent','delivered','failed','cancelled')),
    safaricom_response     jsonb,
    created_at             timestamptz  NOT NULL DEFAULT now(),
    updated_at             timestamptz  NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_daraja_stk_tenant ON master.daraja_stk_push_tracking (tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_daraja_stk_invoice ON master.daraja_stk_push_tracking (tenant_id, invoice_no)
    WHERE status = 'pending';

-- ---------------------------------------------------------------------------
-- updated_at trigger for stk push tracking
-- ---------------------------------------------------------------------------
DROP TRIGGER IF EXISTS trg_daraja_stk_updated_at ON master.daraja_stk_push_tracking;
CREATE TRIGGER trg_daraja_stk_updated_at BEFORE UPDATE ON master.daraja_stk_push_tracking
    FOR EACH ROW EXECUTE FUNCTION master.set_updated_at();

-- ---------------------------------------------------------------------------
-- Grants
-- ---------------------------------------------------------------------------
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA master TO CURRENT_USER;
ALTER DEFAULT PRIVILEGES IN SCHEMA master
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO CURRENT_USER;

COMMIT;

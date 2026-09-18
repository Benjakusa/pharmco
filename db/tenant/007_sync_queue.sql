-- ============================================================================
-- PHARMCO — 007_sync_queue.sql  (offline-first sync support)
-- ----------------------------------------------------------------------------
-- Additive + idempotent changes to the per-tenant schema.
-- Adds sync_queue table for offline operation queuing.
-- ============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- sync_queue — pending operations queued while offline
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS {tenant_schema}.sync_queue (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    entity         text        NOT NULL CHECK (entity IN ('product','sale','sale_item','stock_move')),
    operation      text        NOT NULL CHECK (operation IN ('create','update','delete')),
    payload_json   jsonb       NOT NULL,
    client_uuid    uuid        NOT NULL,           -- client-generated UUID for idempotency
    created_at     timestamptz NOT NULL DEFAULT now(),
    retry_count    integer     NOT NULL DEFAULT 0,
    last_error     text,
    synced_at      timestamptz,
    UNIQUE (client_uuid)                          -- prevent duplicate processing
);

CREATE INDEX IF NOT EXISTS ix_sync_queue_pending ON {tenant_schema}.sync_queue (created_at)
    WHERE synced_at IS NULL;

-- ---------------------------------------------------------------------------
-- client_meta — local client state metadata
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS {tenant_schema}.client_meta (
    key          text        PRIMARY KEY,
    value        jsonb       NOT NULL DEFAULT '{}',
    updated_at   timestamptz NOT NULL DEFAULT now()
);

COMMIT;

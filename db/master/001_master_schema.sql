-- ============================================================================
-- PHARMCO — Master schema (runs once against the `pharmco` database)
-- ----------------------------------------------------------------------------
-- Holds tenant lifecycle data used by the API/auth/license services. All
-- transactional pharmacy data (products, sales, stock) lives in per-tenant
-- schemas created at provisioning time from db/tenant-template/001_tenant_tables.sql
--
-- Conventions:
--   * uuid primary keys everywhere (cloud + local SQLite ids must never collide)
--   * timestamptz in UTC; app always stores UTC
--   * passwords: bcrypt cost 12 (server side)
--   * secrets at rest: AES-256-GCM, key from DARAJA_ENC_KEY env (VPS .env, chmod 600)
-- ============================================================================

BEGIN;

CREATE EXTENSION IF NOT EXISTS pgcrypto;   -- gen_random_uuid()

-- ---------------------------------------------------------------------------
-- tenants — one row per pharmacy
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS tenants (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    code               text        NOT NULL UNIQUE
                                   CHECK (code ~ '^[A-Z0-9][A-Z0-9._-]{0,63}$'),
    name               text        NOT NULL,
    owner_phone        text        NOT NULL
                                   CHECK (owner_phone ~ '^[0-9]{6,15}$'),
    license_key        text,                   -- RSA-2048 signed claim (base64url JSON)
    license_expires_at timestamptz,
    schema_name        text        NOT NULL UNIQUE
                                   CHECK (schema_name ~ '^tenant_[a-z0-9_]+$'),
    status             text        NOT NULL DEFAULT 'provisioning'
                                   CHECK (status IN ('provisioning','active','suspended','expired')),
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- users — tenant staff accounts used by the desktop client
-- (denormalized into each tenant's local SQLite cache on first sync)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS users (
    id            uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    username      text        NOT NULL,
    password_hash text        NOT NULL,       -- bcrypt, cost 12
    role          text        NOT NULL CHECK (role IN ('admin','pharmacist','cashier')),
    is_active     boolean     NOT NULL DEFAULT true,
    last_login_at timestamptz,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_users_tenant_username ON users (tenant_id, username);

-- ---------------------------------------------------------------------------
-- daraja_config — per-tenant M-Pesa Daraja credentials (AES-256-GCM encrypted)
-- Env switch so pilot pharmacies stay on the sandbox while others go live.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS daraja_config (
    tenant_id           uuid        PRIMARY KEY REFERENCES tenants(id) ON DELETE CASCADE,
    consumer_key_enc    text        NOT NULL,
    consumer_secret_enc text        NOT NULL,
    passkey_enc         text        NOT NULL,
    shortcode           text        NOT NULL,
    shortcode_type      text        NOT NULL DEFAULT 'paybill'
                                    CHECK (shortcode_type IN ('paybill','till')),
    environment         text        NOT NULL DEFAULT 'sandbox'
                                    CHECK (environment IN ('sandbox','production')),
    verified_at         timestamptz,
    updated_at          timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- refresh_tokens — revocable server-side refresh tokens (30-day lifetime).
-- Stored hashed; Redis holds nothing but cache/rate-limit state (Phase 1).
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS refresh_tokens (
    id         uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id    uuid        NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash text        NOT NULL UNIQUE,   -- SHA-256 of raw refresh JWT; never store raw
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- tenant_events — append-only lifecycle audit (created, provisioned, renewed, …)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS tenant_events (
    id         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id  uuid        REFERENCES tenants(id) ON DELETE CASCADE,
    action     text        NOT NULL,
                            -- created | provisioned | licensed | renewed | suspended | activated
    detail     jsonb       NOT NULL DEFAULT '{}',
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_tenant_events_tenant ON tenant_events (tenant_id, created_at);

-- ---------------------------------------------------------------------------
-- updated_at maintenance
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.set_updated_at()
RETURNS trigger AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_tenants_updated_at       ON tenants;
DROP TRIGGER IF EXISTS trg_users_updated_at         ON users;
DROP TRIGGER IF EXISTS trg_daraja_config_updated_at ON daraja_config;

CREATE TRIGGER trg_tenants_updated_at
    BEFORE UPDATE ON tenants FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();
CREATE TRIGGER trg_users_updated_at
    BEFORE UPDATE ON users FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();
CREATE TRIGGER trg_daraja_config_updated_at
    BEFORE UPDATE ON daraja_config FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

COMMIT;
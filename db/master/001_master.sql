-- ============================================================================
-- PHARMCO — 001_master.sql  (master schema for multi-tenant provisioning)
-- ----------------------------------------------------------------------------
-- Idempotent: safe to run repeatedly. Executed by docker-entrypoint on first
-- boot, by `Pharmco.Cli` bootstrap, and by the test fixture.
--
-- Schemas:  master  — tenant lifecycle (this file)
--           tenant_<code> — created per tenant from 002_tenant_template.sql
--
-- Grants use CURRENT_USER (the role running this script) so it works both in
-- the docker init context (POSTGRES_USER) and in CI/service containers.
-- ============================================================================

BEGIN;

CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE SCHEMA IF NOT EXISTS master;

GRANT USAGE ON SCHEMA master TO CURRENT_USER;
GRANT CREATE ON SCHEMA master TO CURRENT_USER;

-- ---------------------------------------------------------------------------
-- tenants — one row per pharmacy
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS master.tenants (
    id                 uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    code               varchar(20)  NOT NULL UNIQUE
                                    CHECK (code ~ '^[A-Z0-9][A-Z0-9._-]{0,19}$'),
    name               text         NOT NULL,
    owner_phone        varchar(15)  NOT NULL CHECK (owner_phone ~ '^[0-9]{6,15}$'),
    license_key        text,
    license_expires_at timestamptz,
    schema_name        varchar(80)  NOT NULL UNIQUE
                                    CHECK (schema_name ~ '^tenant_[a-z0-9_]+$'),
    status             varchar(20)  NOT NULL DEFAULT 'active'
                                    CHECK (status IN ('provisioning','active','suspended','expired','inactive')),
    created_at         timestamptz  NOT NULL DEFAULT now(),
    updated_at         timestamptz  NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- users — tenant staff (bcrypt hash, cost 12)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS master.users (
    id            uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id     uuid         NOT NULL REFERENCES master.tenants(id) ON DELETE CASCADE,
    username      varchar(50)  NOT NULL,
    password_hash text         NOT NULL,
    role          varchar(20)  NOT NULL CHECK (role IN ('admin','pharmacist','cashier')),
    is_active     boolean      NOT NULL DEFAULT true,
    last_login    timestamptz,
    created_at    timestamptz  NOT NULL DEFAULT now(),
    updated_at    timestamptz  NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, username)
);

-- ---------------------------------------------------------------------------
-- daraja_config — per-tenant M-Pesa Daraja credentials (AES-256 encrypted,
-- stored as bytea; key lives in the API env — never in the DB itself)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS master.daraja_config (
    tenant_id           uuid        PRIMARY KEY REFERENCES master.tenants(id) ON DELETE CASCADE,
    consumer_key_enc    bytea       NOT NULL,
    consumer_secret_enc bytea       NOT NULL,
    passkey_enc         bytea       NOT NULL,
    shortcode           varchar(10) NOT NULL,
    shortcode_type      varchar(10) NOT NULL DEFAULT 'paybill'
                                    CHECK (shortcode_type IN ('paybill','till')),
    verified_at         timestamptz,
    updated_at          timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- audit_logs — master-level, append-only. Every provisioning action is logged
-- here (the per-tenant audit_logs table records pharmacy-side actions).
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS master.audit_logs (
    id         bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id  uuid         REFERENCES master.tenants(id),
    action     varchar(60)  NOT NULL,
    detail     jsonb        NOT NULL DEFAULT '{}',
    created_at timestamptz  NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_master_audit_tenant ON master.audit_logs (tenant_id, created_at DESC);

-- ---------------------------------------------------------------------------
-- updated_at maintenance triggers
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION master.set_updated_at()
RETURNS trigger AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_tenants_updated_at ON master.tenants;
DROP TRIGGER IF EXISTS trg_users_updated_at   ON master.users;
DROP TRIGGER IF EXISTS trg_daraja_updated_at  ON master.daraja_config;

CREATE TRIGGER trg_tenants_updated_at BEFORE UPDATE ON master.tenants
    FOR EACH ROW EXECUTE FUNCTION master.set_updated_at();
CREATE TRIGGER trg_users_updated_at BEFORE UPDATE ON master.users
    FOR EACH ROW EXECUTE FUNCTION master.set_updated_at();
CREATE TRIGGER trg_daraja_updated_at BEFORE UPDATE ON master.daraja_config
    FOR EACH ROW EXECUTE FUNCTION master.set_updated_at();

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA master TO CURRENT_USER;
ALTER DEFAULT PRIVILEGES IN SCHEMA master
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO CURRENT_USER;

COMMIT;
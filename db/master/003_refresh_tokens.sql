-- ============================================================================
-- PHARMCO — 003_refresh_tokens.sql  (Auth subsystem — Week 2)
-- ----------------------------------------------------------------------------
-- Adds the revocable, rotating refresh-token store, the login-attempt audit
-- columns on master.audit_logs, and soft-delete support for master.users.
--
-- Idempotent — safe to run repeatedly. Requires 001_master.sql to have run
-- first (it creates schema `master` and master.tenants/users/audit_logs).
-- ============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- refresh_tokens — opaque 64-byte base64url tokens stored ONLY as SHA-256 hashes.
--   * family_id links every rotation of one login (reuse detection: if a
--     revoked family member is ever presented again, the WHOLE family dies).
--   * replaced_by records which row superseded a rotated token (audit trail).
-- ===========================================================================
CREATE TABLE IF NOT EXISTS master.refresh_tokens (
    id           uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id      uuid        NOT NULL REFERENCES master.users(id) ON DELETE CASCADE,
    family_id    uuid        NOT NULL,                 -- rotation chain for one login
    token_hash   text        NOT NULL UNIQUE,          -- SHA-256 hex; never the raw token
    expires_at   timestamptz NOT NULL,
    revoked_at   timestamptz,
    replaced_by  uuid        REFERENCES master.refresh_tokens(id),
    ip           inet,
    user_agent   text,
    created_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_refresh_tokens_user   ON master.refresh_tokens (user_id);
CREATE INDEX IF NOT EXISTS ix_refresh_tokens_family ON master.refresh_tokens (family_id, revoked_at);

-- ---------------------------------------------------------------------------
-- master.audit_logs — widen so every login attempt (success + failure +
-- rate_limited) carries the dimensions the auth spec requires. Existing rows
-- get NULLs for the new columns; detail jsonb stays for extra context.
-- ---------------------------------------------------------------------------
ALTER TABLE master.audit_logs ADD COLUMN IF NOT EXISTS username    text;
ALTER TABLE master.audit_logs ADD COLUMN IF NOT EXISTS tenant_code text;
ALTER TABLE master.audit_logs ADD COLUMN IF NOT EXISTS ip          inet;
ALTER TABLE master.audit_logs ADD COLUMN IF NOT EXISTS user_agent  text;
ALTER TABLE master.audit_logs ADD COLUMN IF NOT EXISTS outcome     text NOT NULL DEFAULT 'success'
    CHECK (outcome IN ('success','failure','rate_limited'));

CREATE INDEX IF NOT EXISTS ix_master_audit_created ON master.audit_logs (created_at DESC);

-- ---------------------------------------------------------------------------
-- master.users — soft delete for DELETE /api/users + case-insensitive usernames.
-- ---------------------------------------------------------------------------
ALTER TABLE master.users ADD COLUMN IF NOT EXISTS deleted_at timestamptz;

CREATE UNIQUE INDEX IF NOT EXISTS uq_users_tenant_username_ci
    ON master.users (tenant_id, lower(username));

COMMIT;
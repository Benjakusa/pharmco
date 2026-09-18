# Pharmco.Cli — tenant provisioning console tool

`dotnet` project (net8.0). Runs on the Pharmco server / any admin workstation.

```bash
# one-time RSA keypair (keep private key server-side; embed public key in client)
dotnet run --project server/Pharmco.Cli -- gen-keys --out-dir ./keys

# bootstrap the master schema into an empty database
ConnectionStrings__Master='Host=...;Database=pharmco;Username=pharmco;Password=...' \
  dotnet run --project server/Pharmco.Cli -- init-db

# provision a tenant (KSh 10,000 onboarding)
License__PrivateKeyPath=./keys/license_private.pem \
ConnectionStrings__Master='Host=...' \
  dotnet run --project server/Pharmco.Cli -- provision-tenant \
    --code TEST-001 --name "Test Pharmacy" --owner-phone 254700000000

# renew (KSh 5,000 / year) — extends from current expiry, re-signs
  dotnet run --project server/Pharmco.Cli -- renew-license --code TEST-001

# ops
  dotnet run --project server/Pharmco.Cli -- list-tenants
  dotnet run --project server/Pharmco.Cli -- deprovision-tenant --code TEST-001 --confirm TEST-001
```

Exit codes: `0` ok · `1` domain/db error (`tenant code exists`, invalid code…) · `2` usage/config error.

## What each command does (single transaction where noted)

| command | effect |
|---|---|
| `provision-tenant` | `master.tenants` row + per-tenant schema (5 tables) + admin user (bcrypt-12, random 10-char temp password) + RSA-2048 signed 365-day license; all atomic; audit row written |
| `renew-license` | expiry = max(current, now) + days; license re-signed; audit row |
| `list-tenants` | table of code/status/schema/expiry |
| `deprovision-tenant` | requires `--confirm <code>`; drops schema, marks row inactive, audit row |
| `init-db` | applies the idempotent `001_master.sql` (safe to rerun) |
| `gen-keys` | 2048-bit RSA keypair as PEM |

## Environment
| variable | used by |
|---|---|
| `ConnectionStrings__Master` | all DB commands |
| `License__PrivateKeyPath` | `provision-tenant`, `renew-license` |
# PHARMCO POS — monorepo

Offline-first, multi-tenant pharmacy POS for Kenya. Lightweight edition:
**ASP.NET Core 8 (minimal API) + PostgreSQL 16 schema-per-tenant + WPF desktop
client + SQLite HUB + Daraja M-Pesa.** See `docs/architecture.md` for the
revised decisions (single-writer HUB, self-updater, no SQLCipher).

**Status: Phase-0 scaffold + Week-2 auth.** DB schemas are real and validated;
provisioning CLI is implemented + tested; server/client are structural scaffolds
plus the implemented auth subsystem (JWT login/refresh, admin user management,
WPF credential cache with offline login + auto-lock — see `docs/api-contract.md`
and `client/Pharmco.Client/README.md`).

## Layout

```
docs/                    architecture + data model + API contract + runbook
db/
  master/                master schema (tenants, users, daraja, refresh, events)
  tenant/                per-tenant schema chain (products, sales, sync queue, …)
  scripts/               apply_tenant_schema.sh (placeholder substitution)
deploy/                  docker-compose (api+postgres+redis+caddy), Caddyfile,
                         .env.example, gen-env.sh, backup.sh, restore.sh
cli/                     python bootstrap: provision-tenant / renew-license / …
server/Pharmco.Api/      ASP.NET Core 8 minimal API scaffold (health endpoint)
client/Pharmco.Client/   WPF .NET 8 scaffold (single-writer HUB design)
```

## Quickstart

Shell into the repo; the Python CLI runs with any Python 3.10+ (cryptography
optional):

```bash
cd cli
./pharmco.py provision-tenant --code PHARMCO-001 --name "Nairobi Chemist" \
    --owner-phone 254712345678 --dry-run     # previews the request, no network
python3 -m unittest discover -s tests -v     # sign/verify + client-shape tests
```

DB schema validation (Postgres 16 used in prod; any 12+ works):

```bash
# on a machine with a live Postgres:
createdb pharmco
for f in db/master/*.sql; do psql pharmco -f "$f"; done
db/scripts/apply_tenant_schema.sh "postgres://user@host/pharmco" tenant_pharmco_001
```

Deploy: `cp deploy/.env.example deploy/.env && deploy/scripts/gen-env.sh` then
`docker compose up -d --build` (full guide in `deploy/README.md`).

## Build plan (8 weeks, from the proposal, re-sequenced)

| Week | Deliverable |
|---|---|
| 1 | API: master schema live, provisioning endpoint (`/v1/admin/tenants`), license signing the CLI already does |
| 2 | Auth (JWT 15m + refresh 30d), tenant middleware, device registration |
| 3 | Product catalog + stock-moves ledger (backend + client catalog screen) |
| 4 | WPF cash POS + 80 mm receipt printing |
| 5 | HUB single-writer SQLite + outbox → `/v1/sync/*` |
| 6 | Daraja STK Push + callback + per-tenant sandbox switch |
| 7 | Offline fallback, license enforcement timeline, self-updater, ClickOnce retired |
| 8 | Cross-PC LAN wiring, pilot at 2 pharmacies, bug bash |

Keep the Phase-2 list guarded (`docs/architecture.md` §8): no prescriptions,
batches, loyalty, SMS, web portal, analytics.

## Repository hygiene

- `.gitignore` blocks `.env`, `*.pem`, `client-publish/`, build artifacts.
- Secrets never generated into tracked files: `deploy/scripts/gen-env.sh`.
- SQL changes land as versioned files under `db/` and are CI-validated against a
  scratch Postgres before merge (the smoke script used at Phase 0 lives in
  `.tmp/smoke.sql`).
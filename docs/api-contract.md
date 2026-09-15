# Pharmco API — endpoint contract (v0.1)

Consumed by the WPF client (HUB sync agent), the bootstrap CLI (`cli/`), and
Daraja. All bodies JSON. Errors: `{ "error": { "code": "...", "message": "..." } }`.

## Auth (Phase 1)

| method | path | body / query | returns |
|---|---|---|---|
| POST | `/v1/auth/token` | `{ code, username, password, device {machine_id, name} }` | `{ access_token (15 min), refresh_token (30 d), license {status, expires_at, days_left, max_users, max_terminals} }` |
| POST | `/v1/auth/refresh` | `{ refresh_token }` | new access + (rotated) refresh |
| POST | `/v1/auth/device/heartbeat` | (bearer) `{ device_id }` | `{ ok }` — used for check-in gate (≤ 14 d) and `last_seen_at` |

## Tenant lifecycle (admin — `X-Admin-Key` header)

| method | path | body | returns | CLI |
|---|---|---|---|---|
| POST | `/v1/admin/tenants` | `{ code, name, owner_phone, admin_username?, license_days? }` | `{ tenant_id, code, schema_name, admin_username, temp_password, license_expires_at }` | `provision-tenant` |
| POST | `/v1/admin/tenants/{code}/license` | `{ days }` | `{ license_key, license_expires_at }` | `renew-license` |
| GET  | `/v1/admin/tenants` | — | `{ tenants: [{ code, status, license_expires_at }] }` | `list-tenants` |
| POST | `/v1/admin/tenants/{code}/status` | `{ status: active\|suspended\|expired }` | `{ code, status }` | `set-status` |

Provisioning (transactional, server-side): insert `tenants` row → create schema
from `db/tenant-template` → create `users.admin` (bcrypt; temp password
generated + returned once) → sign license claim (RSA-2048) → `tenant_events`
row. No SMS in MVP (welcome message is delivered by the Pharmco team; SMS is
Phase 2).

## Sync (Phase 5 — HUB → cloud)

| method | path | body | notes |
|---|---|---|---|
| POST | `/v1/sync/push` | `{ outbox_rows: [{ entity, entity_id, payload, seq }] }` | server applies & replies `{ acked_seqs }`; CONFLICT policy = LWW by `updated_at` |
| GET  | `/v1/sync/changes?since={since}&limit=500` | bearer, tenant | catalog + stock upserts to pull into HUB |

## Inventory (Phase 3)

| method | path | notes |
|---|---|---|
| GET/POST | `/v1/products` | search by name/barcode (`?q=`), paginated |
| POST | `/v1/stock-moves` | ledger insert; server recomputes `stock_qty` |
| POST | `/v1/products/import` | CSV import (VAT-free MVP; columns documented in client) |

## Sales + M-Pesa (Phases 4 & 6)

| method | path | notes |
|---|---|---|
| POST | `/v1/sales` | hub-finalized sale (offline outbox replay lands here) |
| POST | `/v1/mpesa/stkpush` | live STK Push (payer phone, amount) using tenant creds |
| POST | `/v1/mpesa/callback` | Daraja result callback (single public URL, routes by shortcode/acc-ref) |
| POST | `/v1/mpesa/verify` | `{ mpesa_ref }` → `stkpushquery` (offline fallback: ≤ 7 days) |

## OpenAPI

Swagger UI generated once tooling lands (Week 2) at `/swagger` in dev only.
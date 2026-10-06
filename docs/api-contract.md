# Pharmco API — endpoint contract (v0.2)

Consumed by the WPF client (HUB sync agent), the bootstrap CLI (`cli/`), and
Daraja. All bodies JSON. Errors: `{ "error": { "code": "...", "message": "..." } }`.

## Auth (implemented — desktop + API clients)

| method | path | body / header | returns | notes |
|---|---|---|---|---|
| POST | `/api/auth/login` | `{ pharmacy_code, username, password }` | `{ access_token (15 min), refresh_token (30 d, rotating), token_type, expires_in, user { id, role, tenant_code } }` | tenant by `pharmacy_code`; bcrypt cost 12; JWT HS256 claims `tenant_id, tenant_code, user_id, role, license_expires_at`; 5 failed attempts / username / 15 min → 429; every attempt audited to `master.audit_logs` (username, tenant_code, ip, user_agent, outcome) |
| POST | `/api/auth/refresh` | `{ refresh_token }` | new `access_token` + rotated `refresh_token` | old token revoked (`revoked_at`); replaying a revoked token kills the whole rotation family |
| POST | `/api/auth/logout` | bearer (optional body `{ refresh_token }`) | `204` | revokes the presented refresh token, or all of the user's refresh sessions when omitted |
| GET  | `/api/auth/session` | bearer | `{ user { id, role, tenant_code }, license_expires_at, token_expires_at }` | used by the desktop client at launch to validate cached credentials online |

## Users (admin only — `[Authorize(Roles = "admin")]`)

| method | path | body | returns |
|---|---|---|---|
| GET    | `/api/users`             | bearer    | `{ users: [{ id, username, role, is_active, last_login_at, created_at }] }` |
| POST   | `/api/users`             | `{ username, password, role }` | `201 { user }` |
| PATCH  | `/api/users/{id}`        | `{ role?, is_active? }` | `{ user }` |
| DELETE | `/api/users/{id}`        | —         | `204` (soft delete: `is_active=false`, `deleted_at=now()`) |

Roles (fixed enum): `admin` = full access · `pharmacist` = sales, stock, view
reports · `cashier` = sales only (no stock edit, no user mgmt).

## Tenant lifecycle (admin — `X-Admin-Key` header)

| method | path | body | returns | CLI |
|---|---|---|---|---|
| POST | `/v1/admin/tenants` | `{ code, name, owner_phone, admin_username?, license_days? }` | `{ tenant_id, code, schema_name, admin_username, temp_password, license_expires_at }` | `provision-tenant` |
| POST | `/v1/admin/tenants/{code}/license` | `{ days }` | `{ license_key, license_expires_at }` | `renew-license` |
| GET  | `/v1/admin/tenants` | — | `{ tenants: [{ code, status, license_expires_at }] }` | `list-tenants` |
| POST | `/v1/admin/tenants/{code}/status` | `{ status: active\|suspended\|expired }` | `{ code, status }` | `set-status` |

Provisioning (transactional, server-side): insert `tenants` row → create schema
from `db/tenant` → create `users.admin` (bcrypt; temp password
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
# Pharmco POS — Architecture (v1.1, revised from the MVP proposal)

This document records the *decisions that changed* relative to the original
proposal, and the ones that survived it. Requirement gates: 20–30 tenants,
1–3 PCs per pharmacy, offline-first, one VPS, ≤ KSh 5,000/month.

## 1. Topology: single-writer hub (replaces "peer-to-peer LAN sync")

The proposal's "P2P LAN sync of N SQLite DBs" was the highest-risk component.
Three writers × conflict resolution = data-loss bug generator. Revised:

```
 PHARMACY (1–3 PCs on LAN)
 ┌───────────────────────────────────────────────────────┐
 │  POS PC 2 ──┐                                         │
 │  POS PC 3 ──┤  thin: read-only catalog cache +        │
 │             │  short write queue → HUB (HTTP on LAN)  │
 │  ┌──────────┴──────────────┐                          │
 │  │ HUB PC (= POS PC 1)     │                          │
 │  │ • single writable SQLite│ • local HTTP listener    │
 │  │ • outbox (pending rows) │ • sync agent → cloud     │
 │  └──────────┬──────────────┘                          │
 └─────────────┼─────────────────────────────────────────┘
               │ HTTPS (when online)
               ▼
        PHARMCO CLOUD (1 VPS): API + Postgres 16 + Redis 7 + Caddy
```

Rules:
- **One writer** per pharmacy LAN: the HUB's SQLite. Terminals 2..3 write nothing
  directly; they send sale/stock events to the HUB over HTTP. If the HUB dies,
  terminals queue locally (small SQLite journal) and replay — never concurrent
  writes to the shared DB.
- The HUB's **outbox** is the only path into the cloud. Every local mutation
  appends `outbox` (status `pending`); the sync agent drains `pending → acked`
  only after the API confirms.
- 1–2 PC pharmacies: the HUB is POS PC 1 and the LAN hop disappears entirely.

## 2. Sync model (cloud == source of truth for reconciliation)

| Element | Value |
|---|---|
| Record identity | UUID everywhere (cloud + local cannot collide) |
| Local change tracking | `sync_seq` (monotonic) + `updated_at` + `deleted_at` soft delete |
| Queue | per-tenant `outbox` table (jsonb payload), drained oldest-seq first |
| Stock truth | `stock_moves` is the ONLY ledger; `products.stock_qty` is a derived cache |
| Conflict policy | Last-Write-Wins by `updated_at`; stock ops are additive (ledger) so no LWW needed for qty |
| Offline auth | cached refresh token (+ local passwordless grace ≤ 7 days), then forced online re-login |
| Clock abuse | HUB persists a monotonic date high-watermark; license checks use `max(now, watermark)`; online check-in required every ≤ 14 days to keep full POS mode |

`max_terminals` is enforced against `devices` (active rows) at login, and
`max_users` against tenant user count — both from the signed license claim.

## 3. Cloud footprint (honest count: 4 containers)

`api` + `postgres` + `redis` + `caddy` in `deploy/docker-compose.yml`.
Backups run from host cron (`deploy/backup.sh`); monitoring is Uptime Kuma
running on the host or a sidecar (kept out of compose on purpose). Costs on a
Hetzner CX22-class box stay ≈ KSh 1,650/mo.

## 4. Security (right-sized)

- TLS 1.3 via Caddy (Let's Encrypt, auto).
- Passwords bcrypt cost 12; JWT access 15 min + refresh 30 days (stored hashed
  server-side, revocable).
- Tenant isolation: schema-per-tenant + JWT `tenant_id` + `search_path` routing.
- Daraja creds: AES-256-GCM server-side, key from `.env` (`chmod 600`). Phase 2
  for KMS at > 100 tenants.
- Local SQLite: **not** SQLCipher (commercial license ≈ $999/yr). Boundary:
  OS full-disk encryption + Windows accounts; no payment credentials stored locally.
  If a customer demands DB encryption pre-Phase-2, ship app-level AES for the
  local file or re-evaluate.

## 5. M-Pesa (Daraja)

- Per-tenant credential row (`daraja_config`) with `environment` = sandbox|production.
- One public callback: `POST /v1/mpesa/callback` routes by shortcode/account reference.
- Live STK Push online; offline fallback = `pending_verification` + manual ref,
  auto-verified via `stkpushquery` within 7 days, else stays manual.
- Adapter isolated in one class (4 endpoints: oauth, stkpush, stkpushquery, callback).

## 6. Distribution: self-updater (replaces ClickOnce)

`version.json` on `https://{BASE_DOMAIN}/client/`:
```json
{ "version": "1.2.3", "sha256": "...", "url": "/client/Pharmco.Client-1.2.3.zip" }
```
Client checks at startup / every 6 h, downloads, verifies SHA-256, swaps, relaunches.
No code-signing cert required for MVP (SmartScreen warnings accepted; EV cert is a
Phase-2 cost item).

## 7. License enforcement timeline (unchanged from proposal, minus clock trust)

30 d banner → 14 d login popup → 7 d red banner → expiry + 14 d grace
(full POS) → +14 d read-only → +30 d hard lock. Enforcement inputs = signed
claim (from `/v1/admin/*` renewals) + HUB high-watermark + ≤ 14 d online check-in.

## 8. Out-of-scope (Phase 2+) — protect this list

Prescriptions · drug interactions · batch/expiry · suppliers/POs · loyalty ·
insurance · multi-branch · mobile · analytics · SMS · controlled-substances log ·
web admin portal (desktop app is the admin UI).
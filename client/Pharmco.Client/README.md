# Pharmco.Client — WPF desktop POS (Phase-0 scaffold + Week-2 auth)

**Status:** scaffold. Authoring environment (Linux sandbox) has no Windows/.NET SDK,
so this project is a structural starting point — open it in Visual Studio 2022 on
Windows (with .NET 8 workload) and it should build with `dotnet build`. The heavy
MVVM screens land in Phase 1 (Weeks 3-7).

## Layout

| Path             | Role |
|------------------|------|
| `App.xaml(.cs)`  | app bootstrap: local settings, SQLite open, self-update, login flow |
| `MainWindow.xaml`| root shell; Phase-1 hosts the real screens |
| `src/Models/`    | entity DTOs mirroring the cloud schema (docs/data-model.md) |
| `src/Services/`  | local SQLite access, sync engine, Daraja/STK adapter, receipt printing |
| `src/ViewModels/`| MVVM view models (Login, Catalog, POS, Stock, Reports, Settings) |

## Auth (Week 2 — PROMPT-2)

`src/Services/AuthService.cs` — API client for `/api/auth/login`,
`/api/auth/refresh`, `/api/auth/logout`, `/api/auth/session` (+ tiny `Json`).
`src/Services/CredentialCache.cs` — encrypted offline-credential store.
`src/Services/SessionManager.cs` — login/launch flows + auto-lock + logout.
Shared policy (`OfflineGate`, `IdlePolicy`, `UserRole`, bcrypt) lives in
`Pharmco.Core` so the same code is unit-tested by `server/Pharmco.Tests`.

Offline semantics (matches docs/architecture.md §2):
- Successful online login writes a cache row: username + **client-side bcrypt
  second hash (cost 12)**, role, tenant_code, user_id, DPAPI-encrypted tokens,
  `last_verified_at`.
- Offline login verifies the password against the cached hash **and** requires
  `last_verified_at` within **7 days**; older caches force an online login.
- Launch validates online first (access token, then refresh rotation),
  refreshing `last_verified_at` on success.
- Auto-lock after 5 minutes idle (configurable via `SessionManager.Options`).
- Logout revokes server-side (best-effort) and **clears the whole cache**.

### SQLCipher scope
The **credential cache** is the one local database that stores credentials, and
per the auth spec it IS SQLCipher-encrypted (AES-256; PRAGMA key = 256-bit hex
wrapped at rest by DPAPI). The business HUB SQLite (catalog/sales mirrors) stays
unencrypted per the original architecture decision — it holds no credentials.
Windows ships the SQLCipher build of the JDBC SQLite driver beside the exe; the
bare driver is dev-only (`PHARMCO_PLAINTEXT_SQLITE=1`, see CredentialCache.cs).

## Key decisions locked in for Phase 1 (see docs/architecture.md)

1. **Single-writer on the HUB PC.** At most one SQLite file is *written* per
   pharmacy LAN — the HUB's. POS PC 2..3 keep a read-only catalog cache + a short
   write queue to the HUB over HTTP (hosted by the HUB WPF instance). No P2P SQLite
   merging, ever.
2. **Self-updater, not ClickOnce.** `version.json` on `https://{BASE_DOMAIN}/client/`
   (`{version, sha256, url}`); app checks at startup, downloads, verifies hash,
   swaps, relaunches. ClickOnce is maintenance-mode + needs paid code-signing.
3. **Business SQLite is not SQLCipher.** Local business DB holds no payment
   credentials; OS disk encryption + Windows user accounts is the MVP boundary
   (commercial SQLCipher licensing ~$999/yr not justified for the business cache).
   The **credential cache is the exception and IS SQLCipher** (see above).
4. **Offline M-Pesa fallback:** `pending_verification` sales with a manual
   reference; Daraja auto-verify within 7 days once online (docs/architecture.md).

## Planned screens (Phase 1)
`Login → (Catalog | POS | Stock | Reports | Settings)` — Admin/Pharmacist/Cashier
roles gate the navigable set. Barcode entry fields are hotkey-first for speed
(strip scanners are just fast keyboards).

## Build & publish
```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o ../../client-publish
```
then upload `client-publish/` to the VPS (`deploy/README.md` → Caddy serves
`https://{BASE_DOMAIN}/client/`).
# Pharmco.Client — WPF desktop POS (Phase-0 scaffold)

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

## Key decisions locked in for Phase 1 (see docs/architecture.md)

1. **Single-writer on the HUB PC.** At most one SQLite file is *written* per
   pharmacy LAN — the HUB's. POS PC 2..3 keep a read-only catalog cache + a short
   write queue to the HUB over HTTP (hosted by the HUB WPF instance). No P2P SQLite
   merging, ever.
2. **Self-updater, not ClickOnce.** `version.json` on `https://{BASE_DOMAIN}/client/`
   (`{version, sha256, url}`); app checks at startup, downloads, verifies hash,
   swaps, relaunches. ClickOnce is maintenance-mode + needs paid code-signing.
3. **No SQLCipher.** Local DB holds no payment credentials; OS disk encryption +
   Windows user accounts are the MVP boundary (SQLCipher commercial licensing is
   ~$999/yr — not worth it for the MVP).
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
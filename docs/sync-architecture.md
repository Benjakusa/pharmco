# Pharmco POS — Offline-First Sync Engine

## Architecture Overview

The offline-first sync engine enables Pharmco POS to operate without internet connectivity,
queuing all sales locally and synchronizing when connectivity is restored.

## Components

### Client-Side (WPF)

| Component | File | Purpose |
|-----------|------|---------|
| LocalDatabase | `src/Services/LocalDatabase.cs` | SQLCipher-encrypted SQLite database mirroring server schema |
| SyncEngine | `src/Services/SyncEngine.cs` | Background IHostedService for push/pull synchronization |
| LicenseValidator | `src/Services/LicenseValidator.cs` | RSA signature verification for offline license validation |
| LicenseEnforcementService | `src/Services/LicenseEnforcementService.cs` | Tier-based license enforcement (Normal → HardLock) |
| SyncModels | `src/Models/SyncModels.cs` | DTOs for sync operations |

### Server-Side (ASP.NET Core)

| Component | File | Purpose |
|-----------|------|---------|
| SyncEndpoints | `Endpoints/SyncEndpoints.cs` | POST /api/sync/push, GET /api/sync/pull |
| LicenseEndpoints | `Endpoints/LicenseEndpoints.cs` | POST /api/license/renew-request |
| PendingVerificationJob | `Services/PendingVerificationJob.cs` | Background job for M-Pesa verification |

### Database

| Migration | Purpose |
|-----------|---------|
| `db/tenant/007_sync_queue.sql` | sync_queue + client_meta tables |

### UI Components

| Component | File | Purpose |
|-----------|------|---------|
| SyncStatusIndicator | `src/Views/SyncStatusIndicator.xaml` | Green/yellow/red status indicator |
| LicenseBanner | `src/Views/LicenseBanner.xaml` | Warning banners for expiring licenses |
| RenewDialog | `src/Views/RenewDialog.xaml` | License renewal dialog with Paybill details |

## Sync Flow

### Push (Client → Server)
1. Client creates sale locally in SQLite
2. Sale is added to sync_queue table
3. SyncEngine runs every 60s (or on manual trigger)
4. Pending operations batched (max 500, 5MB limit)
5. POST /api/sync/push with gzip-compressed JSON
6. Server processes idempotently by client_uuid
7. Client marks operations as synced

### Pull (Server → Client)
1. GET /api/sync/pull?since=<last_sync_at>
2. Server returns products, users, license updated since timestamp
3. Client applies changes with LWW (Last-Write-Wins) for products
4. Sales are append-only (no conflicts)

### Conflict Resolution
- **Products**: LWW by `updated_at` timestamp
- **Sales**: Append-only — client UUID prevents duplicates
- **Stock moves**: Created server-side from sale sync

## License Enforcement Tiers

| Tier | Days to Expiry | Behavior |
|------|----------------|----------|
| Normal | > 30 | No restrictions |
| WarningYellow | 30–15 | Yellow banner "Renews in X days" |
| DailyModal | 14–8 | Daily modal on login |
| WarningRed | 7–1 | Red banner on every screen |
| GracePeriod | 0 to -14 | Warning on every sale, sales allowed |
| ReadOnly | -14 to -30 | Sales blocked, reports work |
| HardLock | < -30 | App won't launch past login |

## ClickOnce Deployment

- Publish profile: `Properties/PublishProfiles/Pharmco.pubxml`
- URL: `https://pharmco.co.ke/client/`
- Offline available: true
- Auto-update check on startup
- Version: `1.0.<build>`

## Encryption

- Local database: SQLCipher via `PRAGMA key`
- Encryption key: `Pharmco__DatabaseKey` environment variable
- License tokens: RSA-2048 signed JWT
- Public key embedded in client for offline verification

## Testing

### Unit Tests
```bash
# Client tests
cd client/Pharmco.Client.Tests
dotnet test
```

### Required Test Coverage
- OfflineSale_QueuedInSyncQueue
- SyncPush_SendsQueuedOps
- SyncPush_Idempotent_DuplicateUUID
- SyncPull_AppliesProductChanges
- Sync_Conflict_ProductLWW
- Sync_SaleAppendOnly
- Sync_BackoffOnFailure
- Sync_ResumesAfterReconnect
- License_SignatureVerified
- License_Expired30Days_Lock
- License_GracePeriod_AllowsSales
- License_ClockTamper_Detected
- Renewal_NewLicensePushedOnSync

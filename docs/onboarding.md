# Pharmco POS — tenant onboarding runbook

Time: ~30 min per pharmacy once the platform is live (Week 8+; bootstrap CLI is
ready now and provisioning API lands Week 1).

## Prereqs (Pharmco team)

- Payment confirmed: KSh 10,000 onboarding.
- A VPS with the stack deployed (`deploy/README.md`).
- `PHARMCO_API_URL` + `PHARMCO_ADMIN_KEY` exported (key from `deploy/.env`).

## Steps

```bash
# 1. Provision (creates tenant row + per-tenant schema + admin user + 365-day license)
cd cli
./pharmco.py provision-tenant \
    --code PHARMCO-001 --name "Nairobi Chemist" --owner-phone 254712345678 \
    --admin-username admin@nairobi-chemist

#   ✓ schema_name: tenant_pharmco_001
#   ✓ admin_username: admin@nairobi-chemist
#   ✓ temp_password: <returned once>
#   ✓ license_expires_at: 2027-09-15T00:00:00Z

# 2. Relay a welcome message with the install link (SMS auto-send is Phase 2):
#    https://pharmco.co.ke/client/   — desktop app works offline after install
```

## On the pharmacy side (steps 3–6 happen in-app)

1. Install desktop app on up to 3 PCs (first PC = HUB).
2. Login as `admin@nairobi-chemist` with the temp password → forced password change.
3. Register this machine as a device (hardware fingerprint → `devices` row).
4. Enter their M-Pesa Daraja credentials (Paybill → `daraja_config`, sandbox
   flipped to production after a live test goes through).
5. Add products (CSV import from supplier list or manual entry).
6. Start selling.

The desktop team boxes run `renew-license --code PHARMCO-001 --days 365` after
the KSh 5,000/annual renewal is confirmed, then sync pushes the new claim.

## Failures & fallbacks

| symptom | action |
|---|---|
| `provision-tenant` API not up (Week 1) | schema can still be applied manually: `db/scripts/apply_tenant_schema.sh <db-url> tenant_pharmco_001` |
| Tenant abandoned pre-launch | `set-status --code PHARMCO-001 --status suspended` (keeps data, blocks logins) |
| Owner lost password | Pharmco team resets via admin endpoint (Phase 1) |

## Signals for the billing board

Daily: `list-tenants` → watch `license_expires_at`; 30/14/7-day renewal banners
are client-side (docs/architecture.md §7) but the daily report stays the source
of truth.
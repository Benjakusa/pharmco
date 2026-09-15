# Pharmco CLI (bootstrap)

Thin provisioning tool for tenant onboarding and license renewal. It calls the
API's `/v1/admin/*` endpoints (contract in `docs/api-contract.md`) — the C#
CLI that ships inside the API container in Phase 1 will implement the same
contract; this Python version exists so onboarding works from week 1 and is
fully testable on any machine.

## Requirements
- Python 3.10+
- Optional: `cryptography` (only for license signing/verification, e.g. tests)

```bash
python3 -m venv .venv && . .venv/bin/activate
pip install -r requirements.txt        # optional, see above
```

## Usage
```bash
# Provision a tenant (onboarding, KSh 10,000)
PHARMCO_API_URL=https://api.pharmco.co.ke \
PHARMCO_ADMIN_KEY=<from deploy/.env JWT_ADMIN_KEY> \
./pharmco.py provision-tenant --code PHARMCO-001 \
    --name "Nairobi Chemist" --owner-phone 254712345678

# Renew (after KSh 5,000 payment verified)
./pharmco.py renew-license --code PHARMCO-001 --days 365

# Ops
./pharmco.py list-tenants
./pharmco.py set-status --code PHARMCO-001 --status suspended

# Preview the exact request without network I/O
./pharmco.py provision-tenant ... --dry-run
```

## Action mapping
| pharmco.py            | API call                                     | DB effect (server side)     |
|-----------------------|----------------------------------------------|-----------------------------|
| `provision-tenant`    | `POST /v1/admin/tenants`                    | tenant row + schema + admin |
| `renew-license`       | `POST /v1/admin/tenants/{code}/license`     | license_key / expires_at    |
| `list-tenants`        | `GET  /v1/admin/tenants`                    | read-only                   |
| `set-status`          | `POST /v1/admin/tenants/{code}/status`      | status column               |

## Tests
```bash
python3 -m unittest discover -s tests -v
```

## Onboarding runbook
See `docs/onboarding.md` — it references this CLI step by step.
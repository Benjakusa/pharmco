#!/usr/bin/env python3
"""Pharmco provisioning CLI — tenant onboarding & license renewal.

Talk to the running API's /v1/admin endpoints (docs/api-contract.md). Standard
flow (docs/onboarding.md):

    PHARMCO_API_URL=https://api.pharmco.co.ke \\
    PHARMCO_ADMIN_KEY=<from .env> \\
    ./pharmco.py provision-tenant \\
        --code PHARMCO-001 --name "Nairobi Chemist" --owner-phone 254712345678

    ./pharmco.py renew-license --code PHARMCO-001 --days 365

Use --dry-run to preview the exact request without network I/O.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import date

from pharmco_cli import __version__
from pharmco_cli.client import AdminClient, ApiError

DEFAULT_API_URL = "https://api.pharmco.co.ke"


def _client(args: argparse.Namespace) -> AdminClient:
    return AdminClient(
        base_url=args.api_url or os.environ.get("PHARMCO_API_URL", DEFAULT_API_URL),
        admin_key=args.admin_key or os.environ.get("PHARMCO_ADMIN_KEY", ""),
        dry_run=args.dry_run,
    )


def _print_result(resp: dict) -> None:
    if resp.get("dry_run"):
        print(json.dumps(resp, indent=2, sort_keys=True))
        return
    for key in ("code", "schema_name", "admin_username", "temp_password", "license_expires_at"):
        if resp.get(key):
            print(f"✓ {key}: {resp[key]}")
    # pretty-print anything unknown (e.g. server-supplied message)
    for key in ("tenant_id", "license_key", "message"):
        if resp.get(key):
            label = key
            value = resp[key] if key != "license_key" else (resp[key][:64] + "…" if len(resp[key]) > 64 else resp[key])
            print(f"✓ {label}: {value}")


def cmd_provision(args: argparse.Namespace) -> None:
    if not args.owner_phone.isdigit() or not (6 <= len(args.owner_phone) <= 15):
        sys.exit(f"error: owner-phone must be 6-15 digits, got {args.owner_phone!r}")
    resp = _client(args).provision_tenant(
        code=args.code, name=args.name, owner_phone=args.owner_phone,
        admin_username=args.admin_username, license_days=args.license_days,
    )
    _print_result(resp)


def cmd_renew(args: argparse.Namespace) -> None:
    resp = _client(args).renew_license(code=args.code, days=args.days)
    _print_result(resp)


def cmd_list(args: argparse.Namespace) -> None:
    resp = _client(args).list_tenants()
    if args.dry_run or isinstance(resp, dict) and resp.get("dry_run"):
        print(json.dumps(resp, indent=2, sort_keys=True))
        return
    for t in resp.get("tenants", []):
        exp = (t.get("license_expires_at") or "n/a")
        print(f"{t.get('code'):<16} {t.get('status'):<12} expires {exp}")


def cmd_status(args: argparse.Namespace) -> None:
    resp = _client(args).set_status(code=args.code, status=args.status)
    _print_result(resp)


def build_parser() -> argparse.ArgumentParser:
    # Shared flags are inherited by every subcommand via `parents`, so
    # `pharmco.py provision-tenant ... --dry-run` works as users expect.
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--api-url", help=f"API base URL (env PHARMCO_API_URL, default {DEFAULT_API_URL})")
    common.add_argument("--admin-key", help="admin key (env PHARMCO_ADMIN_KEY)")
    common.add_argument("--dry-run", action="store_true", help="print the request without sending it")

    p = argparse.ArgumentParser(prog="pharmco", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--version", action="version", version=f"pharmco-cli {__version__}")

    sub = p.add_subparsers(dest="command", required=True)

    prov = sub.add_parser("provision-tenant", parents=[common],
                          help="onboard a new pharmacy (KSh 10,000 onboarding)")
    prov.add_argument("--code", required=True, help="e.g. PHARMCO-001")
    prov.add_argument("--name", required=True, help="pharmacy display name")
    prov.add_argument("--owner-phone", required=True, help="254712345678")
    prov.add_argument("--admin-username", help="default 'admin@<code-lower>'")
    prov.add_argument("--license-days", type=int, default=365)
    prov.set_defaults(func=cmd_provision)

    ren = sub.add_parser("renew-license", parents=[common],
                         help="renew after KSh 5,000 payment is verified")
    ren.add_argument("--code", required=True)
    ren.add_argument("--days", type=int, default=365)
    ren.set_defaults(func=cmd_renew)

    lst = sub.add_parser("list-tenants", parents=[common], help="list tenants + license expiry")
    lst.set_defaults(func=cmd_list)

    st = sub.add_parser("set-status", parents=[common], help="suspend / activate a tenant")
    st.add_argument("--code", required=True)
    st.add_argument("--status", required=True, choices=["active", "suspended", "expired"])
    st.set_defaults(func=cmd_status)

    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if not args.admin_key and not os.environ.get("PHARMCO_ADMIN_KEY") and not args.dry_run:
        print("error: --admin-key (or PHARMCO_ADMIN_KEY) is required unless --dry-run", file=sys.stderr)
        return 2
    try:
        args.func(args)
        return 0
    except ApiError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    except (OSError, ValueError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
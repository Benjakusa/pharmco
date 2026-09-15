"""Minimal HTTP admin client — stdlib only (urllib).

Target endpoint contract: docs/api-contract.md.
Admin endpoints are guarded by the shared X-Admin-Key header (JWT_ADMIN_KEY).
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Any, Optional


class ApiError(RuntimeError):
    def __init__(self, status: int, body: str):
        super().__init__(f"HTTP {status}: {body[:400]}")
        self.status = status
        self.body = body


class AdminClient:
    def __init__(
        self,
        base_url: str,
        admin_key: str,
        timeout: float = 20.0,
        dry_run: bool = False,
    ):
        self.base_url = base_url.rstrip("/")
        self.admin_key = admin_key
        self.timeout = timeout
        self.dry_run = dry_run

    def _request(self, method: str, path: str, body: Optional[dict] = None) -> dict:
        url = f"{self.base_url}{path}"
        if self.dry_run:
            return {"dry_run": True, "method": method, "url": url, "body": body}

        headers = {"X-Admin-Key": self.admin_key, "Accept": "application/json"}
        data = None
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"

        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:  # noqa: S310 (admin tool)
                raw = resp.read().decode("utf-8")
                return json.loads(raw) if raw else {}
        except urllib.error.HTTPError as exc:
            raise ApiError(exc.code, exc.read().decode("utf-8", "replace")) from exc

    # -- tenant lifecycle ----------------------------------------------------
    def provision_tenant(
        self,
        code: str,
        name: str,
        owner_phone: str,
        admin_username: Optional[str] = None,
        license_days: int = 365,
    ) -> dict:
        return self._request("POST", "/v1/admin/tenants", {
            "code": code,
            "name": name,
            "owner_phone": owner_phone,
            "admin_username": admin_username,
            "license_days": license_days,
        })

    def renew_license(self, code: str, days: int = 365) -> dict:
        return self._request("POST", f"/v1/admin/tenants/{code}/license", {"days": days})

    def list_tenants(self) -> dict:
        return self._request("GET", "/v1/admin/tenants")

    def set_status(self, code: str, status: str) -> dict:
        return self._request("POST", f"/v1/admin/tenants/{code}/status", {"status": status})
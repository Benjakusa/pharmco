"""RSA-2048 signed license claims.

Format (JWT-like, but domain-specific):
    base64url(header) . base64url(payload) . base64url(signature)

Payload schema (mirrors docs/data-model.md):
    {tenant_id, code, expires_at (ISO-8601 UTC), max_users, max_terminals, features}

Requires the `cryptography` package (see cli/requirements.txt). The C# port
lives server-side in Phase 1; this module powers the bootstrap CLI + tests.
"""

from __future__ import annotations

import base64
import json
from datetime import datetime, timezone
from typing import Any, Optional

try:
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import padding, rsa
    from cryptography.exceptions import InvalidSignature
except ImportError:  # pragma: no cover
    rsa = None  # type: ignore[assignment]

ALG = "RS256"
KID = "pharmco-v1"
HDR = {"alg": ALG, "typ": "JWT", "kid": KID}


class LicenseError(RuntimeError):
    """Raised for malformed, unsigned, or expired claims."""


def _require_crypto():  # pragma: no cover
    if rsa is None:
        raise LicenseError("cryptography not installed — run: pip install -r cli/requirements.txt")


def b64url_encode(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")


def b64url_decode(enc: str) -> bytes:
    pad = "=" * (-len(enc) % 4)
    return base64.urlsafe_b64decode(enc + pad)


class LicenseSigner:
    """Signs and verifies 365-day tenant licenses."""

    def __init__(self, private_pem: bytes):
        _require_crypto()
        self._key = serialization.load_pem_private_key(private_pem, password=None)

    @classmethod
    def generate_keypair(cls) -> tuple[bytes, bytes]:
        """Returns (private_pem, public_pem). Private key stays server-side."""
        _require_crypto()
        key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        priv = key.private_bytes(
            serialization.Encoding.PEM,
            serialization.PrivateFormat.PKCS8,
            serialization.NoEncryption(),
        )
        pub = key.public_key().public_bytes(
            serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo
        )
        return priv, pub

    def sign(
        self,
        tenant_id: str,
        code: str,
        expires_at: datetime,
        max_users: int = 10,
        max_terminals: int = 5,
        features: Optional[list[str]] = None,
    ) -> str:
        payload = {
            "tenant_id": tenant_id,
            "code": code,
            "expires_at": expires_at.astimezone(timezone.utc).isoformat().replace("+00:00", "Z"),
            "max_users": max_users,
            "max_terminals": max_terminals,
            "features": features or ["pos", "inventory", "mpesa"],
        }
        header = b64url_encode(json.dumps(HDR, separators=(",", ":")).encode())
        body = b64url_encode(json.dumps(payload, separators=(",", ":")).encode())
        signing_input = f"{header}.{body}".encode()
        sig = self._key.sign(signing_input, padding.PKCS1v15(), hashes.SHA256())
        return f"{header}.{body}.{b64url_encode(sig)}"

    def verify(self, token: str, public_pem: bytes) -> dict[str, Any]:
        """Verifies signature and expiry. Returns the payload dict on success."""
        _require_crypto()
        try:
            header, body, sig = token.split(".")
            payload = json.loads(b64url_decode(body))
        except (ValueError, json.JSONDecodeError) as exc:
            raise LicenseError(f"malformed license token: {exc}") from exc
        try:
            h = json.loads(b64url_decode(header))
            assert h.get("alg") == ALG and h.get("kid") == KID, "unexpected header"
        except (ValueError, AssertionError) as exc:
            raise LicenseError(f"unsupported license header: {exc}") from exc

        pub = serialization.load_pem_public_key(public_pem)
        try:
            pub.verify(
                b64url_decode(sig),
                f"{header}.{body}".encode(),
                padding.PKCS1v15(),
                hashes.SHA256(),
            )
        except InvalidSignature as exc:
            raise LicenseError("license signature invalid") from exc

        expires_at = datetime.fromisoformat(payload["expires_at"].replace("Z", "+00:00"))
        if expires_at.tzinfo is None:
            expires_at = expires_at.replace(tzinfo=timezone.utc)
        if expires_at < datetime.now(timezone.utc):
            raise LicenseError("license expired")
        return payload
"""License sign/verify round-trip tests. Run: python3 -m unittest discover -s tests -v"""

from __future__ import annotations

import unittest
from datetime import datetime, timedelta, timezone

from pharmco_cli.license import LicenseError, LicenseSigner

try:
    from cryptography.hazmat.primitives import serialization
except ImportError:  # pragma: no cover
    serialization = None

PUBLIC_PEM: bytes = b""


def setUpModule():
    global PUBLIC_PEM  # noqa: PLW0603
    priv, PUBLIC_PEM = LicenseSigner.generate_keypair()
    # stash a module-level signer for the tests
    _SIGNER.key = priv  # type: ignore[attr-defined]


class _SignerProxy:
    def __init__(self):
        self.key = None

    def __call__(self):
        return LicenseSigner(self.key)


_SIGNER = _SignerProxy()


def _make(token_ok: bool = True) -> str:
    exp = datetime.now(timezone.utc) + (timedelta(days=365) if token_ok else timedelta(days=-1))
    return _SIGNER().sign(
        tenant_id="0997f33c-7f5f-4b78-a3b1-9d9e31b5f9d2",
        code="PHARMCO-001",
        expires_at=exp,
        max_users=10,
        max_terminals=5,
        features=["pos", "inventory", "mpesa"],
    )


@unittest.skipIf(serialization is None, "cryptography not installed")
class LicenseSignerTests(unittest.TestCase):
    def test_round_trip(self):
        payload = _SIGNER().verify(_make(), PUBLIC_PEM)
        self.assertEqual(payload["code"], "PHARMCO-001")
        self.assertEqual(payload["max_terminals"], 5)
        self.assertIn("mpesa", payload["features"])

    def test_tampered_payload_rejected(self):
        token = _make()
        header, body, sig = token.split(".")
        import base64

        raw = bytearray()
        from pharmco_cli.license import b64url_decode

        raw.extend(b64url_decode(body))
        # flip one char deep in the payload json
        raw[-2] = 0x31 if raw[-2] != 0x31 else 0x32
        from pharmco_cli.license import b64url_encode

        forged = f"{header}.{b64url_encode(bytes(raw))}.{sig}"
        with self.assertRaises(LicenseError):
            _SIGNER().verify(forged, PUBLIC_PEM)

    def test_expired_rejected(self):
        with self.assertRaises(LicenseError):
            _SIGNER().verify(_make(token_ok=False), PUBLIC_PEM)

    def test_wrong_key_rejected(self):
        other_priv, other_pub = LicenseSigner.generate_keypair()
        token = LicenseSigner(other_priv).sign(
            tenant_id="x", code="OTHER", expires_at=datetime.now(timezone.utc) + timedelta(days=1)
        )
        with self.assertRaises(LicenseError):
            _SIGNER().verify(token, PUBLIC_PEM)


if __name__ == "__main__":
    unittest.main()
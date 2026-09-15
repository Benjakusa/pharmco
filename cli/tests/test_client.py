"""AdminClient tests (no network — dry-run + request shape)."""

from __future__ import annotations

import unittest

from pharmco_cli.client import AdminClient


class AdminClientTests(unittest.TestCase):
    def test_provision_dry_run_shape(self):
        c = AdminClient("https://api.pharmco.co.ke", "adminkey", dry_run=True)
        req = c.provision_tenant(
            code="PHARMCO-001",
            name="Nairobi Chemist",
            owner_phone="254712345678",
        )
        self.assertTrue(req["dry_run"])
        self.assertEqual(req["method"], "POST")
        self.assertEqual(req["url"], "https://api.pharmco.co.ke/v1/admin/tenants")
        self.assertEqual(req["body"]["code"], "PHARMCO-001")
        self.assertEqual(req["body"]["license_days"], 365)

    def test_renew_dry_run_url(self):
        c = AdminClient("https://api.pharmco.co.ke/", "k", dry_run=True)
        req = c.renew_license("PHARMCO-001", days=365)
        self.assertEqual(req["url"], "https://api.pharmco.co.ke/v1/admin/tenants/PHARMCO-001/license")

    def test_list_dry_run(self):
        c = AdminClient("https://api.pharmco.co.ke", "k", dry_run=True)
        req = c.list_tenants()
        self.assertEqual(req["method"], "GET")


if __name__ == "__main__":
    unittest.main()
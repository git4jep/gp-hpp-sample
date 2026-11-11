import importlib.util
import json
import os
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SCRIPT = Path(__file__).resolve().parents[1] / "azure.py"
SPEC = importlib.util.spec_from_file_location("merchant_azure", SCRIPT)
azure = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(azure)


class DeploymentTests(unittest.TestCase):
    def config(self):
        return {
            "subscription": "11111111-1111-1111-1111-111111111111",
            "resourceGroup": "rg-merchant-tests", "prefix": "merchantlab",
            "location": "eastus", "operatorIp": "8.8.8.8",
        }

    def state(self):
        return {
            **self.config(), "apiName": "test-api", "clientName": "test-client",
            "apiOutboundIps": "8.8.4.4", "clientOutboundIps": "1.1.1.1",
            "apiPrincipalId": "22222222-2222-2222-2222-222222222222",
            "sqlServer": "test-sql", "sqlHost": "test-sql.database.windows.net",
            "databaseName": "merchant", "vaultName": "test-vault",
        }

    def load(self, data):
        with tempfile.TemporaryDirectory() as directory:
            filename = Path(directory) / "config.json"
            filename.write_text(json.dumps(data))
            return azure.load_config(filename)

    def test_valid_config(self):
        self.assertEqual(self.config(), self.load(self.config()))

    def test_rejects_credentials_and_unsafe_parameters(self):
        for change in [
            {"GP_APP_KEY": "fake-sensitive"}, {"operatorIp": "0.0.0.0"},
            {"operatorIp": "10.0.0.1"}, {"operatorIp": "8.8.8.8/0"},
            {"prefix": "invalid-prefix"}, {"subscription": "unselected"},
            {"paymentMode": "production"}, {"currency": "INVALID"},
            {"resourceGroup": "../other"}, {"location": "eastus;command"},
        ]:
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.load({**self.config(), **change})

    def test_outbound_ips_never_fall_back_to_broad_access(self):
        self.assertEqual(["1.1.1.1", "8.8.8.8"], azure.outbound_ips("8.8.8.8,1.1.1.1,8.8.8.8"))
        for value in ("", "0.0.0.0", "10.0.0.1", "0.0.0.0/0", "::"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                azure.outbound_ips(value)

    def test_archive_has_publish_contents_at_root(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish = root / "publish"
            (publish / "wwwroot").mkdir(parents=True)
            (publish / "Merchant.Api.dll").write_text("fake-assembly")
            (publish / "wwwroot/app.css").write_text("body{}")
            azure.package(publish, root / "release.zip")
            with zipfile.ZipFile(root / "release.zip") as archive:
                self.assertEqual(["Merchant.Api.dll", "wwwroot/app.css"], archive.namelist())

    def test_archive_rejects_local_secrets_and_database_sidecars(self):
        for name in (".env", ".env.sandbox", "merchant.sqlite", "merchant.sqlite-wal", "merchant.db-shm"):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                publish = root / "publish"
                publish.mkdir()
                (publish / name).write_text("fake-private")
                with self.assertRaises(ValueError):
                    azure.package(publish, root / "release.zip")

    def test_secret_is_never_a_cli_argument_and_tempfile_is_private(self):
        deployment = azure.Deployment(self.config())
        filename = None

        def check(*arguments):
            nonlocal filename
            self.assertNotIn("fake-sensitive-secret", arguments)
            filename = Path(arguments[arguments.index("--file") + 1])
            self.assertEqual("fake-sensitive-secret", filename.read_text())
            self.assertEqual(0o600, stat.S_IMODE(filename.stat().st_mode))

        with patch.object(deployment, "az", side_effect=check):
            deployment.set_secret(self.state(), "gp-app-key", "fake-sensitive-secret")
        self.assertFalse(filename.exists())

    def test_secret_tempfile_is_removed_after_failure(self):
        deployment = azure.Deployment(self.config())
        paths = []

        def fail(*arguments):
            paths.append(Path(arguments[arguments.index("--file") + 1]))
            raise RuntimeError("simulated failure")

        with patch.object(deployment, "az", side_effect=fail), patch.object(azure.time, "sleep"), self.assertRaises(RuntimeError):
            deployment.set_secret(self.state(), "gp-app-key", "fake-sensitive")
        self.assertTrue(paths)
        self.assertTrue(all(not path.exists() for path in paths))

    def test_sql_operator_firewall_is_removed_even_when_initialization_fails(self):
        deployment = azure.Deployment(self.config())
        commands = []

        def fake_az(*arguments):
            commands.append(arguments)
            if arguments[:2] == ("account", "show"):
                return {"tenantId": "33333333-3333-3333-3333-333333333333"}

        def fake_execute(arguments, **kwargs):
            if "run" in arguments:
                self.assertNotIn("DATABASE_CONNECTION_STRING", arguments)
                self.assertIn("DATABASE_CONNECTION_STRING", kwargs["environment"])
                self.assertNotIn("MERCHANT_DATABASE_LOCAL_TEST", kwargs["environment"])
                raise RuntimeError("simulated SQL failure")

        with (patch.object(deployment, "state", return_value=self.state()),
              patch.object(deployment, "az", side_effect=fake_az),
              patch.object(azure, "execute", side_effect=fake_execute),
              patch.object(azure.time, "sleep"),
              patch.dict(os.environ, {"MERCHANT_DATABASE_LOCAL_TEST": "1"}),
              self.assertRaises(RuntimeError)):
            deployment.initialize_database()
        self.assertEqual(("sql", "server", "firewall-rule", "delete"), commands[-1][:4])
        self.assertIn("temporary-deployment-operator", commands[-1])

    def test_network_rules_are_reconciled_and_never_use_all_azure_services(self):
        deployment = azure.Deployment(self.config())
        commands = []

        def fake_az(*arguments):
            commands.append(arguments)
            if arguments[:4] == ("webapp", "config", "access-restriction", "show"):
                return {
                    "ipSecurityRestrictions": [
                        {"name": "blazor-egress-99", "ipAddress": "9.9.9.9/32"},
                        {"name": "manual-admin", "action": "Allow", "ipAddress": "8.8.8.8/32", "priority": 50},
                    ],
                    "scmIpSecurityRestrictions": [{"name": "deployment-operator", "ipAddress": "9.9.9.9/32"}],
                }
            if arguments[:4] == ("sql", "server", "firewall-rule", "list"):
                return [{"name": "api-egress-99"}, {"name": "manual-rule"}]
            return None

        with patch.object(deployment, "az", side_effect=fake_az):
            deployment.install_network_rules(self.state())
        settings = [json.loads(command[-1]) for command in commands if "--generic-configurations" in command]
        self.assertEqual("Deny", settings[0]["ipSecurityRestrictionsDefaultAction"])
        self.assertEqual(["manual-admin", "blazor-egress-0"],
                         [rule["name"] for rule in settings[0]["ipSecurityRestrictions"]])
        self.assertEqual("8.8.8.8/32", settings[0]["scmIpSecurityRestrictions"][0]["ipAddress"])
        self.assertTrue(any(command[:4] == ("sql", "server", "firewall-rule", "delete") and "api-egress-99" in command
                            for command in commands))
        self.assertFalse(any("0.0.0.0" in command for command in commands))

    def test_deployment_state_cannot_target_a_different_subscription(self):
        deployment = azure.Deployment(self.config())
        with tempfile.TemporaryDirectory() as directory:
            deployment.state_path = Path(directory) / "state.json"
            deployment.state_path.write_text(json.dumps({**self.state(), "subscription": "other"}))
            with self.assertRaises(ValueError):
                deployment.state()

    def test_azure_cli_diagnostics_do_not_expose_secrets(self):
        class Failed:
            returncode = 1
            stdout = "fake-sensitive-response"
            stderr = "fake-sensitive-error"

        with patch.object(azure.subprocess, "run", return_value=Failed()):
            with self.assertRaises(RuntimeError) as raised:
                azure.execute(["az", "keyvault", "secret", "set"], capture=True)
            self.assertNotIn("fake-sensitive", str(raised.exception))


if __name__ == "__main__":
    unittest.main()

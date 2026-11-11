"""Deploy the API and Blazor client to Azure. Never loads the repository .env."""
import argparse
import getpass
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import tempfile
import time
import uuid
import urllib.error
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
SKUS = {"B1", "B2", "B3", "S1", "P0v3", "P1v3"}


def load_config(path):
    config = json.loads(Path(path).read_text())
    if not isinstance(config, dict) or any(not isinstance(value, str) for value in config.values()):
        raise ValueError("Deployment configuration must be an object of non-secret string settings.")
    allowed = {
        "subscription", "resourceGroup", "location", "prefix", "operatorIp",
        "planSku", "gpAccountName", "gpMerchantId", "gpAppId",
        "currency", "country", "paymentMode",
    }
    if set(config) - allowed:
        raise ValueError("Unsupported configuration fields. Never put credentials in deployment JSON.")
    uuid.UUID(config["subscription"])
    if not re.fullmatch(r"[a-z0-9]{3,16}", config["prefix"]):
        raise ValueError("prefix must contain 3–16 lowercase letters/digits.")
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", config["resourceGroup"]):
        raise ValueError("resourceGroup must contain letters, digits, underscores or hyphens.")
    if not re.fullmatch(r"[a-z0-9]+", config["location"]):
        raise ValueError("location must be an Azure region identifier.")
    address = ipaddress.IPv4Address(config["operatorIp"])
    if not address.is_global:
        raise ValueError("operatorIp must be your specific public IPv4 address, not a private address or range.")
    if config.get("planSku", "B1") not in SKUS:
        raise ValueError("Unsupported App Service SKU.")
    if config.get("paymentMode", "sandbox") not in {"demo", "sandbox"}:
        raise ValueError("paymentMode must be demo or sandbox.")
    if config.get("currency", "USD") not in {"USD", "EUR", "GBP"}:
        raise ValueError("Unsupported currency.")
    if not re.fullmatch(r"[A-Z]{2}", config.get("country", "US")):
        raise ValueError("country must contain two uppercase letters.")
    return config


def execute(arguments, *, environment=None, capture=False):
    result = subprocess.run(arguments, cwd=ROOT, env=environment, text=True,
                            stdout=subprocess.PIPE if capture else None,
                            stderr=subprocess.PIPE if capture else None)
    if result.returncode:
        # Azure CLI can include credentials or settings in diagnostics; don't relay them.
        raise RuntimeError(f"{Path(arguments[0]).name} operation failed; sensitive diagnostics were suppressed.")
    return result.stdout if capture else None


class Deployment:
    def __init__(self, config):
        self.config = config
        self.state_path = ROOT / ".azure-deploy" / (config["resourceGroup"] + ".json")

    def az(self, *arguments):
        subscription = [] if arguments[:3] == ("ad", "signed-in-user", "show") else [
            "--subscription", self.config["subscription"],
        ]
        result = execute(["az", *arguments, *subscription,
                          "--only-show-errors", "--output", "json"], capture=True)
        return json.loads(result) if result and result.strip() else None

    def state(self):
        state = json.loads(self.state_path.read_text())
        for key in ("subscription", "resourceGroup", "prefix"):
            if state[key] != self.config[key]:
                raise ValueError("Deployment state does not match configuration; provision this environment first.")
        return state

    def web_arguments(self, name):
        return ["--resource-group", self.config["resourceGroup"], "--name", name]

    def sql_arguments(self, state):
        return ["--resource-group", self.config["resourceGroup"], "--server", state["sqlServer"]]

    def install_network_rules(self, state):
        api_ips = outbound_ips(state["apiOutboundIps"])
        client_ips = outbound_ips(state["clientOutboundIps"])
        for name in (state["apiName"], state["clientName"]):
            current = self.az("webapp", "config", "access-restriction", "show", *self.web_arguments(name))
            # Preserve manually added rules but replace managed rules, including an old operator IP.
            scm = [rule for rule in current.get("scmIpSecurityRestrictions", [])
                   if rule.get("name") not in {"deployment-operator", "Allow all", "Deny all"}]
            scm.append({"name": "deployment-operator", "action": "Allow",
                        "ipAddress": self.config["operatorIp"] + "/32", "priority": 100})
            settings = {
                "scmIpSecurityRestrictions": scm, "scmIpSecurityRestrictionsDefaultAction": "Deny",
                "scmIpSecurityRestrictionsUseMain": False,
            }
            if name == state["apiName"]:
                rules = [rule for rule in current.get("ipSecurityRestrictions", [])
                         if not rule.get("name", "").startswith("blazor-egress-")
                         and rule.get("name") not in {"Allow all", "Deny all"}]
                rules.extend({"name": f"blazor-egress-{index}", "action": "Allow",
                              "ipAddress": address + "/32", "priority": 100 + index}
                             for index, address in enumerate(client_ips))
                settings.update(ipSecurityRestrictions=rules, ipSecurityRestrictionsDefaultAction="Deny")
            self.az("webapp", "config", "set", *self.web_arguments(name),
                    "--generic-configurations", json.dumps(settings))
        for index, address in enumerate(api_ips):
            self.az("sql", "server", "firewall-rule", "create", *self.sql_arguments(state),
                    "--name", f"api-egress-{index}", "--start-ip-address", address, "--end-ip-address", address)
        desired = {f"api-egress-{index}" for index in range(len(api_ips))}
        for rule in self.az("sql", "server", "firewall-rule", "list", *self.sql_arguments(state)):
            if rule["name"].startswith("api-egress-") and rule["name"] not in desired:
                self.az("sql", "server", "firewall-rule", "delete", *self.sql_arguments(state), "--name", rule["name"])

    def provision(self, confirmed):
        print("This creates billable App Service, Azure SQL Basic, and Key Vault resources.")
        print(f"Subscription: {self.config['subscription']} | Resource group: {self.config['resourceGroup']}")
        if not confirmed and input("Type provision to continue: ") != "provision":
            raise ValueError("Provisioning cancelled.")
        # Require the selected .NET runtime rather than creating apps that cannot start.
        runtimes = self.az("webapp", "list-runtimes", "--os", "linux")
        if "DOTNETCORE:10.0" not in runtimes:
            raise ValueError("This Azure environment does not advertise .NET 10 Linux App Service support.")
        operator = self.az("ad", "signed-in-user", "show")
        uuid.UUID(operator["id"])
        self.az("group", "create", "--name", self.config["resourceGroup"], "--location", self.config["location"])
        parameters = {
            key: {"value": value} for key, value in self.config.items()
            if key not in {"subscription", "resourceGroup", "operatorIp"}
        }
        parameters["operatorObjectId"] = {"value": operator["id"]}
        parameters["operatorLogin"] = {"value": operator["userPrincipalName"]}
        with tempfile.TemporaryDirectory(prefix="merchant-arm-") as temporary:
            filename = Path(temporary) / "parameters.json"
            filename.write_text(json.dumps({"parameters": parameters}))
            deployment = self.az("deployment", "group", "create",
                                 "--resource-group", self.config["resourceGroup"],
                                 "--name", "merchant-sandbox",
                                 "--template-file", str(ROOT / "infra/azure/main.bicep"),
                                 "--parameters", "@" + str(filename))
        state = {key: value["value"] for key, value in deployment["properties"]["outputs"].items()}
        state.update({key: self.config[key] for key in ("subscription", "resourceGroup", "prefix")})
        self.state_path.parent.mkdir(exist_ok=True)
        self.state_path.write_text(json.dumps(state, indent=2) + "\n")
        # Reassert the admin on re-runs; server-level administrators are create-time properties.
        self.az("sql", "server", "ad-admin", "create", *self.sql_arguments(state),
                "--display-name", operator["userPrincipalName"], "--object-id", operator["id"])
        self.install_network_rules(state)
        print("Infrastructure provisioned. Next run secrets, database, deploy, then verify.")
        print("Client URL:", state["clientUrl"])

    def set_secret(self, state, name, value):
        # The secret is supplied via a protected temporary file, never a process argument.
        with tempfile.TemporaryDirectory(prefix="merchant-secret-") as temporary:
            filename = Path(temporary) / "value"
            descriptor = os.open(filename, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, "w") as output:
                output.write(value)
            for attempt in range(12):
                try:
                    self.az("keyvault", "secret", "set", "--vault-name", state["vaultName"],
                            "--name", name, "--file", str(filename), "--encoding", "utf-8")
                    return
                except RuntimeError:
                    if attempt == 11:
                        raise
                    time.sleep(10)

    def configure_secrets(self):
        state = self.state()
        app_key = os.environ.get("GP_APP_KEY")
        if not app_key:
            if self.config.get("paymentMode", "sandbox") == "demo":
                app_key = "demo-not-a-provider-key"
            else:
                if not sys.stdin.isatty():
                    raise ValueError("Secret entry requires a terminal or an already securely supplied GP_APP_KEY.")
                app_key = getpass.getpass("GP sandbox App Key (hidden): ")
        if not app_key:
            raise ValueError("A sandbox App Key is required.")
        self.set_secret(state, "gp-app-key", app_key)
        # Preserve an existing administrator key unless the operator explicitly supplies one.
        admin_key = os.environ.get("ADMIN_API_KEY")
        if admin_key is not None and len(admin_key) < 32:
            raise ValueError("ADMIN_API_KEY must have at least 32 characters.")
        if admin_key is None:
            names = self.az("keyvault", "secret", "list", "--vault-name", state["vaultName"])
            if not any(secret["name"] == "admin-api-key" for secret in names):
                admin_key = secrets.token_hex(32)
        if admin_key is not None:
            self.set_secret(state, "admin-api-key", admin_key)
        # Force versionless Key Vault references to refresh without waiting for their cache.
        resource = self.az("webapp", "show", *self.web_arguments(state["apiName"]))
        self.az("rest", "--method", "post", "--uri",
                "https://management.azure.com" + resource["id"] +
                "/config/configreferences/appsettings/refresh?api-version=2022-03-01")
        self.az("webapp", "restart", *self.web_arguments(state["apiName"]))
        print("Secrets configured in Key Vault. No secret values were printed.")

    def initialize_database(self):
        state = self.state()
        connection = (f"Server=tcp:{state['sqlHost']},1433;Database={state['databaseName']};"
                      "Encrypt=True;TrustServerCertificate=False;Connect Timeout=30;")
        environment = os.environ.copy()
        environment["DATABASE_CONNECTION_STRING"] = connection
        account = self.az("account", "show")
        environment["MERCHANT_AZURE_TENANT_ID"] = account["tenantId"]
        environment.pop("MERCHANT_DATABASE_LOCAL_TEST", None)
        # The tool explicitly acquires an Azure CLI token; no token is printed or passed as an argument.
        tool = ROOT / "tools/Merchant.Database/Merchant.Database.csproj"
        execute(["dotnet", "build", str(tool), "-c", "Release", "--nologo"])
        self.az("sql", "server", "firewall-rule", "create", *self.sql_arguments(state),
                "--name", "temporary-deployment-operator",
                "--start-ip-address", self.config["operatorIp"], "--end-ip-address", self.config["operatorIp"])
        try:
            for action in (["initialize"], ["grant", state["apiPrincipalId"]]):
                for attempt in range(12):
                    try:
                        execute(["dotnet", "run", "--project", str(tool), "-c", "Release",
                                 "--no-build", "--no-launch-profile", "--", *action], environment=environment)
                        break
                    except RuntimeError:
                        if attempt == 11:
                            raise
                        time.sleep(10)
        finally:
            self.az("sql", "server", "firewall-rule", "delete", *self.sql_arguments(state),
                    "--name", "temporary-deployment-operator")
        print("SQL schema and least-privilege managed identity user are initialized.")

    def deploy(self):
        state = self.state()
        execute(["dotnet", "test", str(ROOT / "Merchant.sln"), "--nologo"])
        for folder, project, name in [
            ("api", "backend/Merchant.Api", state["apiName"]),
            ("payment-client", "client/Merchant.PaymentClient", state["clientName"]),
        ]:
            # A fresh directory avoids retaining stale files from earlier publish results.
            with tempfile.TemporaryDirectory(prefix="merchant-publish-") as temporary:
                output = Path(temporary) / "publish"
                execute(["dotnet", "publish", str(ROOT / project), "-c", "Release",
                         "--runtime", "linux-x64", "--self-contained", "false",
                         "--output", str(output), "--nologo"])
                archive = Path(temporary) / (folder + ".zip")
                package(output, archive)
                self.az("webapp", "deploy", *self.web_arguments(name), "--src-path", str(archive),
                        "--type", "zip", "--clean", "true", "--restart", "true",
                        "--async", "true", "--track-status", "false")
        print("Release uploads accepted. Run verify to wait for readiness.")

    def verify(self):
        state = self.state()
        url = state["clientUrl"] + "/health/ready"
        for _ in range(60):
            try:
                with urllib.request.urlopen(url, timeout=15) as response:
                    ready = json.load(response)
                if ready.get("ready") is True:
                    print("PASS: Blazor -> API -> database readiness.")
                    print("Open", state["clientUrl"], "and create a NEW sandbox checkout.")
                    return
            except (urllib.error.URLError, TimeoutError, ValueError):
                pass
            time.sleep(5)
        raise RuntimeError("Readiness did not succeed. Check deployment status, Key Vault references, SQL identity, schema, and IP rules.")


def outbound_ips(value):
    addresses = sorted({str(ipaddress.IPv4Address(part.strip())) for part in value.split(",") if part.strip()})
    if not addresses or any(not ipaddress.IPv4Address(address).is_global for address in addresses):
        raise ValueError("Azure did not return a usable public outbound-IP list; refusing broad firewall access.")
    if len(addresses) > 200:
        raise ValueError("Too many outbound IP rules; use private networking instead.")
    return addresses


def package(directory, archive):
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as output:
        for filename in sorted(directory.rglob("*")):
            if filename.is_symlink():
                raise ValueError("Refusing to package symlinks.")
            if not filename.is_file():
                continue
            relative = filename.relative_to(directory)
            if (any(part.startswith(".env") for part in relative.parts)
                    or ".sqlite" in filename.name or filename.suffix in {".db", ".db-wal", ".db-shm"}):
                raise ValueError("Refusing to package a local environment/database file.")
            output.write(filename, relative.as_posix())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True, help="Non-secret deployment JSON; see infra/azure/sandbox.example.json.")
    actions = parser.add_subparsers(dest="action", required=True)
    actions.add_parser("provision").add_argument("--yes", action="store_true", help="Accept creation of billable resources.")
    for name in ("secrets", "database", "deploy", "verify"):
        actions.add_parser(name)
    args = parser.parse_args()
    try:
        deployment = Deployment(load_config(args.config))
        if args.action == "provision":
            deployment.provision(args.yes)
        else:
            method = {"secrets": "configure_secrets", "database": "initialize_database"}.get(args.action, args.action)
            getattr(deployment, method)()
        return 0
    except (ValueError, KeyError, OSError, RuntimeError, EOFError) as error:
        # Configuration input might itself contain a secret. Do not echo arbitrary values.
        if isinstance(error, RuntimeError):
            print(str(error), file=sys.stderr)
        else:
            print("Invalid or missing deployment configuration/state. Review the example and non-secret parameter values.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

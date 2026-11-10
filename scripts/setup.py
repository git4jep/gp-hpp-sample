"""Configure sandbox credentials without putting the App Key in terminal history."""
import getpass
import os
from pathlib import Path
import secrets

root = Path(__file__).resolve().parent.parent
target = root / ".env"
if target.exists():
    raise SystemExit(".env already exists. Edit it privately; setup will not overwrite it.")
base_url = input("Public HTTPS origin (for example your ngrok URL): ").strip().rstrip("/")
if not base_url.startswith("https://"):
    raise SystemExit("Sandbox callbacks require a public HTTPS origin.")
account = input("HPP-enabled account name [transaction_processing]: ").strip() or "transaction_processing"
app_key = getpass.getpass("Global Payments App Key (hidden): ").strip()
if not app_key or any(c in app_key + account + base_url for c in "\r\n"):
    raise SystemExit("Invalid configuration.")
values = {
    "GP_MODE": "sandbox",
    "PUBLIC_BASE_URL": base_url,
    "GP_MERCHANT_ID": "MER_d30e85f8e3d64aa9b4535b14e1c2095d",
    "GP_APP_ID": "8KR95sQLBorAJBAhsdxGxyn6GLO2RfEewWxjYbYKAP6DREvn",
    "GP_APP_KEY": app_key,
    "GP_ACCOUNT_NAME": account,
    "GP_CURRENCY": "USD",
    "GP_COUNTRY": "US",
    "ADMIN_API_KEY": secrets.token_urlsafe(48),
    "DATABASE_PATH": "data/merchant.sqlite",
}
fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, "w") as stream:
    stream.write("".join(f"{key}={value}\n" for key, value in values.items()))
print("Saved private configuration to .env. No secrets were displayed.")

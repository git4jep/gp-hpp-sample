"""Load the local environment into the API process without displaying secrets."""
from pathlib import Path
import os
import subprocess

root = Path(__file__).resolve().parent.parent
env = os.environ.copy()
path = root / ".env"
if path.exists():
    for line in path.read_text().splitlines():
        if line.strip() and not line.lstrip().startswith("#"):
            key, value = line.split("=", 1)
            env.setdefault(key.strip(), value.strip())
env.setdefault("ASPNETCORE_URLS", "http://127.0.0.1:5080")
database = Path(env.get("DATABASE_PATH", "data/merchant.sqlite"))
env["DATABASE_PATH"] = str(database if database.is_absolute() else root / database)
raise SystemExit(subprocess.call(
    ["dotnet", "run", "--project", str(root / "backend/Merchant.Api")],
    cwd=root, env=env
))

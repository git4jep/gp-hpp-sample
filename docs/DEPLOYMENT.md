# Setup and deployment runbook

This guide covers the Angular storefront/API deployment and the separate **Blazor payment test client**. Both support a local simulator or the Global Payments **sandbox**, not production payment processing.

Sections 2–10 describe the Angular/API deployment and shared operational requirements. For the standalone Blazor deployment, follow [section 11](#11-deploy-the-blazor-payment-test-client); Angular and Node.js are not required for that application.

For scripted Azure provisioning and deployment using **Azure SQL Database and two App Services**, follow [section 12](#12-azure-sql-and-two-azure-app-service-applications).

All build commands below run from the repository root unless stated otherwise. Examples use a Unix shell; substitute your own hostname and writable storage paths.

## 1. Choose a deployment mode

- **Local development:** Angular serves on port 4200 and proxies API/callback requests to C# on port 5080.
- **Single-origin local demo:** build Angular, then let C# serve the storefront, APIs, and callbacks together.
- **Public sandbox deployment:** publish the built Angular assets inside the C# release artifact. Expose one HTTPS origin through a reverse proxy or managed application host.
- **Standalone Blazor client:** publish the API and Blazor client separately. Keep the API private and expose the Blazor host over HTTPS; it forwards provider callbacks to the API.

Do not deploy the Angular development server as the public release server. Do not deploy only Angular assets to a static host: HPP callbacks need the C# backend, and the current frontend uses same-origin `/api` URLs.

For local demo and ngrok setup commands, see the [README](../README.md#start-immediately-local-demonstration).

## 2. Prerequisites

On the build machine:

- .NET 10 SDK.
- Node.js 24.15+ within the 24.x series and npm.
- Access to NuGet and the npm registry.
- Chrome or Playwright Chromium if running browser tests.

On the server:

- ASP.NET Core Runtime 10 for the framework-dependent publication below.
- A non-root service identity with write access to the database directory.
- A persistent local disk or volume for SQLite.
- HTTPS termination with a valid publicly trusted certificate.
- Outbound DNS and HTTPS access to `apis.sandbox.globalpay.com`.
- Sandbox app credentials with HPP link creation and transaction lookup permissions.

The published application does **not** require Node.js, npm, Python, or a .NET SDK at runtime. Python is only used by the optional local helpers.

## 3. Build and test a release

```sh
dotnet restore Merchant.sln
npm ci --prefix frontend
dotnet test Merchant.sln --no-restore
npm run build --prefix frontend
npm run test:e2e --prefix frontend
```

Browser tests start their own API on port 5080; stop your local API before running them. They force demo mode and use a separate test database. They do not validate the actual GP-hosted payment page.

Publish **after** building Angular:

```sh
dotnet publish backend/Merchant.Api/Merchant.Api.csproj \
  --configuration Release \
  --self-contained false \
  --output dist/merchant
```

The output must contain:

- `Merchant.Api.dll`
- `Merchant.Api.deps.json`
- `Merchant.Api.runtimeconfig.json`
- Runtime dependencies and the appropriate native SQLite library assets.
- `wwwroot/index.html` and Angular JavaScript/CSS assets.

Deploy the **entire** `dist/merchant` directory, not just the DLL. If the target operating system/CPU differs from the build machine and you need a platform-specific publication, add an appropriate runtime identifier, such as `--runtime linux-x64` or `--runtime linux-arm64`, and test that artifact on the target platform.

Optional dependency checks:

```sh
npm audit --prefix frontend
dotnet list backend/Merchant.Api/Merchant.Api.csproj package \
  --vulnerable --include-transitive
```

## 4. Configure the deployed process

Inject the following non-secret values through the host's environment configuration:

```text
GP_MODE=sandbox
PUBLIC_BASE_URL=https://merchant.example.com
LOCAL_DEV_ORIGINS=
GP_ACCOUNT_NAME=transaction_processing
GP_CURRENCY=USD
GP_COUNTRY=US
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5080
DATABASE_PATH=/srv/northstar/data/merchant.sqlite
```

Replace the example hostname, account name, country/currency, and storage path as needed. The sample merchant and App IDs default to those configured in the source; set `GP_MERCHANT_ID` and `GP_APP_ID` explicitly when deploying another account.

Separately inject these secrets from your host's secret manager or a protected service environment file:

- `GP_APP_KEY`: your sandbox App Key.
- `ADMIN_API_KEY`: an independent random administrator bearer key of at least 32 characters.

Do not put secret values directly into deployment commands or container image layers, and do not print token endpoint responses into CI logs. Avoid copying local `.env` files into release artifacts.

`LOCAL_DEV_ORIGINS=` intentionally removes extra localhost access on a public deployment. While testing the public sandbox from a local Angular server, omit that override to use the four development origins, or explicitly list the loopback origins needed.

`PUBLIC_BASE_URL` must be the externally reachable **origin only**. It cannot include a path prefix such as `/shop`, credentials, a query, or a fragment. Changing it requires restarting the API and creating new HPP links.

`ASPNETCORE_ENVIRONMENT=Production` does not enable real payments. The GP API endpoint is hardcoded to sandbox, and `GP_MODE` accepts only `demo` or `sandbox`.

## 5. Prepare durable SQLite storage

Create the configured database directory using your deployment platform and grant the service identity read/write access. Keep it outside both `wwwroot` and the release directory.

The application creates the database and its tables on first use. It needs permission to create and modify the main file plus SQLite's `-wal` and `-shm` sidecars in the same directory.

- Use an absolute `DATABASE_PATH` so a restart or working-directory change does not create another database.
- Keep the volume across releases and process restarts.
- Do not place the database on a read-only release filesystem.
- Use one application instance for this sample. SQLite and the current design are not a horizontally scaled deployment solution.
- Back up with a SQLite-aware backup mechanism, or stop writes and checkpoint/close the database before copying it. Copying only the main file while WAL writes are active may omit recent orders.
- Establish retention and access controls for order/event records and their capability keys.

The current schema uses `CREATE TABLE IF NOT EXISTS`, not a migration framework. Future schema changes require a deliberate migration and rollback procedure.

## 6. Start the published application

After the environment and database directory are prepared, run the release from the repository/build root:

```sh
dotnet dist/merchant/Merchant.Api.dll \
  --contentRoot "$PWD/dist/merchant"
```

`--contentRoot` ensures ASP.NET finds the deployed `wwwroot`, rather than looking in the caller's working directory. On a deployed machine, substitute the absolute release location, for example `/srv/northstar/releases/current`.

Use your platform's service supervisor to set the environment, run as the service identity, restart after failures, and retain operational logs. The published executable does not load `.env` automatically. Do not use `dotnet run` or a development file watcher as the deployed process.

The loopback binding above assumes the HTTPS proxy runs on the same host. In a container or managed-host environment where ingress reaches the app over a private network, use the host-required internal binding and port instead; do not expose an unprotected Kestrel listener directly to the internet.

## 7. Configure HTTPS and routing

Point the public hostname to your reverse proxy/managed ingress and forward requests to the C# listener **without stripping or rewriting paths or query strings**.

Forward all of the following to the same application:

- `/` and Angular JavaScript/CSS assets.
- `/api/**`, including `POST /api/checkout`.
- `/payments/return/{id}` and `/payments/cancel/{id}` for both GET and POST.
- `/api/payments/notifications/{id}` for POST.
- `/payment/**` so direct navigation to Angular result routes serves the SPA entry point.

Preserve methods and callback bodies; accept JSON and URL-encoded payloads. The application has a 32 KB request-body limit. Do not redirect a callback POST through another path or send API/callback requests to a static SPA fallback.

The return/cancel responses deliberately include a small script that navigates the top-level browser back to Angular. Do not have ingress overwrite their per-response Content Security Policy or add `X-Frame-Options: DENY`; GP may render that bridge within its hosted page. Keep the application's default framing protection for ordinary merchant pages.

GP status notifications must reach the API without interactive login screens, tunnel-warning pages, or browser-only challenges. Keep the admin endpoints separately protected with their bearer key; normal Angular checkout does not call them.

The application currently rate-limits using `RemoteIpAddress` and does not configure trusted forwarded headers. Behind a reverse proxy, clients can share the proxy's rate-limit bucket. Address this with deployment-specific trusted-proxy configuration before a larger deployment; do not blindly trust internet-supplied forwarding headers.

## 8. Validate the deployment

### Basic availability

From the application host, with the configured listener running:

```sh
curl --fail --silent --show-error http://127.0.0.1:5080/api/config
```

From outside the network, substitute the deployed hostname:

```sh
curl --fail --silent --show-error https://merchant.example.com/api/config
curl --fail --silent --show-error --output /dev/null \
  https://merchant.example.com/
curl --fail --silent --show-error --output /dev/null \
  https://merchant.example.com/payment/pending
```

Confirm `/api/config` reports the expected mode, currency, and public origin. It contains no provider or administrator secrets. The result route without an order capability only tests SPA routing; it is not a valid receipt.

`GET /health/ready` checks database connectivity and schema access. The Blazor host also exposes `/health/ready`, checking the API's database readiness server-to-server. Neither readiness check calls GP or proves HPP permissions; perform the full sandbox acceptance below.

### Full sandbox acceptance

1. Open the public storefront, or Angular on an explicitly allowed local origin.
2. Add a product and enter a fictitious email.
3. Click **Continue to secure payment**.
4. Confirm `POST /api/checkout` returns 201 and the browser reaches an actual GP sandbox hosted page.
5. Verify the newly created link's return, cancel, and status notification URLs use the configured public HTTPS origin. Do not publish the state/access keys from those URLs.
6. Complete a payment using GP's account/region-appropriate sandbox test data and any required 3DS interaction.
7. Confirm return to `/payment/success`, and inspect the merchant API's stored result. A `PREAUTHORIZED` response must show authorization rather than captured funds.
8. Start another checkout and use the **GP-hosted** cancel action. Confirm return to `/payment/cancelled` unless an approval has already been verified.
9. Exercise a documented decline scenario and confirm a declined/pending outcome is not shown as paid.
10. Confirm a verified notification can update an order even without a browser return. Use authenticated event/reconciliation APIs for recovery as needed.
11. Restart the service and confirm the same order can still be retrieved from durable storage.

Automated verification after Azure SQL support passed 46 .NET tests, with one SQL Server integration test explicitly skipped pending a configured test database, plus 11 offline deployment-script tests. The Blazor Chrome smoke test also passed automatic success, decline, and cancellation navigation, status actions, and mobile layout checks. These are demo/fake-provider checks, not actual GP transactions. The last attempted real sandbox checkout from localhost returned upstream **502** before reaching HPP. Actual provider payment/cancellation redirects remain an **outstanding acceptance check**, not a completed deployment guarantee.

## 9. Troubleshooting

### Angular returns 502 for `/api/config`

The Angular development proxy cannot reach C# on `127.0.0.1:5080`. Start the API with `python3 scripts/run-api.py`, check its startup output, and ensure no other process is occupying the port.

### Checkout returns “Invalid request origin”

Compare the browser's `Origin` request header with `PUBLIC_BASE_URL` and `LOCAL_DEV_ORIGINS`. Scheme, host, and port must match an explicit entry. Restart the API after configuration changes. Keep the public HTTPS base URL for GP callbacks; add an appropriate loopback development origin rather than replacing it with localhost.

### Checkout returns 502 from the GP client

This is different from the Angular proxy's 502: the C# API has run and could not reach/parse a GP response, or GP rejected the operation.

- Check outbound DNS, port 443, HTTPS proxy settings, and the server's trusted certificate chain.
- Confirm sandbox credentials belong to the configured merchant.
- Confirm `GP_ACCOUNT_NAME`, HPP enablement, `LNK_POST_Create`, and transaction lookup permissions.
- Inspect sanitized API errors and hosting/network diagnostics, without logging credentials, access tokens, or raw payment data.
- Do not disable TLS certificate validation to get past a network/certificate problem.

### Storefront loads but Angular result routes return 404

Ensure Angular was built before publishing, `wwwroot/index.html` is present, and the process has the correct content root. Route `/payment/**` to C#, not an unrelated static server.

### GP cannot call back or the shopper returns to an old hostname

Check public DNS/TLS, ingress route handling, and the current `PUBLIC_BASE_URL`. For ngrok, keep the tunnel running and confirm it forwards to the C# host. After a URL change, restart the API and create a new checkout; existing links keep their original callback URLs.

### SQLite is read-only, orders disappear, or deployment creates a new database

Check the absolute path, service-user permissions, writable directory for WAL files, persistent volume mount, and working directory. Do not locate the database inside `wwwroot` or replace it as part of a release rollout.

### Admin routes return 401

Set an independent `ADMIN_API_KEY` of at least 32 characters and send it only as a server-to-server bearer header. The local setup script generates this key but does not display it. Do not add it to Angular.

## 10. Updates, rollback, and operating limits

Build releases into separate directories, preserve persistent storage and secrets, and switch the supervised process to the new artifact only after smoke checks. Retain the previous release for rollback. Restart into the prior artifact if necessary, but do not overwrite the live database with a stale copy. If a future release changes the schema, verify rollback compatibility before deploying it.

Review the [production-work limitations](../README.md#scope-and-production-work) before expanding the sample: checkout idempotency, reliable background reconciliation, refund/reversal states, durable queues, monitoring, migrations, and real GP production onboarding are not implemented here.

## 11. Deploy the Blazor payment test client

### Architecture and prerequisites

The Blazor application is an interactive server-side .NET 10 application, not a static WebAssembly site. It requires a continuously running ASP.NET Core process and a browser connection to its Blazor circuit.

Request flow:

1. Browser → public HTTPS Blazor host.
2. Blazor → private API for catalog, checkout, orders, and reconciliation.
3. Browser → GP-hosted payment page.
4. GP browser return/cancel or server notification → public Blazor callback route → private API.
5. API verifies/persists the result and returns breakout HTML → browser navigates to a Blazor result page.

Use the .NET 10 SDK to build and ASP.NET Core Runtime 10 to run both applications. Node.js is needed only for the optional Playwright smoke test, not for building or serving Blazor. Apply the shared SQLite storage and sandbox credential requirements in sections 2, 4, and 5.

### Build, test, and publish

From the repository root:

```sh
dotnet restore Merchant.sln
dotnet test Merchant.sln --no-restore
dotnet publish backend/Merchant.Api/Merchant.Api.csproj \
  --configuration Release --self-contained false --output dist/api
dotnet publish client/Merchant.PaymentClient/Merchant.PaymentClient.csproj \
  --configuration Release --self-contained false --output dist/payment-client
```

Deploy both **entire** output directories, including runtime dependencies, static web assets, configuration files, and SQLite native assets. For example, place them at `/srv/northstar/releases/current/api` and `/srv/northstar/releases/current/payment-client`. Do not build Angular for this deployment; the API does not need its storefront assets.

When publishing from a checkout where Angular has previously been built, the API may still contain Angular files in `wwwroot`. Those assets are not used by Blazor. Keep the API listener private rather than exposing its old storefront as another public entry point.

Add and test an appropriate `--runtime` identifier if targeting a different OS/CPU. Do not copy `.env`, local databases, or developer-specific launch configuration into service environment files.

### Configure the two services separately

Set these non-secret environment variables on the **API service**:

```text
GP_MODE=sandbox
PUBLIC_BASE_URL=https://payments.example.com
LOCAL_DEV_ORIGINS=
GP_ACCOUNT_NAME=transaction_processing
GP_CURRENCY=USD
GP_COUNTRY=US
DATABASE_PATH=/srv/northstar/data/merchant.sqlite
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5080
```

Replace the origin, processing account, currency/country, and database path with your deployment values. Supply `GP_APP_KEY` and the separate `ADMIN_API_KEY` privately to the API service using the host's secret manager. Set the merchant/App IDs if not using the sample defaults. Do not put any of these secrets in the Blazor service.

Set these variables on the **Blazor service**:

```text
MerchantApi__BaseUrl=http://127.0.0.1:5080/
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5090
AllowedHosts=payments.example.com
```

`MerchantApi__BaseUrl` uses the double-underscore convention for `MerchantApi:BaseUrl`. It must be an HTTP(S) origin without credentials, path prefixes, query strings, or fragments. It points to the API listener, **not** the public Blazor host; pointing it at Blazor creates failed requests or callback loops.

`PUBLIC_BASE_URL` belongs to the API and must point to the **public Blazor HTTPS origin**. It controls the HPP link's callback URLs and the final result-page destination. The client makes normal API requests server-to-server, so adding its origin to `LOCAL_DEV_ORIGINS` is unnecessary.

The loopback bindings assume all processes run on one host. With separate containers/services, use private service DNS, the required container bindings/ports, and appropriate network policy. Keep port 5080 inaccessible from the public internet. Use authenticated/encrypted private transport when the API is not on a trusted local network.

### Start the published services

After injecting the respective environments and preparing durable SQLite storage, run each command as a separate supervised service:

```sh
dotnet /srv/northstar/releases/current/api/Merchant.Api.dll \
  --contentRoot /srv/northstar/releases/current/api
```

```sh
dotnet /srv/northstar/releases/current/payment-client/Merchant.PaymentClient.dll \
  --contentRoot /srv/northstar/releases/current/payment-client
```

Run under non-root service identities and configure automatic restart. The API identity needs database-directory write access; Blazor does not need database access. Start the API first. Neither published application automatically loads the repository's `.env`, and `launchSettings.json` does not configure a published process.

Keep one API and one Blazor instance for this sample. Interactive circuits live in Blazor server memory; deployments/restarts interrupt them. A scaled deployment needs a deliberate circuit/session-affinity and shared Data Protection key strategy in addition to addressing the API's SQLite limits.

### HTTPS ingress and WebSocket routing

Route the public hostname's **entire path space** to the Blazor listener on port 5090, preserving methods, paths, query strings, callback content types, and bodies. Do not route ordinary `/api/**` traffic directly to the API: this client intentionally exposes only the three provider callback patterns, not public API/admin proxying.

Ingress must support:

- `/`, `/payment`, and static assets, including `/_framework/**`.
- `/_blazor` negotiation and WebSocket upgrades/long-lived connections.
- `GET|POST /payments/return/{id}` and `/payments/cancel/{id}`.
- `POST /api/payments/notifications/{id}`.
- `/payment/**` result pages and `/demo/checkout/**` when using demo mode.

Example Nginx configuration inside its `http` context (adapt certificate paths and timeouts to your host):

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    '' close;
}

# Do not record callback state or receipt access keys in access logs.
log_format payment_safe '$remote_addr $request_method $uri $status';

server {
    listen 443 ssl;
    server_name payments.example.com;
    ssl_certificate /etc/letsencrypt/live/payments.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/payments.example.com/privkey.pem;
    access_log /var/log/nginx/payment-access.log payment_safe;
    client_max_body_size 32k;

    location / {
        proxy_pass http://127.0.0.1:5090;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_read_timeout 3600s;
        proxy_buffering off;
    }
}
```

Point DNS at this ingress and install the actual certificate before use. If offering HTTP, redirect browser GET traffic to HTTPS, but configure GP callback URLs as HTTPS from the outset rather than depending on a redirect for provider POST requests.

Do not overwrite callback responses' Content Security Policy or add a blanket `X-Frame-Options: DENY`: GP may render the API's HTML bridge in a frame before it navigates the top-level browser. Do not cache callbacks or result pages. Disable/redact query strings in all ingress, tunnel, tracing, and application request logs; callback and receipt URLs contain private capabilities.

Notifications must bypass interactive authentication and browser-only gateway challenges. Callback state validation remains in the API; preserving the `state` query is mandatory. Keep the API's rate limits in mind: forwarded requests share the Blazor server's API-facing IP and therefore share its rate-limit buckets.

### Validate the deployed release

From the host, confirm the private API configuration:

```sh
curl --fail --silent --show-error http://127.0.0.1:5080/api/config
```

From outside the network, check the Blazor routes:

```sh
curl --fail --silent --show-error --output /dev/null https://payments.example.com/
curl --fail --silent --show-error --output /dev/null \
  https://payments.example.com/payment/return
curl --fail --silent --show-error --output /dev/null \
  https://payments.example.com/payment/cancel
```

The two result routes without an order link should render a message requesting the complete link, never a successful-payment receipt. `/api/config` is intentionally **not** exposed on the public Blazor host; use the private API listener for that check.

In a browser, verify that the payment form becomes enabled after its Blazor connection is established. Create a new sandbox checkout and exercise actual GP completion and cancellation. Confirm the HPP link uses:

- `https://payments.example.com/payments/return/{orderId}?state=...`
- `https://payments.example.com/payments/cancel/{orderId}?state=...`
- `https://payments.example.com/api/payments/notifications/{orderId}?state=...`

Do not configure `/payment/return` or `/payment/cancel` as provider callback URLs: these are display-only result-page aliases. The API's real callbacks validate state, reconcile payment status, then navigate to `/payment/success`, `/payment/pending`, `/payment/declined`, or `/payment/cancelled`, carrying the separate order access key.

Check **Refresh status** and **Verify with API**, and confirm a recorded authorization is not presented as captured funds. Restart both services and verify that a complete stored order link still loads. Follow the full sandbox acceptance steps in section 8; demo checks do not certify real GP behavior.

For a credential-free deployment check, set `GP_MODE=demo` on the API while retaining the public Blazor origin, restart the API, and create new demo orders. To run automated local browser checks with temporary servers/storage:

```sh
dotnet build Merchant.sln
npm ci --prefix frontend
node client/Merchant.PaymentClient.Tests/browser-smoke.cjs
```

The script uses ports 15080/15090 and installed Chrome. For Playwright Chromium, install it from the frontend directory and set `PLAYWRIGHT_CHANNEL=chromium`. Node/Playwright are verification tools only, not Blazor runtime dependencies.

### Blazor-specific troubleshooting and rollback

- **Form remains disabled or buttons do not respond:** check that `/_blazor` negotiation/WebSockets reach the Blazor process and that ingress permits long-lived connections. Reload after a service restart.
- **“Cannot connect to the merchant API”:** verify the Blazor service's `MerchantApi__BaseUrl`, API availability, private network/firewall rules, and private `/api/config` response.
- **Callback returns 502:** the Blazor callback forwarder cannot reach the API, or the upstream API returned an error. Check both services without logging state keys or raw callback payloads.
- **Callback returns 404:** check the complete state query, order/mode, API database, and whether an older HPP link points at an obsolete deployment origin.
- **Browser returns to Angular, localhost, or the wrong hostname:** set the API's `PUBLIC_BASE_URL` to the public Blazor origin, restart it, and create a new HPP link.
- **Published app is missing assets:** deploy the full publish directory and use the correct absolute `--contentRoot`; do not copy only the DLL.

Version and roll out the API and client as a compatible pair. Retain the previous pair for rollback, preserve the database and secrets, and expect active Blazor circuits to reconnect/reload during a release. Never roll back by replacing the live database with a stale release copy.

## 12. Azure SQL and two Azure App Service applications

### What the scripts create

`infra/azure/main.bicep` provisions a dedicated sandbox resource group deployment containing:

- One Linux App Service plan, **B1 with one instance** by default, shared by two .NET 10 Web Apps.
- A public Blazor Web App with HTTPS, Always On, and session affinity.
- An API Web App with a system-assigned managed identity and default-deny IP access restrictions.
- An Azure SQL logical server with **Microsoft Entra-only authentication**, plus a **Basic 5-DTU / 2-GB** database.
- A Key Vault with RBAC, soft-delete/purge protection, and API-only application access to GP/admin secrets.

These are **billable resources**. Stopping a Web App does not stop App Service plan/database billing. Check current regional pricing and set an Azure budget before provisioning. This is a single-region sandbox, not a production payment platform.

The API connects to SQL with its managed identity, not a SQL username/password. The deployment operator initializes the schema and grants only:

- `SELECT`, `INSERT`, `UPDATE` on `dbo.orders`.
- `SELECT`, `INSERT` on `dbo.payment_events`.
- `SELECT` on `dbo.schema_versions`.

The runtime identity has no schema-creation, user-management, delete, or `db_owner` permissions. Azure RBAC on the logical server alone does not grant database data access; the `database` command creates the contained SQL user explicitly.

### Prerequisites

- .NET 10 SDK, Python 3.10+, Azure CLI, and the Bicep CLI (`az bicep install`).
- An Azure subscription permitting App Service Linux/.NET 10, Azure SQL, Key Vault, and role assignments in the chosen region.
- A human Microsoft Entra user signed in with `az login`. The current script uses that user as SQL Entra administrator; service-principal/CI bootstrap is not implemented.
- Subscription/resource-group provisioning permission and permission to create RBAC assignments, such as Owner or Contributor plus User Access Administrator. Your tenant policies must permit configuring the SQL Entra administrator.
- Your current public **IPv4** address. The scripts allow this specific address for SCM deployment and temporarily for SQL bootstrap; they do not discover it or open broad ranges.
- Outbound HTTPS to Azure/NuGet and TCP 1433 for operator SQL bootstrap. Azure SQL redirect-mode connections may also require the documented outbound SQL port ranges.
- Your sandbox App Key and an HPP-enabled processing account with link creation/transaction lookup permissions.

Use your organization's trusted CA configuration for Python, Azure CLI, and .NET if a corporate HTTPS proxy is involved. Do not disable TLS verification.

### Configure the environment

From the repository root:

```sh
cp infra/azure/sandbox.example.json infra/azure/sandbox.local.json
```

Edit `sandbox.local.json` with:

- `subscription`: the exact subscription UUID; every Azure CLI operation targets it explicitly.
- `resourceGroup`, `location`, and `prefix`: use a dedicated sandbox. The prefix is 3–16 lowercase letters/digits.
- `operatorIp`: your single public IPv4 address, without a CIDR suffix.
- `gpAccountName`: the processing account enabled for your GP sandbox application.
- Optional `gpMerchantId`, `gpAppId`, `currency`, `country`, `planSku`, and `paymentMode`.

The default `paymentMode` is `sandbox`; `demo` allows a preliminary Azure-only simulator check. Provider secrets must **never** go in this JSON. Unknown configuration keys are rejected. Local parameter files and `.azure-deploy/` state are gitignored.

Run commands in this order:

```sh
az login
az bicep install
python3 scripts/azure.py --config infra/azure/sandbox.local.json provision
python3 scripts/azure.py --config infra/azure/sandbox.local.json secrets
python3 scripts/azure.py --config infra/azure/sandbox.local.json database
python3 scripts/azure.py --config infra/azure/sandbox.local.json deploy
python3 scripts/azure.py --config infra/azure/sandbox.local.json verify
```

`provision` displays the subscription/resource group and requires typing `provision` before creating billable resources. Use `provision --yes` only when intentionally suppressing that confirmation.

The provisioning command records non-secret resource names, actual hostnames, identity object ID, and outbound IPs in `.azure-deploy/<resourceGroup>.json`. Keep that file for later commands. If provisioning fails after resources were created, fix the cause and rerun with the same configuration; the scripts do not automatically delete resources or roll back cloud operations.

### Secret handling

`secrets` prompts for the GP App Key with input hidden, or uses an already securely supplied `GP_APP_KEY` environment variable. It creates a random administrator key only if one does not already exist; an explicitly supplied `ADMIN_API_KEY` must contain at least 32 characters. Existing administrator keys are not silently rotated.

Secrets go through owner-only temporary files to Key Vault, never command-line values, source files, ARM parameters, or printed command responses. Temporary files are removed even after failures. The script never loads your local repository `.env`. Do not enable Azure CLI debug logging or run wrappers that dump subprocess environments.

The API receives versionless Key Vault references. The command requests reference refresh and restarts the API; role assignments/Key Vault references can take time to propagate. The Blazor service receives no GP, admin, or SQL credentials.

In demo mode a clearly fake GP key is stored only to keep the reference resolvable. Before switching to sandbox, rerun `secrets` with the actual key.

### SQL schema and bootstrap

`database` builds `tools/Merchant.Database`, temporarily permits your configured IP in the SQL firewall, and connects using an **Azure CLI access token** for the selected subscription's tenant. Tokens are not printed or passed as arguments. It then:

1. Acquires a transaction-scoped SQL application lock.
2. Applies `backend/Merchant.Api/Database/001-initial.sql` if schema version 1 is not recorded.
3. Creates the API's Entra contained user from its object SID without requiring Directory Readers/Microsoft Graph lookup permissions.
4. Grants the table-level permissions above.
5. Removes the temporary operator SQL firewall rule in a `finally` block.

If the process is forcibly terminated, inspect/remove the `temporary-deployment-operator` firewall rule manually. The schema initializer is repeatable and rejects newer schemas. If the API identity changes, the tool rejects the existing user's SID rather than silently reassigning it; review and replace that contained user as an administrator.

An API process configured with `DATABASE_PROVIDER=sqlserver` checks the schema version but never creates SQL tables. Missing schema or grants cause failure, not fallback to a fresh local database.

The tool starts a new schema; it does **not** migrate existing SQLite order/event data. Retain the original SQLite database separately if those local receipts matter.

### Application configuration and networking

Bicep sets the API's configuration to:

```text
DATABASE_PROVIDER=sqlserver
DATABASE_CONNECTION_STRING=Server=tcp:<actual-sql-host>,1433;Database=merchant;Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30;
PUBLIC_BASE_URL=https://<actual-blazor-host>
LOCAL_DEV_ORIGINS=
ASPNETCORE_ENVIRONMENT=Production
```

Blazor receives `MerchantApi__BaseUrl=https://<actual-api-host>/` and an explicit `AllowedHosts`. Neither app uses localhost listener settings from your laptop. The scripts do not build Angular or depend on ngrok.

This sandbox uses public TLS endpoints with scoped IP rules, **not private endpoints**:

- API main ingress allows the Blazor app's possible outbound IPs; other callers are denied by default.
- Both apps' SCM endpoints allow the configured operator IP, independently of main-site restrictions.
- SQL firewall allows the API app's possible outbound IPs, plus the short-lived operator bootstrap rule.
- There is no SQL `0.0.0.0` “allow all Azure services” rule.

App Service outbound IPs can be shared with other workloads. These allowlists are a network filter, not proof of caller identity; admin and order/callback capability checks remain required. Use VNet integration/private endpoints or authenticated service-to-service calls when stronger isolation is needed.

Provisioning replaces its own managed IP rules and removes obsolete managed SQL rules, while retaining manually added named rules. Audit manual rules because they can broaden access. Rerun `provision` after changing your operator IP, plan/region, or outbound IP allocation; then verify again. ARM updates can restart the applications, so perform provisioning changes outside active test payments.

Keep public Blazor callback routes accessible without Easy Auth login challenges. Linux App Service supports WebSockets; session affinity is configured for Blazor. Do not add a global frame-denial header or override callback CSP. HTTP request tracing/logging is disabled in the template; if adding Application Insights or diagnostics, redact callback/receipt query strings and bearer credentials.

### Code redeployment and rollback

After the first bootstrap, routine code updates normally require only:

```sh
python3 scripts/azure.py --config infra/azure/sandbox.local.json deploy
python3 scripts/azure.py --config infra/azure/sandbox.local.json verify
```

`deploy` runs .NET tests, publishes both projects for `linux-x64`, packages fresh publish directories with files at the ZIP root, and submits API/client ZIP deployments using Azure CLI authentication. Environment files, local database files, and symlinks are rejected from archives. ZIP deployments run from package; app data is in Azure SQL, not the release filesystem.

Uploads are asynchronous; `verify` waits for public Blazor `/health/ready` to confirm Blazor → API → database connectivity and schema access. A successful result does **not** validate GP credentials or payment processing. Check deployment status in App Service if a package fails to start.

Preserve release source/artifacts and roll back by deploying the previous compatible API/client pair. Do not restore an old database as a code rollback. This initial schema is versioned, but future migrations need explicit backward-compatibility/rollback design. App restarts interrupt active Blazor circuits.

### Acceptance and troubleshooting

Open the URL printed by `verify`, create a **new** checkout, and complete real GP sandbox success, decline, and cancellation tests. Confirm all provider callbacks use the **Blazor** origin and the API's stored state controls the displayed result. Reload an existing order link after restarting the API to verify Azure SQL persistence.

- **Provisioning fails:** check selected subscription, region quotas, .NET 10 availability, registered resource providers, and role-assignment permissions. No automatic cleanup is performed.
- **Secret setup fails:** allow RBAC propagation, confirm the signed-in user is the provisioned operator and Key Vault networking permits access. Secret values are deliberately omitted from failure output.
- **Database bootstrap fails:** confirm the current signed-in user is SQL Entra administrator, `operatorIp` matches the current egress IP, and SQL network/TLS requirements are met.
- **API cannot authenticate to SQL:** confirm managed identity is enabled, the contained user SID matches the API identity, grants exist, and the connection string specifies `merchant`.
- **API or SCM returns 403:** review main-site versus SCM allowlists. Your laptop is intentionally not permitted on the API's main endpoint.
- **Readiness fails:** check both deployments, SQL schema/grants, IP allowlists, and Key Vault reference resolution. Use sanitized operational logs; do not dump app settings or token responses.
- **Form remains disabled:** inspect `/_blazor` WebSocket negotiation and reload after deployment.

To stop ongoing charges, intentionally delete the dedicated sandbox resource group through your Azure administration workflow after preserving any required records. There is no automatic destructive cleanup script. Key Vault purge protection prevents immediate permanent purge/name reuse during its retention period.

### Local provider selection and validation

SQLite remains the default and needs no Azure connection. Select SQL Server using `DATABASE_PROVIDER=sqlserver` plus a securely supplied `DATABASE_CONNECTION_STRING`. SQL Server uses the same atomic payment-status transitions, capability keys, and sanitized audit payloads. `DATABASE_PATH` is ignored for SQL Server.

For local access to Azure SQL, use an authorized identity and an appropriate authentication mode such as `Active Directory Default`, with TLS certificate validation enabled. Access still requires an explicit firewall rule and SQL permissions.

Run offline validation:

```sh
dotnet test Merchant.sln
python3 -m unittest discover -s scripts/tests -v
az bicep build --file infra/azure/main.bicep --outfile dist/azure-template.json
node client/Merchant.PaymentClient.Tests/browser-smoke.cjs
```

The SQL Server integration test is explicitly skipped unless `MERCHANT_TEST_SQL_CONNECTION_STRING` is securely set to a **dedicated test database**. It applies the schema twice, checks persistence, case-sensitive IDs, parameterization, sanitized/ordered events, authorization/capture semantics, and concurrent cancellation/decline protection, then deletes only its own random order/events. Use an operator/test identity with DDL/delete permissions, never the restricted API runtime identity or a live payment database.

`tools/Merchant.Database` normally forces Azure CLI authentication and validated TLS. `MERCHANT_DATABASE_LOCAL_TEST=1` permits an explicitly configured SQL connection for isolated local test infrastructure only; the Azure deployment script removes this override before invoking the tool.

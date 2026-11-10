# Setup and deployment runbook

This guide deploys the existing Angular storefront and ASP.NET Core API as **one web application**. It supports a local simulator or the Global Payments **sandbox**, not production payment processing.

All build commands below run from the repository root unless stated otherwise. Examples use a Unix shell; substitute your own hostname and writable storage paths.

## 1. Choose a deployment mode

- **Local development:** Angular serves on port 4200 and proxies API/callback requests to C# on port 5080.
- **Single-origin local demo:** build Angular, then let C# serve the storefront, APIs, and callbacks together.
- **Public sandbox deployment:** publish the built Angular assets inside the C# release artifact. Expose one HTTPS origin through a reverse proxy or managed application host.

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

There is no dedicated health/readiness endpoint. A successful `/api/config` is only a basic application check; it does not prove that SQLite, GP authentication, or HPP link creation works.

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

Automated verification after endpoint extraction passed 25 backend tests and 4 Chrome demo-flow tests. The last attempted real sandbox checkout from localhost returned upstream **502** before reaching HPP. Consequently, actual provider payment/cancellation redirects remain an **outstanding acceptance check**, not a completed deployment guarantee.

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

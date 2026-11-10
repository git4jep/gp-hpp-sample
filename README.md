# Northstar Supply — Global Payments HPP sample

A fictitious merchant storefront built with **Angular 22**, an **ASP.NET Core / C# .NET 10 API**, and **SQLite**.

An independent **Blazor / C# payment test client** is also available in `client/Merchant.PaymentClient`. It uses the same API and supports the complete HPP return/cancel flow without building or running Angular. See [Blazor payment test client](#blazor-payment-test-client).

The important behavior is a full-page round trip: merchant → Global Payments Hosted Payment Page → merchant return or cancellation callback → Angular merchant result page. Card details are entered only on the actual GP-hosted page, never on this application.

## Documentation

- [Local setup](#start-immediately-local-demonstration): run immediately without credentials.
- [Sandbox setup](#actual-global-payments-sandbox): use the actual GP API and public HTTPS callbacks.
- [Configuration reference](#configuration-reference): environment variables and secret handling.
- [Deployment guide](docs/DEPLOYMENT.md): release builds, runtime configuration, HTTPS, SQLite storage, verification, and troubleshooting.
- [Blazor deployment](docs/DEPLOYMENT.md#11-deploy-the-blazor-payment-test-client): separate API/client services, callback forwarding, and WebSocket ingress.

## Requirements

- .NET 10 SDK.
- Node.js 24.15+ (24.x) and npm.
- Python 3 for the small private configuration/launch helpers.
- For actual sandbox payments: the supplied App Key configured privately, an HPP-enabled GP processing account, and a public HTTPS origin.
- Chrome for the included browser tests, or Playwright Chromium.

Check the build tools from the repository root:

```sh
dotnet --version
node --version
npm --version
python3 --version
```

### Project structure

- `frontend/`: Angular application, development proxy, and Playwright browser tests.
- `client/Merchant.PaymentClient/`: standalone .NET 10 Blazor payment test client, result pages, demo checkout, and HPP callback forwarding.
- `client/Merchant.PaymentClient.Tests/`: Blazor/API HTTP integration tests.
- `backend/Merchant.Api/Program.cs`: dependency injection, middleware, and endpoint registration.
- `backend/Merchant.Api/Endpoints/`: configuration, checkout, orders, admin, demo, and HPP callback classes. Edit `PaymentCallbackEndpoints.cs` for return/cancel handling.
- `backend/Merchant.Api/GpClient.cs`: GP authentication, HPP link creation, and transaction verification.
- `backend/Merchant.Api/OrderStore.cs`: SQLite persistence.
- `backend/Merchant.Api/Models.cs`: configuration, request/response types, and payment rules.
- `backend/Merchant.Api.Tests/`: fake-provider and HTTP integration tests.
- `scripts/`: hidden-input credential setup and local API launch helpers.
- `.env.example`: non-secret configuration reference; do not put real keys in this file.

## Blazor payment test client

Only the .NET 10 SDK is required for this client; Node.js and Angular are not needed. Run the API and Blazor host in **separate terminals**, from the repository root.

Start the API in demo mode with callbacks pointing at Blazor:

```sh
GP_MODE=demo PUBLIC_BASE_URL=http://localhost:5090 python3 scripts/run-api.py
```

Start the new client:

```sh
dotnet run --project client/Merchant.PaymentClient
```

Open **http://localhost:5090**. Enter a test email, select a product and quantity, and continue to demo checkout. Choose **Simulate success**, **Simulate decline**, or **Cancel payment**. Each action travels through the actual API callback handlers and returns to a Blazor result page.

The client calls the API server-to-server at `http://127.0.0.1:5080/`. Override this using `MerchantApi__BaseUrl` or the `MerchantApi:BaseUrl` setting in `client/Merchant.PaymentClient/appsettings.json`. Keep this configured as the **API's internal origin**, not the Blazor origin, to avoid a forwarding loop. No GP credentials or administrator keys are needed in the client.

### Blazor with the real GP sandbox

1. Start the client as above and expose **the Blazor host**, not the API, through a public HTTPS tunnel:

   ```sh
   ngrok http 5090
   ```

2. Configure your existing private API `.env` with `GP_MODE=sandbox`, `PUBLIC_BASE_URL` set to that tunnel's HTTPS origin, and your GP credentials/account as described in [sandbox setup](#actual-global-payments-sandbox). Do not put secrets in the Blazor project. If `.env` does not exist, `python3 scripts/setup.py` can create it privately.
3. Restart the API using `python3 scripts/run-api.py`. Ensure no exported `GP_MODE` or `PUBLIC_BASE_URL` overrides the intended `.env` values.
4. Open the tunnel URL, create a **new** checkout, and complete or cancel payment on the GP-hosted page. The API supplies the public callback URLs when creating the HPP link.

The Blazor host forwards only these provider callback routes to the configured API, preserving GET/POST payloads, query strings, status codes, and the API's iframe-breakout HTML/CSP:

- `GET|POST /payments/return/{id}?state=...`
- `GET|POST /payments/cancel/{id}?state=...`
- `POST /api/payments/notifications/{id}?state=...`

The API still validates callback state and reconciles sandbox payments against GP. It then navigates to Blazor's `/payment/success`, `/payment/pending`, `/payment/declined`, or `/payment/cancelled` page. `/payment/return` and `/payment/cancel` are also result-page aliases; they need the API-supplied `order` and `key` query parameters. **Do not use these result pages as the provider callback URLs**: provider callbacks must pass through `/payments/...` first.

Result pages read the stored order from the API; changing the route cannot fabricate success. **Refresh status** reads current state; **Verify with API** requests reconciliation. An authorization is displayed separately from capture. Keep receipt URLs private.

For deployment, publish the API and Blazor project separately, configure the client's internal API origin, and route public HTTPS traffic (including Blazor WebSockets) to the Blazor host. Keep `PUBLIC_BASE_URL` set to that public Blazor origin. No Angular build is required:

```sh
dotnet publish backend/Merchant.Api -c Release -o dist/api
dotnet publish client/Merchant.PaymentClient -c Release -o dist/payment-client
```

Run all API and Blazor HTTP integration tests with `dotnet test Merchant.sln`. Actual GP account permissions, 3DS, and real HPP behavior still require a sandbox run.

Optional real-browser verification reuses the existing frontend Playwright installation:

```sh
dotnet build Merchant.sln
npm ci --prefix frontend
node client/Merchant.PaymentClient.Tests/browser-smoke.cjs
```

The smoke test starts and stops its own demo API/client on ports 15080/15090 with a temporary database. It checks interactive checkout, automatic success/decline/cancel callback navigation, status buttons, and mobile layout. It uses installed Chrome by default; set `PLAYWRIGHT_CHANNEL=chromium` after installing Playwright Chromium if needed.

## Start immediately: local demonstration

No credentials are necessary. With no `.env` file, the API defaults to **demo** mode.

From the project root, install and build Angular:

```sh
dotnet restore Merchant.sln
npm ci --prefix frontend
npm run build --prefix frontend
```

`npm run build` writes the Angular application into `backend/Merchant.Api/wwwroot`. The API serves that directory; no separate Node.js server is needed for the built application.

Run the built Angular app and API together on one origin:

```sh
GP_MODE=demo PUBLIC_BASE_URL=http://localhost:5080 python3 scripts/run-api.py
```

Open **http://localhost:5080**. Add products, enter a fictitious email, and continue to checkout. The clearly labeled merchant-owned simulator lets you try success, decline, and cancellation. It is **not the real Global Payments HPP** and makes no GP API calls.

The application uses the exact same backend return/cancel routes in demo and sandbox modes. Demo records carry `mode=demo`, `DEMO_` transaction IDs, and are inaccessible from sandbox routes.

### Angular development server

For live Angular reload, run these in separate terminals:

```sh
GP_MODE=demo PUBLIC_BASE_URL=http://localhost:4200 python3 scripts/run-api.py
```

```sh
npm start --prefix frontend
```

Open **http://localhost:4200**. Angular proxies `/api/**` and `/payments/**` to C# on port 5080.

### Local browser origins with public sandbox callbacks

`PUBLIC_BASE_URL` determines HPP return, cancel, and status notification URLs. Keep it set to your public HTTPS ngrok origin in sandbox mode, even when opening Angular locally.

Both CORS and the checkout origin check allow `PUBLIC_BASE_URL` plus these explicit local origins by default:

- `http://localhost:4200`
- `http://127.0.0.1:4200`
- `http://localhost:5080`
- `http://127.0.0.1:5080`

You can now start checkout from localhost while GP still sends the shopper and notifications back through the public ngrok origin. Merchant result pages also use that public origin; this does not redirect the shopper back to localhost.

Override the local list with the comma-separated `LOCAL_DEV_ORIGINS` environment variable (or add it to `.env`). Only explicit HTTP(S) loopback origins are accepted; no paths, trailing slashes, or wildcards. Set `LOCAL_DEV_ORIGINS=` to disable additional local origins. Restart the C# API after changing configuration.

CORS permits GET/POST requests and the Content-Type header, without cross-origin credentials. Unlisted origins remain blocked by the checkout guard. This setting does not bypass admin authentication or change the callback state checks.

## Actual Global Payments sandbox

1. Build Angular as above.
2. Start an HTTPS tunnel to the single-origin C# host:

   ```sh
   ngrok http 5080
   ```

3. Configure the backend:

   ```sh
   python3 scripts/setup.py
   ```

   Enter the tunnel's public HTTPS origin and the App Key at the **hidden prompt**. The supplied merchant ID and App ID are already configured. The script writes an ignored `.env` with owner-only permissions and generates a backend admin key. It refuses to overwrite an existing file. The App Key from the conversation is intentionally not copied into tracked files, browser bundles, command history, or this guide.

4. Verify `GP_ACCOUNT_NAME` matches an account in your token scope with **`LNK_POST_Create`** permission. The default `transaction_processing` is a configurable example, not a verified property of your account. Some accounts use `transaction_processing_hpp`. Transaction lookup permissions must also be enabled.
5. Start the backend:

   ```sh
   python3 scripts/run-api.py
   ```

6. Open the **public HTTPS URL**, or use Angular at an allowed localhost development origin. Add a product and continue to payment. The backend requests a token and an actual `HOSTED_PAYMENT_PAGE` link; Angular navigates to the URL GP returns. All HPP callbacks and merchant result pages continue to use the public HTTPS origin.
7. Complete a sandbox payment, or use the hosted page's cancel action. Use GP's current [testing documentation](https://developer.globalpayments.com/docs/getting-started/testing.md) for your account/region's test cards. No real cards.

If GP requires return-origin/referrer registration for your account, register the public origin. When the tunnel changes, update `PUBLIC_BASE_URL`, restart the API, and create a **new** checkout. Existing links retain their original callback URLs.

Only sandbox and demo are supported; there is intentionally no production-payment switch.

## Configuration reference

Set configuration before starting the API, and restart it after changes. `AppSettings.Load()` reads operating-system environment variables directly; it does not read `appsettings.json` or automatically load `.env`.

`scripts/run-api.py` loads root `.env` entries only when the same variable is not already exported. **Existing process environment variables take precedence over `.env`**, including an explicitly empty value. The helper accepts simple `NAME=value` lines and comments; it is not a shell parser, does not expand variables, and does not remove quotes. Do not use `export` prefixes or quote values in that file.

- `GP_MODE`: `demo` by default, or `sandbox`. There is no supported production GP endpoint.
- `PUBLIC_BASE_URL`: merchant origin used for HPP callbacks and merchant result redirects. Defaults to `http://localhost:4200`; sandbox requires public HTTPS. No path, query, fragment, or user credentials.
- `LOCAL_DEV_ORIGINS`: additional comma-separated HTTP(S) loopback origins. Defaults to localhost/127.0.0.1 on ports 4200 and 5080. Set an empty value on a deployed host to disable them.
- `GP_MERCHANT_ID` and `GP_APP_ID`: the supplied sample merchant/application IDs are the defaults. Override if using a different sandbox app.
- `GP_APP_KEY`: required in sandbox. Enter privately with `python3 scripts/setup.py` locally; inject from a secret store on a deployed host.
- `GP_ACCOUNT_NAME`: `transaction_processing` by default. Must identify an HPP-enabled processing account in your GP token scope.
- `GP_CURRENCY`: `USD` by default; supported values are `USD`, `EUR`, and `GBP`.
- `GP_COUNTRY`: `US` by default; use a two-letter uppercase country code appropriate to the merchant account.
- `ADMIN_API_KEY`: private backend administrator bearer key, at least 32 characters. Setup generates it. Without a valid key, all admin routes reject requests.
- `DATABASE_PATH`: defaults to `data/merchant.sqlite`. The Python helper resolves relative paths from the repository root; direct/published execution resolves them from the process working directory. Use an absolute persistent path for deployment.
- `ASPNETCORE_URLS`: the helper defaults this to `http://127.0.0.1:5080`. Set it explicitly for published execution; the helper's port default does not apply there.
- `ASPNETCORE_ENVIRONMENT`: controls ASP.NET hosting behavior, not the GP environment. A deployed service can use `Production` here while retaining `GP_MODE=sandbox`.

Keep `.env` private and out of version control. Setup creates it with owner-only permissions and will not overwrite it. If one already exists, edit it privately rather than copying `.env.example` over it. Never embed `GP_APP_KEY`, `ADMIN_API_KEY`, or GP access tokens in Angular environment files, build arguments, URLs, or committed configuration.

## Return URL and cancel URL

For a configured origin `https://your-merchant.example`, each checkout creates these exact URL patterns:

- **Return URL:** `https://your-merchant.example/payments/return/{orderId}?state={randomCallbackKey}`
- **Cancel URL:** `https://your-merchant.example/payments/cancel/{orderId}?state={randomCallbackKey}`
- **Status notification URL:** `https://your-merchant.example/api/payments/notifications/{orderId}?state={randomCallbackKey}`

These are sent to `POST /ucp/links` under `notifications.return_url`, `notifications.cancel_url`, and `notifications.status_url`, with `type: HOSTED_PAYMENT_PAGE`, single usage, automatic capture, and 3DS challenge preference.

Both browser callbacks accept **GET or POST**. Supported POST representations are JSON, URL-encoded fields, or JSON inside a `response`, `hppResponse`, or `hpp_response` form field. Transaction ID hints may be `id`, `TRN_ID`, or the `TRN_ID`/`transaction_id` query parameter.

The callbacks return deliberately small HTML containing a top-window JavaScript redirect plus a manual continuation link. GP's HPP guide explains that it may render the return response, so returning the entire Angular app directly from a POST callback is not appropriate.

After processing, the browser lands on:

- `/payment/success?order={id}&key={orderAccessKey}` for verified captured/authorized payments.
- `/payment/cancelled?order={id}&key={orderAccessKey}` when the shopper cancels without an already verified approval.
- `/payment/declined?...` for verified declines.
- `/payment/pending?...` while verification is incomplete or GP is temporarily unavailable.

The Angular page **always retrieves the stored order**. Editing `/payment/pending` to `/payment/success` cannot fabricate a successful payment. `PREAUTHORIZED` is shown as **authorized**, not captured/paid.

Cancelling checkout records abandonment. It does **not** reverse a payment, invalidate an HPP link, or guarantee that an in-flight transaction cannot complete. A later verified approval takes precedence, and the merchant page updates accordingly.

## Backend API

Public/customer routes:

- `GET /api/config`: merchant name, catalog, currency, and demo/sandbox mode. No credentials.
- `POST /api/checkout`: persist an order, generate/retrieve the provider token internally, create an HPP link, return `hppUrl`, `order`, `orderKey`, and configured callback URLs.
- `GET /api/orders/{id}?key={orderAccessKey}`: read a capability-protected order.
- `POST /api/orders/{id}/reconcile?key={orderAccessKey}`: retrieve payment information from GP and update SQLite.
- `GET|POST /payments/return/{id}?state=...`: process browser return, then navigate to the merchant result page.
- `GET|POST /payments/cancel/{id}?state=...`: process cancellation, then navigate to the merchant result page.
- `POST /api/payments/notifications/{id}?state=...`: verify a GP status notification and persist its result; return 503 on provider verification failure so delivery can be retried.
- `POST /api/demo/orders/{id}/complete?key=...`: explicitly simulated outcomes; disabled outside demo mode.

Example checkout request:

```json
{
  "items": [
    { "productId": "weekender", "quantity": 1 },
    { "productId": "tumbler", "quantity": 2 }
  ],
  "email": "shopper@example.com"
}
```

Amounts and product descriptions come from the server-side catalog, not from browser-submitted prices. Amounts are integer currency minor units.

### Server-to-server token endpoints

These are **not** called by Angular. They require `Authorization: Bearer <ADMIN_API_KEY>`; configure a key of at least 32 characters.

- `POST /api/admin/access-token`: generate a new GP token.
- `GET /api/admin/access-token`: retrieve a valid cached token, generating one if absent/near expiry.
- `GET /api/admin/orders/{id}/events`: retrieve sanitized callback/verification events.
- `POST /api/admin/orders/{id}/reconcile`: reconcile a known order without its customer capability, for backend recovery.

Token responses contain `accessToken` and `expiresAt`, use `Cache-Control: no-store`, and must be consumed only by trusted backend callers. The API hashes `nonce + app_key` with SHA-512 before requesting the GP token. The App Key is never sent as a plaintext API field.

## Persistence and trust

The SQLite file defaults to `data/merchant.sqlite` when launched through the helper. The `orders` table stores totals, items, GP link and transaction IDs, order status, cancellation time, and an allowlisted verified response. `payment_events` stores an append-only record of sanitized callbacks and API verification attempts. This sample does not store card details, payer payloads, raw callback bodies, App Keys, or GP access tokens in SQLite.

Callbacks are **untrusted hints**, even when their random state is correct. The backend reads the transaction via GP's authenticated transaction API and validates:

- Merchant ID and processing account ID/name.
- Order reference, amount, currency, and SALE transaction type.
- Actual transaction status.

Database updates are atomic and prevent a late pending, declined, or cancellation callback from downgrading an approved/captured payment. Duplicate callbacks can add audit events but do not create another order or charge. If the customer closes their browser, `status_url` still provides a server-to-server path to update the order. Explicit reconciliation is available when delivery is missed; an automatic background reconciliation worker is not included.

Random callback and order-read capabilities are different. Keep callback URLs and receipt URLs private, redact query strings in tunnel/reverse-proxy logs, and do not include third-party analytics on receipt pages. ASP.NET request logging is suppressed for these paths, referrers are disabled, requests are size-limited, token APIs are protected, and endpoints are rate-limited. A callback state is not claimed to be a GP signature; payment authenticity comes from the authenticated GP readback.

## Tests and build

```sh
dotnet test Merchant.sln
npm run build --prefix frontend
npm run test:e2e --prefix frontend
npm audit --prefix frontend
dotnet list backend/Merchant.Api/Merchant.Api.csproj package --vulnerable --include-transitive
```

The browser tests start/stop their own local C# host, force demo mode, use a separate database, and verify success/cancel/decline return pages and mobile cart behavior. Keep port 5080 free. They use installed Chrome by default. Without Chrome:

```sh
cd frontend
npx playwright install chromium
PLAYWRIGHT_CHANNEL=chromium npm run test:e2e
```

For single-origin publishing, build Angular **before** publishing the API so `wwwroot` is included:

```sh
npm run build --prefix frontend
dotnet publish backend/Merchant.Api -c Release -o dist/merchant
```

Inject the documented environment variables when running the published application. `.env` loading is done by `scripts/run-api.py` for local development; it is not automatically performed by the published executable.

See the [deployment guide](docs/DEPLOYMENT.md) for the complete runtime command, persistent storage requirements, HTTPS routing, smoke checks, and failure diagnosis.

## Scope and production work

This is an integration example, not a production commerce system. Before production use, add customer authentication/session-bound orders, checkout idempotency and durable retry handling, background reconciliation, refund/reversal state handling, durable notification queuing, monitoring, deployment-specific proxy/IP rate-limit configuration, database migrations/backups/retention, managed secret storage, and GP account certification. Authorization states can later expire or reverse; this sample's approval-preserving transition policy is intentionally limited to the checkout flow. Do not use it as a settlement/refund ledger.

**Real GP sandbox account access, actual HPP cancel-button behavior, and a bank/3DS payment have not been validated by the local simulator or fake-provider tests.** The public HTTPS origin and your account's HPP permissions must be verified in a real sandbox run.

## References

- [GP API Hosted Payment Page guide](https://developer.globalpayments.com/docs/payments/online/hosted-payment-page-guide.md)
- [GP Links schema, including return/cancel/status URLs](https://developer.globalpayments.com/api/links.md)
- [GP access-token guide](https://developer.globalpayments.com/docs/getting-started/generate-token.md)

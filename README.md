# Northstar Supply — Global Payments HPP sample

A fictitious merchant storefront built with **Angular 22**, an **ASP.NET Core / C# .NET 10 API**, and **SQLite or Azure SQL Database**.

An independent **Blazor / C# payment test client** is also available in `client/Merchant.PaymentClient`. It uses the same API and supports the complete HPP return/cancel flow without building or running Angular. See [Blazor payment test client](#blazor-payment-test-client).

The important behavior is a full-page round trip: merchant → Global Payments Hosted Payment Page → merchant return or cancellation callback → Angular merchant result page. Card details are entered only on the actual GP-hosted page, never on this application.

## Documentation

- [Local setup](#start-immediately-local-demonstration): run immediately without credentials.
- [Sandbox setup](#actual-global-payments-sandbox): use the actual GP API and public HTTPS callbacks.
- [Configuration reference](#configuration-reference): environment variables and secret handling.
- [Payment workflow](#payment-workflow-end-to-end): checkout creation, GP verification, callbacks, order states, and client recovery.
- [Callback security](#callback-security-considerations): trust boundaries, capability URLs, replay handling, safe navigation, and deployment requirements.
- [Cancellation workflow](#cancellation-workflow-api-to-merchant-website): HPP cancel callback, order updates, and navigation back to the merchant cancel page.
- [Deployment guide](docs/DEPLOYMENT.md): release builds, runtime configuration, HTTPS, SQLite storage, verification, and troubleshooting.
- [Blazor deployment](docs/DEPLOYMENT.md#11-deploy-the-blazor-payment-test-client): separate API/client services, callback forwarding, and WebSocket ingress.
- [Azure sandbox deployment](docs/DEPLOYMENT.md#12-azure-sql-and-two-azure-app-service-applications): managed-identity SQL access and provisioning/deployment scripts.

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
- `backend/Merchant.Api/OrderStore.cs`: SQLite/SQL Server persistence.
- `backend/Merchant.Api/Models.cs`: configuration, request/response types, and payment rules.
- `backend/Merchant.Api.Tests/`: fake-provider and HTTP integration tests.
- `scripts/`: hidden-input credential setup and local API launch helpers.
- `infra/azure/`: Bicep infrastructure and non-secret Azure deployment configuration example.
- `tools/Merchant.Database/`: operator-only SQL schema initialization and managed-identity grants.
- `scripts/azure.py`: separate provision, secrets, database, deploy, and verify commands.
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
- `GET|POST /api/payments/cancel/{id}?state=...`
- `GET|POST /payments/cancel/{id}?state=...`
- `POST /api/payments/notifications/{id}?state=...`

The API still validates callback state and reconciles sandbox payments against GP. It then navigates to Blazor's `/payment/success`, `/payment/pending`, `/payment/declined`, or `/payment/cancelled` page. `/payment/return` and `/payment/cancel` are also result-page aliases; they need the API-supplied `order` and `key` query parameters. **Do not use these result pages as the provider callback URLs**: use the API-supplied return/cancel callback URLs instead.

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

The application uses the exact same backend return/cancel routes in demo and sandbox modes. Demo records carry `mode=demo` and are inaccessible from sandbox routes. Simulated success or decline assigns a `DEMO_` transaction ID; pending orders and orders that have only been cancelled can have no transaction ID.

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
- `DATABASE_PROVIDER`: `sqlite` (default) or `sqlserver`. SQLite retains its existing automatic local schema creation.
- `DATABASE_CONNECTION_STRING`: required for `sqlserver`, with explicit Server and Database. Azure uses `Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False`. The SQL schema must be initialized separately; the API never runs SQL DDL or silently falls back to SQLite.
- `ASPNETCORE_URLS`: the helper defaults this to `http://127.0.0.1:5080`. Set it explicitly for published execution; the helper's port default does not apply there.
- `ASPNETCORE_ENVIRONMENT`: controls ASP.NET hosting behavior, not the GP environment. A deployed service can use `Production` here while retaining `GP_MODE=sandbox`.

Keep `.env` private and out of version control. Setup creates it with owner-only permissions and will not overwrite it. If one already exists, edit it privately rather than copying `.env.example` over it. Never embed `GP_APP_KEY`, `ADMIN_API_KEY`, or GP access tokens in Angular environment files, build arguments, URLs, or committed configuration.

## Payment workflow: end to end

The sandbox flow is **merchant checkout → API-created order and HPP link → GP-hosted payment → API verification → stored order → merchant result page**. Browser return and server-to-server notification are independent paths; neither callback's claimed status is proof of payment.

Workflow at a glance:

1. The client loads the catalog and submits product IDs, quantities, and email to `POST /api/checkout`.
2. The API calculates server-side totals, persists a `PENDING` order, and creates a GP HPP link using backend-only credentials.
3. The browser navigates to the validated hosted link; card entry and any 3DS challenge happen on the provider's flow.
4. Return, cancellation, and notification callbacks validate order state and trigger authenticated transaction verification.
5. The API persists the verified outcome using approval-preserving updates. Cancellation records abandonment, not a refund or void.
6. Browser callbacks navigate to the merchant receipt. The stored order, not the result-page URL, determines the displayed outcome; reconciliation can recover incomplete verification.

Demo mode exercises the local callback round trip without GP API calls. See [callback security](#callback-security-considerations) for the safeguards and remaining limitations behind this workflow.

### 1. Build checkout and persist the order

Angular's `StorefrontComponent` loads `GET /api/config`, builds a cart, and submits product IDs, quantities, and email to `POST /api/checkout` through `MerchantApi.checkout`. Blazor's `Payment.razor` submits the same request server-to-server through `MerchantApiClient.Checkout`.

`CheckoutEndpoints.CreateCheckout` checks any supplied browser Origin against the configured allowlist and validates email. `OrderStore.Create` accepts 1–10 cart entries, each with a known catalog product and quantity of 1–10. It calculates the total from **server-side prices in integer minor units**, then persists a `PENDING` order before contacting GP.

Each order has an `ord_...` reference, its demo/sandbox mode, item snapshots, currency, and two independently generated capabilities:

- `AccessKey`: supplied as `key` to read or reconcile the order and view its receipt.
- `CallbackKey`: supplied as `state` to provider return, cancel, and notification endpoints.

These keys are excluded from the serialized order object. Checkout returns the access key separately as `orderKey`; callback URLs contain the callback state.

### 2. Create the hosted link and leave the merchant

In sandbox mode, `GpClient.CreateLink` obtains a backend-only access token with `POST /ucp/accesstoken`. Authentication uses the App ID, a random nonce, and SHA-512 of `nonce + App Key`. The token's merchant scope and selected account's `LNK_POST_Create` permission are checked. Tokens are cached in memory and reused while more than one minute of validity remains.

The API then sends `POST /ucp/links` with the order reference, total, currency, payer email, and the three [callback URLs](#return-url-and-cancel-url) derived from `PUBLIC_BASE_URL`. The link is single-use `HOSTED_PAYMENT_PAGE`, card-only/CNP, with automatic capture and `CHALLENGE_PREFERRED` authentication. The API accepts only an `LNK_` link ID and an HTTPS URL on `globalpay.com` or a subdomain.

`OrderStore.Link` persists the link ID and URL. Checkout responds **201** with `order`, `orderKey`, `hppUrl`, and `callbacks`. Angular uses `window.location.assign(hppUrl)`; Blazor uses `Navigation.NavigateTo(hppUrl, forceLoad: true)`. Both perform a full-page navigation to the backend-returned URL. Card entry and any bank/3DS challenge happen on the hosted flow, not the merchant form.

If token acquisition or link creation/validation fails, the persisted order becomes `LINK_FAILED` and checkout returns an error instead of a usable link. There is no checkout idempotency key or automatic link-creation retry; submitting again creates another order.

### 3. Receive browser callbacks or asynchronous notifications

`PaymentCallbackEndpoints.HandleCallback` is the shared pipeline for browser return, browser cancellation, and status notification:

1. Load the order and verify its current mode and query-string `state`; a missing order, wrong mode, or wrong state returns **404**.
2. Parse the supported JSON/form payload and optional transaction ID hint. A supplied hint must match `TRN_` followed by 1–180 letters, digits, underscores, or hyphens; invalid hints return **400**.
3. Append an allowlisted callback event. Cancellation also records abandonment before reconciliation; see the [cancellation workflow](#cancellation-workflow-api-to-merchant-website).
4. In sandbox mode, call `GpClient.Reconcile` against the authenticated GP API. Demo callbacks do not contact GP.
5. For notifications, return JSON with **200** on successful processing, or **503** when reconciliation raises an `ApiException`, allowing provider delivery to be retried. A successful read with no matching transaction leaves the stored order unchanged and still acknowledges the notification.
6. For browser callbacks, read the stored order and return **200 HTML** that navigates the top window to the merchant result page, with a manual continuation link. An `ApiException` during verification records `verification-pending` but does not prevent browser navigation.

Browser destinations are `/payment/success` for `PAID`/`AUTHORIZED`; otherwise a cancel callback selects `/payment/cancelled`, while a return selects `/payment/declined` for `DECLINED` or `/payment/pending` for other states. Every destination includes `order` and the separate receipt `key`.

For Angular, the API serves the built application or the development proxy forwards callbacks. For Blazor, `PaymentCallbackProxy` forwards only the provider callback routes to the internal API and relays its status, body, and security headers. `PUBLIC_BASE_URL` must therefore identify the public merchant host that can handle both callbacks and result pages.

### 4. Verify with GP and apply the payment state

With a transaction hint, `GpClient.Reconcile` reads `GET /ucp/transactions/{transactionId}`. Without one, it searches `GET /ucp/transactions` by order reference and account name, requesting up to 100 transactions sorted newest first. It filters responses by transaction ID format, merchant ID, token-scoped account ID, optional account name, exact order reference, amount, currency, and `SALE` type.

A hinted transaction that does not match raises **409**. Matching results are ranked by mapped outcome, and the highest-ranked result is recorded as a verified `gp-api` event and applied to the order:

- `CAPTURED` or `FUNDED` → `PAID`.
- `PREAUTHORIZED` → `AUTHORIZED` (approval, **not confirmed capture**).
- `DECLINED` or `REJECTED` → `DECLINED`.
- Any other GP status → `PENDING`.

`OrderStore.Apply` uses an atomic, rank-guarded database update: `PAID` > `AUTHORIZED` > `DECLINED` > other states. A lower-ranked verification cannot overwrite a higher-ranked stored outcome. Cancellation preserves existing approvals, pending verification preserves recorded cancellation, and a later verified authorization/capture can supersede cancellation. Duplicate callbacks may append events but do not initiate another charge. SQLite and SQL Server use the same transition rules.

### 5. Display the receipt and recover incomplete verification

Result-page paths are presentation hints only. The clients fetch `GET /api/orders/{id}?key=...`; the stored order determines the displayed payment status.

- **Angular:** `ResultComponent` reads the order, immediately calls `POST /api/orders/{id}/reconcile?key=...`, and replaces the result route to match the returned state. While the result stays `PENDING`, it schedules up to five additional checks at five-second intervals. A **Check payment status** button is available for non-approved orders and authorizations. Verification errors are displayed rather than treated as success.
- **Blazor:** `PaymentResult.razor` initially reads the stored order. **Refresh status** reads it again; **Verify with API** explicitly requests reconciliation. It has no automatic polling.
- **Without a browser return:** a valid status notification can still update the database. Customer reconciliation or the protected admin reconciliation endpoint can recover a missed update. No automatic background reconciliation worker is included.

### Demo branch and implementation map

In `GP_MODE=demo`, checkout persists the same order but returns a merchant-owned `/demo/checkout/{id}?key=...` URL and `DEMO_` link ID. The simulator posts `success`, `declined`, or `cancel` to `/api/demo/orders/{id}/complete?key=...`. Success/decline directly apply a simulated status and transaction ID; cancellation is recorded by the cancel callback. The simulator returns a callback `redirectUrl`, which the browser follows through the same HTML return flow. Demo reconciliation only reads the stored order; no GP token, link, or transaction API is called.

Trace entry points:

- [Angular storefront, API wrapper, simulator, and result handling](frontend/src/main.ts): `StorefrontComponent.checkout`, `MerchantApi`, `DemoComponent.complete`, `ResultComponent.refresh`.
- [Checkout endpoint](backend/Merchant.Api/Endpoints/CheckoutEndpoints.cs): `CreateCheckout`.
- [GP authentication, link creation, and verification](backend/Merchant.Api/GpClient.cs): `GetToken`, `CreateLink`, `Reconcile`, `Matches`.
- [Callback processing](backend/Merchant.Api/Endpoints/PaymentCallbackEndpoints.cs): `HandleCallback`; [capability checks](backend/Merchant.Api/Endpoints/EndpointGuards.cs): `FindOrder`.
- [Persistence](backend/Merchant.Api/OrderStore.cs): `Create`, `Link`, `LinkFailed`, `Cancel`, `Apply`, `Event`; [payment rules](backend/Merchant.Api/Models.cs): `PaymentRules`.
- [Order read/reconciliation](backend/Merchant.Api/Endpoints/OrderEndpoints.cs) and [demo completion](backend/Merchant.Api/Endpoints/DemoEndpoints.cs).
- [Blazor checkout](client/Merchant.PaymentClient/Components/Pages/Payment.razor), [API client](client/Merchant.PaymentClient/MerchantApiClient.cs), [callback proxy](client/Merchant.PaymentClient/PaymentCallbackProxy.cs), and [receipt](client/Merchant.PaymentClient/Components/PaymentResult.razor).

Existing coverage includes token/link contracts, mismatched transaction rejection, approval-preserving updates, notification-only completion, and demo HTTP round trips in [PaymentTests.cs](backend/Merchant.Api.Tests/PaymentTests.cs); browser success/cancel/decline journeys are in [checkout.spec.ts](frontend/e2e/checkout.spec.ts). These tests use demo/fake-provider behavior, not a live GP sandbox payment.

## Return URL and cancel URL

For a configured origin `https://your-merchant.example`, each checkout creates these exact URL patterns:

- **Return URL:** `https://your-merchant.example/payments/return/{orderId}?state={randomCallbackKey}`
- **Gateway cancel URL:** `https://your-merchant.example/api/payments/cancel/{orderId}?state={randomCallbackKey}`
- **Status notification URL:** `https://your-merchant.example/api/payments/notifications/{orderId}?state={randomCallbackKey}`

These are sent to `POST /ucp/links` under `notifications.return_url`, `notifications.cancel_url`, and `notifications.status_url`, with `type: HOSTED_PAYMENT_PAGE`, single usage, automatic capture, and 3DS challenge preference.

The gateway cancel URL is an API callback, not a client result page. Both clients route it to the API, which validates state, records cancellation, and redirects to `/payment/cancelled` (or success if payment was already verified). Angular's `/api/**` proxy and Blazor's explicit callback forwarding support this route. The original `/payments/cancel/{orderId}` callback remains available for existing HPP links.

Both browser callbacks accept **GET or POST**. Supported POST representations are JSON, URL-encoded fields, or JSON inside a `response`, `hppResponse`, or `hpp_response` form field. Transaction ID hints may be `id`, `TRN_ID`, or the `TRN_ID`/`transaction_id` query parameter.

The callbacks return deliberately small HTML containing a top-window JavaScript redirect plus a manual continuation link. GP's HPP guide explains that it may render the return response, so returning the entire Angular app directly from a POST callback is not appropriate.

After processing, the browser lands on:

- `/payment/success?order={id}&key={orderAccessKey}` for verified captured/authorized payments.
- `/payment/cancelled?order={id}&key={orderAccessKey}` when the shopper cancels without an already verified approval.
- `/payment/declined?...` for verified declines.
- `/payment/pending?...` while verification is incomplete or GP is temporarily unavailable.

The Angular page **always retrieves the stored order**. Editing `/payment/pending` to `/payment/success` cannot fabricate a successful payment. `PREAUTHORIZED` is shown as **authorized**, not captured/paid.

Cancelling checkout records abandonment. It does **not** reverse a payment, invalidate an HPP link, or guarantee that an in-flight transaction cannot complete. A later verified approval takes precedence, and the merchant page updates accordingly.

### Cancellation workflow: API to merchant website

There are two different URLs in this flow:

- **Provider callback:** `/api/payments/cancel/{orderId}?state={randomCallbackKey}` is an API endpoint, not a UI page. `/payments/cancel/{orderId}` remains a compatibility alias with the same state requirement.
- **Merchant cancel page:** `/payment/cancelled?order={orderId}&key={orderAccessKey}` is the customer-facing Angular or Blazor page.

The normal round trip is: **merchant checkout → GP HPP → API cancel callback → merchant cancel page**.

1. **Register the callback when creating checkout.** `POST /api/checkout` creates the order with separate random callback and order-access keys. `PaymentRules.Urls` builds the callback from `PUBLIC_BASE_URL`; `GpClient.CreateLink` sends it to GP as `notifications.cancel_url` in `POST /ucp/links`. The checkout response also includes it as `callbacks.cancel_url`.
2. **Receive the shopper's cancellation.** When GP invokes the registered cancel URL, `PaymentCallbackEndpoints.HandleCancel` accepts GET or POST and calls the shared callback pipeline with `kind = "cancel"`. No transaction ID is required. The supported payload formats and optional transaction ID hints are described above.
3. **Validate and record cancellation.** The API checks that the order exists, matches the current demo/sandbox mode, and has the correct query-string `state`. A missing or incorrect state returns **404** without recording cancellation or generating redirect HTML. After payload validation, it records a sanitized `cancel` event and sets `CancelledAt`. The order becomes `CANCELLED` unless it is already `PAID` or `AUTHORIZED`.
4. **Check for a payment that completed anyway.** In sandbox mode the API reconciles against GP, using the transaction ID hint if supplied, otherwise searching by order reference. Callback claims alone cannot establish approval. If GP verification raises an `ApiException`, the API records `verification-pending` and still continues the browser flow. Demo mode skips GP reconciliation.
5. **Choose the merchant destination from the stored order.** If the order is now `PAID` or `AUTHORIZED`, the cancel callback targets `/payment/success`. Otherwise it targets `/payment/cancelled`, even if reconciliation found a decline or could not complete. Both URLs include `order` and the separate order-access `key`; the callback `state` is not reused as the receipt key.
6. **Navigate out of HPP.** The callback returns **HTTP 200 `text/html`**, not an HTTP 302/303 or a JSON redirect response. Its nonce-protected script calls `window.top.location.replace(destination)` to leave the HPP frame, falling back to `window.location.replace(destination)` if top-window navigation throws. A **Continue to your order** link with `target="_top"` provides manual navigation when automatic navigation is blocked. The response's CSP allows framing by GP's `*.globalpay.com` and `*.realexpayments.com` hosts.
7. **Load the actual result on the merchant website.** The result page reads the stored order using its access key. Angular also requests reconciliation on initial load and can replace the route with success, declined, or pending if the latest stored result differs. Blazor reads the order initially and offers **Refresh status** and **Verify with API**. Visiting a cancel-page URL does not itself cancel an order or override its payment status.

**Redirect origin and hosting.** The API constructs the destination as `{PUBLIC_BASE_URL}/payment/{page}`; it does not use the incoming referrer or accept a per-request merchant redirect URL. There is no separate configurable cancel-page path. Set `PUBLIC_BASE_URL` to the public merchant website origin, with no path, and make sure that origin serves both the callback routes and result pages. For Blazor, the public host forwards `/payments/cancel/{id}` to the internal API configured by `MerchantApi__BaseUrl`, then relays the API's HTML and CSP unchanged. If that API is unreachable, the proxy returns **502** instead of a merchant redirect.

Restart the API after changing `PUBLIC_BASE_URL` and create a new checkout: existing GP links retain the callback URLs supplied when they were created. If checkout began on localhost while callbacks use a public tunnel, the shopper returns to the public origin, not localhost.

**Demo verification.** Choose **Cancel payment** on the demo checkout. The simulator calls `POST /api/demo/orders/{id}/complete?key={orderAccessKey}` with `{"result":"cancel"}` and receives a JSON `redirectUrl` pointing at the API cancel callback. The browser follows that URL, exercising the same cancellation recording and HTML navigation as the sandbox callback. For a fresh unpaid order, confirm the browser reaches `/payment/cancelled` and the stored order reports `CANCELLED`. This verifies the local application flow, not GP's real HPP cancel action.

Implementation references: `backend/Merchant.Api/Endpoints/PaymentCallbackEndpoints.cs`, `backend/Merchant.Api/Endpoints/EndpointGuards.cs`, `backend/Merchant.Api/OrderStore.cs`, and `client/Merchant.PaymentClient/PaymentCallbackProxy.cs`. Automated coverage includes `HttpDemoReturnsToDifferentMerchantPagesAndChecksCapabilities`, `LateCancellationOrDeclineCannotOverwriteVerifiedPayment`, and `ApprovalAfterCancellationIsStillRecordedAndDuplicatesAreIdempotent` in `backend/Merchant.Api.Tests/PaymentTests.cs`.

### Cancellation database behavior

`OrderStore.Cancel(id)` runs before gateway reconciliation and browser navigation. It executes a single parameterized SQL `UPDATE` against the matching order in SQLite or `dbo.orders` in SQL Server/Azure SQL:

- **Status:** `PAID` and `AUTHORIZED` remain unchanged. Every other existing status becomes `CANCELLED`, including `PENDING`, `DECLINED`, and `LINK_FAILED`.
- **Timestamps:** `cancelled_at` and `updated_at` are set to the same UTC timestamp, including when an approved status is preserved. A non-null `cancelled_at` records a cancellation attempt; it does not prove that no payment completed.
- **Repeated callbacks:** cancellation does not create another order or charge, but each call overwrites both timestamps with the latest cancellation time. The callback pipeline also records another sanitized audit event.
- **Persistence:** `ExecuteNonQuery()` applies the update immediately; no separate save call is required. Status and timestamps change atomically in this statement. The callback event is stored separately, not in the same transaction.
- **Unknown IDs:** calling `Cancel` directly with an unknown ID silently updates zero rows because the method does not check the affected-row count. The HTTP callback first validates order existence, mode, and callback state and returns 404 if validation fails.

Cancellation records checkout abandonment, not a refund, void, or gateway reversal. `OrderStore.Apply` preserves cancellation when a later verified result is still `PENDING`, but allows a verified `AUTHORIZED` or `PAID` result to supersede it. A verified decline may also replace `CANCELLED`. The merchant result page reads the stored status rather than treating the cancel-page URL as authoritative.

`OrderStoreTests.SqliteStoreRetainsThePersistenceContract` covers persisted cancellation, pending reconciliation, later approval, and concurrent cancellation/decline updates after payment. The same persistence contract runs for SQL Server/Azure SQL only when `MERCHANT_TEST_SQL_CONNECTION_STRING` points to a dedicated test database; otherwise that integration test is skipped.

## Backend API

Public/customer routes:

- `GET /api/config`: merchant name, catalog, currency, and demo/sandbox mode. No credentials.
- `GET /health/ready`: database connectivity/schema check; Blazor also exposes an API-backed readiness check.
- `POST /api/checkout`: persist an order and return `hppUrl`, `order`, `orderKey`, and configured callback URLs. Sandbox mode generates/retrieves the provider token internally and creates a GP HPP link; demo mode creates a merchant-owned simulator link without contacting GP.
- `GET /api/orders/{id}?key={orderAccessKey}`: read a capability-protected order.
- `POST /api/orders/{id}/reconcile?key={orderAccessKey}`: in sandbox mode, retrieve payment information from GP and update the configured database. In demo mode, return the stored order without contacting GP.
- `GET|POST /payments/return/{id}?state=...`: process browser return, then navigate to the merchant result page.
- `GET|POST /api/payments/cancel/{id}?state=...`: process gateway cancellation, then navigate to the client's cancel page (or success for an already verified approval).
- `GET|POST /payments/cancel/{id}?state=...`: compatibility alias for existing gateway cancel links.
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

Azure SQL deployments use the same order/event model and approval-preserving updates as SQLite. Follow the [Azure deployment runbook](docs/DEPLOYMENT.md#12-azure-sql-and-two-azure-app-service-applications) to initialize the versioned schema and grant the API identity table-level permissions. Existing local SQLite orders are not automatically imported.

The SQLite file defaults to `data/merchant.sqlite` when launched through the helper. The `orders` table stores totals, items, GP link and transaction IDs, order status, cancellation time, and an allowlisted verified response. `payment_events` stores an append-only record of sanitized callbacks and API verification attempts. This sample does not store card details, payer payloads, raw callback bodies, App Keys, or GP access tokens in SQLite.

Callbacks are **untrusted hints**, even when their random state is correct. The backend reads the transaction via GP's authenticated transaction API and validates:

- Merchant ID and processing account ID/name.
- Order reference, amount, currency, and SALE transaction type.
- Actual transaction status.

Database updates are atomic and prevent a late pending, declined, or cancellation callback from downgrading an approved/captured payment. Duplicate callbacks can add audit events but do not create another order or charge. If the customer closes their browser, `status_url` still provides a server-to-server path to update the order. Explicit reconciliation is available when delivery is missed; an automatic background reconciliation worker is not included.

Random callback and order-read capabilities are different. Keep callback URLs and receipt URLs private, redact query strings in tunnel/reverse-proxy logs, and do not include third-party analytics on receipt pages. ASP.NET request logging is suppressed for these paths, referrers are disabled, requests are size-limited, token APIs are protected, and endpoints are rate-limited. A callback state is not claimed to be a GP signature; payment authenticity comes from the authenticated GP readback.

## Callback security considerations

These controls describe the current sample, not a production certification. The handlers are Minimal API endpoints in `backend/Merchant.Api/Endpoints/PaymentCallbackEndpoints.cs`, with capability checks in `Endpoints/EndpointGuards.cs`, provider verification in `GpClient.cs`, and database transitions in `OrderStore.cs`.

### Callback trust and transaction verification

Treat every callback body and transaction ID as untrusted input. A correct callback `state` permits processing but is **not a GP signature or proof of sender identity**. CORS and browser Origin checks are not provider authentication.

In sandbox mode, payment evidence comes from the authenticated GP transaction API. Before applying an outcome, `GpClient.Matches` checks transaction ID format, configured merchant ID, token-scoped account ID, account name when present, exact order reference, amount, currency, and `SALE` type. Callback status fields and result-page route names cannot establish approval. `AUTHORIZED` is not confirmed capture.

### Capability URLs and confidentiality

Each order has independently generated 24-byte random keys: `CallbackKey` is supplied as `state`; `AccessKey` is supplied as `key` for receipt reads and customer reconciliation. `FindOrder` validates the current mode and compares keys using `CryptographicOperations.FixedTimeEquals`; unknown orders, wrong modes, and invalid keys return 404.

These are bearer capabilities, not customer/session authentication. They have no implemented expiry or rotation policy. Although the keys are separate, a valid browser callback returns a continuation URL containing the receipt key: possession of callback state can therefore expose receipt access as well as permit cancellation processing. Do not treat the two URLs as isolated confidentiality boundaries.

Use public HTTPS for sandbox callbacks. Keep callback and receipt URLs out of tickets, screenshots, analytics, and shared logs. Responses use `Cache-Control: no-store` and `Referrer-Policy: no-referrer`; ASP.NET logging is filtered to Warning. These controls do not remove URLs from browser history or protect reverse-proxy, tunnel, telemetry, or custom logs. Redact query strings in those systems and avoid third-party analytics on receipt pages.

### Input and browser-response controls

The callback parser accepts JSON objects, form fields, and JSON wrapped in `response`, `hppResponse`, or `hpp_response`. Supplied transaction hints must match `TRN_` followed by 1–180 letters, digits, underscores, or hyphens. Kestrel limits request bodies to 32 KB. These checks constrain input but do not authenticate it.

Browser destinations are built from validated `PUBLIC_BASE_URL`, an internally selected result page, and the stored order; the handler does not accept arbitrary redirect destinations. It HTML-encodes the continuation link and JSON-serializes the script destination. The HTTP 200 HTML response uses a random CSP script nonce, `default-src 'none'`, and framing restricted to the specified GP domains. Top-window navigation has a current-window fallback and a manual link; actual browser/HPP navigation still needs sandbox verification.

### Replay, ordering, and cancellation

Duplicate callbacks do not initiate another charge. Atomic, rank-guarded updates prevent a lower-ranked verified outcome from overwriting `AUTHORIZED` or `PAID`; cancellation also preserves approvals. A later verified approval can supersede cancellation.

This is not full replay protection: callbacks can be reused, append audit events, trigger GP requests, and overwrite cancellation timestamps. Callback event insertion and order updates are separate database operations, not one transaction. Cancellation does not invalidate the HPP link or reverse an in-flight payment. The sample's rank policy does not model refunds, reversals, or authorization expiry.

### Verification failures and operational recovery

A reconciliation `ApiException` records `verification-pending`. Notifications return 503 so delivery can be retried; browser callbacks still return to the merchant using stored state. A successful GP search with no matching transaction leaves the order unchanged and acknowledges the notification with 200. Other exceptions use the API's general error handling; not every failure receives this retry response.

No durable notification queue or background reconciliation worker is included. Use customer/admin reconciliation to recover missed updates. In production, add durable delivery processing, background recovery, and alerts for verification failures, unmatched transactions, and sustained pending orders.

### Rate limiting, persistence, and production requirements

Callbacks share the general fixed-window limit of 180 requests per minute per `RemoteIpAddress`, with 429 responses and no queue. Configure trusted proxy/client-IP handling and callback-aware limits before deployment so a shared ingress address or provider retry burst does not block legitimate notifications.

Only allowlisted callback/verification fields are persisted; raw payloads and card data are not stored by this flow. Sanitize telemetry as well, protect database access/backups, and define retention. Keep GP credentials backend-only and protect the separate administrator token endpoints.

Before production use, add customer-bound order authorization, capability lifecycle management, checkout idempotency, replay/abuse monitoring, durable retries, and refund/reversal handling. Validate any additional provider-authentication mechanism against the actual GP account contract rather than assuming one exists in this sample.

**Verification scope:** demo and fake-provider tests exercise local behavior, not live GP sender identity, callback delivery/retries, HPP framing, cancellation, or bank/3DS behavior. Complete a real sandbox run before relying on those integrations.

## Tests and build

```sh
dotnet test Merchant.sln
python3 -m unittest discover -s scripts/tests -v
npm run build --prefix frontend
DATABASE_PROVIDER=sqlite DATABASE_CONNECTION_STRING= npm run test:e2e --prefix frontend
npm audit --prefix frontend
dotnet list backend/Merchant.Api/Merchant.Api.csproj package --vulnerable --include-transitive
```

The SQL Server store integration test is opt-in: set `MERCHANT_TEST_SQL_CONNECTION_STRING` securely to a **dedicated test database** before `dotnet test Merchant.sln`. Without it, the SQL test is explicitly skipped while SQLite and client tests still run. The test initializes schema version 1, inserts random test orders, and removes only its own records. Never point it at a live payment database.

The Angular browser tests start/stop their own local C# host, force demo mode, and set `DATABASE_PATH=data/e2e.sqlite`. They verify success/cancel/decline return pages and mobile cart behavior. The Playwright configuration does **not** override `DATABASE_PROVIDER` or `DATABASE_CONNECTION_STRING`; use the explicit SQLite overrides in the command above so inherited environment variables or root `.env` settings cannot select an existing SQL Server database instead. Keep port 5080 free. They use installed Chrome by default. Without Chrome:

```sh
cd frontend
npx playwright install chromium
DATABASE_PROVIDER=sqlite DATABASE_CONNECTION_STRING= PLAYWRIGHT_CHANNEL=chromium npm run test:e2e
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

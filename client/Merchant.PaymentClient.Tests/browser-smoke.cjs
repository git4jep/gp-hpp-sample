// Optional UI verification using the repository's existing Playwright installation.
// Run after dotnet build Merchant.sln and npm ci --prefix frontend.
const { chromium } = require('../../frontend/node_modules/@playwright/test');
const { spawn } = require('node:child_process');
const { mkdtemp, rm } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const path = require('node:path');
const assert = require('node:assert/strict');

const root = path.resolve(__dirname, '../..');
const apiOrigin = 'http://127.0.0.1:15080';
const clientOrigin = 'http://127.0.0.1:15090';
const servers = [];
let browser;

async function waitFor(url) {
  for (let attempt = 0; attempt < 120; attempt++) {
    if (servers.some(server => server.exitCode !== null))
      throw new Error('A smoke-test server exited before becoming ready.');
    try {
      if ((await fetch(url)).ok) return;
    } catch {}
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error('A smoke-test server did not become ready.');
}

function start(project, origin, environment) {
  const server = spawn('dotnet', [
    'run', '--project', project, '--no-build', '--no-launch-profile', '--urls', origin
  ], {
    cwd: root,
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ...environment },
    stdio: ['ignore', 'ignore', 'ignore']
  });
  servers.push(server);
  return server;
}

(async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'blazor-browser-'));
  try {
    // Do not load the merchant's private .env or use an existing payment database.
    start('backend/Merchant.Api', apiOrigin, {
      GP_MODE: 'demo', PUBLIC_BASE_URL: clientOrigin,
      DATABASE_PROVIDER: 'sqlite', DATABASE_CONNECTION_STRING: '',
      DATABASE_PATH: path.join(directory, 'orders.sqlite')
    });
    await waitFor(`${apiOrigin}/api/config`);
    start('client/Merchant.PaymentClient', clientOrigin, { MerchantApi__BaseUrl: apiOrigin });
    await waitFor(clientOrigin);
    browser = await chromium.launch({
      channel: process.env.PLAYWRIGHT_CHANNEL || 'chrome'
    });
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', () => errors.push('Browser JavaScript error'));
    for (const [button, route, heading] of [
      ['Simulate success', 'success', 'Payment completed.'],
      ['Simulate decline', 'declined', 'Payment declined.'],
      ['Cancel payment', 'cancelled', 'Payment cancelled.']
    ]) {
      await page.goto(clientOrigin);
      await page.getByLabel('Email address').fill('browser-test@example.com');
      await page.getByLabel('Product', { exact: true }).selectOption('tumbler');
      await page.getByLabel('Quantity').fill('2');
      await page.getByRole('button', { name: 'Continue to demo checkout' }).click();
      await page.waitForURL('**/demo/checkout/**');
      await page.getByRole('heading', { name: 'USD 56.00' }).waitFor();
      await page.getByRole('button', { name: button, exact: true }).click();
      // This requires the forwarded callback's JavaScript redirect to actually run.
      await page.waitForURL(`**/payment/${route}?**`);
      await page.getByRole('heading', { name: heading, exact: true }).waitFor();
      assert.ok(await page.getByText('USD 56.00', { exact: true }).count());
      await page.getByRole('button', { name: 'Refresh status', exact: true }).click();
      await page.getByRole('button', { name: 'Verify with API', exact: true }).click();
      await page.getByRole('heading', { name: heading, exact: true }).waitFor();
      console.log(`PASS: ${button} -> Blazor ${route} page`);
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(clientOrigin);
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    assert.deepEqual(errors, []);
    console.log('PASS: mobile layout and no browser JavaScript errors');
  } finally {
    if (browser) await browser.close();
    await Promise.all(servers.map(server => new Promise(resolve => {
      if (server.exitCode !== null) return resolve();
      server.once('exit', resolve);
      server.kill('SIGTERM');
    })));
    await rm(directory, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});

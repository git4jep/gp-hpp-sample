import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./e2e",
  workers: 1,
  use: {
    baseURL: "http://127.0.0.1:5080",
    browserName: "chromium",
    // Use the locally installed Chrome; override with PLAYWRIGHT_CHANNEL=chromium
    // after `npx playwright install chromium` on machines without Chrome.
    channel: process.env["PLAYWRIGHT_CHANNEL"] || "chrome",
  },
  webServer: {
    command: "python3 ../scripts/run-api.py",
    url: "http://127.0.0.1:5080/api/config",
    env: {
      GP_MODE: "demo",
      PUBLIC_BASE_URL: "http://127.0.0.1:5080",
      DATABASE_PATH: "data/e2e.sqlite",
      ASPNETCORE_URLS: "http://127.0.0.1:5080",
    },
    reuseExistingServer: false,
    timeout: 60000,
  },
});

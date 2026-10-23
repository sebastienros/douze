import { defineConfig, devices } from "@playwright/test";

// Tests the built site (docs/_site): build it with Douze first.
// SITE_DIR points them at another output folder.

const port = 4173;

export default defineConfig({
  testDir: "tests/e2e",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [["list"], ["github"]] : "list",
  use: {
    baseURL: `http://localhost:${port}`,
    trace: "retain-on-failure"
  },
  projects: [
    { name: "desktop", use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } } },
    { name: "mobile", use: { ...devices["Pixel 7"] } }
  ],
  webServer: {
    command: `node scripts/serve.mjs ${process.env.SITE_DIR ?? "_site"} ${port}`,
    url: `http://localhost:${port}/douze/`,
    reuseExistingServer: !process.env.CI
  }
});

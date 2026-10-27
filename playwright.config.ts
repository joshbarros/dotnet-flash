import { defineConfig, devices } from '@playwright/test';

// The stack under test is the composed one: docker compose up --build --wait
export default defineConfig({
  testDir: './e2e/specs',
  timeout: 60_000,
  fullyParallel: false,
  use: {
    baseURL: process.env.WEB_URL ?? 'http://127.0.0.1:8104',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});

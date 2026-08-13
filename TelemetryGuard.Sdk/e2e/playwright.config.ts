import { defineConfig } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// package root (TelemetryGuard.Sdk/) — webServer cwd defaults to this config's
// directory, which would break the pinned `node e2e/serve.mjs` command.
const sdkRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

export default defineConfig({
  testDir: './tests',
  // The mock server's capture state is shared and reset between tests — one
  // worker keeps runs deterministic (chromium-only for now, per SDK-06 scope).
  workers: 1,
  timeout: 60_000,
  webServer: {
    command: 'node e2e/serve.mjs',
    cwd: sdkRoot,
    port: 4599,
    reuseExistingServer: true,
  },
  use: { baseURL: 'http://localhost:4599' },
});

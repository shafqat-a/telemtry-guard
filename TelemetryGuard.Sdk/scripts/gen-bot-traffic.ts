/**
 * SDK-06 step 9 — synthetic known-bot traffic generator (D18/RSK-08 label
 * source). Drives Playwright bot sessions against the REAL local stack; every
 * browser request carries `X-TG-Synthetic: <runId>`. The API honors that
 * header ONLY when its dev config sets `Synthetic:Enabled=true` (API-04);
 * in production config the header is ignored — never trusted from the wild.
 *
 * Usage:
 *   npm run bot-traffic -- --site-key <key> [--target http://localhost:8080]
 *       [--runs 5] [--profile linear|grid|jitter] [--click-url <url>]
 *       [--port 4650] [--allow-remote]
 *
 * --click-url: optional full /c?... tracker URL (API-02) hit FIRST so the
 * session joins a click. The tracker 302s with tg_sid to wherever ITS
 * configured destination points; the run lands on this script's local fixture
 * only if that destination is configured to http://localhost:<port>/ —
 * otherwise --click-url and the local fixture are alternatives, and the
 * profile simply runs on whatever page the redirect landed on.
 *
 * Dev/test tooling only (D1): Node never serves the product; the real
 * endpoints are .NET.
 */
import http from 'node:http';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { chromium, type Page } from '@playwright/test';

const SDK_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const DIST_BUNDLE = path.join(SDK_ROOT, 'dist', 'tg.js');

type Profile = 'linear' | 'grid' | 'jitter';

interface Args {
  target: string;
  siteKey: string;
  runs: number;
  profile: Profile;
  clickUrl?: string;
  port: number;
  allowRemote: boolean;
}

const USAGE =
  'usage: npm run bot-traffic -- --site-key <key> [--target http://localhost:8080] ' +
  '[--runs 5] [--profile linear|grid|jitter] [--click-url <url>] [--port 4650] [--allow-remote]';

function parseArgs(argv: string[]): Args {
  const args: Args = {
    target: 'http://localhost:8080', // NEVER default to a production URL
    siteKey: '',
    runs: 5,
    profile: 'linear',
    port: 4650,
    allowRemote: false,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]!;
    const next = (): string => {
      const v = argv[++i];
      if (v === undefined) throw new Error(`missing value for ${a}\n${USAGE}`);
      return v;
    };
    switch (a) {
      case '--target':
        args.target = next().replace(/\/+$/, '');
        break;
      case '--site-key':
        args.siteKey = next();
        break;
      case '--runs': {
        const n = Number(next());
        if (!Number.isInteger(n) || n < 1) throw new Error(`--runs must be a positive integer\n${USAGE}`);
        args.runs = n;
        break;
      }
      case '--profile': {
        const p = next();
        if (p !== 'linear' && p !== 'grid' && p !== 'jitter') {
          throw new Error(`--profile must be linear|grid|jitter, got '${p}'\n${USAGE}`);
        }
        args.profile = p;
        break;
      }
      case '--click-url':
        args.clickUrl = next();
        break;
      case '--port': {
        const n = Number(next());
        if (!Number.isInteger(n) || n < 1 || n > 65535) throw new Error(`--port must be a valid port\n${USAGE}`);
        args.port = n;
        break;
      }
      case '--allow-remote':
        args.allowRemote = true;
        break;
      default:
        throw new Error(`unknown argument '${a}'\n${USAGE}`);
    }
  }
  if (!args.siteKey) throw new Error(`--site-key is required\n${USAGE}`);
  return args;
}

/** Only localhost/127.0.0.1/::1/*.local targets unless --allow-remote. */
function isLocalTarget(target: string): boolean {
  let u: URL;
  try {
    u = new URL(target);
  } catch {
    return false;
  }
  const host = u.hostname.toLowerCase();
  return (
    host === 'localhost' ||
    host === '127.0.0.1' ||
    host === '::1' ||
    host === '[::1]' ||
    host.endsWith('.local')
  );
}

/**
 * Probes the target. Unreachable => clear error (stack down). Reachable but
 * without the SDK-08 delivery route => serve the locally built dist/tg.js.
 */
async function resolveSdkSrc(target: string): Promise<string> {
  const probeUrl = target + '/sdk/tg.js';
  let res: Response;
  try {
    res = await fetch(probeUrl, { signal: AbortSignal.timeout(5000) });
  } catch {
    throw new Error(
      `target ${target} is unreachable. Is the local compose stack running ` +
        `(docker compose up -d)? Beacons must land on the real API for labels to exist.`
    );
  }
  if (res.ok) return probeUrl;
  if (!existsSync(DIST_BUNDLE)) {
    throw new Error(
      `${probeUrl} returned ${res.status} and no local bundle exists at ${DIST_BUNDLE} — run 'npm run build' first.`
    );
  }
  console.log(`note: ${probeUrl} returned ${res.status}; serving local dist/tg.js instead.`);
  return '/tg.js';
}

function fixtureHtml(sdkSrc: string, siteKey: string, target: string): string {
  return `<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>TG synthetic bot fixture</title>
<style>body{font-family:sans-serif;margin:2rem}section{height:120vh}</style></head>
<body>
<h1>Synthetic bot landing</h1>
<button id="cta" type="button">Learn more</button>
<section>filler one</section><section>filler two</section>
<form id="lead-form" action="/submitted" method="post">
  <label>Name <input name="name" type="text"></label>
  <label>Email <input name="email" type="email"></label>
  <label>Phone <input name="phone" type="tel"></label>
  <label>Comments <textarea name="comments"></textarea></label>
  <button type="submit">Send</button>
</form>
<script src="${sdkSrc}" data-site-key="${siteKey}" data-endpoint="${target}"></script>
<script>document.getElementById('lead-form').addEventListener('submit',function(e){e.preventDefault();});</script>
</body></html>`;
}

/** Throwaway local static server for the generated fixture page. */
function startFixtureServer(port: number, html: string): Promise<http.Server> {
  return new Promise((resolve, reject) => {
    const server = http.createServer((req, res) => {
      const p = new URL(req.url ?? '/', `http://localhost:${port}`).pathname;
      if (p === '/' || p === '/index.html') {
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
        res.end(html);
      } else if (p === '/tg.js') {
        res.writeHead(200, { 'Content-Type': 'text/javascript; charset=utf-8' });
        res.end(readFileSync(DIST_BUNDLE));
      } else {
        res.writeHead(404).end('not found');
      }
    });
    server.once('error', reject);
    server.listen(port, () => resolve(server));
  });
}

/** Bot mouse profiles — deterministic cadence, mechanical paths. */
async function runProfile(page: Page, profile: Profile): Promise<void> {
  const DELAY_MS = 20; // fixed small delay: constant velocity by construction
  if (profile === 'grid') {
    // Row-by-row sweep.
    for (let y = 80; y <= 560; y += 80) {
      for (let x = 40; x <= 1200; x += 60) {
        await page.mouse.move(x, y);
        await page.waitForTimeout(DELAY_MS);
      }
    }
    return;
  }
  // linear: one long diagonal constant-velocity sweep.
  // jitter: same sweep plus +/-2 px noise (a slightly harder positive).
  for (let i = 0; i <= 60; i++) {
    const jx = profile === 'jitter' ? Math.random() * 4 - 2 : 0;
    const jy = profile === 'jitter' ? Math.random() * 4 - 2 : 0;
    await page.mouse.move(40 + i * 18 + jx, 40 + i * 10 + jy);
    await page.waitForTimeout(DELAY_MS);
  }
}

async function typeForm(page: Page): Promise<void> {
  const TYPE_DELAY = 25; // fixed inter-key delay — mechanical typing cadence
  const fields: Array<[string, string]> = [
    ['input[name="name"]', 'Synthetic Bot'],
    ['input[name="email"]', 'synthetic.bot@example.test'],
    ['input[name="phone"]', '+10000000000'],
    ['textarea[name="comments"]', 'synthetic run for label generation'],
  ];
  for (const [selector, value] of fields) {
    const loc = page.locator(selector);
    if ((await loc.count()) === 0) continue; // click-url landed elsewhere
    await loc.pressSequentially(value, { delay: TYPE_DELAY });
  }
  const submit = page.locator('form button[type="submit"]');
  if ((await submit.count()) > 0) await submit.first().click();
}

async function main(): Promise<void> {
  const args = parseArgs(process.argv.slice(2));

  if (!args.allowRemote && !isLocalTarget(args.target)) {
    throw new Error(
      `refusing non-local target '${args.target}' — synthetic bot traffic must never be ` +
        `pointed at production tenants' pages. Pass --allow-remote only if you are sure.`
    );
  }

  const sdkSrc = await resolveSdkSrc(args.target);
  const html = fixtureHtml(sdkSrc, args.siteKey, args.target);
  const fixtureUrl = `http://localhost:${args.port}/`;

  let server: http.Server | null = null;
  let browser: Awaited<ReturnType<typeof chromium.launch>> | null = null;
  try {
    server = await startFixtureServer(args.port, html);
    console.log(`fixture server on ${fixtureUrl} (sdk: ${sdkSrc}, beacons -> ${args.target})`);
    browser = await chromium.launch();

    for (let run = 1; run <= args.runs; run++) {
      const runId = randomUUID();
      // The header rides EVERY request from this context: /i/init, /i beacons,
      // the SDK script fetch and the optional tracker hit.
      const context = await browser.newContext({
        extraHTTPHeaders: { 'X-TG-Synthetic': runId },
      });
      const page = await context.newPage();

      if (args.clickUrl) {
        await page.goto(args.clickUrl); // follows the tracker's 302 (tg_sid joins the click)
        if (!page.url().startsWith(fixtureUrl)) {
          console.log(
            `  note: --click-url landed on ${page.url()} (not the local fixture); ` +
              `running the profile there.`
          );
        }
      } else {
        await page.goto(fixtureUrl);
      }

      await runProfile(page, args.profile);
      await typeForm(page);
      await page.waitForTimeout(3000); // let the 2 s flush timer ship everything
      await context.close();

      console.log(
        `run ${run}/${args.runs} complete: runId=${runId} profile=${args.profile} ` +
          `(envelopes observed: n/a client-side — beacons are fire-and-forget)`
      );
    }

    console.log(
      `${args.runs} synthetic bot runs sent; header X-TG-Synthetic; labels land in ` +
        `tg_labels (source='synthetic_bot') only when Api config Synthetic:Enabled=true (Development).`
    );
  } finally {
    if (browser) await browser.close().catch(() => undefined);
    if (server) server.close();
  }
}

main().catch((err: unknown) => {
  // Clear message, not a stack trace.
  console.error(`gen-bot-traffic: ${err instanceof Error ? err.message : String(err)}`);
  process.exit(1);
});

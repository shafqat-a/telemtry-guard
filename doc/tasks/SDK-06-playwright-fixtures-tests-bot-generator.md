---
id: SDK-06
title: Playwright fixtures, tests, bot generator
phase: 1
workstream: sdk
depends_on: [SDK-03, SDK-04]
size: M
spec_refs: ["§9 testing strategy", D18, "§3 non-goals", "§7"]
detail_level: full
---

# SDK-06: Playwright fixtures, tests, bot generator

## Objective

Build the SDK's automated test rig: Playwright end-to-end tests in `TelemetryGuard.Sdk/e2e/` against fixture pages (`landing.html`, `form.html`) served with the built `dist/tg.js` and a mock `/i` + `/i/init` that captures payloads. Tests pin the envelope schema (zod), **prove no key-identity data ships** (the §3 compliance guardrail), verify honeypot injection and the storage-age flow, and demonstrate that a bot run (Playwright's default `webdriver=true` plus gridded `page.mouse.move`) is visible in the payload. Also ship `scripts/gen-bot-traffic.ts`, the CLI that drives synthetic bot sessions against the real local stack tagged with `X-TG-Synthetic` — the known-bot label source for model training (D18/RSK-08).

## Spec context (self-contained)

- **§9 SDK testing:** "Playwright-driven fixture pages asserting beacon shape — and doubling as a source of *known-bot* telemetry for early model labels."
- **D18 (cold start):** MVP ships without a trained model; the label pipeline once live includes "Playwright-generated synthetic bot runs = guaranteed positives". RSK-08 (label pipeline + LightGBM training) consumes those labels from a `tg_labels` store with source `synthetic_bot`. The generator built here is that source.
- **§3 non-goal — no keystroke content, ever.** The SDK records keyboard timing only. This suite must contain a guardrail test that fails if any key identity or typed content appears in any captured payload.
- **Synthetic tagging contract (coordinate with API-04):** the generator sends header `X-TG-Synthetic: <runId>` on **all** browser requests. The API honors it **only** when the .NET Api dev config enables it — config key `Synthetic:Enabled` = `true` (set in `appsettings.Development.json` of `TelemetryGuard.Api`); when honored, ingested events/sessions are labeled into `tg_labels` with `source='synthetic_bot'` and the runId. In production config the header is ignored (never trusted from the wild). API-04's task must implement this acceptance; this file defines the client half.
- Wire contracts under test (defined by SDK-02/03/04/05; restated below because this task's direct deps are SDK-03/04): envelope `{k, sid, seq, nonce, sent_at, events[, c]}` in exactly that key order, POSTed to `/i` as `text/plain` JSON; `GET /i/init?k=&sid=` → `{nonce, storageTs, storageSig}`; event vocabulary `pv, pm, pd, cl, sc, ky, ff, fb, fs, pa, af, hp, fi, rt, fp`.
- Node/Playwright here is **dev/test tooling only** — the backend stays .NET (D1); the mock server never ships.

## Prerequisites

- **SDK-03** delivered the collectors and the event table (read its task file — the zod schemas below mirror it): `pm {e,t,s:[[t,x,y],…]}` (≥50 ms coalescing, ≤200 samples), `pd/cl {e,t,tr,sn,pt}`, `sc {e,t,y}`, **`ky {e,t,d}` with exactly those keys**, `ff/fb {e,t,fh,ft}`, `fs {e,t,fh}`, `pa {e,t,fk}`, `af {e,t,fh}`, `hp {e,t,kind}`, `fi {e,t,it}`, `rt {e,t,fcp[,fp]}` (at most once per page load); honeypot input auto-injected per form (offscreen absolute, `aria-hidden="true"`, `tabindex="-1"`, name from `data-honeypot-name`, default `website`).
- **SDK-04** delivered the `fp` event (`vid, conf, scr, tz, langs, canvasBlocked, touch, mob, wd, botd{bot,kind}, storage{ck{present,ts,sig,ageMs}, ls{…}, fresh, cookiesDisabled}`) and the `tg_fp` cookie/localStorage pair sourced from `/i/init`'s `{storageTs, storageSig}`.
- Build tooling from SDK-01: `npm run build` → `dist/tg.js`. Note: the envelope checksum field `c` is added by **SDK-05**, which is *not* a dependency of this task — treat `c` as optional (see step 3) so this suite passes before and after SDK-05 lands.

## Implementation steps

1. **Dev dependencies + scripts** — add to `TelemetryGuard.Sdk/package.json`:

   ```json
   "devDependencies": { "@playwright/test": "^1.47.0", "zod": "^3.23.8", "tsx": "^4.19.0" },
   "scripts": {
     "test": "playwright test --config e2e/playwright.config.ts",
     "bot-traffic": "tsx scripts/gen-bot-traffic.ts"
   }
   ```

   (`npm test` replaces SDK-01's placeholder; CI's sdk job already runs it. Add `npx playwright install chromium --with-deps` to the CI sdk job if not present.)

2. **Mock server — `e2e/serve.mjs`** (plain `node:http`, no frameworks; test infra only):
   - Port `4599`. Routes:
     - `GET /dist/*`, `GET /fixtures/*` — static files from `TelemetryGuard.Sdk/`.
     - `GET /i/init` — `200 application/json` `{"nonce":"e2e-nonce-<random hex>","storageTs":<Date.now()>,"storageSig":"e2e-sig-<random hex>"}`; record each issued nonce keyed by the `sid` query param. CORS: `Access-Control-Allow-Origin: *`.
     - `POST /i` — read body (text/plain JSON), push `{receivedAt: Date.now(), raw: body, parsed: JSON.parse(body), headers: req.headers}` into an in-memory array; `204`.
     - `GET /__captured` — the array as JSON. `POST /__reset` — clears it. (Test-only introspection endpoints.)
   - `e2e/playwright.config.ts`:

     ```ts
     import { defineConfig } from '@playwright/test';
     export default defineConfig({
       testDir: './tests',
       webServer: { command: 'node e2e/serve.mjs', port: 4599, reuseExistingServer: true },
       use: { baseURL: 'http://localhost:4599' },
     });
     ```

   - `e2e/fixtures/landing.html`: scrollable content (~3 viewport heights), a button, and
     `<script src="/dist/tg.js" data-site-key="e2e-site" data-endpoint="http://localhost:4599"></script>`.
   - `e2e/fixtures/form.html`: same script tag plus a form with `name`, `email` (`type=email`), `phone` (`type=tel`), `password` (`type=password`), `comments` (`textarea`), and a submit button whose default is prevented by fixture JS (so tests stay on-page).

3. **`e2e/tests/schema.ts`** — zod envelope schema shared by tests:

   ```ts
   import { z } from 'zod';
   const base = { e: z.string(), t: z.number().int().nonnegative() };
   export const kyEvent = z.object({ ...base, e: z.literal('ky'), d: z.union([z.literal(0), z.literal(1)]) }).strict();
   export const pmEvent = z.object({ ...base, e: z.literal('pm'),
     s: z.array(z.tuple([z.number().int(), z.number().int(), z.number().int()])).max(200) }).strict();
   // …analogous strict schemas for pv, pd, cl, sc, ff, fb, fs, pa, af, hp, fi, rt, fp (per the SDK-03/04 tables)
   export const envelope = z.object({
     k: z.string().min(1), sid: z.string().min(8), seq: z.number().int().nonnegative(),
     nonce: z.string(), sent_at: z.number().int().positive(),
     events: z.array(z.unknown()).min(1),
     c: z.string().regex(/^[0-9a-f]{8}$/).optional(),   // added by SDK-05; optional until it lands
   }).strict();
   ```

   Where `c` **is** present, verify it: FNV-1a 32-bit (offset `0x811c9dc5`, prime `0x01000193`, `Math.imul`, `>>>0`) over the UTF-8 bytes of `raw.slice(0, raw.lastIndexOf(',"c":"')) + '}'` must equal `c`. Also assert raw key order by regex on the raw string: `/^\{"k":.*"sid":.*"seq":.*"nonce":.*"sent_at":.*"events":/s`.

4. **`e2e/tests/envelope.spec.ts`** — load `/fixtures/landing.html`, interact (move mouse, scroll, click) until ≥3 envelopes are captured (poll `/__captured`). Assert: every envelope parses against the zod schema; `seq` is 0,1,2,… strictly increasing with no duplicates; `nonce` equals the nonce the mock issued for that `sid`; `k === 'e2e-site'`; content-type header was `text/plain`-compatible; a `pv` event appears in the first envelope.

5. **`e2e/tests/no-key-identity.spec.ts`** — **the compliance guardrail**:
   - Load `/fixtures/form.html`, focus the email field, `page.keyboard.type('XyZZySentinel42@example.com', {delay: 30})`, type into password too, then flush (tab-hide via `page.evaluate(() => document.dispatchEvent(...))` or just wait 2.5 s).
   - Across **all** captured raw payload strings: (a) the sentinel string and any 4+-char substring of it appear **nowhere**; (b) the JSON property names `key`, `code`, `keyCode`, `charCode`, `which`, `char`, `data`, `text`, `value` appear in **no event object** (deep-walk every event); (c) every `ky` event validates against the strict `{e,t,d}` schema — `.strict()` makes any extra property a failure; (d) raw field names (`email`, `password`, `phone`) appear nowhere in payloads (`ff/fb` carry only 8-hex `fh` + `ft`).
   - This test failing must fail CI — never skip/soft it.

6. **`e2e/tests/honeypot.spec.ts`** — load `/fixtures/form.html`: assert exactly one input named `website` exists per form, with `aria-hidden="true"`, `tabIndex === -1`, and offscreen position (`getBoundingClientRect().left < 0` or computed `position:absolute` + `left:-9999px`). Then `page.evaluate` to set its value and dispatch `input`; assert an `hp` event with `kind:'input'` is captured; submit the form and assert `kind:'submit_filled'` follows.

7. **`e2e/tests/storage-age.spec.ts`** — fresh context: visit landing, await the `fp` event; assert `storage.fresh === true` and cookie `tg_fp` + localStorage now hold `"<storageTs>.<storageSig>"` matching the mock's `/i/init` response. Reload (same context, new `sid` via new tab if needed to trigger a fresh `fp`): assert `storage.ck.present === true`, reported `ts` equals the **first** visit's value (not the second `/i/init`'s), `ageMs >= 0`, `fresh === false`. Delete the cookie only, reload again: pair restored from localStorage with the original `ts`.

8. **`e2e/tests/bot-run.spec.ts`** — Playwright's default Chromium (automation-controlled; `navigator.webdriver === true`):
   - Grid the mouse: `for (let i = 0; i <= 40; i++) await page.mouse.move(100 + i*10, 100 + i*5);` with fixed small delays — a perfectly linear, constant-velocity path.
   - Assert the captured `fp` event has `wd === true` (and/or `botd.bot === true` — accept either; Botd versions vary in what default Playwright triggers).
   - Assert the raw `pm` samples betray automation, computing **in the test** (the SDK must NOT compute this — engine-side feature): path-length / chord-length ratio ≤ 1.02 (near-linear) and coefficient of variation of inter-sample intervals < 0.35. This proves the raw samples carry enough signal for the server's `mouse_path_linearity` / `std_inter_event_ms` features.

9. **`scripts/gen-bot-traffic.ts`** — synthetic known-bot generator CLI (run with `npm run bot-traffic -- --target http://localhost:8080 --site-key <key>`):
   - Args (hand-rolled `process.argv` parsing, no dep): `--target` (API base URL of the FND-02 compose stack, default `http://localhost:8080`), `--site-key` (required), `--runs` (default `5`), `--profile` (`linear` | `grid` | `jitter`, default `linear`), `--click-url` (optional: a full `/c?...` tracker URL to hit first so the session joins a click, per API-02), `--port` (local fixture port, default `4650`).
   - For each run: start (once) a throwaway local static server on `--port` serving a generated landing+form page whose script tag is `<script src="{target}/sdk/tg.js" ...>` (the SDK-08 delivery route on the API host) when the target serves it, else the local `dist/tg.js`, with `data-site-key={site-key}` and `data-endpoint={target}` — so beacons go to the **real** API.
   - `const runId = crypto.randomUUID();` then `const context = await chromium.launchPersistentContext(...)`— plain `chromium.launch()` + `browser.newContext({ extraHTTPHeaders: { 'X-TG-Synthetic': runId } })` is sufficient; the header rides every request from the context, including `/i/init`, `/i` beacons, and the optional tracker hit.
   - Navigate: `--click-url` first if given (follows the 302 with `tg_sid` into the local landing page only if the click-url's redirect target is configured to it — otherwise document that `--click-url` and the local fixture are alternatives), else straight to the local fixture. Execute the profile: `linear` = one long diagonal `mouse.move` sweep; `grid` = row-by-row sweep; `jitter` = linear plus ±2 px noise (a *slightly* harder positive). Type into the form with fixed 25 ms delays, submit, wait 3 s for flushes, close context.
   - Print per run: `runId`, envelopes observed (n/a client-side — just note completion), and a final summary line: `N synthetic bot runs sent; header X-TG-Synthetic; labels land in tg_labels (source='synthetic_bot') only when Api config Synthetic:Enabled=true (Development).`
   - **Never** default `--target` to a production URL; refuse to run if `--target` is not localhost/127.0.0.1/`*.local` unless `--allow-remote` is passed.

10. Run everything: `npm run build && npm test` green locally; `npm run bot-traffic -- --site-key dev` against a running FND-02 stack is a manual smoke (skip gracefully with a clear error when the stack is down).

## Files to create or modify

- `TelemetryGuard.Sdk/e2e/playwright.config.ts` (create)
- `TelemetryGuard.Sdk/e2e/serve.mjs` (create)
- `TelemetryGuard.Sdk/e2e/fixtures/landing.html` (create)
- `TelemetryGuard.Sdk/e2e/fixtures/form.html` (create)
- `TelemetryGuard.Sdk/e2e/tests/schema.ts` (create)
- `TelemetryGuard.Sdk/e2e/tests/envelope.spec.ts` (create)
- `TelemetryGuard.Sdk/e2e/tests/no-key-identity.spec.ts` (create)
- `TelemetryGuard.Sdk/e2e/tests/honeypot.spec.ts` (create)
- `TelemetryGuard.Sdk/e2e/tests/storage-age.spec.ts` (create)
- `TelemetryGuard.Sdk/e2e/tests/bot-run.spec.ts` (create)
- `TelemetryGuard.Sdk/scripts/gen-bot-traffic.ts` (create)
- `TelemetryGuard.Sdk/package.json` (modify — devDeps + scripts)
- `.github/workflows/ci.yml` (modify — add `npx playwright install chromium --with-deps` to the sdk job if missing)

## Acceptance criteria

- `cd TelemetryGuard.Sdk && npm run build && npm test` — all five spec files pass headless on a clean checkout (after `npx playwright install chromium`).
- The no-key-identity test demonstrably works: temporarily adding `key: ev.key` to the SDK's `ky` handler and rebuilding makes it fail (verify once, then revert).
- The honeypot, storage-age, and envelope tests pass both **before and after** SDK-05 merges (the optional-`c` schema guarantees this; once `c` is present the checksum verification path runs and passes).
- `npm run bot-traffic -- --site-key dev --runs 2` against the local compose stack completes 2 runs, prints both runIds, and every HTTP request it caused carried `X-TG-Synthetic: <runId>` (verifiable in the API's dev logs); with the stack down it exits non-zero with a clear message, not a stack trace.
- Bot-run test proves detectability end-to-end: `wd`/`botd.bot` true in `fp`, and the test-side linearity computation on raw `pm` samples crosses the thresholds in step 8.
- CI sdk job runs `npm test` green.

## Testing

This task *is* the test suite. Keep tests deterministic: poll `/__captured` with timeouts ≥ 5 s rather than fixed sleeps where possible; reset capture state between tests (`POST /__reset`); never depend on wall-clock timing tighter than the SDK's 2 s flush interval.

## Out of scope / guardrails

- **The SDK itself must not change** in this task (except nothing — if a test exposes an SDK bug, fix it under the owning task's contract, keeping shapes per SDK-03/04's tables). Never weaken the no-key-identity guardrail to make a test pass — it enforces a §3 compliance non-goal.
- **Derived features stay server-side:** the linearity/interval math in `bot-run.spec.ts` lives in the *test* to prove raw-sample sufficiency — do not move any of it into the SDK.
- **`X-TG-Synthetic` is dev-trust only:** the client always sends it during generation, but only the API's `Synthetic:Enabled=true` dev config may honor it (API-04's side). Never design anything that trusts this header in production; synthetic labels (`tg_labels`, `source='synthetic_bot'`) exist for RSK-08's training pipeline, not for runtime scoring.
- Node/Playwright/mock server are **test tooling only** (D1: no server-side scripting in the product) — `e2e/serve.mjs` must never grow product features; the real endpoints are .NET (API-04).
- No training code, no label-store schema, no listen-only rollout logic here — that is RSK-08. No Turnstile/challenge flows — **SDK-07** implements the client decision flow and adds its `decide.spec.ts` + `/decide` mock to this rig. No cross-browser matrix (chromium only for now).
- Generator must refuse non-local targets without `--allow-remote`; never point it at production tenants' pages.

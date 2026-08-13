# TelemetryGuard SDK

TypeScript client SDK for TelemetryGuard, compiled with esbuild to a minified ES2019 IIFE
bundle. It is **not** a .NET project and is deliberately not part of `TelemetryGuard.sln`
(spec §8, D2). Node.js is build tooling only (D1) — the output is a static artifact; never
add a server/start script here.

## Installing

The API origin serves the built bundle (SDK-08): `GET /sdk/tg.js` (latest) and
`GET /sdk/tg-<version>.js` (version-pinned). In production Cloudflare fronts the API
origin (INT-05) and supplies the CDN layer (D22). Snippet URL contract:

```html
<!-- default: latest, fixes propagate within minutes (D22) -->
<script async src="https://<api-host>/sdk/tg.js" data-site-key="YOUR_SITE_KEY"></script>
<!-- change-controlled alternative -->
<script async src="https://<api-host>/sdk/tg-0.1.0.js" data-site-key="YOUR_SITE_KEY"></script>
```

**No `data-endpoint` is needed when loading from the API origin**: the SDK defaults its
beacon endpoint to the origin of its own `script src` (SDK-02), so the minimal snippet
above works out of the box. Hosting the bundle on any *other* origin requires
`data-endpoint="https://<api-host>"` on the script tag.

Cache contract (fixed by SDK-08 — never swap the two policies): `tg.js` is served with
`Cache-Control: public, max-age=300, stale-while-revalidate=60`, so a detection fix
reaches every tenant page within ~5 minutes; `tg-<version>.js` is served with
`Cache-Control: public, max-age=31536000, immutable` — the content of a versioned file
never changes (bump `package.json` instead).

Locally: after `npm run build`, the API's `CopySdkBundle` MSBuild target copies `dist/tg*.js`
into `TelemetryGuard.Api/wwwroot/sdk/` (git-ignored) on the next `dotnet build`/`dotnet run`,
and the bundle is served at `http://localhost:<api-port>/sdk/tg.js`.

## Commands

```
npm ci             # install (uses package-lock.json)
npm run typecheck  # tsc --noEmit, strict
npm run build      # esbuild -> dist/tg.js, dist/tg-<version>.js, dist/meta.json
npm run size       # gzip size gate: fails if dist/tg.js > 30 KB gzipped (D2)
npm run test:integrity  # mechanical seal() self-test (SDK-05): key order, FNV-1a checksum, byte-flip detection
npm test           # Playwright e2e suite (SDK-06): envelope schema, no-key-identity guardrail,
                   # honeypot, storage-age, bot-run — needs `npx playwright install chromium` once
npm run bot-traffic -- --site-key <key>  # synthetic known-bot generator (SDK-06, D18/RSK-08):
                   # drives Playwright bot sessions against the real local stack with
                   # X-TG-Synthetic headers; refuses non-local targets without --allow-remote
```

The e2e rig lives in `e2e/` (mock `/i` + `/i/init` + `/decide` server `serve.mjs`,
fixture pages, Playwright specs). It is dev/test tooling only (D1) — the mock never
ships; the real endpoints are .NET (API-04/API-05). The no-key-identity spec is the §3
compliance guardrail: never skip or weaken it. `decide.spec.ts` covers the SDK-07 gating
flow against a scripted `/decide` queue and a stubbed Turnstile widget — no real
Cloudflare traffic in CI.

Every build emits both `dist/tg.js` (latest) and `dist/tg-<version>.js` (version-pinned, D22),
plus source maps and `dist/meta.json` (esbuild metafile — SDK-05 uses it to prove its module
stays under 2 KB). `dist/` is git-ignored; serving these files to tenants is SDK-08.

## Form gating (SDK-07, spec §6.2)

Gating — the lead-form decision flow — is **strictly opt-in**. Without opt-in the SDK
never intercepts anything (SDK-03's passive rule); with opt-in, `preventDefault` on the
gated form's submit is the single sanctioned exception. Opt in per form or per page:

- add `data-tg-gate` (any value) to a `<form>`; or
- add `data-gate-forms="<CSS selector>"` to the SDK script tag — the selector is
  evaluated at submit time, so forms added after load are gated too.

On submit of an opted-in form the SDK holds the submission and calls
`POST {endpoint}/decide?k={siteKey}` with `{sid[, turnstileToken]}` (UTF-8 JSON,
`Content-Type: text/plain`, no cookies), then acts on the response's `action`:

- `allow` — the submit is released (`form.requestSubmit(...)`, so native validation
  and other submit handlers re-run).
- `block` — the submit stays held; a bubbling `CustomEvent('tg:decision')` with detail
  `{action:'block'}` fires on the form so the page can message the user. The SDK
  renders no text and never says "fraud"; scores/bands/rules never reach the client.
- `challenge` — the Cloudflare Turnstile widget is lazy-loaded (never bundled), rendered
  inside the form; the obtained token is re-POSTed to `/decide`, whose final answer
  releases or holds. The server never re-challenges a token round-trip.

**Fail open:** if `/decide` is unreachable, slow (4 s timeout), or the Turnstile script
fails to load (5 s deadline), the submit is released — an outage in fraud scoring never
costs tenants their leads. Abandoned/failed-open sessions are still scored server-side
by the grace-period worker. Fail-closed is only ever an explicit `block` from the server.

**CSP note:** tenants with gated forms must allow `https://challenges.cloudflare.com`
(`script-src`, plus `frame-src` for the widget iframe). The Turnstile script loads
lazily from there only when a challenge is issued; if CSP blocks it the SDK fails open.
Only the Turnstile *site* key ever appears client-side, and only inside the challenge
response — the secret key stays server-side.

## Bundle size

| Date       | Version | Raw bytes | Gzip bytes | Decision      | Note                                      |
|------------|---------|-----------|------------|---------------|-------------------------------------------|
| 2026-08-12 | 0.1.0   | 53,753    | 21,042     | single bundle | SDK-01 placeholder (fp + botd statically) |
| 2026-08-12 | 0.1.0   | 3,382     | 1,594      | single bundle | SDK-02 runtime; fp/botd not imported yet — SDK-04 re-adds them (~19 KB gz, still under gate) |
| 2026-08-12 | 0.1.0   | 65,588    | 25,040     | single bundle | SDK-04: fp + botd statically wired (fingerprint event, storage age) on top of SDK-02/03 runtime; ~5.5 KB gz headroom |
| 2026-08-12 | 0.1.0   | 65,712    | 25,089     | single bundle | SDK-05: real `seal()` (canonical order + FNV-1a checksum); `src/integrity.ts` contributes **161 B** minified to `dist/tg.js` (< 2048 B budget, per `dist/meta.json`) |
| 2026-08-12 | 0.1.0   | 69,144    | 26,277     | single bundle | SDK-07: decision flow plumbing (gate/decide/challenge); Turnstile is lazy-loaded, never bundled — only its URL string appears in `dist/tg.js`; ~4.3 KB gz headroom |

Decision: single bundle; botd statically imported; measured 20.6 KB gzip (21,042 B) with
both libraries in the bundle, well under the 30,720 B (30 KB) gate — no lazy botd chunk
needed. The SDK-01 row was measured with a placeholder entry that statically imported both
`@fingerprintjs/fingerprintjs` and `@fingerprintjs/botd`, so it reflects the real library
payload. SDK-02 replaced that placeholder with the actual bootstrap (config, session,
transport, init fetch) which does not yet import the libraries; SDK-04 wires them in.
Expected headroom for SDK-03/04/05 code on top of the library payload: ~9 KB gzipped.

For SDK-04's implementer: if future additions push gzip past 30 KB, do **not** raise the
limit — split botd into a lazily loaded second chunk (`dist/tg-botd.js`) via a second
esbuild call in `scripts/build.mjs`, as described in SDK-01 step 8.

## Dependencies

Runtime `dependencies` are exactly two: `@fingerprintjs/fingerprintjs` and
`@fingerprintjs/botd`. Nothing else may be added there; dev tooling goes in
`devDependencies` only. No frameworks, no polyfills.

## CI

CI: un-gate the sdk job in .github/workflows/ci.yml (see SDK-01 step 9).
`.github/workflows/ci.yml` did not exist when SDK-01 was implemented (FND-03 not merged);
when it lands, the `sdk` job should run `npm ci`, `npm run typecheck`, `npm run build`,
`npm run size`, `npm test` with `working-directory: TelemetryGuard.Sdk` and npm caching
keyed on `TelemetryGuard.Sdk/package-lock.json`.

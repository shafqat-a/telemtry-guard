---
id: SDK-02
title: Core runtime, session, transport
phase: 1
workstream: sdk
depends_on: [SDK-01]
size: M
spec_refs: [D2, D22, "§4 (SDK component)", "§6.1", "§7 (raw events)"]
detail_level: full
---

# SDK-02: Core runtime, session, transport

## Objective

Implement the SDK's core: bootstrap from the `<script>` tag's data attributes, session-ID resolution (tracker-provided `tg_sid` URL param > sessionStorage > `crypto.randomUUID`), a one-time bootstrap fetch to `GET /i/init` that obtains the server nonce and signed storage timestamp (necessary because `sendBeacon` cannot read responses), and a batched fire-and-forget transport that flushes the event queue to `POST /i` via `navigator.sendBeacon` with a `fetch(..., {keepalive:true})` fallback. This file defines the **canonical wire contract** (envelope shape and `/i/init` response) that API-04 implements server-side and SDK-03/04/05/06 build on.

## Spec context (self-contained)

- **D2:** the SDK is a plain-TypeScript IIFE bundle (no framework) embedded on third-party tenant pages as `<script src="https://cdn.../tg.js" data-site-key="…">`. Delivery via `navigator.sendBeacon()` so data survives page unloads.
- **§6.1 request flow:** ad click hits the server-side tracker `GET /c?cid=…` (task API-02), which logs the click and 302-redirects to the landing page **appending `tg_sid=<session id>` to the landing URL** — this is how the tracker hit and subsequent SDK beacons join on one session ID. If no beacon arrives within a ~10 s grace period the session is scored as non-JS (`has_js_beacon=0`), so the SDK must get its first envelope out quickly.
- **§7:** the SDK ships **raw events**; the risk engine derives all features server-side. `has_js_beacon` is itself a T2 feature — the mere presence/absence of beacons is a signal. Missing signal ≠ zero (NaN semantics server-side); the SDK must therefore *omit* what it cannot measure, never send zeros.
- **Beacon integrity (§7 T1 `beacon_integrity_ok`):** envelopes carry a server-issued **nonce** (echoed from `/i/init`), a **monotonic `seq`**, and `sent_at`. The server (API-04) verifies nonce match, seq continuity, and timing plausibility. `sendBeacon` cannot read HTTP responses — hence the design: one ordinary `fetch` at bootstrap (`GET /i/init`) retrieves `{nonce, storageTs, storageSig}`, and all subsequent traffic is one-way.
- **Fire-and-forget, no retries:** a dropped envelope produces a **seq gap that is server-visible by design** — retrying would destroy that signal and add complexity. Never buffer across page loads.
- The SDK must never break a host page: every entry point is wrapped so no exception propagates, and nothing on the page is mutated (honeypot injection in SDK-03 is the single sanctioned exception).

## Prerequisites

After SDK-01: `TelemetryGuard.Sdk/` exists with `package.json` (TS 5 strict, esbuild), `scripts/build.mjs` (IIFE es2019 → `dist/tg.js` + `dist/tg-<version>.js`, `__TG_VERSION__` define, metafile), `scripts/check-size.mjs` (30 KB gzip gate), `src/index.ts` (placeholder to replace), `src/types.ts` (base `TgEvent`), `src/global.d.ts`.

Server-side counterpart contracts (implemented by API-04 `/i` + `/i/init` and API-02 tracker redirect) do **not** need to exist to complete this task — SDK-06 provides mocks. This file is the client-side source of truth for those contracts.

## Implementation steps

1. **`src/config.ts`** — read configuration from the script tag:

   ```ts
   export interface TgConfig {
     siteKey: string;
     endpoint: string;      // origin+base for /i and /i/init, no trailing slash
     honeypotName: string;  // consumed by SDK-03; parsed here
   }

   /** Returns null when config is unusable (SDK then stays inert). */
   export function readConfig(): TgConfig | null;
   ```

   - Locate the script element: `document.currentScript` if it is a `<script>` with `dataset.siteKey`; otherwise the **last** match of `document.querySelector('script[data-site-key][src*="tg"]') ?? document.querySelector('script[data-site-key]')`.
   - `siteKey` = `dataset.siteKey`. Missing/empty → return `null` (silent no-op; never log loudly on tenant pages).
   - `endpoint` = `dataset.endpoint` if present (strip trailing `/`), else the **origin of the script's own `src`** (`new URL(script.src, location.href).origin`). If `src` is empty/inline and no `data-endpoint`, return `null`.
   - `honeypotName` = `dataset.honeypotName ?? 'website'`.

2. **`src/util.ts`** — shared helpers (used by SDK-03 field hashing and SDK-05 checksums; the algorithm must match the server, so it is pinned here):

   ```ts
   /** FNV-1a 32-bit over UTF-8 bytes, lowercase hex, zero-padded to 8 chars. */
   export function fnv1aHex(input: string): string {
     let h = 0x811c9dc5;
     const bytes = new TextEncoder().encode(input);
     for (let i = 0; i < bytes.length; i++) {
       h ^= bytes[i]!;
       h = Math.imul(h, 0x01000193) >>> 0;
     }
     return h.toString(16).padStart(8, '0');
   }

   /** Integer ms since performance.timeOrigin. */
   export function nowT(): number { return Math.round(performance.now()); }
   ```

3. **`src/session.ts`** — session-ID resolution, priority order fixed by the design:

   ```ts
   export function resolveSid(): string;
   ```

   1. `new URLSearchParams(location.search).get('tg_sid')` — set by the API-02 tracker redirect. Accept only if it matches `/^[A-Za-z0-9_-]{8,64}$/`; otherwise ignore.
   2. else `sessionStorage.getItem('tg_sid')`.
   3. else `crypto.randomUUID()`; if unavailable (older WebViews), build a v4 UUID from `crypto.getRandomValues(new Uint8Array(16))`.

   Always attempt `sessionStorage.setItem('tg_sid', sid)` inside try/catch (privacy modes may throw). **Do not** rewrite `location.href` to strip `tg_sid` — the SDK never mutates host-page state.

4. **`src/state.ts`** — one module-level runtime state object:

   ```ts
   export interface TgState {
     cfg: TgConfig;
     sid: string;
     nonce: string;               // '' until /i/init resolves; '' forever if it fails
     storageTs: number | null;    // consumed by SDK-04
     storageSig: string | null;   // consumed by SDK-04
     initSettled: boolean;
     seq: number;                 // next envelope sequence number, starts at 0
   }
   export let state: TgState;     // assigned once in bootstrap
   export function initState(cfg: TgConfig, sid: string): void;
   ```

5. **`src/init-fetch.ts`** — the bootstrap fetch. **Wire contract (API-04 implements this):**

   ```
   GET {endpoint}/i/init?k={siteKey}&sid={sid}
   → 200 application/json
   { "nonce": "<opaque string, unique per sid>",
     "storageTs": 1723400000000,          // server epoch ms
     "storageSig": "<opaque signature over storageTs, server-keyed>" }
   ```

   ```ts
   export function fetchInit(): Promise<void>;
   ```

   - Ordinary `fetch` (GET, `credentials: 'omit'`, `cache: 'no-store'`) with a 3-second `AbortController` timeout.
   - On success: populate `state.nonce`, `state.storageTs`, `state.storageSig`.
   - On any failure/timeout/non-200/bad JSON: leave `nonce=''` and storage fields null. **Never retry** — the server treats missing nonce as an integrity signal, and SDK-04 degrades gracefully.
   - Either way set `state.initSettled = true` and notify the transport (step 6) to release held flushes.

6. **`src/transport.ts`** — the batched queue. Public API (SDK-03/04 consume this; keep signatures exact):

   ```ts
   import type { TgEvent } from './types';

   export function enqueue(ev: TgEvent): void;
   /** Collectors with internal buffers/caps register a drain; called at every
    *  flush. Return events to append; also use it to reset per-flush counters. */
   export function registerDrain(drain: () => TgEvent[]): void;
   export function flush(reason: 'count' | 'timer' | 'hidden' | 'pagehide'): void;
   export function startTransport(): void;  // installs listeners + timers
   ```

   Behavior:
   - `enqueue` appends to an in-memory array. On the **first** event after a flush, start a **2000 ms** `setTimeout` → `flush('timer')`. When the queue reaches **10 events** → `flush('count')` immediately.
   - `startTransport()` adds `document.addEventListener('visibilitychange', …)` → if `document.visibilityState === 'hidden'` then `flush('hidden')`; and `window.addEventListener('pagehide', …)` → `flush('pagehide')`. Both passive, wrapped in try/catch.
   - **Hold-until-init:** if `state.initSettled === false` and reason is `'count' | 'timer'`, mark a pending flag and return; when init settles (or its 3 s timeout fires), perform the held flush. Reasons `'hidden'`/`'pagehide'` flush **immediately** regardless (last chance to ship), with whatever nonce is present (possibly `''`).
   - Flushing with an empty queue and no drained events is a no-op (no empty envelopes; `has_js_beacon` must mean real telemetry).
   - **Envelope** — construct with this exact key order (canonical order matters: SDK-05 checksums the serialized form; API-04 verifies):

     ```jsonc
     {
       "k": "<siteKey>",
       "sid": "<sid>",
       "seq": 0,                    // monotonic per page load, increments per envelope sent
       "nonce": "<from /i/init or ''>",
       "sent_at": 1723400001234,    // Date.now() epoch ms
       "events": [ { "e": "...", "t": 123, ... } ]
     }
     ```

   - Serialization goes through **`seal(envelope)`** from `src/integrity.ts` (step 7). Payload = `new Blob([seal(env)], { type: 'text/plain' })` — text/plain keeps the request a CORS "simple request" (no preflight), which both `sendBeacon` and the fetch fallback need. API-04 must parse JSON out of a `text/plain` body.
   - Send: `navigator.sendBeacon(state.cfg.endpoint + '/i', blob)`. If `sendBeacon` is missing **or returns false** (quota — payloads must stay well under the ~64 KB beacon budget; the 10-event trigger plus SDK-03's per-flush caps guarantee this), fall back to `fetch(url, { method: 'POST', body: blob, keepalive: true, credentials: 'omit' })` with a swallowed promise rejection.
   - `state.seq++` **only after a send attempt** (successful or not). **No retries, no persistence, no re-queue** — dropped envelopes surface server-side as seq gaps, by design.

7. **`src/integrity.ts`** — stub for SDK-05 to replace (keeping transport stable):

   ```ts
   import type { Envelope } from './types';
   /** SDK-05 replaces this with canonical-order serialization + FNV-1a checksum. */
   export function seal(env: Envelope): string {
     return JSON.stringify(env);
   }
   ```

8. **`src/types.ts`** — extend with the envelope type:

   ```ts
   export interface Envelope {
     k: string;
     sid: string;
     seq: number;
     nonce: string;
     sent_at: number;
     events: TgEvent[];
   }
   ```

9. **`src/index.ts`** — replace the SDK-01 placeholder with the real bootstrap IIFE:

   ```ts
   import { readConfig } from './config';
   import { resolveSid } from './session';
   import { initState, state } from './state';
   import { fetchInit } from './init-fetch';
   import { enqueue, startTransport } from './transport';
   import { nowT } from './util';

   (() => {
     try {
       const cfg = readConfig();
       if (!cfg) return;                    // inert without a site key
       initState(cfg, resolveSid());
       startTransport();
       enqueue({ e: 'pv', t: nowT() });     // guarantees ≥1 envelope per JS session,
                                            // so has_js_beacon=1 lands inside the ~10 s grace period
       void fetchInit();                    // never awaited; collectors don't block on it
       // SDK-03 (collectors) and SDK-04 (fingerprint) hook in here in later tasks.
     } catch { /* never throw on the host page */ }
   })();
   ```

   The `pv` (pageview) event is deliberately minimal: `{e:'pv', t}`. URL, referrer, UA etc. are HTTP-layer signals the server already sees on the `/i` request itself — do not duplicate them client-side.

10. Event timestamp convention (document in `types.ts` doc comment): every event's `t` is **integer ms since `performance.timeOrigin`**; the envelope's `sent_at` is **epoch ms**. The server reconstructs absolute event times as `sent_at_receive_side − (performance-now-at-send − t)` approximations — precision is the engine's problem; the SDK just ships both clocks consistently.

11. Run `npm run typecheck && npm run build && npm run size` — the size gate must still pass.

## Files to create or modify

- `TelemetryGuard.Sdk/src/index.ts` (replace placeholder)
- `TelemetryGuard.Sdk/src/config.ts` (create)
- `TelemetryGuard.Sdk/src/session.ts` (create)
- `TelemetryGuard.Sdk/src/state.ts` (create)
- `TelemetryGuard.Sdk/src/init-fetch.ts` (create)
- `TelemetryGuard.Sdk/src/transport.ts` (create)
- `TelemetryGuard.Sdk/src/integrity.ts` (create — stub `seal`)
- `TelemetryGuard.Sdk/src/util.ts` (create — `fnv1aHex`, `nowT`)
- `TelemetryGuard.Sdk/src/types.ts` (extend — `Envelope`)

## Acceptance criteria

- `cd TelemetryGuard.Sdk && npm run typecheck && npm run build && npm run size` all exit 0.
- Loading `dist/tg.js` on a page **without** `data-site-key` produces zero network requests and zero console errors.
- Loading it with `<script src=".../tg.js" data-site-key="k1" data-endpoint="http://localhost:9999">` on a page at `http://localhost:8000` (manual check with a static server and browser devtools):
  - exactly one `GET http://localhost:9999/i/init?k=k1&sid=<sid>` fires at load;
  - within ~2 s one `POST http://localhost:9999/i` (content-type `text/plain`) carries `{"k":"k1","sid":"<sid>","seq":0,"nonce":...,"sent_at":...,"events":[{"e":"pv","t":...}]}` with keys in exactly that order;
  - reloading the page reuses the same `sid` (sessionStorage); opening the URL with `?tg_sid=abcdefgh12345678` uses that value instead.
  - with `/i/init` unreachable, the first envelope still ships (after the 3 s init timeout or on tab hide) with `"nonce":""`.
- Killing the endpoint entirely: no retries occur, no exceptions surface on the page, `seq` still increments per attempted envelope.
- No `console.log`/`console.error` calls remain in shipped code paths.

## Testing

- Automated coverage lands in SDK-06 (Playwright + mock `/i` + `/i/init`); this task requires the manual devtools verification above.
- Add a temporary check that `JSON.stringify` of the built envelope preserves key order (`node -e` with a quick object literal test is sufficient — JS preserves string-key insertion order; the point is that `buildEnvelope` constructs keys in canonical order).

## Out of scope / guardrails

- **No collectors** (pointer/keys/scroll/forms — SDK-03), **no fingerprint/botd wiring** (SDK-04), **no checksum** (SDK-05 replaces the `seal` stub). Do not remove the SDK-01 static imports' packages from `package.json` even if temporarily unused.
- **Raw events only** — never compute derived features (linearity, rates, deltas) client-side; the engine owns feature derivation (§7).
- **Fire-and-forget is a hard rule:** no retry loops, no offline queues, no localStorage event buffering. Seq gaps are a server-side integrity signal, not a bug to fix.
- **Never throw or log on the host page**; never mutate host DOM/state (no URL rewriting, no global namespace beyond what's specified).
- **Missing ≠ zero (NaN semantics):** omit fields you cannot measure; never default measurements to `0`.
- **No cookies are set by this task** (storage-age cookie `tg_fp` is SDK-04's job); requests use `credentials: 'omit'`.
- Site key is mandatory — there is no "anonymous" mode; without it the SDK is inert. (Server-side, tenant identity is never optional — same principle.)
- Keep the bundle within the 30 KB gzip gate; no new runtime dependencies.
- Node remains build tooling only (D1) — no server component in this task; API-04 (C#/.NET) owns the real endpoints.

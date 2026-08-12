---
id: SDK-04
title: Fingerprint, Botd, storage age
phase: 1
workstream: sdk
depends_on: [SDK-02]
size: M
spec_refs: ["§4 (storage age)", "§7 T1/T2/T3/CTX", D2, D22]
detail_level: full
---

# SDK-04: Fingerprint, Botd, storage age

## Objective

Wire FingerprintJS and Botd into the SDK and emit **one fingerprint event per session** carrying the visitor ID, component-derived device signals (screen, timezone, language, canvas-blocked), Botd automation flags, and the first-party **storage age** report: the server-signed `{storageTs, storageSig}` pair from `/i/init` persisted in both a cookie (`tg_fp`) and localStorage, with both ages and presence reported every visit, plus cookies-disabled detection.

## Spec context (self-contained)

- **§4 — "Cookie age" means our own cookie's age.** The SDK sets a first-party cookie + localStorage entry with a **signed timestamp** on first visit and reports its age thereafter. A fingerprint seen 40 times that presents brand-new storage every visit is a bot farm wiping state — the T2 signal `storage_age_zero_repeat`. The pairing of "zero age" with "visitor seen many times" is computed **server-side**; the SDK's job is to faithfully report the stored pair (or its absence) each visit. The signature exists so bots cannot forge old timestamps — the server verifies `storageSig` against `storageTs`.
- **`/i/init` (API-04)** returns `{ nonce, storageTs, storageSig }`: `storageTs` is server epoch ms, `storageSig` an opaque server-keyed signature over `storageTs`. This design exists because `sendBeacon` cannot read responses — the one bootstrap `fetch` is the only response the SDK ever sees. SDK-02 already performs the fetch and stores the values in runtime state.
- **§7 signals this task feeds:** T1 `webdriver_flag` / `headless_browser` (≥85, via Botd); T2 `emulator_or_vm`, `screen_res_anomalous`, `storage_age_zero_repeat`; T3 (weak, **never rules** — privacy-tool false positives by design): `cookies_disabled`, `canvas_fp_blocked` (**Brave and Firefox block/randomize canvas by design** — that is exactly why it is T3), `timezone_ip_mismatch`, `language_geo_mismatch`. CTX: `is_mobile`. The SDK ships raw material (components, flags, stored timestamps); the engine derives the features and applies tiering.
- **D2:** FingerprintJS OSS + Botd are the SDK's only runtime dependencies; bundle ≤ 30 KB gzipped. Check `TelemetryGuard.Sdk/README.md` — SDK-01 recorded whether botd is statically bundled or lazily loaded as `dist/tg-botd.js`; follow that decision.
- Missing ≠ zero: if fingerprinting fails or `/i/init` never answered, omit the affected fields — the server maps absence to NaN.

## Prerequisites

- **SDK-02** provides: `state` (`src/state.ts`) with `nonce`, `storageTs: number | null`, `storageSig: string | null`, `initSettled: boolean`, `sid`; `enqueue()` (`src/transport.ts`); `nowT()` (`src/util.ts`); bootstrap `src/index.ts` with a hook point. Read the SDK-02 task file for the envelope contract.
- **Cross-file coordination (not a build dependency):** API-04 implements the live `GET /i/init` endpoint returning `{nonce, storageTs, storageSig}` and the beacon ingestion `POST /i` that persists the `fp` event; it also owns server-side signature verification and the visitor seen-count pairing. Only the response contract — defined client-side in SDK-02 — matters here; SDK-06's mock server suffices for local work, so API-04 does not need to exist before this task starts.

## Implementation steps

1. **`src/fingerprint.ts`** — FingerprintJS + Botd collection:

   ```ts
   import * as FingerprintJS from '@fingerprintjs/fingerprintjs';
   import { load as loadBotd } from '@fingerprintjs/botd';

   export function startFingerprint(): void;  // schedules collection at idle
   ```

   - Schedule via `requestIdleCallback(cb, { timeout: 2000 })`, falling back to `setTimeout(cb, 500)` — fingerprinting must not compete with page load or the first envelope.
   - **One fingerprint event per session:** before collecting, check `sessionStorage.getItem('tg_fp_sent') === state.sid`; if it matches, skip entirely. After successfully enqueueing the `fp` event, set it (try/catch — storage may throw).
   - FingerprintJS: `const fp = await FingerprintJS.load(); const res = await fp.get();` → `visitorId = res.visitorId`, `confidence = res.confidence.score`, `components = res.components`.
   - Component-derived signals (each individually try/caught; omit a field rather than guessing):
     - `scr: [screen.width, screen.height, screen.colorDepth, Math.round(devicePixelRatio * 100) / 100]`
     - `tz`: `Intl.DateTimeFormat().resolvedOptions().timeZone` (fallback: `-new Date().getTimezoneOffset()` as number)
     - `langs`: `navigator.languages?.slice(0, 5) ?? [navigator.language]`
     - `canvasBlocked: boolean` — true when the canvas component failed or is empty: `('error' in (components.canvas ?? {})) || !(components.canvas as any)?.value`. (Brave/Firefox privacy modes trigger this by design — server treats it as weak T3.)
     - `touch`: `navigator.maxTouchPoints > 0`
     - `mob`: coarse `is_mobile` raw material — `/Mobi|Android/i.test(navigator.userAgent)`
   - Botd (respect the SDK-01 README decision — static import, or lazy `dist/tg-botd.js` chunk): `const botd = await loadBotd(); const r = botd.detect();` → `botd: { bot: r.bot, kind: (r as any).botKind }`. Independently include the raw flag `wd: navigator.webdriver === true` (the classic T1 `webdriver_flag`; cheap and not subject to library changes).
   - Botd/FingerprintJS failures: omit the failed sub-object; still send the rest.

2. **`src/storage-age.ts`** — signed first-party storage:

   ```ts
   export interface StorageReport {
     ck: { present: boolean; ts?: number; sig?: string; ageMs?: number };
     ls: { present: boolean; ts?: number; sig?: string; ageMs?: number };
     fresh: boolean;            // true when this visit wrote a new pair
     cookiesDisabled: boolean;
   }
   export function collectStorageAge(): StorageReport;
   ```

   - Stored format in **both** places: the string `"<storageTs>.<storageSig>"`.
     - Cookie: name `tg_fp`, `document.cookie = 'tg_fp=' + val + '; max-age=34560000; path=/; SameSite=Lax'` (400 days — the Chrome cap).
     - localStorage: key `tg_fp`, same string.
   - Algorithm each page load (after `state.initSettled`):
     1. Parse existing cookie (`document.cookie` scan) and `localStorage.getItem('tg_fp')`; a valid entry splits into an integer `ts` and non-empty `sig` on the first `.`.
     2. For each present store, report `{present: true, ts, sig, ageMs: Date.now() - ts}`; absent → `{present: false}`.
     3. **Never overwrite an existing valid pair.** If a store is missing/invalid AND `state.storageTs`/`state.storageSig` are non-null (fresh from `/i/init`), write the fresh pair into that store and set `fresh: true`. If `/i/init` failed, write nothing (absence is the honest report).
     4. If one store has a valid pair and the other is empty, copy the **existing** (older) pair into the empty store — resurrecting age from the surviving store is the point of double-writing; still `fresh: false`.
   - `cookiesDisabled`: `navigator.cookieEnabled === false`, OR a write-then-read probe fails: set `tg_ct=1`, read it back from `document.cookie`, then expire it (`max-age=0`).
   - Report raw `ts` + `sig` (server re-verifies the signature and derives age authoritatively); `ageMs` is client-computed convenience. Forged/tampered sigs are the server's problem to detect — the SDK never validates signatures (it has no key).

3. **`src/fingerprint.ts` — the `fp` event.** Combine everything into a single event (fields omitted when unavailable):

   ```jsonc
   { "e": "fp", "t": 1234,
     "vid": "<visitorId>", "conf": 0.99,
     "scr": [1920, 1080, 24, 1], "tz": "Europe/Stockholm", "langs": ["en-US", "sv"],
     "canvasBlocked": false, "touch": false, "mob": false,
     "wd": false, "botd": { "bot": false },
     "storage": { "ck": {"present": true, "ts": 1723...., "sig": "...", "ageMs": 86400000},
                   "ls": {"present": true, "ts": 1723...., "sig": "...", "ageMs": 86400000},
                   "fresh": false, "cookiesDisabled": false } }
   ```

   `enqueue` it like any other event — normal batching applies. If collection completes while the page is already hidden, the pagehide/hidden flush picks it up.

4. **Waiting on init:** storage-age needs `/i/init`'s outcome. Run `collectStorageAge()` only after `state.initSettled === true`; since fingerprinting is idle-scheduled this is almost always already true — otherwise poll with a short `setTimeout` loop (max 4 s) and then proceed with whatever is settled (absence stays absent).

5. Wire `startFingerprint()` into the bootstrap in `src/index.ts` (after `startTransport()`/collectors hook). If SDK-01's README chose the **lazy botd chunk**: implement the loader here — inject `<script src="<script-origin-and-path>/tg-botd.js">`, await `window.__tgBotd`, 3 s timeout, omit `botd` field on failure; keep `wd` regardless.

6. Run `npm run typecheck && npm run build && npm run size` — the gzip gate must still pass now that both libraries are genuinely exercised. Update the size table in `TelemetryGuard.Sdk/README.md` with the new measurement.

## Files to create or modify

- `TelemetryGuard.Sdk/src/fingerprint.ts` (create)
- `TelemetryGuard.Sdk/src/storage-age.ts` (create)
- `TelemetryGuard.Sdk/src/index.ts` (modify — call `startFingerprint()`)
- `TelemetryGuard.Sdk/src/types.ts` (modify — optional `fp` event typing)
- `TelemetryGuard.Sdk/README.md` (modify — updated size measurement)
- (`TelemetryGuard.Sdk/src/botd-chunk.ts` + `scripts/build.mjs` — only if SDK-01's measured decision was the lazy chunk)

## Acceptance criteria

- `npm run typecheck && npm run build && npm run size` exit 0 (≤ 30 KB gzip).
- On a fixture page (manual devtools; SDK-06 automates):
  - exactly **one** `fp` event is emitted per session — reloading within the same tab session does emit again only because sessionStorage keys off `sid`… verify: same tab reload with same `sid` → **no** second `fp` event; new tab (new `sid`) → new `fp` event.
  - first ever visit: `storage.ck.present === false` before write, event reports `fresh: true`, and afterwards the `tg_fp` cookie and localStorage entry both exist with the same `"<ts>.<sig>"` value that `/i/init` returned.
  - reload: `fp.storage.ck.present === true`, `ts` **unchanged** from the first visit (never overwritten by the new `/i/init` values), `ageMs > 0`, `fresh: false`.
  - deleting only the cookie then reloading: the pair is restored **from localStorage** (same old `ts`), not from the fresh `/i/init` values.
  - with cookies blocked in the browser: `storage.cookiesDisabled === true`, localStorage side still works, no exceptions.
  - `/i/init` unreachable on a clean profile: `fp` event still ships with `storage.ck.present=false, ls.present=false, fresh=false` — no fabricated pair.
  - under Playwright/headless: `wd === true` and/or `botd.bot === true` appears in the payload (this becomes an SDK-06 automated assertion).
- `vid` is a stable hex string across reloads in the same browser profile; `conf`, `scr`, `tz`, `langs` populated on a normal browser.
- No PII beyond the fingerprinting library's own output is collected: no raw UA parsing shipped beyond the `mob` boolean, no geolocation API use, no clipboard/storage enumeration.

## Testing

- Automated Playwright tests (fp event schema, storage-age first/second visit flow, botd flags under automation) are SDK-06 deliverables — keep shapes exactly as specified so those tests can pin them.
- Local iteration without the real API: SDK-06's mock server serves `/i/init`; until then, a 10-line static mock (any local static server + a JSON route) is acceptable for manual verification.

## Out of scope / guardrails

- **One `fp` event per session** — never per-envelope or per-page-load re-fingerprinting (cost + payload budget).
- **Never fabricate storage state:** no client-generated timestamps, ever — only server-signed pairs from `/i/init` are written; absent means absent (NaN semantics — missing ≠ zero). Never overwrite an existing pair with a fresh one (that would destroy the age signal).
- The SDK **never validates or interprets** `storageSig` and never derives `storage_age_zero_repeat` — server-side (API-04 / risk engine) owns verification and the seen-count pairing.
- `canvasBlocked`, `cookiesDisabled` etc. are **T3 raw material** — no client-side scoring, weighting, or filtering of "suspicious" values; ship facts only. Rules and scores live server-side, and rules only ever *raise* scores there.
- No extension enumeration (explicit §3 non-goal), no keystroke content, no clipboard reads.
- No new runtime dependencies beyond the two FingerprintJS packages; 30 KB gzip gate holds; follow the SDK-01 README botd decision rather than re-deciding ad hoc (re-measure and update the README if you must flip it).
- Node stays build-tooling only (D1); the endpoints are .NET (API-04) — never stand up a JS server as anything but a dev mock.

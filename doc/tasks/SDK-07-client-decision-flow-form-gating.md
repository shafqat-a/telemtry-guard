---
id: SDK-07
title: Client decision flow (form gating, Turnstile round-trip)
phase: 1
workstream: sdk
depends_on: [SDK-03, SDK-06]
size: M
spec_refs: ["§6.2", "§6.3", D14, D2]
detail_level: full
---

# SDK-07: Client decision flow (form gating, Turnstile round-trip)

## Objective

Implement the client half of the lead-form decision flow (spec §6.2): on submit of a tenant-gated form, hold the submission, `POST /decide` with the session ID, and act on the response — `allow` releases the submit, `block` keeps it held, `challenge` lazy-loads the Cloudflare Turnstile widget, obtains a token, re-POSTs `/decide` with `turnstileToken`, and releases or holds per the final answer. Ships with Playwright coverage in the SDK-06 rig (mock `/decide`, stubbed Turnstile). This task is what makes the 31–70 challenge band function end-to-end for lead forms in Phase 1; API-05 owns the server half.

## Spec context (self-contained)

- **§6.2 lead-form flow:** "SDK monitors the form; on submit the page calls the decision endpoint with the session ID → allow / challenge (Turnstile token round-trip) / block before the lead is accepted." This task is that call and round-trip.
- **§6.3 bands:** 0–30 allow / 31–70 challenge / 71–100 block — computed entirely server-side. The client only ever sees the action string; the numeric score, band names, and rule hits are never in any response (API-05 guarantees), so this module has nothing to leak.
- **API-05 request/response contract (authoritative — mirror exactly; consult API-05's task file):**
  - `POST {endpoint}/decide?k={siteKey}`, body UTF-8 JSON sent with `Content-Type: text/plain` (preflight-free CORS simple request): `{ "sid": "<sid>", "turnstileToken": "<optional widget token>" }`.
  - Responses, always `200 application/json` for a decision: `{"action":"allow"}` · `{"action":"challenge","turnstileSiteKey":"0x4AAA..."}` · `{"action":"block"}`.
  - API-05 serves `Access-Control-Allow-Origin: *` on this route, so the SDK **can** read the response (unlike `/i`).
  - A token round-trip never returns `challenge` again — API-05 maps it to allow or block; the client must not build a re-challenge loop.
- **D14 Turnstile:** the widget script is `https://challenges.cloudflare.com/turnstile/v0/api.js`. It is loaded **lazily, only when a `challenge` response arrives** — never bundled (most sessions never see a challenge, and D2's 30 KB gate budgets our code, not Cloudflare's). Only the *site* key is ever client-side, and it arrives in the challenge response — never from SDK config.
- **D2 / host-page safety:** gating intercepts form submits — the **single sanctioned exception** to SDK-03's passive rule, and only on forms the tenant explicitly opted in (see step 1). Ungated forms are never touched.
- **Availability over enforcement:** if `/decide` is unreachable, slow, or the Turnstile script fails to load, the SDK **fails open** (releases the submit). An outage in fraud scoring must never cost tenants their leads. Abandoned or failed-open sessions are still scored server-side by the API-06 grace-period worker — nothing is lost except the inline gate.

## Prerequisites

- **SDK-02/03** shipped: `state` (`sid`), `cfg` (`endpoint`, `siteKey`), `enqueue`, and the forms collector. The passive `fs` (form submit) event keeps firing unchanged — gating is layered on top, not into, the collectors.
- **SDK-06** shipped the e2e rig (`e2e/serve.mjs`, Playwright config, fixtures) — this task extends it.
- **Cross-file coordination (not build dependencies):** API-05 implements `POST /decide` per the contract above; SDK-06's mock suffices for all local work, so API-05 does not need to exist first. One contract detail to flag to API-05's implementer: its `sid` validation must accept SDK-02's sid alphabet (tracker `tg_sid` form `[A-Za-z0-9_-]{8,64}` **or** a hyphenated UUID) — a `^[0-9a-f]{32}$`-only validator would reject every SDK-generated sid. INT-01 owns server-side token verification; nothing Turnstile-secret ever appears client-side.

## Implementation steps

1. **Opt-in config** (`src/config.ts` — extend `TgConfig`): gating applies only to forms matching either
   - a form-level attribute `data-tg-gate` (any value), or
   - a CSS selector given on the script tag as `data-gate-forms="<selector>"` (evaluated at submit time so late-added forms work).

   With neither present this entire module is inert and the SDK remains fully passive (SDK-03's guardrail). Parse `gateForms: string | null` in `readConfig()`.

2. **`src/gate.ts`** — submit interception:
   - Install (only when gating can apply) **one** capture-phase `submit` listener on `document`. Per-form state in a `WeakMap<HTMLFormElement, 'idle'|'pending'|'approved'|'blocked'>`.
   - On submit of a gated form: `approved` → return (native submit proceeds); `pending`/`blocked` → `ev.preventDefault()` and return; `idle` → `ev.preventDefault()` (do **not** call `stopImmediatePropagation` — SDK-03's passive `fs` listener must still observe the attempt), remember `ev.submitter`, set `pending`, run the decide flow (step 3).
   - Outcomes: **allow** → set `approved`, then `form.requestSubmit(submitter)` (re-fires native validation and other handlers; the `approved` state lets it through). **block** → set `blocked`; dispatch a bubbling `CustomEvent('tg:decision', { detail: { action: 'block' } })` on the form so the page can message the user — the SDK itself renders no text and never says "fraud". Any availability failure → treat as allow (**fail open**).

3. **`src/decide.ts`** — the API round-trip:

   ```ts
   export type DecideResponse =
     | { action: 'allow' } | { action: 'block' }
     | { action: 'challenge'; turnstileSiteKey: string };
   /** Resolves null on network failure / timeout / non-200 / bad JSON — caller fails open. */
   export function decide(turnstileToken?: string): Promise<DecideResponse | null>;
   ```

   - `fetch(cfg.endpoint + '/decide?k=' + encodeURIComponent(cfg.siteKey), { method: 'POST', body, headers: { 'Content-Type': 'text/plain' }, credentials: 'omit' })` with a **4 s** `AbortController` timeout; body `JSON.stringify({ sid: state.sid, turnstileToken })` (omit the key when absent).
   - Validate the parsed response shape; anything unexpected → `null`. No retries — one call per user action.

4. **`src/challenge.ts`** — lazy Turnstile:

   ```ts
   /** Renders the widget inside the form; resolves the token, or null on load
    *  failure / error / expiry — caller fails open. */
   export function runChallenge(form: HTMLFormElement, siteKey: string): Promise<string | null>;
   ```

   - Inject `<script src="https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit" async>` once per page; poll for `window.turnstile` with a **5 s** deadline → `null` on timeout (fail open; tenant CSP may block Cloudflare — document in the README that gated tenants must allow `challenges.cloudflare.com`).
   - Render into a `<div>` appended just before the submitter (fallback: end of form): `turnstile.render(container, { sitekey, callback, 'error-callback': …, 'expired-callback': … })` — `callback(token)` resolves the token; error/expired resolve `null`. Remove the container once the flow completes either way.
   - Challenge flow in `gate.ts`: `challenge` → `runChallenge(...)` → token ? `decide(token)` → `allow`/`block` as in step 2 (a `null` second response fails open) : fail open. Never loop back into a second challenge.

5. **Wire-up:** call `startGate(cfg)` from the `src/index.ts` bootstrap after `startCollectors(...)`. Module-scope guard against double install. Run `npm run typecheck && npm run build && npm run size` — the 30 KB gate must still pass (Turnstile is not bundled; this is plumbing only).

6. **SDK-06 rig extensions:**
   - `e2e/serve.mjs`: add `POST /decide` — pops the next scripted response from a queue set via `POST /__decide-script` (JSON array, e.g. `[{"action":"challenge","turnstileSiteKey":"e2e-sk"},{"action":"allow"}]`; empty queue → `{"action":"allow"}`), records each received body + headers into the `/__captured` store (tagged `kind:'decide'`), replies `200 application/json` with `Access-Control-Allow-Origin: *`. Also add `POST /__submitted` (records, 204) as a form target.
   - `e2e/fixtures/gated-form.html`: same fields as `form.html`, but the form has `data-tg-gate`, `method="post"`, `action="/__submitted"`, and **no** fixture-JS preventDefault.
   - `e2e/tests/decide.spec.ts`, stubbing Turnstile via `page.route('**/turnstile/v0/api.js*', …)` fulfilling a JS body that sets `window.turnstile = { render: (el, opts) => (setTimeout(() => opts.callback('e2e-token'), 50), 'w1'), remove: () => {} }`:
     - **allow:** script `[allow]`; fill + submit → exactly one `/decide` call carrying the page's `sid` and `k=e2e-site`, and `/__submitted` was hit.
     - **block:** script `[block]` → `/__submitted` never hit; the page received a `tg:decision` event with `action:'block'` (fixture listener records it to a `window` var).
     - **challenge → allow:** script `[challenge, allow]` → Turnstile stub was requested **only after** the challenge response, second `/decide` body has `turnstileToken:"e2e-token"`, form submitted.
     - **challenge → block:** script `[challenge, block]` → form held.
     - **fail-open:** `/decide` scripted to 500 (or hang past the 4 s timeout) → form submits anyway.
     - **ungated:** on `form.html` (no `data-tg-gate`), submitting triggers **zero** `/decide` calls.

## Files to create or modify

- `TelemetryGuard.Sdk/src/gate.ts` (create)
- `TelemetryGuard.Sdk/src/decide.ts` (create)
- `TelemetryGuard.Sdk/src/challenge.ts` (create)
- `TelemetryGuard.Sdk/src/config.ts` (modify — `gateForms`)
- `TelemetryGuard.Sdk/src/index.ts` (modify — `startGate`)
- `TelemetryGuard.Sdk/src/types.ts` (modify — `DecideResponse`)
- `TelemetryGuard.Sdk/e2e/serve.mjs` (modify — `/decide` mock, `/__decide-script`, `/__submitted`)
- `TelemetryGuard.Sdk/e2e/fixtures/gated-form.html` (create)
- `TelemetryGuard.Sdk/e2e/tests/decide.spec.ts` (create)
- `TelemetryGuard.Sdk/README.md` (modify — gating opt-in attributes, CSP note for `challenges.cloudflare.com`)

## Acceptance criteria

- `npm run typecheck && npm run build && npm run size` exit 0 — Turnstile is never part of the bundle (grep `dist/tg.js` for `challenges.cloudflare.com` finds only the lazy-load URL string, no vendored widget code).
- `npm test` green including all six `decide.spec.ts` scenarios of step 6.
- The Turnstile script URL is requested only in the challenge scenarios — never on allow/block/ungated paths (assert via Playwright request log).
- No response field other than `action`/`turnstileSiteKey` is read; no score/band/rule string ever appears in SDK code or the `tg:decision` event detail.
- Every `/decide` request body is valid JSON `{sid[, turnstileToken]}` with `Content-Type: text/plain` and no cookies (`credentials: 'omit'`).
- Forms without opt-in are byte-for-byte unaffected (no listener side effects, no extra DOM).

## Testing

Covered by `decide.spec.ts` in the SDK-06 rig (step 6) — deterministic via the scripted `/decide` queue and the Turnstile stub; no real Cloudflare traffic in CI. Manual smoke against the real stack (API-05 + INT-01 live) is a Phase-1 integration checkpoint, not an acceptance gate here.

## Out of scope / guardrails

- **Fail open on every availability failure** (decide timeout/error, Turnstile load failure) — an outage must never make a tenant's form unsubmittable. Fail-*closed* is only ever an explicit `block` decision from the server.
- **Only opted-in forms are gated**; `preventDefault` here is the single sanctioned exception to SDK-03's passive rule and must never leak into the collectors.
- **Never bundle or vendor Turnstile**; lazy-load on challenge only. The Turnstile *secret* key never exists client-side (INT-01); the *site* key comes only from the challenge response.
- **No re-challenge loops, no retries** beyond the single token re-POST — API-05 guarantees the token round-trip terminates in allow/block.
- **Never expose or interpret scores** — act on the action string only; rules/bands/thresholds live server-side (§6.3, API-05).
- The server half (endpoint, band mapping, token verification, finalization) is API-05/INT-01/API-06 — do not duplicate any of it, and do not add SDK-side "pre-checks" that second-guess the server.
- 30 KB gzip gate holds; no new runtime dependencies (D2).

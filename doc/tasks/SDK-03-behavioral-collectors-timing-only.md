---
id: SDK-03
title: Behavioral collectors (timing only)
phase: 1
workstream: sdk
depends_on: [SDK-02]
size: L
spec_refs: ["§3 non-goals (no keystroke content)", "§7 signal spec", D2, "§4 (honeypots)"]
detail_level: full
---

# SDK-03: Behavioral collectors (timing only)

## Objective

Add the passive behavioral collectors to the SDK: coalesced raw pointer-move samples, pointerdown/click trust flags, scroll positions, **timing-only** keyboard events, form field focus/fill timing with hashed field names, paste classification, an autofill heuristic, per-form honeypot injection, and the first-interaction marker. Everything ships as **raw events** — the risk engine derives every feature (linearity, inter-event stats, fill times) server-side.

## Spec context (self-contained)

- **§3 non-goal — keystroke content capture is a compliance and liability line.** The SDK records keyboard **timing metadata only, never key values**. No `key`, `code`, `keyCode`, `charCode`, `which`, no characters, no field values. This must be *provable from the payload* (SDK-06 ships a guardrail test asserting no key-identity fields exist).
- **§7 architectural implication:** "the SDK ships *raw events*; the risk engine computes derived features (linearity, inter-event stats, all velocity…). Keeps the snippet dumb and lets features evolve without redeploying tenant pages." Do not compute `mouse_path_linearity`, `std_inter_event_ms`, fill durations, or any aggregate client-side.
- **Signals these raw events power (server-side):** T1 `honeypot_touched` (≥95), `click_before_render` / `pointer_untrusted` (≥90), near-1.0 `mouse_path_linearity`, near-0 `std_inter_event_ms`; T2 `form_fill_time_sec` (conditioned on `autofill_detected`), `first_interaction_delay_ms`, `input_modality_mismatch`, `time_on_page_sec`; T3 `paste_in_identity_fields` (password managers cause false positives — that's why it is T3 and classification is coarse), `scroll_events`, raw event counts; CTX `form_submitted`, `autofill_detected`.
- **§4:** honeypots are SDK-injected hidden inputs; any interaction with them is near-deterministic bot evidence.
- **D2:** plain TS, no framework, bundle ≤ 30 KB gzipped. Collectors must be passive and must never break or visibly alter the host page. The off-screen honeypot input and inert decoy link are the sanctioned DOM mutation set; the SDK prevents navigation only on the decoy link it owns.
- Missing ≠ zero: if something can't be observed (e.g. no forms on the page), emit nothing — the server maps absence to NaN.

## Prerequisites

After SDK-02, `TelemetryGuard.Sdk/src/` contains:

- `transport.ts` exporting `enqueue(ev: TgEvent): void` and `registerDrain(drain: () => TgEvent[]): void` (drains are called at every flush — use them to hand over buffered samples and reset per-flush caps).
- `util.ts` exporting `fnv1aHex(input: string): string` (FNV-1a 32-bit, UTF-8, 8-char hex) and `nowT(): number` (integer ms since `performance.timeOrigin`).
- `config.ts` whose `TgConfig` already carries `honeypotName` (default `'website'`, overridable via the script tag's `data-honeypot-name`).
- `types.ts` with `TgEvent { e: string; t: number; ... }`; `index.ts` bootstrap with a marked hook point for collectors.

Read the SDK-02 task file for the envelope/flush semantics (10 events / 2 s / hidden / pagehide).

## Implementation steps

All listeners: `addEventListener(type, handler, { passive: true, capture: true })` on `window` or `document` as noted, every handler body wrapped in try/catch. Only **trusted** events are recorded except where `isTrusted` is itself the datum (pointerdown/click).

1. **`src/collectors/pointer.ts`**
   - `pointermove` (document): keep an internal sample buffer. Record a sample only if ≥ **50 ms** have passed since the last recorded sample (coalescing). Sample = `[t, x, y]` with `t = nowT()`, `x = Math.round(ev.clientX)`, `y = Math.round(ev.clientY)`. Hard cap **200 samples per flush window**; drop further samples until the buffer is drained.
   - Register a drain: if the buffer is non-empty, return one batch event `{ e: 'pm', t: <t of first sample>, s: [[t,x,y], ...] }` and clear the buffer (this batch counts as a single queued event; samples inside are raw). Empty buffer → return `[]`.
   - `pointerdown` (document): `enqueue({ e: 'pd', t: nowT(), tr: ev.isTrusted ? 1 : 0, sn: Math.round(ev.timeStamp), pt: pointerTypeCode(ev) })`.
   - `click` (document): `enqueue({ e: 'cl', t: nowT(), tr: ev.isTrusted ? 1 : 0, sn: Math.round(ev.timeStamp), pt: pointerTypeCode(ev) })`.
   - `pointerTypeCode`: `'mouse'→'m'`, `'touch'→'t'`, `'pen'→'p'`, else `'u'`. (`pt` is raw modality data powering `input_modality_mismatch`; `tr`/`sn` power `pointer_untrusted` and `click_before_render` — `sn` is the event's own `timeStamp`, i.e. ms since navigation start, which the server compares against the paint mark shipped by the `rt` event of step 7.)

2. **`src/collectors/scroll.ts`**
   - `scroll` (window): buffer `{ t: nowT(), y: Math.round(window.scrollY) }` with ≥ **100 ms** coalescing, cap **30 per flush window**. Drain returns them as individual `{ e: 'sc', t, y }` events and resets buffer + cap.

3. **`src/collectors/keys.ts`** — **timing only**:
   - `keydown` / `keyup` (document): `enqueue({ e: 'ky', t: nowT(), d: ev.type === 'keydown' ? 1 : 0 })`.
   - **The handler must never touch** `ev.key`, `ev.code`, `ev.keyCode`, `ev.charCode`, `ev.which`, or any input value. A `ky` event has **exactly** the keys `{e, t, d}` — nothing else, ever. This exact shape is asserted by the SDK-06 guardrail test.
   - Cap 150 `ky` events per flush window (register a drain that returns `[]` and resets the counter). Also increment a per-focused-field keydown counter shared with the forms collector (step 4) via a small module-scope registry.

4. **`src/collectors/forms.ts`** — field timing with hashed identity:
   - Field identity hash: `fh = fnv1aHex((el.name || el.id || el.getAttribute('autocomplete') || 'anon').toLowerCase())`. **Only the hash ships** — never the raw name/id, never any value.
   - Field type `ft`: the element's `type` attribute lowercased if in `{text,email,tel,password,number,search,url,checkbox,radio,select,textarea,other}` (map `<select>`→`select`, `<textarea>`→`textarea`), else `'other'`.
   - `focusin` (document): for form fields (`input`, `select`, `textarea`, excluding the honeypot) → `enqueue({ e: 'ff', t: nowT(), fh, ft })`; start per-field tracking state in a `WeakMap<Element, {keydowns: number; lastLen: number; pasteAt: number}>`.
   - `focusout` (document): → `enqueue({ e: 'fb', t: nowT(), fh, ft })`. (Server derives per-field fill time from `ff`/`fb`/`ky` timings — do not compute durations client-side.)
   - `submit` (document, capture): → `enqueue({ e: 'fs', t: nowT(), fh: fnv1aHex(formIdentity(form)) })` where `formIdentity` = `form.id || form.name || form.action || 'form'` lowercased. Also run the honeypot submit check (step 5). Never call `preventDefault`.
   - **Paste** — `paste` (document): classify the target field, then `enqueue({ e: 'pa', t: nowT(), fk })` with `fk: 'identity' | 'other'`. `identity` when: `ft ∈ {email, tel, password}` OR `autocomplete` attribute contains any of `name|email|tel|username` OR the raw (local, never transmitted) `name`/`id` matches `/email|phone|mobile|name|user/i`. **Never read clipboard contents.** Record `pasteAt = nowT()` in the field's tracking state.
   - **Autofill heuristic** — value change without keystrokes. On `input` (document) over a tracked field: let `len = (el as HTMLInputElement).value.length` (**length only — the value string itself must never be stored, hashed, or transmitted**). If `len - state.lastLen > 1` AND `state.keydowns === 0` AND `nowT() - state.pasteAt > 500` → `enqueue({ e: 'af', t: nowT(), fh })`, at most once per field per page load. Also treat a `change` event with zero keydowns, no paste, and non-zero length as the same signal. Update `lastLen`.

5. **`src/collectors/honeypot.ts`** — auto-injected per form:
   - For every `<form>` present at init **and** any `<form>` later added (one `MutationObserver` on `document.body`, `{childList: true, subtree: true}`, checking added nodes for forms), inject once per form:

     ```ts
     const hp = document.createElement('input');
     hp.type = 'text';
     hp.name = cfg.honeypotName;                    // default 'website', data-honeypot-name overrides
     hp.setAttribute('aria-hidden', 'true');
     hp.tabIndex = -1;
     hp.autocomplete = 'off';
     hp.style.cssText = 'position:absolute!important;left:-9999px!important;top:-9999px!important;height:1px;width:1px;opacity:0;pointer-events:auto;';
     form.appendChild(hp);
     ```

     Skip injection if the form already contains an input with that name. Do **not** use `display:none`/`visibility:hidden`/`type=hidden` — naïve bots skip those; offscreen-absolute is the standard honeypot placement.
   - Report **any** interaction: `focus` on hp → `{ e: 'hp', t: nowT(), kind: 'focus' }`; `input` on hp → `{ e: 'hp', t, kind: 'input' }`; at form submit, if `hp.value !== ''` → `{ e: 'hp', t, kind: 'submit_filled' }`. Then `flush` is *not* forced — normal batching applies (T1 scoring happens server-side).
   - Honeypot fields are excluded from the forms collector (`ff`/`fb`/`af`) so they only ever produce `hp` events.

6. **`src/collectors/first-interaction.ts`**
   - Listen once (`{once:true}` semantics per type, first across all wins) for trusted `pointerdown`, `keydown`, `wheel`, `touchstart`, `scroll`. On the first trusted one: `enqueue({ e: 'fi', t: nowT(), it: 'pointer'|'key'|'wheel'|'touch'|'scroll' })`, then remove all five listeners. Since `t` is ms since `performance.timeOrigin`, `t` **is** the raw material for `first_interaction_delay_ms` — the server computes the feature; the SDK just marks the moment.

7. **`src/collectors/paint.ts`** — render-timing mark (the missing half of `click_before_render`):
   - On install, check `performance.getEntriesByType('paint')`; if a `first-contentful-paint` entry already exists, emit immediately. Otherwise install `new PerformanceObserver(cb).observe({ type: 'paint', buffered: true })` inside try/catch — browsers without paint timing emit nothing (missing ≠ zero; the server maps absence to NaN).
   - Emit **once per page load**, as soon as `first-contentful-paint` is observed: `enqueue({ e: 'rt', t: nowT(), fcp: Math.round(fcpEntry.startTime), fp: Math.round(fpEntry.startTime) })`, omitting `fp` when no `first-paint` entry exists. Disconnect the observer after emitting. `startTime` is already ms since `performance.timeOrigin` — the same clock as `cl`/`pd`'s `sn` (ms since navigation start) — so the server can compare the two directly.
   - **Server-side coordination (API-04 + RSK-04, cross-file — not this task):** API-04's session aggregation derives `click_before_render = 1` when any `cl` event's `sn` (fallback `t`) is **less than** the session's `fcp` from this event; RSK-04 maps the `ClickBeforeRender` feature from that derived hash field. The SDK ships both raw marks and never computes the comparison itself (§7 raw-events rule).

8. **`src/collectors/index.ts`** — `export function startCollectors(cfg: TgConfig): void` calling each collector's install function; wire it into `src/index.ts` bootstrap after `startTransport()` (the hook point SDK-02 marked). Guard so double-injection is impossible if the script is included twice (module-scope `started` flag).

9. **Event vocabulary added by this task** (server/API-04 + SDK-06 zod schema must mirror; all `t` = int ms since timeOrigin):

   | e | payload | powers |
   |---|---------|--------|
   | `pm` | `{e,t,s:[[t,x,y],…]}` ≤200 samples, ≥50 ms apart | mouse_path_linearity, inter-event stats |
   | `pd` | `{e,t,tr:0\|1,sn:int,pt:'m'\|'t'\|'p'\|'u'}` | pointer_untrusted, input_modality_mismatch |
   | `cl` | `{e,t,tr,sn,pt}` | click_before_render |
   | `sc` | `{e,t,y:int}` | scroll_events, time_on_page |
   | `ky` | `{e,t,d:0\|1}` — **exactly these keys** | inter-keystroke timing |
   | `ff` | `{e,t,fh:hex8,ft:string}` | form_fill_time |
   | `fb` | `{e,t,fh,ft}` | form_fill_time |
   | `fs` | `{e,t,fh}` | form_submitted |
   | `pa` | `{e,t,fk:'identity'\|'other'}` | paste_in_identity_fields |
   | `af` | `{e,t,fh}` | autofill_detected |
   | `hp` | `{e,t,kind:'focus'\|'input'\|'submit_filled'}` | honeypot_touched |
   | `fi` | `{e,t,it:string}` | first_interaction_delay_ms |
   | `rt` | `{e,t,fcp:int[,fp:int]}` — at most once per page load | click_before_render (server compares `cl.sn` against `fcp`) |

10. Run `npm run typecheck && npm run build && npm run size` — the 30 KB gzip gate must still pass.

## Files to create or modify

- `TelemetryGuard.Sdk/src/collectors/pointer.ts` (create)
- `TelemetryGuard.Sdk/src/collectors/scroll.ts` (create)
- `TelemetryGuard.Sdk/src/collectors/keys.ts` (create)
- `TelemetryGuard.Sdk/src/collectors/forms.ts` (create)
- `TelemetryGuard.Sdk/src/collectors/honeypot.ts` (create)
- `TelemetryGuard.Sdk/src/collectors/first-interaction.ts` (create)
- `TelemetryGuard.Sdk/src/collectors/paint.ts` (create)
- `TelemetryGuard.Sdk/src/collectors/index.ts` (create)
- `TelemetryGuard.Sdk/src/index.ts` (modify — call `startCollectors`)
- `TelemetryGuard.Sdk/src/types.ts` (modify — optional: narrow event-type unions)

## Acceptance criteria

- `npm run typecheck && npm run build && npm run size` exit 0.
- Manual devtools check on a fixture page with a form (SDK-06 automates all of this later):
  - moving the mouse produces `pm` batch events whose samples are ≥50 ms apart and ≤200 per envelope;
  - typing produces only `ky` events of shape `{e,t,d}` — searching the raw request payloads for `"key"`, `"code"`, `"keyCode"`, `"charCode"`, `"which"` finds nothing, and no typed character appears anywhere in any payload;
  - every form on the page contains exactly one injected offscreen input named `website` (or the `data-honeypot-name` override) with `aria-hidden="true"` and `tabindex="-1"`; filling it via console emits `hp` events;
  - focusing/blurring a field emits `ff`/`fb` with an 8-hex-char `fh` and a sane `ft`; the raw field name appears nowhere in payloads;
  - pasting into an email field emits `pa` with `fk:'identity'`; pasting into a comment box emits `fk:'other'`;
  - the first click/keystroke/scroll emits exactly one `fi` event;
  - a normal page load emits exactly one `rt` event with an integer `fcp` (and `fp` when the browser reports a `first-paint` entry); reloading does not produce a second one, and a browser/profile without paint timing produces none (no zero-filled placeholder);
  - submitting the form is never blocked or altered by the SDK.
- No event ever contains a field value, raw field name, key identity, or clipboard content — by code inspection **and** payload inspection.
- Page with zero forms: no honeypot events, no errors, other collectors unaffected.

## Testing

- Automated Playwright coverage (including the no-key-identity guardrail and honeypot injection assertions) is SDK-06's deliverable; this task must leave the fixtures testable: stable event names/shapes exactly as tabled in step 8.
- Quick self-check worth scripting locally: serve a page, type a sentinel string (e.g. `XyZZy123`), dump captured `/i` bodies, `grep` must not find the sentinel or any `key`-identity property.

## Out of scope / guardrails

- **NEVER capture key identity or content** — no `key`/`code`/`keyCode`/`charCode`/`which`, no characters, no field values, no clipboard reads. Value **length** is the only permitted value-derived datum (autofill heuristic), and it is never transmitted either — only the boolean-ish `af` event is. This is a §3 compliance line, not a preference.
- **No derived features client-side** (no linearity, no averages/std, no fill-time subtraction) — raw events only; the engine derives (§7). Do not "optimize" by pre-aggregating.
- **Passive only:** never `preventDefault`, never block submits, never alter layout/visible DOM. Honeypot injection is the only DOM write allowed. (The decision-endpoint form-gating flow — which *does* intercept submits, but only on forms the tenant explicitly opts in — is **SDK-07**; keep these collectors independent of it.)
- Missing ≠ zero: emit nothing for what didn't happen; never send zero-filled placeholder events.
- No new runtime dependencies; stay within the 30 KB gzip gate; no framework (D2).
- Fire-and-forget transport semantics are owned by SDK-02 — do not add retries/acking here.
- No fingerprinting or storage access in this task (SDK-04); no checksum work (SDK-05).

/**
 * Timestamp convention (both clocks ship consistently; reconciliation is the
 * server's problem):
 * - every event's `t` is integer ms since `performance.timeOrigin` (see
 *   `nowT()` in util.ts);
 * - the envelope's `sent_at` is epoch ms (`Date.now()`).
 * The server reconstructs absolute event times as
 * `sent_at_receive_side − (performance-now-at-send − t)` approximations.
 */

import type { StorageReport } from './storage-age';

/** Base shape of every SDK event. t = ms since performance.timeOrigin (integer). */
export interface TgEvent {
  e: string;
  t: number;
  [key: string]: unknown;
}

/**
 * Wire envelope for POST /i. Keys are constructed in exactly this order —
 * canonical order matters: SDK-05 checksums the serialized form and API-04
 * verifies it.
 */
export interface Envelope {
  k: string;
  session_id: string;
  sid: string;
  visit_id: string;
  device_id: string;
  seq: number;
  nonce: string;
  sent_at: number;
  // SDK-09 page context. Present so the API keeps working when it is mounted on a
  // different origin than the page, where the server can see neither the landing URL
  // (Referer is trimmed to the origin) nor the cookies (SameSite=Lax). Same-origin the
  // server observes both itself and these are redundant — it prefers what it observed.
  u?: string;                    // location.href, query string included
  r?: string;                    // document.referrer — the TRUE external referrer
  ck?: Record<string, string>;   // cookies readable by script (never HttpOnly ones)
  events: TgEvent[];
}

// ---------------------------------------------------------------------------
// SDK-03 behavioral-collector event vocabulary (raw events only — the risk
// engine derives every feature server-side, §7). API-04 and the SDK-06 zod
// schema mirror these shapes exactly.
// ---------------------------------------------------------------------------

/** One coalesced pointer-move sample: [t, x, y]; t = ms since timeOrigin, x/y rounded client px. */
export type PmSample = [number, number, number];

/** Raw pointer modality: mouse / touch / pen / unknown. */
export type PointerTypeCode = 'm' | 't' | 'p' | 'u';

/** Batched pointer-move samples (≥50 ms apart, ≤200 per flush window). */
export type PmEvent = { e: 'pm'; t: number; s: PmSample[] };
/** Pointerdown; tr = isTrusted, sn = event.timeStamp (ms since navigation start). */
export type PdEvent = { e: 'pd'; t: number; tr: 0 | 1; sn: number; pt: PointerTypeCode };
/** Click; same payload as pd — server compares sn against rt.fcp for click_before_render. */
export type ClEvent = { e: 'cl'; t: number; tr: 0 | 1; sn: number; pt: PointerTypeCode };
/** Scroll position sample (≥100 ms apart, ≤30 per flush window). */
export type ScEvent = { e: 'sc'; t: number; y: number };
/**
 * Timing-only keyboard event — EXACTLY these keys, never key identity (§3
 * compliance line: no key/code/keyCode/charCode/which, no characters).
 */
export type KyEvent = { e: 'ky'; t: number; d: 0 | 1 };
/** Field focus; fh = FNV-1a hash of name|id|autocomplete (raw identity never ships). */
export type FfEvent = { e: 'ff'; t: number; fh: string; ft: string };
/** Field blur. */
export type FbEvent = { e: 'fb'; t: number; fh: string; ft: string };
/** Form submit; fh = FNV-1a hash of form identity. */
export type FsEvent = { e: 'fs'; t: number; fh: string };
/** Paste classification (coarse; clipboard contents are never read). */
export type PaEvent = { e: 'pa'; t: number; fk: 'identity' | 'other' };
/** Autofill heuristic fired for a field (at most once per field per page load). */
export type AfEvent = { e: 'af'; t: number; fh: string };
/** Honeypot interaction — near-deterministic bot evidence (§4). */
export type HpEvent = {
  e: 'hp';
  t: number;
  kind: 'focus' | 'input' | 'submit_filled' | 'link_clicked';
};
/** First trusted interaction marker (once per page load). */
export type FiEvent = { e: 'fi'; t: number; it: 'pointer' | 'key' | 'wheel' | 'touch' | 'scroll' };
/** Render-timing mark (once per page load; fp omitted when unavailable — missing ≠ zero). */
export type RtEvent = { e: 'rt'; t: number; fcp: number; fp?: number };
/** Google Analytics runtime delivery status for this page visit. */
export type GaEvent = { e: 'ga'; t: number; s: 'loaded' | 'blocked' | 'unknown' | 'page_view_sent' | 'page_view_accepted' };

// ---------------------------------------------------------------------------
// SDK-04 fingerprint event — exactly ONE per session (sessionStorage keyed on
// sid). Every field except e/t is optional: a failed source is OMITTED, never
// zeroed (missing ≠ zero; the server maps absence to NaN).
// ---------------------------------------------------------------------------

/**
 * The once-per-session fingerprint event.
 * - `vid`/`conf`: FingerprintJS visitor id + confidence score.
 * - `scr`: [width, height, colorDepth, devicePixelRatio (2 dp)].
 * - `tz`: IANA zone string, or `-getTimezoneOffset()` minutes as a number fallback.
 * - `langs`: up to 5 entries of navigator.languages.
 * - `canvasBlocked`/`touch`/`mob`/`wd`: raw booleans (T3/CTX/T1 raw material).
 * - `botd`: Botd verdict; `kind` present only when `bot` is true.
 * - `storage`: signed first-party storage-age report (see storage-age.ts).
 */
export type FpEvent = {
  e: 'fp';
  t: number;
  vid?: string;
  conf?: number;
  scr?: [number, number, number, number];
  tz?: string | number;
  langs?: string[];
  canvasBlocked?: boolean;
  touch?: boolean;
  mob?: boolean;
  wd?: boolean;
  botd?: { bot: boolean; kind?: string };
  storage?: StorageReport;
};

// ---------------------------------------------------------------------------
// SDK-07 decision flow — client half of API-05's POST /decide contract. The
// client only ever sees the action string: the numeric score, band names and
// rule hits are never in any response (API-05 guarantees), so there is
// nothing here to leak.
// ---------------------------------------------------------------------------

/**
 * Validated /decide response. `turnstileSiteKey` is the Turnstile *site* key
 * (D14) — it arrives only in the challenge response, never from SDK config;
 * the secret key never exists client-side (INT-01).
 */
export type DecideResponse =
  | { action: 'allow' }
  | { action: 'block' }
  | { action: 'challenge'; turnstileSiteKey: string };

/** Union of every event the SDK-03 collectors emit. */
export type CollectorEvent =
  | PmEvent
  | PdEvent
  | ClEvent
  | ScEvent
  | KyEvent
  | FfEvent
  | FbEvent
  | FsEvent
  | PaEvent
  | AfEvent
  | HpEvent
  | FiEvent
  | RtEvent
  | GaEvent;

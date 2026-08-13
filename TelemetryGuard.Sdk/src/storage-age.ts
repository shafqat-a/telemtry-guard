import { state } from './state';

/**
 * Signed first-party storage age (§4). The SDK persists the server-signed
 * `"<storageTs>.<storageSig>"` pair from /i/init in BOTH a cookie (`tg_fp`)
 * and localStorage, and faithfully reports what it finds each visit. The
 * server re-verifies the signature and derives age authoritatively — the SDK
 * never validates or interprets `sig` (it has no key), and it NEVER fabricates
 * timestamps: only server-signed pairs are written, and an existing valid pair
 * is never overwritten (that would destroy the age signal).
 */
export interface StorageReport {
  ck: { present: boolean; ts?: number; sig?: string; ageMs?: number };
  ls: { present: boolean; ts?: number; sig?: string; ageMs?: number };
  fresh: boolean; // true when this visit wrote a new pair
  cookiesDisabled: boolean;
}

const KEY = 'tg_fp';
// 400 days — the Chrome cookie-lifetime cap.
const COOKIE_ATTRS = '; max-age=34560000; path=/; SameSite=Lax';

interface Pair {
  ts: number;
  sig: string;
}

/** A valid entry splits into an integer ts and non-empty sig on the FIRST '.'. */
function parsePair(raw: string | null): Pair | null {
  if (!raw) return null;
  const i = raw.indexOf('.');
  if (i <= 0 || i >= raw.length - 1) return null;
  const ts = Number(raw.slice(0, i));
  const sig = raw.slice(i + 1);
  if (!Number.isInteger(ts) || ts <= 0 || sig === '') return null;
  return { ts, sig };
}

function readCookie(): string | null {
  try {
    const parts = document.cookie.split(';');
    for (let i = 0; i < parts.length; i++) {
      const p = parts[i]!.replace(/^\s+/, '');
      if (p.indexOf(KEY + '=') === 0) return p.slice(KEY.length + 1);
    }
  } catch {
    /* ignore */
  }
  return null;
}

function writeCookie(val: string): void {
  try {
    document.cookie = KEY + '=' + val + COOKIE_ATTRS;
  } catch {
    /* ignore */
  }
}

function readLocal(): string | null {
  try {
    return localStorage.getItem(KEY);
  } catch {
    return null;
  }
}

function writeLocal(val: string): void {
  try {
    localStorage.setItem(KEY, val);
  } catch {
    /* ignore */
  }
}

/** navigator.cookieEnabled === false, OR a write-then-read probe fails. */
function detectCookiesDisabled(): boolean {
  try {
    if (navigator.cookieEnabled === false) return true;
    document.cookie = 'tg_ct=1; path=/; SameSite=Lax';
    const ok = document.cookie.indexOf('tg_ct=1') !== -1;
    document.cookie = 'tg_ct=1; max-age=0; path=/; SameSite=Lax';
    return !ok;
  } catch {
    return true;
  }
}

function fmt(p: Pair): string {
  return String(p.ts) + '.' + p.sig;
}

/**
 * Run only after `state.initSettled` — needs /i/init's outcome. Reports the
 * state observed on entry (before any write this call performs):
 * - both stores valid → report both, touch nothing (even if they differ);
 * - exactly one valid → copy the EXISTING (older) pair into the empty store
 *   (resurrecting age from the surviving store is the point of
 *   double-writing); still `fresh: false`;
 * - both missing/invalid AND /i/init delivered a pair → write the fresh pair
 *   into both stores, `fresh: true`;
 * - both missing and /i/init failed → write nothing (absence is the honest
 *   report; missing ≠ zero).
 */
export function collectStorageAge(): StorageReport {
  const now = Date.now();
  const ckPair = parsePair(readCookie());
  const lsPair = parsePair(readLocal());

  const report: StorageReport = {
    ck: ckPair
      ? { present: true, ts: ckPair.ts, sig: ckPair.sig, ageMs: now - ckPair.ts }
      : { present: false },
    ls: lsPair
      ? { present: true, ts: lsPair.ts, sig: lsPair.sig, ageMs: now - lsPair.ts }
      : { present: false },
    fresh: false,
    cookiesDisabled: detectCookiesDisabled(),
  };

  if (ckPair && !lsPair) {
    writeLocal(fmt(ckPair));
  } else if (lsPair && !ckPair) {
    writeCookie(fmt(lsPair));
  } else if (!ckPair && !lsPair && state.storageTs !== null && state.storageSig !== null) {
    const val = String(state.storageTs) + '.' + state.storageSig;
    writeCookie(val);
    writeLocal(val);
    report.fresh = true;
  }
  return report;
}

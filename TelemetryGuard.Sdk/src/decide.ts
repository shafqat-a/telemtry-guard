/**
 * SDK-07 — the /decide API round-trip (client half of API-05's contract).
 *
 * `POST {endpoint}/decide?k={siteKey}`, body UTF-8 JSON `{sid[, turnstileToken]}`
 * sent with Content-Type: text/plain (preflight-free CORS simple request).
 * API-05 serves Access-Control-Allow-Origin: * on this route, so the SDK can
 * read the response — unlike /i.
 *
 * Only `action` and `turnstileSiteKey` are ever read from the response —
 * scores, bands and rule hits live server-side and never reach the client
 * (§6.3; API-05 guarantees they are not in any response).
 */
import type { DecideResponse } from './types';
import { state } from './state';

export type { DecideResponse } from './types';

const TIMEOUT_MS = 4000;

/** Shape-validate the parsed response; anything unexpected → null (fail open). */
function validate(v: unknown): DecideResponse | null {
  if (typeof v !== 'object' || v === null) return null;
  const action = (v as Record<string, unknown>)['action'];
  if (action === 'allow') return { action: 'allow' };
  if (action === 'block') return { action: 'block' };
  if (action === 'challenge') {
    const sk = (v as Record<string, unknown>)['turnstileSiteKey'];
    if (typeof sk === 'string' && sk.length > 0) {
      return { action: 'challenge', turnstileSiteKey: sk };
    }
  }
  return null;
}

/** Resolves null on network failure / timeout / non-200 / bad JSON — caller fails open. */
export async function decide(turnstileToken?: string): Promise<DecideResponse | null> {
  try {
    const ctrl = new AbortController();
    const timer = window.setTimeout(() => {
      try {
        ctrl.abort();
      } catch {
        /* ignore */
      }
    }, TIMEOUT_MS);
    try {
      const body: { sid: string; turnstileToken?: string } = { sid: state.sid };
      if (turnstileToken !== undefined) body.turnstileToken = turnstileToken;
      const res = await fetch(
        state.cfg.endpoint + '/decide?k=' + encodeURIComponent(state.cfg.siteKey),
        {
          method: 'POST',
          body: JSON.stringify(body),
          headers: { 'Content-Type': 'text/plain' },
          credentials: 'omit',
          signal: ctrl.signal,
        }
      );
      if (res.status !== 200) return null;
      return validate((await res.json()) as unknown);
    } finally {
      clearTimeout(timer);
    }
  } catch {
    // No retries — one call per user action; availability failures fail open.
    return null;
  }
}

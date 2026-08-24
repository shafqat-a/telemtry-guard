const SID_PATTERN = /^[A-Za-z0-9_-]{8,64}$/;

/** v4 UUID without crypto.randomUUID (older WebViews). */
function uuidFromRandomValues(): string {
  const b = new Uint8Array(16);
  crypto.getRandomValues(b);
  b[6] = (b[6]! & 0x0f) | 0x40; // version 4
  b[8] = (b[8]! & 0x3f) | 0x80; // variant 10
  let hex = '';
  for (let i = 0; i < 16; i++) {
    hex += (b[i]! + 0x100).toString(16).slice(1);
  }
  return (
    hex.slice(0, 8) +
    '-' +
    hex.slice(8, 12) +
    '-' +
    hex.slice(12, 16) +
    '-' +
    hex.slice(16, 20) +
    '-' +
    hex.slice(20)
  );
}

function generateUuid(): string {
  try {
    if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  } catch {
    /* fall through to manual v4 */
  }
  return uuidFromRandomValues();
}

/**
 * Session-ID resolution, priority order fixed by the design:
 * 1. `tg_sid` URL param (set by the API-02 tracker redirect), validated;
 * 2. sessionStorage;
 * 3. fresh v4 UUID.
 * Never rewrites location.href to strip `tg_sid` — the SDK never mutates
 * host-page state.
 */
export function resolveSid(): string {
  let sid: string | null = null;

  try {
    const fromUrl = new URLSearchParams(location.search).get('tg_sid');
    if (fromUrl && SID_PATTERN.test(fromUrl)) sid = fromUrl;
  } catch {
    /* ignore */
  }

  if (!sid) {
    try {
      sid = sessionStorage.getItem('tg_sid');
    } catch {
      /* privacy modes may throw */
    }
  }

  if (!sid) sid = generateUuid();

  try {
    sessionStorage.setItem('tg_sid', sid);
  } catch {
    /* privacy modes may throw */
  }

  return sid;
}

const TRACKED_VISIT_KEY = 'tg_vid_used';

/**
 * A fresh identifier for this document load; never persisted as the visit id itself.
 *
 * A tracker redirect already minted a unique request id (`tg_sid`); the FIRST
 * document load that carries it adopts it, which keeps the paid-click Redis
 * context and the landing-page visit on one identity. It is adopted at most once
 * per tab: the server scopes the envelope sequence counter and the /i/init nonce
 * per visit id, so a reload (F5) of the tagged landing URL must NOT reuse it —
 * `seq` restarts at 0 on every document load and a reused id would read as a
 * replay of the first visit. The reload gets a fresh id; `session_id` (resolveSid)
 * still links it to the same session.
 */
export function createVisitId(): string {
  try {
    const tracked = new URLSearchParams(location.search).get('tg_sid');
    if (tracked && SID_PATTERN.test(tracked)) {
      let used: string | null = null;
      try {
        used = sessionStorage.getItem(TRACKED_VISIT_KEY);
      } catch {
        /* privacy modes may throw — fall through and adopt (single-page safe) */
      }
      if (used !== tracked) {
        try {
          sessionStorage.setItem(TRACKED_VISIT_KEY, tracked);
        } catch {
          /* ignore */
        }
        return tracked;
      }
    }
  } catch {
    /* ignore */
  }
  return generateUuid();
}

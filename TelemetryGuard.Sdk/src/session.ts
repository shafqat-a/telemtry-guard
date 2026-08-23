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

/** A fresh identifier for this document load; intentionally never persisted. */
export function createVisitId(): string {
  // A tracker redirect already minted a unique request id; retaining it keeps
  // paid-click Redis context and the first page visit on the same identity.
  try {
    const tracked = new URLSearchParams(location.search).get('tg_sid');
    if (tracked && SID_PATTERN.test(tracked)) return tracked;
  } catch {
    /* ignore */
  }
  return generateUuid();
}

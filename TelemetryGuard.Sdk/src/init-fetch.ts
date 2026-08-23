import { state } from './state';
import { notifyInitSettled } from './transport';

const INIT_TIMEOUT_MS = 3000;

function settle(): void {
  state.initSettled = true;
  try {
    notifyInitSettled();
  } catch {
    /* ignore */
  }
}

/**
 * One-time bootstrap fetch. Wire contract (API-04 implements this):
 *
 *   GET {endpoint}/i/init?k={siteKey}&sid={sid}
 *   -> 200 application/json
 *   { "nonce": "<opaque, unique per sid>",
 *     "storageTs": <server epoch ms>,
 *     "storageSig": "<opaque signature over storageTs, server-keyed>" }
 *
 * Necessary because sendBeacon cannot read responses; all later traffic is
 * one-way. On any failure/timeout/non-200/bad JSON the nonce stays '' and the
 * storage fields stay null — never retried; the server treats a missing nonce
 * as an integrity signal.
 */
export function fetchInit(): Promise<void> {
  try {
    const ctl = new AbortController();
    const timeoutId = window.setTimeout(() => {
      try {
        ctl.abort();
      } catch {
        /* ignore */
      }
    }, INIT_TIMEOUT_MS);
    const url =
      state.cfg.endpoint +
      '/i/init?k=' +
      encodeURIComponent(state.cfg.siteKey) +
      '&sid=' +
      encodeURIComponent(state.visitId);
    return fetch(url, {
      method: 'GET',
      credentials: 'omit',
      cache: 'no-store',
      signal: ctl.signal,
    })
      .then((res) => (res.status === 200 ? res.json() : null))
      .then((body: unknown) => {
        if (body && typeof body === 'object') {
          const b = body as Record<string, unknown>;
          if (typeof b['nonce'] === 'string') state.nonce = b['nonce'];
          if (typeof b['storageTs'] === 'number') state.storageTs = b['storageTs'];
          if (typeof b['storageSig'] === 'string') state.storageSig = b['storageSig'];
        }
      })
      .catch(() => {
        /* never retry */
      })
      .then(() => {
        clearTimeout(timeoutId);
        settle();
      });
  } catch {
    settle();
    return Promise.resolve();
  }
}

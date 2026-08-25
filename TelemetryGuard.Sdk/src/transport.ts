import type { Envelope, TgEvent } from './types';
import { state } from './state';
import { seal } from './integrity';
import { pageUrl, pageReferrer, readableCookies } from './page-context';

const MAX_QUEUE = 10; // events per envelope before an immediate flush
const FLUSH_DELAY_MS = 2000;

const queue: TgEvent[] = [];
const drains: Array<() => TgEvent[]> = [];
let timer: number | null = null;
let pendingReason: 'count' | 'timer' | null = null; // flush held until init settles

export function enqueue(ev: TgEvent): void {
  try {
    queue.push(ev);
    if (queue.length >= MAX_QUEUE) {
      flush('count');
      return;
    }
    if (timer === null) {
      timer = window.setTimeout(() => {
        timer = null;
        flush('timer');
      }, FLUSH_DELAY_MS);
    }
  } catch {
    /* never throw on the host page */
  }
}

/**
 * Collectors with internal buffers/caps register a drain; called at every
 * flush. Return events to append; also use it to reset per-flush counters.
 */
export function registerDrain(drain: () => TgEvent[]): void {
  drains.push(drain);
}

export function flush(reason: 'count' | 'timer' | 'hidden' | 'pagehide'): void {
  try {
    if (timer !== null) {
      clearTimeout(timer);
      timer = null;
    }
    // Hold-until-init: 'hidden'/'pagehide' ship immediately regardless (last
    // chance), with whatever nonce is present (possibly '').
    if (!state.initSettled && (reason === 'count' || reason === 'timer')) {
      pendingReason = reason;
      return;
    }
    pendingReason = null;

    const events = queue.splice(0, queue.length);
    for (let i = 0; i < drains.length; i++) {
      try {
        const drained = drains[i]!();
        if (drained && drained.length > 0) {
          for (let j = 0; j < drained.length; j++) events.push(drained[j]!);
        }
      } catch {
        /* a broken drain must not kill the flush */
      }
    }
    // No empty envelopes; has_js_beacon must mean real telemetry.
    if (events.length === 0) return;

    // Canonical key order — SDK-05 checksums the serialized form; API-04 verifies.
    const env: Envelope = {
      k: state.cfg.siteKey,
      session_id: state.sid,
      sid: state.visitId,
      visit_id: state.visitId,
      device_id: state.deviceId,
      seq: state.seq,
      nonce: state.nonce,
      sent_at: Date.now(),
      // SDK-09: what the server cannot observe cross-origin. Collected at flush time,
      // so the URL is the page the batch is being flushed from.
      u: pageUrl(),
      r: pageReferrer(),
      ck: readableCookies(),
      events,
    };
    send(env);
  } catch {
    /* never throw on the host page */
  }
}

function send(env: Envelope): void {
  try {
    const url = state.cfg.endpoint + '/i';
    // text/plain keeps the request a CORS "simple request" (no preflight).
    const blob = new Blob([seal(env)], { type: 'text/plain' });
    let accepted = false;
    try {
      if (typeof navigator.sendBeacon === 'function') {
        accepted = navigator.sendBeacon(url, blob);
      }
    } catch {
      accepted = false;
    }
    if (!accepted) {
      try {
        void fetch(url, {
          method: 'POST',
          body: blob,
          keepalive: true,
          credentials: 'omit',
        }).catch(() => {
          /* fire-and-forget: dropped envelopes surface as seq gaps by design */
        });
      } catch {
        /* ignore */
      }
    }
  } finally {
    // Increment only after a send attempt (successful or not).
    // No retries, no persistence, no re-queue.
    state.seq++;
  }
}

/** Called by init-fetch when /i/init settles: releases a held flush. */
export function notifyInitSettled(): void {
  try {
    if (pendingReason !== null) {
      const reason = pendingReason;
      pendingReason = null;
      flush(reason);
    }
  } catch {
    /* ignore */
  }
}

/** Installs listeners; flush-on-hide is the last chance to ship. */
export function startTransport(): void {
  try {
    document.addEventListener(
      'visibilitychange',
      () => {
        try {
          if (document.visibilityState === 'hidden') flush('hidden');
        } catch {
          /* ignore */
        }
      },
      { passive: true }
    );
    window.addEventListener(
      'pagehide',
      () => {
        try {
          flush('pagehide');
        } catch {
          /* ignore */
        }
      },
      { passive: true }
    );
  } catch {
    /* never throw on the host page */
  }
}

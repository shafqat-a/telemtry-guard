import * as FingerprintJS from '@fingerprintjs/fingerprintjs';
import { load as loadBotd } from '@fingerprintjs/botd';
import { state } from './state';
import { enqueue } from './transport';
import { nowT } from './util';
import { collectStorageAge } from './storage-age';
import type { FpEvent } from './types';

/**
 * SDK-04: FingerprintJS + Botd collection and the once-per-session `fp` event.
 * The SDK ships raw material only (components, flags, stored timestamps) —
 * the risk engine derives features and applies tiering server-side (§7).
 * Every sub-collection is individually try/caught: a failed source means the
 * field is OMITTED, never guessed (missing ≠ zero; server maps absence to NaN).
 */

const SENT_KEY = 'tg_fp_sent';
const IDLE_TIMEOUT_MS = 2000;
const FALLBACK_DELAY_MS = 500;
const INIT_WAIT_MAX_MS = 4000;
const INIT_POLL_MS = 100;

/** One fingerprint event per page visit. */
function alreadySentThisSession(): boolean {
  try {
    return sessionStorage.getItem(SENT_KEY) === state.visitId;
  } catch {
    return false;
  }
}

function markSent(): void {
  try {
    sessionStorage.setItem(SENT_KEY, state.visitId);
  } catch {
    /* storage may throw (privacy modes) */
  }
}

/**
 * Storage-age needs /i/init's outcome. Idle scheduling means init has almost
 * always settled already; otherwise poll briefly (max 4 s) and then proceed
 * with whatever is settled — absence stays absent.
 */
function waitForInit(): Promise<void> {
  return new Promise((resolve) => {
    const deadline = Date.now() + INIT_WAIT_MAX_MS;
    const check = (): void => {
      if (state.initSettled || Date.now() >= deadline) {
        resolve();
        return;
      }
      setTimeout(check, INIT_POLL_MS);
    };
    check();
  });
}

async function collect(): Promise<void> {
  try {
    if (alreadySentThisSession()) return;

    const ev: FpEvent = { e: 'fp', t: nowT() };

    // FingerprintJS: visitor id, confidence, components.
    let components: FingerprintJS.BuiltinComponents | null = null;
    try {
      const fp = await FingerprintJS.load();
      const res = await fp.get();
      ev.vid = res.visitorId;
      ev.conf = res.confidence.score;
      components = res.components;
    } catch {
      /* omit vid/conf/canvasBlocked; still send the rest */
    }

    try {
      ev.scr = [
        screen.width,
        screen.height,
        screen.colorDepth,
        Math.round(devicePixelRatio * 100) / 100,
      ];
    } catch {
      /* omit */
    }
    try {
      const tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
      ev.tz = tz ? tz : -new Date().getTimezoneOffset();
    } catch {
      try {
        ev.tz = -new Date().getTimezoneOffset();
      } catch {
        /* omit */
      }
    }
    try {
      ev.langs =
        navigator.languages && navigator.languages.length > 0
          ? navigator.languages.slice(0, 5)
          : [navigator.language];
    } catch {
      /* omit */
    }
    if (components) {
      try {
        // True when the canvas component failed or is empty. Brave/Firefox
        // privacy modes trigger this by design — server treats it as weak T3.
        const c = components.canvas as { error?: unknown; value?: unknown } | undefined;
        ev.canvasBlocked = 'error' in (c ?? {}) || !(c && c.value);
      } catch {
        /* omit */
      }
    }
    try {
      ev.touch = navigator.maxTouchPoints > 0;
    } catch {
      /* omit */
    }
    try {
      // Coarse is_mobile raw material only — no UA parsing beyond this boolean.
      ev.mob = /Mobi|Android/i.test(navigator.userAgent);
    } catch {
      /* omit */
    }

    // Classic T1 webdriver_flag — cheap, independent of library outcomes.
    try {
      ev.wd = navigator.webdriver === true;
    } catch {
      /* omit */
    }

    // Botd automation flags (statically bundled per the SDK-01 README decision).
    try {
      const botd = await loadBotd();
      const r = botd.detect();
      ev.botd = r.bot ? { bot: true, kind: r.botKind } : { bot: false };
    } catch {
      /* omit botd; wd already captured above */
    }

    // Signed first-party storage age — only meaningful after init settled.
    await waitForInit();
    try {
      ev.storage = collectStorageAge();
    } catch {
      /* omit */
    }

    // Normal batching applies; if the page is already hidden, the
    // pagehide/hidden flush picks this up.
    enqueue(ev);
    markSent();
  } catch {
    /* never throw on the host page */
  }
}

/**
 * Schedules collection at idle — fingerprinting must not compete with page
 * load or the first envelope.
 */
export function startFingerprint(): void {
  try {
    const cb = (): void => {
      void collect();
    };
    if (typeof requestIdleCallback === 'function') {
      requestIdleCallback(cb, { timeout: IDLE_TIMEOUT_MS });
    } else {
      setTimeout(cb, FALLBACK_DELAY_MS);
    }
  } catch {
    /* never throw on the host page */
  }
}

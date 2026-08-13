import type { KyEvent } from '../types';
import { enqueue, registerDrain } from '../transport';
import { nowT } from '../util';

const CAP_PER_FLUSH = 150; // ky events per flush window

let count = 0;

/**
 * Module-scope registry sharing per-focused-field keydown counts with the
 * forms collector (autofill heuristic). Only the event target is handed over —
 * never key identity.
 */
let fieldKeydownListener: ((target: EventTarget | null) => void) | null = null;

export function registerFieldKeydownListener(
  fn: (target: EventTarget | null) => void
): void {
  fieldKeydownListener = fn;
}

/**
 * Timing only (§3 compliance line): this handler must never touch `ev.key`,
 * `ev.code`, `ev.keyCode`, `ev.charCode`, `ev.which`, or any input value. A ky
 * event has EXACTLY the keys {e, t, d} — the SDK-06 guardrail test asserts
 * this shape.
 */
function onKey(ev: KeyboardEvent): void {
  try {
    if (!ev.isTrusted) return;
    const down = ev.type === 'keydown';
    if (down && fieldKeydownListener) {
      try {
        fieldKeydownListener(ev.target); // field counter is not subject to the ky cap
      } catch {
        /* ignore */
      }
    }
    if (count >= CAP_PER_FLUSH) return;
    count++;
    const ky: KyEvent = { e: 'ky', t: nowT(), d: down ? 1 : 0 };
    enqueue(ky);
  } catch {
    /* never throw on the host page */
  }
}

export function installKeys(): void {
  try {
    document.addEventListener('keydown', onKey as EventListener, {
      passive: true,
      capture: true,
    });
    document.addEventListener('keyup', onKey as EventListener, {
      passive: true,
      capture: true,
    });
    // Drain contributes no events — it only resets the per-flush cap.
    registerDrain(() => {
      count = 0;
      return [];
    });
  } catch {
    /* never throw on the host page */
  }
}

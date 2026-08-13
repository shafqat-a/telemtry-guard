import type { ScEvent, TgEvent } from '../types';
import { registerDrain } from '../transport';
import { nowT } from '../util';

const MIN_GAP_MS = 100; // coalescing: at most one position per 100 ms
const CAP_PER_FLUSH = 30;

let buffer: ScEvent[] = [];
let lastT = -1e9;

function onScroll(ev: Event): void {
  try {
    if (!ev.isTrusted) return;
    const t = nowT();
    if (t - lastT < MIN_GAP_MS) return;
    if (buffer.length >= CAP_PER_FLUSH) return;
    lastT = t;
    buffer.push({ e: 'sc', t, y: Math.round(window.scrollY) });
  } catch {
    /* never throw on the host page */
  }
}

/** Drain: return buffered positions as individual sc events, reset buffer + cap. */
function drainScroll(): TgEvent[] {
  if (buffer.length === 0) return [];
  const out = buffer;
  buffer = [];
  return out;
}

export function installScroll(): void {
  try {
    window.addEventListener('scroll', onScroll, { passive: true, capture: true });
    registerDrain(drainScroll);
  } catch {
    /* never throw on the host page */
  }
}

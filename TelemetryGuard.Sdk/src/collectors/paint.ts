import type { RtEvent } from '../types';
import { enqueue } from '../transport';
import { nowT } from '../util';

/**
 * Render-timing mark — the missing half of click_before_render. Emits AT MOST
 * once per page load as soon as first-contentful-paint is available; browsers
 * without paint timing emit nothing (missing ≠ zero; the server maps absence
 * to NaN). `startTime` is ms since performance.timeOrigin — the same clock as
 * cl/pd's `sn` — so the server compares the two directly (API-04/RSK-04); the
 * SDK never computes the comparison itself (§7 raw-events rule).
 */

let emitted = false;
let observer: PerformanceObserver | null = null;

function tryEmit(): void {
  if (emitted) return;
  const entries = performance.getEntriesByType('paint');
  let fcp: PerformanceEntry | undefined;
  let fp: PerformanceEntry | undefined;
  for (let i = 0; i < entries.length; i++) {
    const en = entries[i]!;
    if (en.name === 'first-contentful-paint') fcp = en;
    else if (en.name === 'first-paint') fp = en;
  }
  if (!fcp) return;
  emitted = true;
  const rt: RtEvent = { e: 'rt', t: nowT(), fcp: Math.round(fcp.startTime) };
  if (fp) rt.fp = Math.round(fp.startTime); // omitted when unavailable
  enqueue(rt);
  if (observer) {
    try {
      observer.disconnect();
    } catch {
      /* ignore */
    }
    observer = null;
  }
}

export function installPaint(): void {
  try {
    if (
      typeof performance === 'undefined' ||
      typeof performance.getEntriesByType !== 'function'
    ) {
      return;
    }
    tryEmit(); // an fcp entry may already exist by install time
    if (emitted) return;
    if (typeof PerformanceObserver !== 'function') return;
    observer = new PerformanceObserver(() => {
      try {
        tryEmit();
      } catch {
        /* ignore */
      }
    });
    // Throws on browsers without the 'paint' entry type → caught → no event.
    observer.observe({ type: 'paint', buffered: true });
  } catch {
    observer = null; /* no paint timing → emit nothing */
  }
}

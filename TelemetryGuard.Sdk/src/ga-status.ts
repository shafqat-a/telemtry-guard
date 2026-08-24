import { enqueue } from './transport';
import { nowT } from './util';

export type GaStatus = 'loaded' | 'blocked' | 'unknown';

const LOAD_TIMEOUT_MS = 5000;
const POLL_MS = 250;

function gaScripts(): HTMLScriptElement[] {
  return Array.from(document.scripts).filter((script) => {
    const src = script.src || '';
    return src.includes('googletagmanager.com/gtag/js')
      || src.includes('google-analytics.com/analytics.js');
  });
}

function hasLoadedGa(): boolean {
  try {
    const w = window as Window & { google_tag_manager?: unknown };
    if (w.google_tag_manager && typeof w.google_tag_manager === 'object') return true;
    return (performance.getEntriesByType('resource') as PerformanceResourceTiming[]).some((entry) =>
      (entry.name.includes('googletagmanager.com/gtag/js')
        || entry.name.includes('google-analytics.com/analytics.js'))
      && entry.responseEnd > 0);
  } catch {
    return false;
  }
}

/**
 * Reports whether the page's configured GA runtime actually loaded. Merely finding
 * window.gtag is insufficient: the standard inline stub exists even when an ad blocker
 * prevents gtag.js from loading. A script error or five-second timeout is "blocked";
 * pages with no GA script are "unknown" rather than a false failure.
 */
export function startGaStatus(): void {
  try {
    const scripts = gaScripts();
    if (scripts.length === 0) {
      enqueue({ e: 'ga', t: nowT(), s: 'unknown' });
      return;
    }

    let reported = false;
    let poll: number | undefined;
    let timeout: number | undefined;
    const report = (status: GaStatus): void => {
      if (reported) return;
      reported = true;
      if (poll !== undefined) clearInterval(poll);
      if (timeout !== undefined) clearTimeout(timeout);
      enqueue({ e: 'ga', t: nowT(), s: status });
    };

    if (hasLoadedGa()) {
      report('loaded');
      return;
    }

    for (const script of scripts) {
      script.addEventListener('load', () => report('loaded'), { once: true });
      script.addEventListener('error', () => report('blocked'), { once: true });
    }
    poll = window.setInterval(() => {
      if (hasLoadedGa()) report('loaded');
    }, POLL_MS);
    timeout = window.setTimeout(() => report(hasLoadedGa() ? 'loaded' : 'blocked'), LOAD_TIMEOUT_MS);

    // Installed before transport's pagehide handler: record the best-known state in
    // the final envelope even when a fast bounce happens before the timeout.
    window.addEventListener('pagehide', () => report(hasLoadedGa() ? 'loaded' : 'blocked'), { once: true });
  } catch {
    enqueue({ e: 'ga', t: nowT(), s: 'unknown' });
  }
}

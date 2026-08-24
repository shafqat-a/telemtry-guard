import { enqueue } from './transport';
import { nowT } from './util';

export type GaStatus = 'loaded' | 'blocked' | 'unknown' | 'page_view_sent' | 'page_view_accepted';

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

function isGaPageView(entry: PerformanceResourceTiming): boolean {
  try {
    const url = new URL(entry.name);
    const gaHost = url.hostname === 'www.google-analytics.com'
      || url.hostname === 'google-analytics.com'
      || url.hostname.endsWith('.google-analytics.com');
    if (!gaHost || !url.pathname.endsWith('/g/collect')) return false;
    return url.searchParams.get('en') === 'page_view';
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

    let current: GaStatus | undefined;
    let poll: number | undefined;
    let timeout: number | undefined;
    let observer: PerformanceObserver | undefined;
    const rank: Record<GaStatus, number> = {
      unknown: 0, blocked: 1, loaded: 2, page_view_sent: 3, page_view_accepted: 4,
    };
    const report = (status: GaStatus): void => {
      if (current !== undefined && rank[status] <= rank[current]) return;
      current = status;
      enqueue({ e: 'ga', t: nowT(), s: status });
      if (status === 'page_view_accepted') {
        if (poll !== undefined) clearInterval(poll);
        if (timeout !== undefined) clearTimeout(timeout);
        observer?.disconnect();
      }
    };

    const observeCollect = (entries: readonly PerformanceEntry[]): void => {
      for (const item of entries) {
        const entry = item as PerformanceResourceTiming;
        if (!isGaPageView(entry)) continue;
        report('page_view_sent');
        // Cross-origin Resource Timing does not expose the HTTP status. A completed
        // responseEnd proves network delivery only; GA report processing remains async.
        if (entry.responseEnd > 0) report('page_view_accepted');
      }
    };
    try {
      observer = new PerformanceObserver((list) => observeCollect(list.getEntries()));
      observer.observe({ type: 'resource', buffered: true });
      observeCollect(performance.getEntriesByType('resource'));
    } catch {
      observer = undefined;
    }

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
    window.addEventListener('pagehide', () => {
      if (current !== 'page_view_accepted') report(hasLoadedGa() ? 'loaded' : 'blocked');
    }, { once: true });
  } catch {
    enqueue({ e: 'ga', t: nowT(), s: 'unknown' });
  }
}

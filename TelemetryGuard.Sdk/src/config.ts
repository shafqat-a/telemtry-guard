export interface TgConfig {
  siteKey: string;
  endpoint: string; // origin+base for /i and /i/init, no trailing slash
  honeypotName: string; // consumed by SDK-03; parsed here
  /**
   * SDK-07 gating opt-in: CSS selector from the script tag's
   * `data-gate-forms` attribute (evaluated at submit time so late-added forms
   * work), or null when absent. Forms may also opt in individually via a
   * `data-tg-gate` attribute. With neither present the gate module is inert
   * and the SDK stays fully passive.
   */
  gateForms: string | null;
}

/** Finds the SDK's own <script> element. */
function findScript(): HTMLScriptElement | null {
  const cur = document.currentScript;
  if (cur instanceof HTMLScriptElement && cur.dataset.siteKey) return cur;
  const tagged = document.querySelectorAll<HTMLScriptElement>(
    'script[data-site-key][src*="tg"]'
  );
  if (tagged.length > 0) return tagged[tagged.length - 1]!;
  const any = document.querySelectorAll<HTMLScriptElement>('script[data-site-key]');
  if (any.length > 0) return any[any.length - 1]!;
  return null;
}

/** Returns null when config is unusable (SDK then stays inert). */
export function readConfig(): TgConfig | null {
  const script = findScript();
  if (!script) return null;

  const siteKey = script.dataset.siteKey;
  if (!siteKey) return null; // silent no-op; never log loudly on tenant pages

  let endpoint: string;
  const dataEndpoint = script.dataset.endpoint;
  if (dataEndpoint) {
    endpoint = dataEndpoint.replace(/\/+$/, '');
  } else {
    const src = script.src;
    if (!src) return null; // inline script and no data-endpoint: unusable
    try {
      endpoint = new URL(src, location.href).origin;
    } catch {
      return null;
    }
  }
  if (!endpoint) return null;

  return {
    siteKey,
    endpoint,
    honeypotName: script.dataset.honeypotName ?? 'website',
    gateForms: script.dataset.gateForms ?? null,
  };
}

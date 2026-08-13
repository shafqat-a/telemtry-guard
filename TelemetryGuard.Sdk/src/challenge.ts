/**
 * SDK-07 — lazy Cloudflare Turnstile (D14). The widget script is loaded ONLY
 * when a `challenge` decision arrives — never bundled (most sessions never
 * see a challenge, and D2's 30 KB gate budgets our code, not Cloudflare's).
 * Only the *site* key ever exists client-side, and it comes from the
 * challenge response — never from SDK config; the secret key is INT-01's,
 * server-side only.
 *
 * Tenants gating forms must allow challenges.cloudflare.com in their CSP
 * (see README). When the script cannot load, render errors, or the token
 * expires, this resolves null and the caller FAILS OPEN.
 */

const TURNSTILE_SRC =
  'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';
const LOAD_DEADLINE_MS = 5000;
const POLL_MS = 100;

interface TurnstileApi {
  render(
    container: HTMLElement,
    params: {
      sitekey: string;
      callback: (token: string) => void;
      'error-callback': () => void;
      'expired-callback': () => void;
    }
  ): unknown;
}

function api(): TurnstileApi | null {
  try {
    const ts = (window as unknown as { turnstile?: TurnstileApi }).turnstile;
    return ts && typeof ts.render === 'function' ? ts : null;
  } catch {
    return null;
  }
}

let injected = false; // the script tag is added at most once per page

/** Injects the widget script (once) and polls for window.turnstile; null on the 5 s deadline. */
function loadTurnstile(): Promise<TurnstileApi | null> {
  return new Promise((resolve) => {
    try {
      const now = api();
      if (now) {
        resolve(now);
        return;
      }
      if (!injected) {
        injected = true;
        const s = document.createElement('script');
        s.src = TURNSTILE_SRC;
        s.async = true;
        (document.head || document.documentElement).appendChild(s);
      }
      const deadline = Date.now() + LOAD_DEADLINE_MS;
      const timer = window.setInterval(() => {
        try {
          const ts = api();
          if (ts) {
            clearInterval(timer);
            resolve(ts);
            return;
          }
          if (Date.now() >= deadline) {
            clearInterval(timer);
            resolve(null); // fail open: tenant CSP may block Cloudflare
          }
        } catch {
          clearInterval(timer);
          resolve(null);
        }
      }, POLL_MS);
    } catch {
      resolve(null);
    }
  });
}

/** Widget placement anchor: the form's submit control, if any. */
function findSubmitter(form: HTMLFormElement): Element | null {
  try {
    return form.querySelector(
      'button[type="submit"], input[type="submit"], button:not([type])'
    );
  } catch {
    return null;
  }
}

/**
 * Renders the widget inside the form; resolves the token, or null on load
 * failure / error / expiry — caller fails open. The container is removed
 * once the flow completes either way.
 */
export async function runChallenge(
  form: HTMLFormElement,
  siteKey: string
): Promise<string | null> {
  try {
    const ts = await loadTurnstile();
    if (!ts) return null;

    const container = document.createElement('div');
    const submitter = findSubmitter(form);
    if (submitter && submitter.parentNode) {
      submitter.parentNode.insertBefore(container, submitter);
    } else {
      form.appendChild(container);
    }

    return await new Promise<string | null>((resolve) => {
      let settled = false;
      const done = (token: string | null): void => {
        if (settled) return;
        settled = true;
        try {
          container.remove();
        } catch {
          /* ignore */
        }
        resolve(token);
      };
      try {
        ts.render(container, {
          sitekey: siteKey,
          callback: (token: string) =>
            done(typeof token === 'string' && token.length > 0 ? token : null),
          'error-callback': () => done(null),
          'expired-callback': () => done(null),
        });
      } catch {
        done(null);
      }
    });
  } catch {
    return null;
  }
}

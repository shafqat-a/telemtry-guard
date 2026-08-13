/**
 * SDK-07 — form gating (spec §6.2): the client half of the lead-form
 * decision flow. On submit of a tenant-OPTED-IN form the SDK holds the
 * submission, POSTs /decide with the session id, and acts on the response:
 * allow releases the submit, block keeps it held, challenge runs the lazy
 * Turnstile round-trip (challenge.ts) and releases or holds per the final
 * answer.
 *
 * preventDefault here is the SINGLE sanctioned exception to SDK-03's passive
 * rule, and applies only to opted-in forms (`data-tg-gate` attribute, or the
 * script tag's `data-gate-forms` selector — evaluated at submit time so
 * late-added forms work). Ungated forms are never touched.
 *
 * Availability over enforcement: every availability failure (decide
 * unreachable/slow, Turnstile load failure) FAILS OPEN — an outage in fraud
 * scoring must never cost tenants their leads. Abandoned or failed-open
 * sessions are still scored server-side by the API-06 grace-period worker.
 * Fail-closed is only ever an explicit block decision from the server.
 */
import type { TgConfig } from './config';
import { decide } from './decide';
import { runChallenge } from './challenge';

type GateState = 'idle' | 'pending' | 'approved' | 'blocked';

const formState = new WeakMap<HTMLFormElement, GateState>();
let started = false; // module-scope guard against double install
let gateCfg: TgConfig | null = null;

function isGated(form: HTMLFormElement): boolean {
  try {
    if (form.hasAttribute('data-tg-gate')) return true;
    const sel = gateCfg && gateCfg.gateForms;
    if (sel) {
      try {
        return form.matches(sel);
      } catch {
        return false; // invalid tenant selector: never gate
      }
    }
    return false;
  } catch {
    return false;
  }
}

/** allow / fail-open: re-fire the submit; the 'approved' state lets it through. */
function release(form: HTMLFormElement, submitter: HTMLElement | null): void {
  formState.set(form, 'approved');
  try {
    if (typeof form.requestSubmit === 'function') {
      // requestSubmit re-runs native validation and other submit handlers.
      if (submitter) {
        try {
          form.requestSubmit(submitter);
          return;
        } catch {
          /* submitter no longer valid — fall through */
        }
      }
      form.requestSubmit();
      return;
    }
    // Ancient fallback: skips validation/handlers but still delivers the lead.
    form.submit();
  } catch {
    /* never throw on the host page */
  }
}

/** Explicit server block: keep the submission held. */
function hold(form: HTMLFormElement): void {
  formState.set(form, 'blocked');
  try {
    // The page may message the user off this event; the SDK itself renders no
    // text and never says "fraud". Detail carries ONLY the action string —
    // no score/band/rule ever exists client-side to leak (§6.3).
    form.dispatchEvent(
      new CustomEvent('tg:decision', { bubbles: true, detail: { action: 'block' } })
    );
  } catch {
    /* ignore */
  }
}

async function runFlow(
  form: HTMLFormElement,
  submitter: HTMLElement | null
): Promise<void> {
  const first = await decide();
  if (first === null) {
    release(form, submitter); // fail open
    return;
  }
  if (first.action === 'allow') {
    release(form, submitter);
    return;
  }
  if (first.action === 'block') {
    hold(form);
    return;
  }
  // challenge: lazy Turnstile round-trip. The site key comes ONLY from the
  // challenge response (D14), never from SDK config.
  const token = await runChallenge(form, first.turnstileSiteKey);
  if (token === null) {
    release(form, submitter); // fail open: widget blocked / errored / expired
    return;
  }
  const second = await decide(token);
  if (second === null) {
    release(form, submitter); // fail open
    return;
  }
  if (second.action === 'block') {
    hold(form);
    return;
  }
  // 'allow' — or a contract-violating second 'challenge', which must never
  // loop (API-05 guarantees the token round-trip terminates in allow/block):
  // release either way.
  release(form, submitter);
}

function onSubmit(ev: Event): void {
  try {
    const form = ev.target;
    if (!(form instanceof HTMLFormElement)) return;
    if (!isGated(form)) return;
    const st = formState.get(form) ?? 'idle';
    if (st === 'approved') return; // our own requestSubmit: native submit proceeds
    if (st === 'pending' || st === 'blocked') {
      ev.preventDefault();
      return;
    }
    // idle: hold the submission and run the decide flow. Deliberately NO
    // stopImmediatePropagation — SDK-03's passive fs listener must still
    // observe the attempt.
    ev.preventDefault();
    const sub = (ev as SubmitEvent).submitter;
    const submitter = sub instanceof HTMLElement ? sub : null;
    formState.set(form, 'pending');
    void runFlow(form, submitter).catch(() => {
      // Unexpected failure: fail open rather than wedge the form in pending.
      release(form, submitter);
    });
  } catch {
    /* never throw on the host page */
  }
}

function install(): void {
  // ONE capture-phase listener on document. NOT passive — this listener is
  // the sanctioned preventDefault exception (collectors stay passive).
  document.addEventListener('submit', onSubmit, { capture: true });
}

/**
 * Installs the gate ONLY when gating can apply: a `data-gate-forms` selector
 * on the script tag, or at least one `form[data-tg-gate]` in the document
 * (re-checked at DOMContentLoaded when the script runs in <head>). With
 * neither present this module is inert and the SDK stays fully passive
 * (SDK-03's guardrail): no listener, no side effects, no extra DOM.
 */
export function startGate(cfg: TgConfig): void {
  try {
    if (started) return;
    started = true;
    gateCfg = cfg;
    if (cfg.gateForms !== null) {
      install();
      return;
    }
    if (document.querySelector('form[data-tg-gate]')) {
      install();
      return;
    }
    if (document.readyState === 'loading') {
      document.addEventListener(
        'DOMContentLoaded',
        () => {
          try {
            if (document.querySelector('form[data-tg-gate]')) install();
          } catch {
            /* ignore */
          }
        },
        { once: true }
      );
    }
  } catch {
    /* never throw on the host page */
  }
}

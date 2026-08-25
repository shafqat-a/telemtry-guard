import type { TgConfig } from '../config';
import type { HpEvent } from '../types';
import { enqueue } from '../transport';
import { nowT } from '../util';

// Injected honeypot inputs. The forms collector consults this set so honeypot
// fields never produce ff/fb/af/pa — only hp events.
const injected = new WeakSet<Element>();
// Forms already considered (whether or not injection happened).
const processed = new WeakSet<HTMLFormElement>();
// Form → its injected honeypot (for the submit-time value check).
const hpByForm = new WeakMap<HTMLFormElement, HTMLInputElement>();
let linkInjected = false;

export function isHoneypot(el: Element): boolean {
  return injected.has(el);
}

function report(kind: HpEvent['kind']): void {
  const hp: HpEvent = { e: 'hp', t: nowT(), kind };
  enqueue(hp);
}

/**
 * Called by the forms collector's submit handler. Any non-empty honeypot value
 * at submit is near-deterministic bot evidence (§4) — checked regardless of
 * event trust because the datum is the value, not the event. The value itself
 * is only compared against ''; it is never transmitted.
 */
export function honeypotSubmitCheck(form: HTMLFormElement): void {
  try {
    const hp = hpByForm.get(form);
    if (hp && hp.value !== '') report('submit_filled');
  } catch {
    /* ignore */
  }
}

function injectInto(form: HTMLFormElement, name: string): void {
  try {
    if (processed.has(form)) return; // once per form
    processed.add(form);
    // Skip if the form already contains a control with that name — that field
    // is a real tenant field, not ours.
    if (form.elements.namedItem(name)) return;
    const hp = document.createElement('input');
    hp.type = 'text';
    hp.name = name;
    hp.setAttribute('aria-hidden', 'true');
    hp.tabIndex = -1;
    hp.autocomplete = 'off';
    // Offscreen-absolute, NOT display:none/visibility:hidden/type=hidden —
    // naïve bots skip those; offscreen placement is the standard honeypot.
    hp.style.cssText =
      'position:absolute!important;left:-9999px!important;top:-9999px!important;height:1px;width:1px;opacity:0;pointer-events:auto;';
    // Any interaction is reported (no isTrusted filter: synthetic interaction
    // with an invisible field is itself bot evidence).
    hp.addEventListener(
      'focus',
      () => {
        try {
          report('focus');
        } catch {
          /* ignore */
        }
      },
      { passive: true }
    );
    hp.addEventListener(
      'input',
      () => {
        try {
          report('input');
        } catch {
          /* ignore */
        }
      },
      { passive: true }
    );
    form.appendChild(hp); // part of the sanctioned honeypot DOM mutation set (D2)
    injected.add(hp);
    hpByForm.set(form, hp);
  } catch {
    /* never throw on the host page */
  }
}

/** Inject one inert off-screen decoy link. JS-capable automation produces the
 * link_clicked event; crawlers that follow href without running the handler load
 * the same page with tg_honey present, which the server records separately. */
export function installDecoyLinks(paths: string[]): void {
  try {
    if (linkInjected || !document.body || paths.length === 0) return;
    linkInjected = true;
    for (let i = 0; i < paths.length; i++) {
      const path = paths[i];
      if (!path || !path.startsWith('/')) continue;
      const link = document.createElement('a');
      const target = new URL(path, location.origin);
      target.searchParams.set('tg_honey', '1');
      link.href = target.toString();
      link.rel = 'nofollow';
      link.tabIndex = -1;
      link.setAttribute('aria-hidden', 'true');
      link.setAttribute('data-tg-honey-link', path);
      link.style.cssText =
        'position:absolute!important;left:-9999px!important;top:-9999px!important;height:1px;width:1px;opacity:0;';
      link.addEventListener('click', (event) => {
        try {
          event.preventDefault();
          report('link_clicked');
        } catch {
          /* ignore */
        }
      });
      document.body.appendChild(link);
    }
  } catch {
    /* never throw on the host page */
  }
}

function scanForms(root: ParentNode, name: string): void {
  const forms = root.querySelectorAll('form');
  for (let i = 0; i < forms.length; i++) injectInto(forms[i] as HTMLFormElement, name);
}

export function installHoneypot(cfg: TgConfig): void {
  try {
    const name = cfg.honeypotName;
    const start = (): void => {
      try {
        scanForms(document, name);
        const mo = new MutationObserver((mutations) => {
          try {
            for (let m = 0; m < mutations.length; m++) {
              const added = mutations[m]!.addedNodes;
              for (let i = 0; i < added.length; i++) {
                const node = added[i];
                if (!(node instanceof Element)) continue;
                if (node instanceof HTMLFormElement) injectInto(node, name);
                scanForms(node, name);
              }
            }
          } catch {
            /* ignore */
          }
        });
        mo.observe(document.body, { childList: true, subtree: true });
      } catch {
        /* ignore */
      }
    };
    if (document.body) start();
    else document.addEventListener('DOMContentLoaded', start, { once: true });
  } catch {
    /* never throw on the host page */
  }
}

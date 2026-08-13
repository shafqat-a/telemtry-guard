import type { FiEvent } from '../types';
import { enqueue } from '../transport';
import { nowT } from '../util';

/**
 * First-interaction marker: the first TRUSTED pointerdown / keydown / wheel /
 * touchstart / scroll wins; all five listeners are removed after it fires.
 * `t` is ms since performance.timeOrigin, so it IS the raw material for
 * first_interaction_delay_ms — the server computes the feature.
 */
const TYPES: Array<[string, FiEvent['it']]> = [
  ['pointerdown', 'pointer'],
  ['keydown', 'key'],
  ['wheel', 'wheel'],
  ['touchstart', 'touch'],
  ['scroll', 'scroll'],
];

let fired = false;
let installed: Array<[string, EventListener]> = [];

function removeAll(): void {
  for (let i = 0; i < installed.length; i++) {
    try {
      window.removeEventListener(installed[i]![0], installed[i]![1], true);
    } catch {
      /* ignore */
    }
  }
  installed = [];
}

export function installFirstInteraction(): void {
  try {
    for (let i = 0; i < TYPES.length; i++) {
      const it = TYPES[i]![1];
      // The keydown path reads only isTrusted — never key identity (§3).
      const handler: EventListener = (ev: Event) => {
        try {
          if (fired || !ev.isTrusted) return;
          fired = true;
          const fi: FiEvent = { e: 'fi', t: nowT(), it };
          enqueue(fi);
          removeAll();
        } catch {
          /* never throw on the host page */
        }
      };
      installed.push([TYPES[i]![0], handler]);
      window.addEventListener(TYPES[i]![0], handler, { passive: true, capture: true });
    }
  } catch {
    /* never throw on the host page */
  }
}

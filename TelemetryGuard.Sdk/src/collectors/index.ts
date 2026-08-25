import type { TgConfig } from '../config';
import { installPointer } from './pointer';
import { installScroll } from './scroll';
import { installKeys } from './keys';
import { installForms } from './forms';
import { installHoneypot } from './honeypot';
import { installFirstInteraction } from './first-interaction';
import { installPaint } from './paint';

let started = false;

/**
 * Installs every SDK-03 behavioral collector. Module-scope `started` flag
 * guards against double-injection if the script is included twice. All
 * collectors are passive (never preventDefault, never block submits);
 * honeypot input/link injection is the sanctioned DOM mutation set (D2).
 */
export function startCollectors(cfg: TgConfig): void {
  try {
    if (started) return;
    started = true;
    installPaint(); // first: an fcp entry may already be buffered
    installFirstInteraction();
    installPointer();
    installScroll();
    installKeys();
    installForms();
    installHoneypot(cfg);
  } catch {
    /* never throw on the host page */
  }
}

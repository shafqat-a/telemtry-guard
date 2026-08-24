import { readConfig } from './config';
import { createVisitId, resolveSid } from './session';
import { initState } from './state';
import { fetchInit } from './init-fetch';
import { enqueue, startTransport } from './transport';
import { nowT } from './util';
import { startCollectors } from './collectors';
import { startFingerprint } from './fingerprint';
import { startGate } from './gate';

(() => {
  try {
    const cfg = readConfig();
    if (!cfg) return; // inert without a site key
    initState(cfg, resolveSid(), createVisitId());
    startTransport();
    enqueue({ e: 'pv', t: nowT() }); // guarantees >=1 envelope per JS session,
    // so has_js_beacon=1 lands inside the ~10 s grace period
    void fetchInit(); // never awaited; collectors don't block on it
    startCollectors(cfg); // SDK-03 behavioral collectors (timing only)
    startGate(cfg); // SDK-07 form gating — inert unless a form opts in
    startFingerprint(); // SDK-04: idle-scheduled, one fp event per session
  } catch {
    /* never throw on the host page */
  }
})();

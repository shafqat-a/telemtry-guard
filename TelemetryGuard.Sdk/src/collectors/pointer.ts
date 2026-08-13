import type { ClEvent, PdEvent, PmEvent, PmSample, PointerTypeCode, TgEvent } from '../types';
import { enqueue, registerDrain } from '../transport';
import { nowT } from '../util';

const SAMPLE_MIN_GAP_MS = 50; // coalescing: record at most one sample per 50 ms
const SAMPLE_CAP_PER_FLUSH = 200; // hard cap; further samples drop until drained

let samples: PmSample[] = [];
let lastSampleT = -1e9;

function pointerTypeCode(ev: Event): PointerTypeCode {
  // click may be a plain MouseEvent in some browsers — pointerType then undefined → 'u'
  const pt = (ev as PointerEvent).pointerType;
  if (pt === 'mouse') return 'm';
  if (pt === 'touch') return 't';
  if (pt === 'pen') return 'p';
  return 'u';
}

function onPointerMove(ev: PointerEvent): void {
  try {
    if (!ev.isTrusted) return; // trusted-only; raw samples power server-side linearity
    const t = nowT();
    if (t - lastSampleT < SAMPLE_MIN_GAP_MS) return;
    if (samples.length >= SAMPLE_CAP_PER_FLUSH) return;
    lastSampleT = t;
    samples.push([t, Math.round(ev.clientX), Math.round(ev.clientY)]);
  } catch {
    /* never throw on the host page */
  }
}

// pd/cl record untrusted events too — isTrusted is itself the datum (tr).
function onPointerDown(ev: PointerEvent): void {
  try {
    const pd: PdEvent = {
      e: 'pd',
      t: nowT(),
      tr: ev.isTrusted ? 1 : 0,
      sn: Math.round(ev.timeStamp),
      pt: pointerTypeCode(ev),
    };
    enqueue(pd);
  } catch {
    /* ignore */
  }
}

function onClick(ev: MouseEvent): void {
  try {
    const cl: ClEvent = {
      e: 'cl',
      t: nowT(),
      tr: ev.isTrusted ? 1 : 0,
      sn: Math.round(ev.timeStamp),
      pt: pointerTypeCode(ev),
    };
    enqueue(cl);
  } catch {
    /* ignore */
  }
}

/** Drain: hand the buffered samples over as one pm batch event, reset buffer + cap. */
function drainSamples(): TgEvent[] {
  if (samples.length === 0) return [];
  const first = samples[0]!;
  const batch: PmEvent = { e: 'pm', t: first[0], s: samples };
  samples = [];
  return [batch];
}

export function installPointer(): void {
  try {
    document.addEventListener('pointermove', onPointerMove as EventListener, {
      passive: true,
      capture: true,
    });
    document.addEventListener('pointerdown', onPointerDown as EventListener, {
      passive: true,
      capture: true,
    });
    document.addEventListener('click', onClick as EventListener, {
      passive: true,
      capture: true,
    });
    registerDrain(drainSamples);
  } catch {
    /* never throw on the host page */
  }
}

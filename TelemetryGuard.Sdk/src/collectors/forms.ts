import type { AfEvent, FbEvent, FfEvent, FsEvent, PaEvent } from '../types';
import { enqueue } from '../transport';
import { fnv1aHex, nowT } from '../util';
import { honeypotSubmitCheck, isHoneypot } from './honeypot';
import { registerFieldKeydownListener } from './keys';

type FormField = HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement;

interface FieldState {
  keydowns: number;
  lastLen: number; // value LENGTH only — the value string is never stored
  pasteAt: number;
}

const NO_PASTE = -1e9; // sentinel: nowT() - NO_PASTE is always > 500

/** Per-field tracking state, created at focusin. */
const tracked = new WeakMap<Element, FieldState>();
/** Fields that already emitted af — at most once per field per page load. */
const afEmitted = new WeakSet<Element>();

/** input `type`s shipped verbatim as ft; anything else becomes 'other'. */
const FT_ALLOWED = [
  'text',
  'email',
  'tel',
  'password',
  'number',
  'search',
  'url',
  'checkbox',
  'radio',
];

/** input `type`s where the autofill heuristic applies (see maybeAutofill). */
const TEXT_ENTRY = ['text', 'email', 'tel', 'password', 'number', 'search', 'url'];

function isField(el: EventTarget | null): el is FormField {
  return (
    (el instanceof HTMLInputElement ||
      el instanceof HTMLSelectElement ||
      el instanceof HTMLTextAreaElement) &&
    // Honeypot fields only ever produce hp events — never ff/fb/af/pa.
    !isHoneypot(el)
  );
}

/**
 * Hashed field identity — only the FNV-1a hash ships, never the raw
 * name/id/autocomplete, never any value.
 */
function fieldHash(el: FormField): string {
  const raw = el.name || el.id || el.getAttribute('autocomplete') || 'anon';
  return fnv1aHex(raw.toLowerCase());
}

function fieldType(el: FormField): string {
  if (el instanceof HTMLSelectElement) return 'select';
  if (el instanceof HTMLTextAreaElement) return 'textarea';
  const ty = (el.getAttribute('type') || 'text').toLowerCase();
  return FT_ALLOWED.indexOf(ty) >= 0 ? ty : 'other';
}

/**
 * getAttribute, not property access: named form controls shadow id/name/action
 * on HTMLFormElement instances (legacy override built-ins), so e.g. an
 * <input name="action"> would make form.action return an element. The raw
 * identity never ships — only its hash.
 */
function formIdentity(form: HTMLFormElement): string {
  const raw =
    form.getAttribute('id') ||
    form.getAttribute('name') ||
    form.getAttribute('action') ||
    'form';
  return raw.toLowerCase();
}

function onFocusIn(ev: FocusEvent): void {
  try {
    if (!ev.isTrusted) return;
    const el = ev.target;
    if (!isField(el)) return;
    const ff: FfEvent = { e: 'ff', t: nowT(), fh: fieldHash(el), ft: fieldType(el) };
    enqueue(ff);
    // lastLen starts at the current length so pre-filled values and select
    // defaults never register as a "jump" (no false autofill).
    tracked.set(el, { keydowns: 0, lastLen: el.value.length, pasteAt: NO_PASTE });
  } catch {
    /* never throw on the host page */
  }
}

function onFocusOut(ev: FocusEvent): void {
  try {
    if (!ev.isTrusted) return;
    const el = ev.target;
    if (!isField(el)) return;
    // Server derives per-field fill time from ff/fb/ky timings — no client math.
    const fb: FbEvent = { e: 'fb', t: nowT(), fh: fieldHash(el), ft: fieldType(el) };
    enqueue(fb);
  } catch {
    /* ignore */
  }
}

function onSubmit(ev: Event): void {
  try {
    const form = ev.target;
    if (!(form instanceof HTMLFormElement)) return;
    // The honeypot value is the datum — checked regardless of event trust.
    honeypotSubmitCheck(form);
    if (!ev.isTrusted) return;
    const fs: FsEvent = { e: 'fs', t: nowT(), fh: fnv1aHex(formIdentity(form)) };
    enqueue(fs);
    // Passive only: never preventDefault, never block or alter the submit.
  } catch {
    /* ignore */
  }
}

function isIdentityField(el: FormField, ft: string): boolean {
  if (ft === 'email' || ft === 'tel' || ft === 'password') return true;
  const ac = (el.getAttribute('autocomplete') || '').toLowerCase();
  if (/name|email|tel|username/.test(ac)) return true;
  // Raw name/id are matched locally only — they are never transmitted.
  const local = (el.name || '') + ' ' + (el.id || '');
  return /email|phone|mobile|name|user/i.test(local);
}

function onPaste(ev: ClipboardEvent): void {
  try {
    if (!ev.isTrusted) return;
    const el = ev.target;
    if (!isField(el)) return; // honeypot pastes surface only as hp input events
    // Coarse classification only — clipboard contents are NEVER read (§3;
    // password managers make this a T3 signal by design).
    const fk = isIdentityField(el, fieldType(el)) ? 'identity' : 'other';
    const pa: PaEvent = { e: 'pa', t: nowT(), fk };
    enqueue(pa);
    const st = tracked.get(el);
    if (st) st.pasteAt = nowT();
  } catch {
    /* ignore */
  }
}

/**
 * Autofill heuristic scope: text-entry fields only. Mouse-picked selects,
 * checkboxes and radios fire change/input with zero keydowns as normal human
 * behavior — that is input modality, not autofill, and would flood
 * autofill_detected with false positives.
 */
function isTextEntry(el: FormField, ft: string): boolean {
  if (el instanceof HTMLTextAreaElement) return true;
  if (!(el instanceof HTMLInputElement)) return false;
  return TEXT_ENTRY.indexOf(ft) >= 0;
}

function maybeAutofill(el: FormField): void {
  if (afEmitted.has(el)) return; // at most once per field per page load
  afEmitted.add(el);
  const af: AfEvent = { e: 'af', t: nowT(), fh: fieldHash(el) };
  enqueue(af);
}

function onInput(ev: Event): void {
  try {
    if (!ev.isTrusted) return;
    const el = ev.target;
    if (!isField(el)) return;
    const st = tracked.get(el);
    if (!st) return;
    // Length only — the value string itself is never stored, hashed or shipped.
    const len = el.value.length;
    if (
      len - st.lastLen > 1 &&
      st.keydowns === 0 &&
      nowT() - st.pasteAt > 500 &&
      isTextEntry(el, fieldType(el))
    ) {
      maybeAutofill(el);
    }
    st.lastLen = len;
  } catch {
    /* ignore */
  }
}

function onChange(ev: Event): void {
  try {
    if (!ev.isTrusted) return;
    const el = ev.target;
    if (!isField(el)) return;
    const st = tracked.get(el);
    if (!st) return;
    const len = el.value.length;
    if (
      len > 0 &&
      st.keydowns === 0 &&
      st.pasteAt === NO_PASTE &&
      isTextEntry(el, fieldType(el))
    ) {
      maybeAutofill(el);
    }
    st.lastLen = len;
  } catch {
    /* ignore */
  }
}

export function installForms(): void {
  try {
    const opts: AddEventListenerOptions = { passive: true, capture: true };
    document.addEventListener('focusin', onFocusIn as EventListener, opts);
    document.addEventListener('focusout', onFocusOut as EventListener, opts);
    document.addEventListener('submit', onSubmit, opts);
    document.addEventListener('paste', onPaste as EventListener, opts);
    document.addEventListener('input', onInput, opts);
    document.addEventListener('change', onChange, opts);
    // keys.ts calls back on every trusted keydown; count it against the
    // focused field's state (autofill heuristic). Key identity never crosses
    // this boundary — only the event target does.
    registerFieldKeydownListener((target) => {
      const st = target instanceof Element ? tracked.get(target) : undefined;
      if (st) st.keydowns++;
    });
  } catch {
    /* never throw on the host page */
  }
}

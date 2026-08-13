/**
 * SDK-06 step 5 — THE COMPLIANCE GUARDRAIL (§3 non-goal: no keystroke content,
 * ever). This test failing must fail CI — never skip or soften it.
 *
 * Proves from captured payloads that:
 *  (a) typed content (sentinels) appears nowhere — not even 4+-char substrings;
 *  (b) key-identity property names appear in no event object (deep walk +
 *      raw-string scan);
 *  (c) every ky event is EXACTLY {e,t,d} (strict schema);
 *  (d) raw field names never ship — ff/fb carry only an 8-hex fh plus the
 *      whitelisted type token ft.
 */
import { test, expect } from '@playwright/test';
import { kyEvent, ffEvent, fbEvent } from './schema';
import { allEvents, getCaptured, pollCaptured, resetCaptured } from './util';

// Sentinels: no 4+-char substring may legitimately occur in any payload.
// (Case-sensitive scan; note 'Sent' != the envelope key 'sent_at'.)
const EMAIL_SENTINEL = 'XyZZySentinel42@example.com';
const PW_SENTINEL = 'ZqPw#81SecretYx';

// JSON property names that would carry key identity or content.
const FORBIDDEN_PROPS = ['key', 'code', 'keyCode', 'charCode', 'which', 'char', 'data', 'text', 'value'];

// Raw field names from the fixture form — must never ship (only fh/ft do).
const RAW_FIELD_NAMES = /email|password|phone|comments/i;

/** All whitelisted ft type tokens (the only sanctioned place 'email' etc. appear). */
const FT_TOKENS = new Set([
  'text', 'email', 'tel', 'password', 'number', 'search', 'url',
  'checkbox', 'radio', 'select', 'textarea', 'other',
]);

function* substringsOf(s: string, len: number): Generator<string> {
  for (let i = 0; i + len <= s.length; i++) yield s.slice(i, i + len);
}

/** Deep-walk an event object; yields [path, key, value] for every property. */
function* walk(
  obj: unknown,
  path = '$'
): Generator<[string, string, unknown]> {
  if (Array.isArray(obj)) {
    for (let i = 0; i < obj.length; i++) yield* walk(obj[i], `${path}[${i}]`);
  } else if (obj !== null && typeof obj === 'object') {
    for (const [k, v] of Object.entries(obj as Record<string, unknown>)) {
      yield [path, k, v];
      yield* walk(v, `${path}.${k}`);
    }
  }
}

test.beforeEach(async ({ request }) => {
  await resetCaptured(request);
});

test('no key identity, typed content, or raw field name ever ships', async ({
  page,
  request,
}) => {
  await page.goto('/fixtures/form.html');

  await page.focus('input[name="email"]');
  await page.keyboard.type(EMAIL_SENTINEL, { delay: 30 });
  await page.focus('input[name="password"]');
  await page.keyboard.type(PW_SENTINEL, { delay: 30 });
  // Blur so ff/fb pairs ship too, then let the 2 s flush timer fire.
  await page.focus('textarea[name="comments"]');
  await page.waitForTimeout(2500);

  // Wait until keyboard telemetry has demonstrably arrived.
  await pollCaptured(
    request,
    (c) => (allEvents(c).some((ev) => ev['e'] === 'ky') ? c : undefined),
    { what: 'ky events' }
  );
  const captured = await getCaptured(request);
  expect(captured.length).toBeGreaterThan(0);

  // ---- (a) sentinels: no 4+-char substring anywhere in any raw payload ----
  for (const cap of captured) {
    for (const sentinel of [EMAIL_SENTINEL, PW_SENTINEL]) {
      for (const sub of substringsOf(sentinel, 4)) {
        expect(
          cap.raw.includes(sub),
          `typed content leaked: substring '${sub}' of sentinel found in payload`
        ).toBe(false);
      }
    }
  }

  // ---- (b) forbidden property names: raw scan + deep walk of every event ----
  for (const cap of captured) {
    for (const prop of FORBIDDEN_PROPS) {
      expect(
        cap.raw.includes(`"${prop}":`),
        `forbidden property name '"${prop}":' found in raw payload`
      ).toBe(false);
    }
  }
  const events = allEvents(captured);
  expect(events.length).toBeGreaterThan(0);
  for (const ev of events) {
    for (const [path, key] of walk(ev)) {
      expect(
        FORBIDDEN_PROPS.includes(key),
        `forbidden property '${key}' at ${path} in event ${JSON.stringify(ev)}`
      ).toBe(false);
    }
  }

  // ---- (c) every ky event is EXACTLY {e,t,d} — strict schema ----
  const kys = events.filter((ev) => ev['e'] === 'ky');
  expect(kys.length).toBeGreaterThan(0);
  for (const ky of kys) {
    const res = kyEvent.safeParse(ky);
    expect(
      res.success,
      `ky event violated the strict {e,t,d} shape: ${JSON.stringify(ky)}`
    ).toBe(true);
  }

  // ---- (d) raw field names never ship ----
  // ff/fb carry only an 8-hex fh + whitelisted ft (pinned by strict schemas);
  // 'email'/'password' may appear ONLY as the sanctioned ft type token.
  const ffs = events.filter((ev) => ev['e'] === 'ff' || ev['e'] === 'fb');
  expect(ffs.length).toBeGreaterThan(0);
  for (const ev of ffs) {
    const res = (ev['e'] === 'ff' ? ffEvent : fbEvent).safeParse(ev);
    expect(res.success, `ff/fb shape violation: ${JSON.stringify(ev)}`).toBe(true);
  }
  for (const ev of events) {
    for (const [path, key, value] of walk(ev)) {
      expect(RAW_FIELD_NAMES.test(key), `field-name-like key '${key}' at ${path}`).toBe(false);
      if (typeof value !== 'string') continue;
      if (key === 'ft') {
        expect(FT_TOKENS.has(value), `ft outside the whitelist: '${value}'`).toBe(true);
        continue; // the type token is the one sanctioned 'email'/'password' string
      }
      expect(
        RAW_FIELD_NAMES.test(value),
        `raw field name leaked in '${key}' at ${path}: '${value}'`
      ).toBe(false);
    }
  }
});

/**
 * SDK-07 — client decision flow (form gating, Turnstile round-trip), §6.2.
 *
 * Deterministic via the mock's scripted /decide queue (POST /__decide-script)
 * and a stubbed Turnstile widget (page.route) — no real Cloudflare traffic.
 * The six scenarios: allow, block, challenge→allow, challenge→block,
 * fail-open, ungated.
 */
import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { resetCaptured } from './util';

/** Raw capture rows from /__captured — beacons (no kind), 'decide', 'submitted'. */
interface RawCapture {
  receivedAt: number;
  raw: string;
  parsed: unknown;
  headers: Record<string, string | string[] | undefined>;
  kind?: string;
  url?: string;
}

const TURNSTILE_STUB =
  "window.turnstile = { render: (el, opts) => (setTimeout(() => opts.callback('e2e-token'), 50), 'w1'), remove: () => {} };";

async function getRawCaptured(request: APIRequestContext): Promise<RawCapture[]> {
  const res = await request.get('/__captured');
  if (!res.ok()) throw new Error(`/__captured failed: ${res.status()}`);
  return (await res.json()) as RawCapture[];
}

function decides(c: RawCapture[]): RawCapture[] {
  return c.filter((x) => x.kind === 'decide');
}

function submissions(c: RawCapture[]): RawCapture[] {
  return c.filter((x) => x.kind === 'submitted');
}

async function scriptDecide(
  request: APIRequestContext,
  script: Array<Record<string, unknown>>
): Promise<void> {
  const res = await request.post('/__decide-script', { data: script });
  if (!res.ok()) throw new Error(`/__decide-script failed: ${res.status()}`);
}

/**
 * Stubs the Turnstile widget script and tracks whether/when it was requested
 * — the acceptance criterion: requested ONLY in challenge scenarios, and only
 * AFTER the challenge response.
 */
async function stubTurnstile(page: Page): Promise<{ requestedAt: () => number | null }> {
  let at: number | null = null;
  await page.route('**/turnstile/v0/api.js*', (route) => {
    if (at === null) at = Date.now();
    void route.fulfill({
      contentType: 'text/javascript; charset=utf-8',
      body: TURNSTILE_STUB,
    });
  });
  return { requestedAt: () => at };
}

async function fillForm(page: Page): Promise<void> {
  await page.fill('input[name="name"]', 'Jane Tester');
  await page.fill('input[name="email"]', 'jane@example.com');
  await page.fill('input[name="phone"]', '5551234567');
  await page.fill('input[name="password"]', 'correct-horse-battery');
  await page.fill('textarea[name="comments"]', 'hello there');
}

async function pollRaw<T>(
  request: APIRequestContext,
  predicate: (captured: RawCapture[]) => T | undefined | false,
  what: string,
  timeoutMs = 20_000
): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const captured = await getRawCaptured(request);
    const out = predicate(captured);
    if (out) return out;
    if (Date.now() >= deadline) {
      throw new Error(
        `timed out after ${timeoutMs} ms waiting for ${what} (captured ${captured.length} row(s))`
      );
    }
    await new Promise((r) => setTimeout(r, 250));
  }
}

function visitSid(captured: RawCapture[]): string {
  const beacon = captured.find((x) => !x.kind && x.parsed && typeof x.parsed === 'object');
  const sid = (beacon?.parsed as { sid?: string } | undefined)?.sid;
  if (!sid) throw new Error('no beacon visit sid captured');
  return sid;
}

test.beforeEach(async ({ request }) => {
  await resetCaptured(request); // also clears the /decide script queue
});

test('allow: exactly one /decide with sid + k, then the form submits', async ({
  page,
  request,
}) => {
  const turnstile = await stubTurnstile(page);
  await scriptDecide(request, [{ action: 'allow' }]);
  await page.goto('/fixtures/gated-form.html');
  await fillForm(page);
  await page.click('button[type="submit"]');

  await pollRaw(request, (c) => (submissions(c).length > 0 ? c : undefined), 'form submission');
  const captured = await getRawCaptured(request);
  const dec = decides(captured);
  expect(dec).toHaveLength(1);

  // Body is valid JSON {sid} — exactly that, no turnstileToken on first call.
  const sid = visitSid(captured);
  expect(JSON.parse(dec[0]!.raw)).toEqual({ sid });
  // ?k= carries the page's site key.
  expect(dec[0]!.url).toContain('k=e2e-site');
  // text/plain simple request, no cookies (credentials: 'omit').
  expect(String(dec[0]!.headers['content-type'])).toContain('text/plain');
  expect(dec[0]!.headers['cookie']).toBeUndefined();

  expect(submissions(captured)).toHaveLength(1);
  // Turnstile is never requested on the allow path.
  expect(turnstile.requestedAt()).toBeNull();
});

test('block: form held, page notified via tg:decision, no submission', async ({
  page,
  request,
}) => {
  const turnstile = await stubTurnstile(page);
  await scriptDecide(request, [{ action: 'block' }]);
  await page.goto('/fixtures/gated-form.html');
  await fillForm(page);
  await page.click('button[type="submit"]');

  await page.waitForFunction(() => {
    const w = window as unknown as { __tgDecisions?: Array<{ action?: string }> };
    return !!w.__tgDecisions && w.__tgDecisions.some((d) => d.action === 'block');
  });
  // Detail carries ONLY the action string — no score/band/rule ever ships.
  const details = await page.evaluate(
    () => (window as unknown as { __tgDecisions: unknown[] }).__tgDecisions
  );
  expect(details).toEqual([{ action: 'block' }]);

  const captured = await getRawCaptured(request);
  expect(decides(captured)).toHaveLength(1);
  expect(submissions(captured)).toHaveLength(0);
  expect(turnstile.requestedAt()).toBeNull();
});

test('challenge then allow: lazy Turnstile, token re-POSTed, form submits', async ({
  page,
  request,
}) => {
  const turnstile = await stubTurnstile(page);
  await scriptDecide(request, [
    { action: 'challenge', turnstileSiteKey: 'e2e-sk' },
    { action: 'allow' },
  ]);
  await page.goto('/fixtures/gated-form.html');
  await fillForm(page);
  await page.click('button[type="submit"]');

  await pollRaw(request, (c) => (submissions(c).length > 0 ? c : undefined), 'form submission');
  const captured = await getRawCaptured(request);
  const dec = decides(captured);
  expect(dec).toHaveLength(2);

  const sid = visitSid(captured);
  expect(JSON.parse(dec[0]!.raw)).toEqual({ sid });
  // Second /decide carries the widget token from the stub.
  expect(JSON.parse(dec[1]!.raw)).toEqual({ sid, turnstileToken: 'e2e-token' });

  // The Turnstile script was requested only AFTER the challenge response.
  const requestedAt = turnstile.requestedAt();
  expect(requestedAt).not.toBeNull();
  expect(requestedAt!).toBeGreaterThanOrEqual(dec[0]!.receivedAt);

  expect(submissions(captured)).toHaveLength(1);
});

test('challenge then block: form held', async ({ page, request }) => {
  const turnstile = await stubTurnstile(page);
  await scriptDecide(request, [
    { action: 'challenge', turnstileSiteKey: 'e2e-sk' },
    { action: 'block' },
  ]);
  await page.goto('/fixtures/gated-form.html');
  await fillForm(page);
  await page.click('button[type="submit"]');

  await page.waitForFunction(() => {
    const w = window as unknown as { __tgDecisions?: Array<{ action?: string }> };
    return !!w.__tgDecisions && w.__tgDecisions.some((d) => d.action === 'block');
  });
  const captured = await getRawCaptured(request);
  const dec = decides(captured);
  expect(dec).toHaveLength(2);
  const sid = visitSid(captured);
  expect(JSON.parse(dec[1]!.raw)).toEqual({ sid, turnstileToken: 'e2e-token' });
  expect(submissions(captured)).toHaveLength(0);
  expect(turnstile.requestedAt()).not.toBeNull();
});

test('fail open: /decide 500 still releases the submit', async ({ page, request }) => {
  const turnstile = await stubTurnstile(page);
  await scriptDecide(request, [{ status: 500 }]);
  await page.goto('/fixtures/gated-form.html');
  await fillForm(page);
  await page.click('button[type="submit"]');

  // Despite the scoring outage the lead is never lost: the form submits.
  await pollRaw(request, (c) => (submissions(c).length > 0 ? c : undefined), 'fail-open submission');
  const captured = await getRawCaptured(request);
  expect(decides(captured)).toHaveLength(1);
  expect(submissions(captured)).toHaveLength(1);
  expect(turnstile.requestedAt()).toBeNull();
});

test('ungated form triggers zero /decide calls', async ({ page, request }) => {
  const turnstile = await stubTurnstile(page);
  // form.html has NO data-tg-gate and the script tag has no data-gate-forms.
  await page.goto('/fixtures/form.html');
  await fillForm(page);
  await page.click('button[type="submit"]');

  // The passive fs beacon proves the submit was observed by SDK-03 — then
  // assert the gate produced zero /decide traffic.
  await pollRaw(
    request,
    (c) => {
      const beacons = c.filter((x) => !x.kind);
      const hasFs = beacons.some((b) => {
        const parsed = b.parsed as { events?: Array<{ e?: string }> } | null;
        return !!parsed?.events?.some((ev) => ev.e === 'fs');
      });
      return hasFs ? c : undefined;
    },
    'passive fs beacon'
  );
  const captured = await getRawCaptured(request);
  expect(decides(captured)).toHaveLength(0);
  expect(submissions(captured)).toHaveLength(0);
  expect(turnstile.requestedAt()).toBeNull();
});

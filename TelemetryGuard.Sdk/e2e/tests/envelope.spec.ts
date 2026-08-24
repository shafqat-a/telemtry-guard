/**
 * SDK-06 step 4 — envelope contract: schema, seq continuity, nonce echo,
 * site key, content type, pv-first, canonical key order + optional checksum.
 */
import { test, expect } from '@playwright/test';
import { anyEvent, envelope, verifyRawEnvelope } from './schema';
import { getInits, pollCaptured, resetCaptured, type Captured } from './util';

test.beforeEach(async ({ request }) => {
  await resetCaptured(request);
});

test('envelopes match the pinned wire contract', async ({ page, request }) => {
  await page.goto('/fixtures/landing.html');

  // Interact until >= 3 envelopes are captured. Each loop iteration produces
  // pointer/scroll/click events; the SDK flushes at 10 events or every 2 s.
  let done: Captured[] | undefined;
  const deadline = Date.now() + 30_000;
  while (!done) {
    // A small burst of interaction: mouse movement, scrolling, a click.
    for (let i = 0; i < 6; i++) {
      await page.mouse.move(120 + i * 37, 140 + i * 23);
      await page.waitForTimeout(60);
    }
    await page.mouse.wheel(0, 300);
    await page.click('#cta');
    await page.waitForTimeout(500);
    const got = await pollCaptured(request, (c) => (c.length >= 3 ? c : undefined), {
      timeoutMs: 100,
      intervalMs: 100,
      what: '>= 3 envelopes',
    }).catch(() => undefined);
    if (got) done = got;
    if (!done && Date.now() > deadline) {
      throw new Error('timed out interacting: fewer than 3 envelopes captured in 30 s');
    }
  }

  expect(done.length).toBeGreaterThanOrEqual(3);

  // Every envelope: zod schema, every event against the event union, raw key
  // order, checksum where `c` is present, content-type, site key.
  for (const cap of done) {
    const env = envelope.parse(cap.parsed);
    expect(env.k).toBe('e2e-site');
    for (const ev of env.events) {
      const res = anyEvent.safeParse(ev);
      expect(
        res.success,
        `event failed schema: ${JSON.stringify(ev)}\n${res.success ? '' : res.error.message}`
      ).toBe(true);
    }
    verifyRawEnvelope(cap.raw);
    const ct = String(cap.headers['content-type'] ?? '');
    expect(ct.startsWith('text/plain'), `content-type was '${ct}'`).toBe(true);
  }

  // Single page => single sid across all envelopes.
  const sids = new Set(done.map((c) => c.parsed.sid));
  expect(sids.size).toBe(1);
  const sessionIds = new Set(done.map((c) => c.parsed.session_id));
  expect(sessionIds.size).toBe(1);
  const visitIds = new Set(done.map((c) => c.parsed.visit_id));
  expect(visitIds.size).toBe(1);
  const sid = done[0]!.parsed.sid;

  // seq: 0,1,2,... strictly increasing, no duplicates, no gaps.
  const seqs = done.map((c) => c.parsed.seq).sort((a, b) => a - b);
  expect(seqs).toEqual(seqs.map((_, i) => i));
  expect(new Set(seqs).size).toBe(seqs.length);

  // nonce echoes what the mock issued for this sid via /i/init.
  const inits = await getInits(request);
  const issued = inits.filter((i) => i.sid === sid).map((i) => i.nonce);
  expect(issued.length).toBeGreaterThanOrEqual(1);
  for (const cap of done) {
    expect(issued).toContain(cap.parsed.nonce);
  }

  // A pv event appears in the first envelope (seq 0).
  const first = done.find((c) => c.parsed.seq === 0)!;
  expect(first).toBeTruthy();
  const hasPv = (first.parsed.events as Array<{ e?: string }>).some((e) => e.e === 'pv');
  expect(hasPv, 'first envelope must contain the pv event').toBe(true);
});

test('a tracked tg_sid is adopted as the visit id once; a reload gets a fresh visit', async ({
  page,
  request,
}) => {
  // The API-02 tracker mints tg_sid on its 302; the SDK adopts it for the landing
  // visit so the click context and the first page share one identity.
  const tracked = '0f1e2d3c-4b5a-4697-8877-665544332211';
  await page.goto(`/fixtures/landing.html?tg_sid=${tracked}`);
  const first = await pollCaptured(request, (c) => (c.length ? c[0] : undefined), {
    what: 'tracked landing beacon',
  });
  expect(first.parsed.visit_id).toBe(tracked);
  expect(first.parsed.session_id).toBe(tracked);
  expect(first.parsed.seq).toBe(0);

  // F5 on the same tagged URL: seq restarts at 0 on every document load, and the
  // server scopes seq/nonce per visit — reusing the tracked id would make the whole
  // reload read as a replay. The reload must mint a fresh visit under the same session.
  await page.reload();
  const both = await pollCaptured(
    request,
    (c) => (new Set(c.map((x) => x.parsed.visit_id)).size >= 2 ? c : undefined),
    { what: 'reloaded visit beacon' }
  );
  const reloaded = both.filter((x) => x.parsed.visit_id !== tracked);
  expect(reloaded.length).toBeGreaterThan(0);
  expect(new Set(reloaded.map((x) => x.parsed.visit_id)).size).toBe(1);
  expect(reloaded.every((x) => x.parsed.session_id === tracked)).toBe(true);
  expect(reloaded.some((x) => x.parsed.seq === 0)).toBe(true);
});

test('two page loads keep one session and create distinct visits', async ({ page, request }) => {
  await page.goto('/fixtures/landing.html');
  const first = await pollCaptured(request, (c) => (c.length ? c[0] : undefined), {
    what: 'first visit beacon',
  });
  await page.reload();
  const both = await pollCaptured(
    request,
    (c) => {
      const visits = new Set(c.map((x) => x.parsed.visit_id));
      return visits.size >= 2 ? c : undefined;
    },
    { what: 'second visit beacon' }
  );

  expect(new Set(both.map((x) => x.parsed.session_id))).toEqual(new Set([first.parsed.session_id]));
  expect(new Set(both.map((x) => x.parsed.visit_id)).size).toBe(2);
  expect(new Set(both.map((x) => x.parsed.sid))).toEqual(
    new Set(both.map((x) => x.parsed.visit_id))
  );
});

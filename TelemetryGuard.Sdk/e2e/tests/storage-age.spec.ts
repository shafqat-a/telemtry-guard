/**
 * SDK-06 step 7 — signed first-party storage-age flow (§4, SDK-04):
 * fresh write on first visit, age reported (never overwritten) on the next,
 * cookie resurrected from localStorage after cookie-only deletion.
 *
 * Each fp event is once-per-session (keyed on sid); a new tab gives a fresh
 * sessionStorage => fresh sid => a new fp event, while cookie + localStorage
 * are shared context-wide.
 */
import { test, expect, type Page, type APIRequestContext, type BrowserContext } from '@playwright/test';
import { fpEvent } from './schema';
import { pollCaptured, resetCaptured, type Captured } from './util';
import type { z } from 'zod';

type Fp = z.infer<typeof fpEvent>;

async function sidOf(page: Page): Promise<string> {
  const sid = await page.evaluate(() => sessionStorage.getItem('tg_sid'));
  expect(sid, 'SDK should have stored tg_sid in sessionStorage').toBeTruthy();
  return sid!;
}

/** Waits for the fp event of a journey and returns its page-visit id. */
async function awaitFp(request: APIRequestContext, sessionId: string): Promise<{ fp: Fp; visitId: string }> {
  const raw = await pollCaptured(
    request,
    (c: Captured[]) => {
      for (const cap of c) {
        if (cap.parsed?.session_id !== sessionId) continue;
        for (const ev of cap.parsed.events as Array<Record<string, unknown>>) {
          if (ev['e'] === 'fp') return { ev, visitId: cap.parsed.sid };
        }
      }
      return undefined;
    },
    { timeoutMs: 25_000, what: `fp event for session ${sessionId}` }
  );
  return { fp: fpEvent.parse(raw.ev), visitId: raw.visitId };
}

async function tgFpCookie(context: BrowserContext): Promise<string | undefined> {
  const cookies = await context.cookies('http://localhost:4599');
  return cookies.find((c) => c.name === 'tg_fp')?.value;
}

test.beforeEach(async ({ request }) => {
  await resetCaptured(request);
});

test('storage-age: fresh write, aged report, restore from localStorage', async ({
  context,
  page,
  request,
}) => {
  // ---- Visit 1 (fresh context): no pair anywhere; SDK writes /i/init's pair.
  await page.goto('/fixtures/landing.html');
  const sid1 = await sidOf(page);
  const first = await awaitFp(request, sid1);
  const fp1 = first.fp;

  expect(fp1.storage, 'fp must carry the storage report').toBeTruthy();
  expect(fp1.storage!.fresh).toBe(true);
  // Report reflects state on entry — nothing was present before the write.
  expect(fp1.storage!.ck.present).toBe(false);
  expect(fp1.storage!.ls.present).toBe(false);

  // Cookie + localStorage now hold "<storageTs>.<storageSig>" matching the
  // mock's /i/init response for sid1.
  const inits = await request.get('/__inits').then((r) => r.json());
  const init1 = (inits as Array<{ sid: string; storageTs: number; storageSig: string }>).find(
    (i) => i.sid === first.visitId
  );
  expect(init1, 'mock must have issued an init for sid1').toBeTruthy();
  const pair1 = `${init1!.storageTs}.${init1!.storageSig}`;

  expect(await tgFpCookie(context)).toBe(pair1);
  expect(await page.evaluate(() => localStorage.getItem('tg_fp'))).toBe(pair1);

  // ---- Visit 2 (new tab => new sid => new fp; storage shared): aged report.
  const page2 = await context.newPage();
  await page2.goto('/fixtures/landing.html');
  const sid2 = await sidOf(page2);
  expect(sid2).not.toBe(sid1);
  const fp2 = (await awaitFp(request, sid2)).fp;

  expect(fp2.storage!.ck.present).toBe(true);
  // ts equals the FIRST visit's value — never the second /i/init's.
  expect(fp2.storage!.ck.ts).toBe(init1!.storageTs);
  expect(fp2.storage!.ck.sig).toBe(init1!.storageSig);
  expect(fp2.storage!.ck.ageMs).toBeGreaterThanOrEqual(0);
  expect(fp2.storage!.ls.present).toBe(true);
  expect(fp2.storage!.ls.ts).toBe(init1!.storageTs);
  expect(fp2.storage!.fresh).toBe(false);

  // ---- Visit 3: delete the cookie only; pair restored from localStorage.
  await context.clearCookies();
  expect(await tgFpCookie(context)).toBeUndefined();

  const page3 = await context.newPage();
  await page3.goto('/fixtures/landing.html');
  const sid3 = await sidOf(page3);
  expect(sid3).not.toBe(sid2);
  const fp3 = (await awaitFp(request, sid3)).fp;

  // Report reflects entry state: cookie gone, localStorage survived with the
  // ORIGINAL ts (never the fresh /i/init pair).
  expect(fp3.storage!.ck.present).toBe(false);
  expect(fp3.storage!.ls.present).toBe(true);
  expect(fp3.storage!.ls.ts).toBe(init1!.storageTs);
  expect(fp3.storage!.ls.sig).toBe(init1!.storageSig);
  expect(fp3.storage!.fresh).toBe(false);

  // And the cookie was resurrected from the surviving store.
  expect(await tgFpCookie(context)).toBe(pair1);
});

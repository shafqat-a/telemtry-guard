/**
 * SDK-06 step 8 — a default Playwright (automation-controlled) run is visible
 * in the payload: webdriver/botd flags in fp, and the RAW pm samples carry
 * enough signal for the server's mouse_path_linearity / std_inter_event_ms
 * features. The linearity/interval math below lives IN THE TEST on purpose —
 * derived features stay server-side; the SDK must never compute them (§7).
 */
import { test, expect } from '@playwright/test';
import { fpEvent, pmEvent } from './schema';
import { eventsOf, pollCaptured, resetCaptured } from './util';

type Sample = [number, number, number]; // [t, x, y]

function pathChordRatio(samples: Sample[]): number {
  let pathLen = 0;
  for (let i = 1; i < samples.length; i++) {
    const dx = samples[i]![1] - samples[i - 1]![1];
    const dy = samples[i]![2] - samples[i - 1]![2];
    pathLen += Math.hypot(dx, dy);
  }
  const first = samples[0]!;
  const last = samples[samples.length - 1]!;
  const chord = Math.hypot(last[1] - first[1], last[2] - first[2]);
  return pathLen / chord;
}

function intervalCv(samples: Sample[]): number {
  const intervals: number[] = [];
  for (let i = 1; i < samples.length; i++) {
    intervals.push(samples[i]![0] - samples[i - 1]![0]);
  }
  const mean = intervals.reduce((a, b) => a + b, 0) / intervals.length;
  const variance =
    intervals.reduce((a, b) => a + (b - mean) * (b - mean), 0) / intervals.length;
  return Math.sqrt(variance) / mean;
}

test.beforeEach(async ({ request }) => {
  await resetCaptured(request);
});

test('a gridded automation run betrays itself in fp flags and raw pm samples', async ({
  page,
  request,
}) => {
  await page.goto('/fixtures/landing.html');

  // Perfectly linear, constant-velocity mouse path with fixed small delays.
  // 70 ms cadence sits safely above the SDK's 50 ms coalescing gap, so every
  // move is recorded as a sample.
  for (let i = 0; i <= 45; i++) {
    await page.mouse.move(100 + i * 10, 100 + i * 5);
    await page.waitForTimeout(70);
  }

  // fp: Playwright's default Chromium is automation-controlled.
  const rawFp = await pollCaptured(
    request,
    (c) => eventsOf(c, 'fp')[0],
    { timeoutMs: 25_000, what: 'fp event' }
  );
  const fp = fpEvent.parse(rawFp);
  expect(
    fp.wd === true || fp.botd?.bot === true,
    `expected wd and/or botd.bot true; got wd=${String(fp.wd)} botd=${JSON.stringify(fp.botd)}`
  ).toBe(true);

  // pm: gather every raw sample, in order, across all pm batch events.
  const captured = await pollCaptured(
    request,
    (c) => {
      const n = eventsOf(c, 'pm').reduce(
        (acc, ev) => acc + ((ev['s'] as unknown[]) ?? []).length,
        0
      );
      return n >= 20 ? c : undefined;
    },
    { what: '>= 20 pm samples' }
  );
  const samples: Sample[] = [];
  for (const ev of eventsOf(captured, 'pm')) {
    const pm = pmEvent.parse(ev);
    for (const s of pm.s) samples.push(s);
  }
  expect(samples.length).toBeGreaterThanOrEqual(20);

  // Server-side-feature raw material check (computed HERE, never in the SDK):
  // near-linear path and near-constant inter-sample cadence.
  const ratio = pathChordRatio(samples);
  const cv = intervalCv(samples);
  expect(ratio, `path/chord ratio ${ratio} should be <= 1.02 (near-linear)`).toBeLessThanOrEqual(
    1.02
  );
  expect(cv, `inter-sample interval CV ${cv} should be < 0.35`).toBeLessThan(0.35);
});

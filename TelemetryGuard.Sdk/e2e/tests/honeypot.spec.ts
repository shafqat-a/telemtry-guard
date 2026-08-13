/**
 * SDK-06 step 6 — honeypot injection and interaction reporting (§4).
 */
import { test, expect } from '@playwright/test';
import { eventsOf, pollCaptured, resetCaptured } from './util';

test.beforeEach(async ({ request }) => {
  await resetCaptured(request);
});

test('honeypot is injected offscreen and reports input / submit_filled', async ({
  page,
  request,
}) => {
  await page.goto('/fixtures/form.html');

  // Exactly one input named 'website' (the default honeypot name) per form.
  const forms = page.locator('form');
  await expect(forms).toHaveCount(1);
  const hp = page.locator('form input[name="website"]');
  await expect(hp).toHaveCount(1);

  // Attributes pinned by SDK-03: aria-hidden, tabindex -1, offscreen-absolute.
  await expect(hp).toHaveAttribute('aria-hidden', 'true');
  expect(await hp.evaluate((el) => (el as HTMLInputElement).tabIndex)).toBe(-1);
  const placement = await hp.evaluate((el) => {
    const rect = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return { left: rect.left, position: cs.position, cssLeft: cs.left };
  });
  const offscreen =
    placement.left < 0 ||
    (placement.position === 'absolute' && placement.cssLeft === '-9999px');
  expect(offscreen, `honeypot not offscreen: ${JSON.stringify(placement)}`).toBe(true);

  // Fill it the way a naive bot would (programmatic value + input event).
  await hp.evaluate((el) => {
    const input = el as HTMLInputElement;
    input.value = 'https://bot.example';
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });

  const inputHp = await pollCaptured(
    request,
    (c) => {
      const hps = eventsOf(c, 'hp');
      return hps.some((ev) => ev['kind'] === 'input') ? hps : undefined;
    },
    { what: "hp kind:'input'" }
  );
  expect(inputHp.some((ev) => ev['kind'] === 'input')).toBe(true);

  // Submit with the honeypot still filled -> submit_filled must follow.
  await page.click('form button[type="submit"]');
  const submitHp = await pollCaptured(
    request,
    (c) => {
      const hps = eventsOf(c, 'hp');
      return hps.some((ev) => ev['kind'] === 'submit_filled') ? hps : undefined;
    },
    { what: "hp kind:'submit_filled'" }
  );
  expect(submitHp.some((ev) => ev['kind'] === 'submit_filled')).toBe(true);
});

/**
 * SDK-06 shared test helpers: typed access to the mock server's introspection
 * endpoints (/__captured, /__inits, /__reset) and polling utilities.
 * Polling with timeouts (>= 5 s) instead of fixed sleeps keeps the suite
 * deterministic; nothing here depends on timing tighter than the SDK's 2 s
 * flush interval.
 */
import type { APIRequestContext } from '@playwright/test';
import { envelope, type EnvelopeT } from './schema';

export interface Captured {
  receivedAt: number;
  raw: string;
  parsed: EnvelopeT;
  headers: Record<string, string | string[] | undefined>;
}

export interface IssuedInit {
  sid: string;
  k: string;
  nonce: string;
  storageTs: number;
  storageSig: string;
  at: number;
}

export async function resetCaptured(request: APIRequestContext): Promise<void> {
  const res = await request.post('/__reset');
  if (!res.ok()) throw new Error(`/__reset failed: ${res.status()}`);
}

export async function getCaptured(request: APIRequestContext): Promise<Captured[]> {
  const res = await request.get('/__captured');
  if (!res.ok()) throw new Error(`/__captured failed: ${res.status()}`);
  return (await res.json()) as Captured[];
}

export async function getInits(request: APIRequestContext): Promise<IssuedInit[]> {
  const res = await request.get('/__inits');
  if (!res.ok()) throw new Error(`/__inits failed: ${res.status()}`);
  return (await res.json()) as IssuedInit[];
}

/**
 * Polls /__captured until `predicate` returns a truthy value or the timeout
 * elapses (then throws). Default timeout comfortably exceeds the SDK's 2 s
 * flush interval.
 */
export async function pollCaptured<T>(
  request: APIRequestContext,
  predicate: (captured: Captured[]) => T | undefined | false,
  opts: { timeoutMs?: number; intervalMs?: number; what?: string } = {}
): Promise<T> {
  const timeoutMs = opts.timeoutMs ?? 20_000;
  const intervalMs = opts.intervalMs ?? 250;
  const deadline = Date.now() + timeoutMs;
  let last: Captured[] = [];
  for (;;) {
    last = await getCaptured(request);
    const out = predicate(last);
    if (out) return out;
    if (Date.now() >= deadline) {
      throw new Error(
        `timed out after ${timeoutMs} ms waiting for ${opts.what ?? 'captured payloads'} ` +
          `(captured ${last.length} envelope(s))`
      );
    }
    await new Promise((r) => setTimeout(r, intervalMs));
  }
}

/** All events (parsed, unvalidated) across the captured envelopes, in arrival order. */
export function allEvents(captured: Captured[]): Array<Record<string, unknown>> {
  const out: Array<Record<string, unknown>> = [];
  for (const c of captured) {
    const env = envelope.parse(c.parsed);
    for (const ev of env.events) out.push(ev as Record<string, unknown>);
  }
  return out;
}

/** Events of one type across captured envelopes, optionally filtered by sid. */
export function eventsOf(
  captured: Captured[],
  e: string,
  sid?: string
): Array<Record<string, unknown>> {
  const out: Array<Record<string, unknown>> = [];
  for (const c of captured) {
    const env = c.parsed;
    if (!env || (sid !== undefined && env.sid !== sid)) continue;
    for (const ev of env.events) {
      if ((ev as Record<string, unknown>)['e'] === e) out.push(ev as Record<string, unknown>);
    }
  }
  return out;
}

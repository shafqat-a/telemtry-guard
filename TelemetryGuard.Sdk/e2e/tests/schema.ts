/**
 * SDK-06 shared zod schemas — pins the wire contract defined by SDK-02/03/04/05.
 * Every event schema is `.strict()`: an extra property anywhere is a failure.
 * The envelope's `c` checksum is OPTIONAL (added by SDK-05) so this suite
 * passes both before and after SDK-05 lands; where present it is verified.
 */
import { z } from 'zod';

const base = { e: z.string(), t: z.number().int().nonnegative() };

const hex8 = z.string().regex(/^[0-9a-f]{8}$/);

/** input `type`s shipped verbatim as ft by SDK-03's forms collector. */
const ftEnum = z.enum([
  'text',
  'email',
  'tel',
  'password',
  'number',
  'search',
  'url',
  'checkbox',
  'radio',
  'select',
  'textarea',
  'other',
]);

export const pvEvent = z.object({ ...base, e: z.literal('pv') }).strict();

export const pmEvent = z
  .object({
    ...base,
    e: z.literal('pm'),
    s: z.array(z.tuple([z.number().int(), z.number().int(), z.number().int()])).max(200),
  })
  .strict();

const ptEnum = z.enum(['m', 't', 'p', 'u']);

export const pdEvent = z
  .object({
    ...base,
    e: z.literal('pd'),
    tr: z.union([z.literal(0), z.literal(1)]),
    sn: z.number().int().nonnegative(),
    pt: ptEnum,
  })
  .strict();

export const clEvent = z
  .object({
    ...base,
    e: z.literal('cl'),
    tr: z.union([z.literal(0), z.literal(1)]),
    sn: z.number().int().nonnegative(),
    pt: ptEnum,
  })
  .strict();

export const scEvent = z
  .object({ ...base, e: z.literal('sc'), y: z.number().int() })
  .strict();

/** Timing-only keyboard event — EXACTLY {e,t,d} (§3 compliance line). */
export const kyEvent = z
  .object({ ...base, e: z.literal('ky'), d: z.union([z.literal(0), z.literal(1)]) })
  .strict();

export const ffEvent = z
  .object({ ...base, e: z.literal('ff'), fh: hex8, ft: ftEnum })
  .strict();

export const fbEvent = z
  .object({ ...base, e: z.literal('fb'), fh: hex8, ft: ftEnum })
  .strict();

export const fsEvent = z.object({ ...base, e: z.literal('fs'), fh: hex8 }).strict();

export const paEvent = z
  .object({ ...base, e: z.literal('pa'), fk: z.enum(['identity', 'other']) })
  .strict();

export const afEvent = z.object({ ...base, e: z.literal('af'), fh: hex8 }).strict();

export const hpEvent = z
  .object({ ...base, e: z.literal('hp'), kind: z.enum(['focus', 'input', 'submit_filled']) })
  .strict();

export const fiEvent = z
  .object({
    ...base,
    e: z.literal('fi'),
    it: z.enum(['pointer', 'key', 'wheel', 'touch', 'scroll']),
  })
  .strict();

export const rtEvent = z
  .object({
    ...base,
    e: z.literal('rt'),
    fcp: z.number().int().nonnegative(),
    fp: z.number().int().nonnegative().optional(),
  })
  .strict();

const storageSide = z
  .object({
    present: z.boolean(),
    ts: z.number().int().positive().optional(),
    sig: z.string().min(1).optional(),
    ageMs: z.number().int().optional(),
  })
  .strict();

export const fpEvent = z
  .object({
    ...base,
    e: z.literal('fp'),
    vid: z.string().min(1).optional(),
    conf: z.number().optional(),
    scr: z.tuple([z.number(), z.number(), z.number(), z.number()]).optional(),
    tz: z.union([z.string(), z.number()]).optional(),
    langs: z.array(z.string()).max(5).optional(),
    canvasBlocked: z.boolean().optional(),
    touch: z.boolean().optional(),
    mob: z.boolean().optional(),
    wd: z.boolean().optional(),
    botd: z.object({ bot: z.boolean(), kind: z.string().optional() }).strict().optional(),
    storage: z
      .object({
        ck: storageSide,
        ls: storageSide,
        fresh: z.boolean(),
        cookiesDisabled: z.boolean(),
      })
      .strict()
      .optional(),
  })
  .strict();

/** Every event the SDK may emit — anything else is a contract break. */
export const anyEvent = z.discriminatedUnion('e', [
  pvEvent,
  pmEvent,
  pdEvent,
  clEvent,
  scEvent,
  kyEvent,
  ffEvent,
  fbEvent,
  fsEvent,
  paEvent,
  afEvent,
  hpEvent,
  fiEvent,
  rtEvent,
  fpEvent,
]);

export const envelope = z
  .object({
    k: z.string().min(1),
    sid: z.string().min(8),
    visit_id: z.string().uuid(),
    seq: z.number().int().nonnegative(),
    nonce: z.string(),
    sent_at: z.number().int().positive(),
    u: z.string().optional(),
    r: z.string().optional(),
    ck: z.record(z.string()).optional(),
    events: z.array(z.unknown()).min(1),
    c: z.string().regex(/^[0-9a-f]{8}$/).optional(), // added by SDK-05; optional until it lands
  })
  .strict();

export type EnvelopeT = z.infer<typeof envelope>;

/** FNV-1a 32-bit over UTF-8 bytes — must match src/util.ts and API-04's verifier. */
export function fnv1aHex(input: string): string {
  let h = 0x811c9dc5;
  const bytes = new TextEncoder().encode(input);
  for (let i = 0; i < bytes.length; i++) {
    h ^= bytes[i]!;
    h = (Math.imul(h, 0x01000193) >>> 0);
  }
  return h.toString(16).padStart(8, '0');
}

/** Canonical key order pinned on the raw wire string. */
export const KEY_ORDER_RE = /^\{"k":.*"sid":.*"visit_id":.*"seq":.*"nonce":.*"sent_at":.*"events":/s;

/**
 * Raw-string checks for one captured payload:
 * - canonical key order (regex on the raw string);
 * - where `c` is present, FNV-1a over `raw[0 .. lastIndexOf(',"c":"')) + '}'`
 *   must equal `c` (SDK-05 wire contract).
 * Throws with a descriptive message on failure.
 */
export function verifyRawEnvelope(raw: string): void {
  if (!KEY_ORDER_RE.test(raw)) {
    throw new Error(`envelope key order violated: ${raw.slice(0, 120)}...`);
  }
  const idx = raw.lastIndexOf(',"c":"');
  if (idx === -1) return; // c is optional until SDK-05 lands
  const m = /,"c":"([0-9a-f]{8})"\}$/.exec(raw);
  if (!m) {
    throw new Error(`malformed trailing checksum field: ...${raw.slice(-40)}`);
  }
  const expected = fnv1aHex(raw.slice(0, idx) + '}');
  if (m[1] !== expected) {
    throw new Error(`checksum mismatch: payload says ${m[1]}, recomputed ${expected}`);
  }
}

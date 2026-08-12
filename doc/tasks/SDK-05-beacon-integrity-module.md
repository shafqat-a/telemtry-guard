---
id: SDK-05
title: Beacon integrity module
phase: 1
workstream: sdk
depends_on: [SDK-02]
size: S
spec_refs: ["§7 T1 beacon_integrity_ok", D2]
detail_level: full
---

# SDK-05: Beacon integrity module

## Objective

Replace the `seal()` stub from SDK-02 with the real beacon-integrity module: canonical field ordering, monotonic sequence, nonce echo, `sent_at`, and an FNV-1a checksum appended as the final envelope field — all in a module that adds **under 2 KB** to the bundle.

## Threat model — read this before implementing (honesty required)

**Client-side JavaScript is fully inspectable by the attacker.** Anyone can open `tg.js`, read the checksum algorithm, extract the nonce from their own `/i/init` call, and craft perfectly "valid" envelopes. This module therefore provides **zero cryptographic security** and is not a security boundary. What it actually buys:

1. **Raises attacker effort.** Naïve replay/spam tooling (curl loops, cheap bot kits) that doesn't execute our JS or reimplement the checksum produces malformed envelopes — instantly detectable. Sophisticated attackers must at minimum run the SDK or faithfully clone the algorithm; every algorithm change (shipped instantly via "latest from CDN", D22) breaks their clones until they re-reverse it.
2. **Detects accidental corruption** — truncated beacons, proxy mangling, extension interference.
3. **Feeds the real signal:** the authoritative **`beacon_integrity_ok`** verdict (§7, T1: `beacon_integrity_ok=false` → score floor ≥85) is computed **server-side in API-04** from things the client *cannot* self-attest: nonce match against the server-issued value for that `sid`, **seq continuity** across the session (gaps/duplicates/regressions), **timing plausibility** (`sent_at` vs server receive time drift), and checksum validity. The client module just makes envelopes *checkable*; the server does the checking.

Never present this module as tamper-proofing. A comment block at the top of `integrity.ts` must restate this threat model (3–4 lines) so future maintainers don't "harden" it with client-side secrets — there is no place to hide a secret in shipped JS.

## Spec context (self-contained)

- **§7 T1 `beacon_integrity_ok=false` (score floor ≥85)** — a near-deterministic fraud rule when it fires; absence proves nothing. Server-side (API-04) verification inputs: nonce echo, monotonic seq, `sent_at`, checksum.
- **Envelope contract (defined in SDK-02, restated):** canonical key order `k, sid, seq, nonce, sent_at, events`; `seq` starts at 0 and increments once per envelope send attempt (retries don't exist — fire-and-forget; seq gaps are a deliberate server-visible signal); `nonce` is echoed verbatim from `GET /i/init` (empty string when init failed — itself informative); `sent_at` is `Date.now()` epoch ms at flush.
- **D2:** bundle budget ≤ 30 KB gzip overall; this module specifically must stay **< 2 KB** of minified bundle contribution (it rides the hot flush path and the budget belongs to detection payload, not plumbing).

## Prerequisites

- **SDK-02** shipped: `src/integrity.ts` containing the stub `export function seal(env: Envelope): string { return JSON.stringify(env); }`; `src/transport.ts` already routes every flush through `seal()`; `src/types.ts` exports `Envelope { k, sid, seq, nonce, sent_at, events }`; `src/util.ts` exports `fnv1aHex(input: string): string` (FNV-1a 32-bit over UTF-8 bytes via `TextEncoder`, `Math.imul`-based, lowercase hex padded to 8 chars). Monotonic seq and nonce storage already live in `src/state.ts`/`src/transport.ts`.
- **Cross-file coordination (not a build dependency — the contract flows FROM this module):** API-04 owns the server-side verifier; the serialization rules below are the shared contract — mirror them exactly in the C# verifier (cross-reference this section from API-04's implementation). Nothing here requires API-04 to be built first.

## Implementation steps

1. Rewrite **`TelemetryGuard.Sdk/src/integrity.ts`**:

   ```ts
   import type { Envelope } from './types';
   import { fnv1aHex } from './util';

   /**
    * THREAT MODEL: shipped JS is fully attacker-inspectable; this checksum only
    * raises attacker effort and catches corruption. The authoritative
    * beacon_integrity_ok verdict is computed SERVER-SIDE (API-04): nonce match,
    * seq continuity, timing plausibility, checksum. Do not add client "secrets".
    */
   export function seal(env: Envelope): string {
     // Canonical order enforced by reconstruction — never trust caller key order.
     const ordered = {
       k: env.k, sid: env.sid, seq: env.seq, nonce: env.nonce,
       sent_at: env.sent_at, events: env.events,
     };
     const json = JSON.stringify(ordered);          // no whitespace; UTF-8 on the wire
     const c = fnv1aHex(json);                      // checksum over the json WITHOUT c
     return json.slice(0, -1) + ',"c":"' + c + '"}';
   }
   ```

2. **Wire-format contract for the server verifier (API-04)** — document as a comment in the file and mirror in C#:
   - Payload is UTF-8 JSON, single line, no whitespace, ending in `,"c":"<8 lowercase hex chars>"}`.
   - Verify: locate the **last** occurrence of `,"c":"`; `prefix = payload[0 .. idx) + '}'`; recompute FNV-1a 32-bit (offset basis `0x811c9dc5`, prime `0x01000193`) over the **UTF-8 bytes** of `prefix`; compare to the hex value. Then independently check: `nonce` equals the value issued to this `sid` by `/i/init`; `seq` continuity per `sid` (track gaps/duplicates); `sent_at` within plausible drift of server receive time.
   - `events` array contents are checksummed as serialized — the client does not sort or normalize event fields; whatever order `JSON.stringify` produced is canonical because the checksum and payload are generated from the *same* string.

3. Confirm `src/transport.ts` needs no change (it already calls `seal`); if SDK-02 left any direct `JSON.stringify(envelope)` call in the send path, remove it so **every** envelope — including hidden/pagehide flushes — goes through `seal`.

4. **Size check (< 2 KB):** rebuild with `npm run build`, then inspect `dist/meta.json` (esbuild metafile written by SDK-01's build script):

   ```
   node -e "const m=require('./dist/meta.json');const o=Object.values(m.outputs)[0];for(const [f,i] of Object.entries(o.inputs))if(f.includes('integrity'))console.log(f,i.bytesInOutput,'bytes')"
   ```

   The `src/integrity.ts` contribution must be **< 2048 bytes** in the minified output. (It will be far under; the constraint guards against future bloat — record the measured number in `TelemetryGuard.Sdk/README.md`.)

5. **Mechanical self-test — `TelemetryGuard.Sdk/scripts/check-integrity.mjs`**, wired as `"test:integrity": "node scripts/check-integrity.mjs"` in `package.json`. This verifies `seal()` end-to-end with zero manual payload capture, so the module is provably correct before SDK-06's browser suite exists (esbuild is already a devDependency; no new deps):

   ```js
   import { build } from 'esbuild';
   import { pathToFileURL } from 'node:url';

   // Bundle src/integrity.ts (+ its imports) into an importable ESM file.
   await build({ entryPoints: ['src/integrity.ts'], bundle: true, format: 'esm',
                 platform: 'neutral', outfile: 'dist/integrity.test.mjs', logLevel: 'silent' });
   const { seal } = await import(pathToFileURL('dist/integrity.test.mjs'));

   const fnv = (s) => { let h = 0x811c9dc5;
     for (const b of new TextEncoder().encode(s)) { h ^= b; h = Math.imul(h, 0x01000193) >>> 0; }
     return h.toString(16).padStart(8, '0'); };
   const fail = (m) => { console.error('INTEGRITY CHECK FAILED:', m); process.exit(1); };

   const p = seal({ k: 'k1', sid: 'a'.repeat(32), seq: 0, nonce: '',
                    sent_at: 1723400001234, events: [{ e: 'pv', t: 12 }] });

   // (a) final key is c with exactly 8 lowercase hex chars; canonical key order preserved.
   if (!/,"c":"[0-9a-f]{8}"\}$/.test(p)) fail('final key is not c:<8 lowercase hex>');
   if (!/^\{"k":"k1","sid":"a{32}","seq":0,"nonce":"","sent_at":1723400001234,"events":/.test(p))
     fail('canonical key order violated');
   // (b) recomputing FNV-1a over the payload minus the c field reproduces the embedded value.
   const i = p.lastIndexOf(',"c":"');
   const expect = fnv(p.slice(0, i) + '}');
   const got = p.slice(i + 7, i + 15);
   if (expect !== got) fail(`checksum mismatch: recomputed ${expect}, embedded ${got}`);
   // (c) flipping one byte breaks the match.
   const flipped = p.slice(0, 10) + (p[10] === 'x' ? 'y' : 'x') + p.slice(11);
   if (fnv(flipped.slice(0, flipped.lastIndexOf(',"c":"')) + '}') === got) fail('byte flip not detected');
   console.log('integrity check OK:', got);
   ```

   The fixture envelope deliberately uses `nonce: ''` so the init-failed shape is covered too.

6. `npm run typecheck && npm run build && npm run size` — overall 30 KB gate still passes.

## Files to create or modify

- `TelemetryGuard.Sdk/src/integrity.ts` (rewrite stub → real `seal` + threat-model comment)
- `TelemetryGuard.Sdk/src/transport.ts` (only if a bypass of `seal` exists — remove it)
- `TelemetryGuard.Sdk/scripts/check-integrity.mjs` (create — mechanical `seal()` self-test, step 5)
- `TelemetryGuard.Sdk/package.json` (modify — add the `test:integrity` script)
- `TelemetryGuard.Sdk/README.md` (modify — record integrity module size measurement)

## Acceptance criteria

- `npm run typecheck && npm run build && npm run size` exit 0.
- **`npm run test:integrity` exits 0** (step 5): it seals the pinned fixture envelope and mechanically asserts (a) the final key is `c` with exactly 8 lowercase hex chars and key order is exactly `k, sid, seq, nonce, sent_at, events, c`; (b) recomputing FNV-1a over the payload minus the `c` field reproduces the embedded checksum; (c) flipping a single byte breaks the match. No manual payload capture is required to accept this task.
- `seq` values across consecutive captured envelopes are 0,1,2,… monotonic; `nonce` matches the `/i/init` value verbatim.
- Metafile shows `src/integrity.ts` contributing < 2048 bytes to `dist/tg.js`.
- The threat-model comment is present at the top of `integrity.ts`.

## Testing

- SDK-06 adds automated envelope-schema + checksum verification (zod schema includes `c`; test recomputes FNV-1a). Keep the exact serialization above so those tests and API-04's C# verifier both pin it.
- Unit-style check now: `npm run test:integrity` (step 5) — mechanical, browser-free, and its fixture covers the `nonce:""` (init-failed) shape. Optionally spot-check one real captured `/i` payload with the same recompute logic, but acceptance does not depend on it.

## Out of scope / guardrails

- **No cryptography:** no HMAC, no keys, no obfuscation theater. A client secret in shipped JS is a contradiction — the threat model above is the design. Signature-grade trust lives server-side only (`/i/init`'s `storageSig` is signed by the *server*, which has a key vault; the client never signs anything).
- **The client never computes `beacon_integrity_ok`** — no self-assessment field in the envelope. API-04 owns the verdict (T1 rule, floor ≥85; rules only ever raise scores, never lower).
- Do not change envelope semantics owned by SDK-02: no retries (seq gaps stay meaningful), no reordering, no extra top-level fields beyond `c`, no compression.
- Module stays < 2 KB minified contribution; no new dependencies; overall 30 KB gzip gate holds (D2).
- Do not normalize/sort `events` before checksumming — checksum whatever string ships; canonicalization beyond top-level key order is explicitly not wanted (it adds bytes and server cost for nothing).

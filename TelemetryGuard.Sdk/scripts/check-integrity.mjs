import { build } from 'esbuild';
import { pathToFileURL } from 'node:url';

// Bundle src/integrity.ts (+ its imports) into an importable ESM file.
await build({
  entryPoints: ['src/integrity.ts'],
  bundle: true,
  format: 'esm',
  platform: 'neutral',
  outfile: 'dist/integrity.test.mjs',
  logLevel: 'silent',
});
const { seal } = await import(pathToFileURL('dist/integrity.test.mjs'));

const fnv = (s) => {
  let h = 0x811c9dc5;
  for (const b of new TextEncoder().encode(s)) {
    h ^= b;
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h.toString(16).padStart(8, '0');
};
const fail = (m) => {
  console.error('INTEGRITY CHECK FAILED:', m);
  process.exit(1);
};

// Fixture deliberately uses nonce: '' so the init-failed shape is covered too.
// Shape mirrors the shipped envelope: session_id (persistent session), sid (= visit id,
// the key the server scopes nonce/seq on) and visit_id, then seq/nonce/sent_at, then
// the SDK-09 page context (u/r/ck), then events.
const p = seal({
  k: 'k1',
  session_id: 'b'.repeat(32),
  sid: '11111111-2222-4333-8444-555555555555',
  visit_id: '11111111-2222-4333-8444-555555555555',
  seq: 0,
  nonce: '',
  sent_at: 1723400001234,
  u: 'https://example.test/landing?x=1',
  r: '',
  ck: { _ga: 'GA1.1.1' },
  events: [{ e: 'pv', t: 12 }],
});

// (a) final key is c with exactly 8 lowercase hex chars; canonical key order preserved.
if (!/,"c":"[0-9a-f]{8}"\}$/.test(p)) fail('final key is not c:<8 lowercase hex>');
if (
  !/^\{"k":"k1","session_id":"b{32}","sid":"11111111-2222-4333-8444-555555555555","visit_id":"11111111-2222-4333-8444-555555555555","seq":0,"nonce":"","sent_at":1723400001234,"u":"https:\/\/example\.test\/landing\?x=1","r":"","ck":\{"_ga":"GA1\.1\.1"\},"events":/.test(
    p
  )
)
  fail('canonical key order violated');
// (b) recomputing FNV-1a over the payload minus the c field reproduces the embedded value.
const i = p.lastIndexOf(',"c":"');
const expect = fnv(p.slice(0, i) + '}');
const got = p.slice(i + 6, i + 14); // ,"c":" is 6 chars; hex is the next 8
if (expect !== got) fail(`checksum mismatch: recomputed ${expect}, embedded ${got}`);
// (c) flipping one byte breaks the match.
const flipped = p.slice(0, 10) + (p[10] === 'x' ? 'y' : 'x') + p.slice(11);
if (fnv(flipped.slice(0, flipped.lastIndexOf(',"c":"')) + '}') === got) fail('byte flip not detected');
console.log('integrity check OK:', got);

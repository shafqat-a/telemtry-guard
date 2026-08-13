import { readFileSync } from 'node:fs';
import { gzipSync } from 'node:zlib';

const LIMIT = 30 * 1024; // D2: bundle must stay <= 30 KB gzipped
const raw = readFileSync('dist/tg.js');
const gz = gzipSync(raw).length;
console.log(`dist/tg.js  raw=${raw.length} B  gzip=${gz} B  limit=${LIMIT} B`);
if (gz > LIMIT) {
  console.error(`SIZE GATE FAILED: gzip ${gz} B exceeds ${LIMIT} B`);
  process.exit(1);
}
console.log('size gate OK');

/**
 * FNV-1a 32-bit over UTF-8 bytes, lowercase hex, zero-padded to 8 chars.
 * Used by SDK-03 field hashing and SDK-05 checksums; the algorithm must match
 * the server, so it is pinned here.
 */
export function fnv1aHex(input: string): string {
  let h = 0x811c9dc5;
  const bytes = new TextEncoder().encode(input);
  for (let i = 0; i < bytes.length; i++) {
    h ^= bytes[i]!;
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h.toString(16).padStart(8, '0');
}

/** Integer ms since performance.timeOrigin. */
export function nowT(): number {
  return Math.round(performance.now());
}

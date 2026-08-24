import type { Envelope } from './types';
import { fnv1aHex } from './util';

/**
 * THREAT MODEL: shipped JS is fully attacker-inspectable; this checksum only
 * raises attacker effort and catches corruption. The authoritative
 * beacon_integrity_ok verdict is computed SERVER-SIDE (API-04): nonce match,
 * seq continuity, timing plausibility, checksum. Do not add client "secrets".
 *
 * WIRE-FORMAT CONTRACT (mirror exactly in API-04's C# verifier):
 * - Payload is UTF-8 JSON, single line, no whitespace, canonical key order
 *   k, session_id, sid, visit_id, seq, nonce, sent_at, u, r, ck, events, ending in ,"c":"<8 lowercase hex>"}
 *   (u/r/ck are SDK-09 page context and are omitted by JSON.stringify when undefined;
 *   the verifier never needs the field list — it hashes the prefix as serialized)
 * - Verify: locate the LAST occurrence of ,"c":"; prefix = payload[0..idx) + '}';
 *   recompute FNV-1a 32-bit (offset basis 0x811c9dc5, prime 0x01000193) over the
 *   UTF-8 bytes of prefix; compare to the hex value. Then independently check:
 *   nonce equals the value issued to this sid by /i/init; seq continuity per sid
 *   (gaps/duplicates); sent_at within plausible drift of server receive time.
 * - The events array is checksummed as serialized — the client does not sort or
 *   normalize event fields; whatever order JSON.stringify produced is canonical
 *   because the checksum and payload are generated from the same string.
 */
export function seal(env: Envelope): string {
  // Canonical order enforced by reconstruction — never trust caller key order.
  const ordered = {
    k: env.k,
    session_id: env.session_id,
    sid: env.sid,
    visit_id: env.visit_id,
    seq: env.seq,
    nonce: env.nonce,
    sent_at: env.sent_at,
    u: env.u,
    r: env.r,
    ck: env.ck,
    events: env.events,
  };
  const json = JSON.stringify(ordered); // no whitespace; UTF-8 on the wire
  const c = fnv1aHex(json); // checksum over the json WITHOUT c
  return json.slice(0, -1) + ',"c":"' + c + '"}';
}

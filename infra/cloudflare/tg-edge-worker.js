// TelemetryGuard edge worker (INT-05). Deployed to Cloudflare via wrangler — this is
// edge configuration, NOT part of the .NET backend (see doc/spec.md D1/D13).
// Forwards request.cf signals to origin as X-TG-* headers; strips inbound spoofs.
export default {
  async fetch(request) {
    const h = new Headers(request.headers);
    // Anti-spoof: never trust client-supplied values for our signal headers.
    for (const n of ["X-TG-JA3", "X-TG-JA4", "X-TG-ASN", "X-TG-Bot-Score"]) h.delete(n);

    const cf = request.cf ?? {};
    if (cf.asn) h.set("X-TG-ASN", String(cf.asn));                    // all plans
    const bm = cf.botManagement ?? {};                                 // Enterprise Bot Management only
    if (bm.ja3Hash) h.set("X-TG-JA3", bm.ja3Hash);
    if (bm.ja4) h.set("X-TG-JA4", bm.ja4);
    if (typeof bm.score === "number") h.set("X-TG-Bot-Score", String(bm.score));

    return fetch(new Request(request, { headers: h }));
  }
};

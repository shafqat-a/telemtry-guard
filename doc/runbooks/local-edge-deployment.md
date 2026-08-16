# Runbook: API behind a local nginx and a platform edge

How the API is deployed on the cloudlabs box that serves the Bangladesh University
WordPress instances. The shape generalizes to any "platform edge → local reverse proxy →
Kestrel" deployment, which is the case the Cloudflare runbook does **not** cover.

```
visitor ──HTTPS──▶ OPNsense edge (192.168.9.1) ──HTTP──▶ nginx (this host) ──▶ Kestrel 127.0.0.1:5120
                   terminates TLS,                        location ^~ /tg/
                   sets X-Forwarded-For/-Proto            appends to X-Forwarded-For
```

The API is exposed as a **path on the site's own hostname** (`/tg`), not a hostname of its
own. That is deliberate: same-origin means no CORS, no extra DNS record, and no new entry
on the shared edge SAN certificate (a manual re-signing step on that platform).

## Service

`/etc/systemd/system/telemetryguard-api.service` runs the published app from
`/opt/telemetryguard` as the `shafqat` user, with configuration and secrets in
`/etc/telemetryguard/tg.env` (root-owned, `0600` — systemd reads it before dropping
privileges, so the secrets never sit in the repo or in a user-readable file).

```bash
sudo systemctl status telemetryguard-api
sudo journalctl -u telemetryguard-api -f
```

Redeploy after a code change — the published copy is a snapshot, so a rebuild in the
working tree changes nothing until this runs:

```bash
dotnet publish TelemetryGuard.Api -c Release -o /tmp/tg-publish
sudo rsync -a --delete /tmp/tg-publish/ /opt/telemetryguard/
sudo systemctl restart telemetryguard-api
```

The datastores are the FND-02 compose containers on the same host; they carry
`--restart unless-stopped` so a reboot brings them back before the API needs them. The
API starts regardless and reports `/ready` 503 until they answer — `/healthz` (liveness)
stays 200, which is what the proxy should health-check.

## The two-hop proxy chain

This is the part that silently ruins the data if it is wrong.

`ForwardedHeaders:ForwardLimit` defaults to **1**, which unwinds a single proxy. Here
there are two — the edge and the local nginx — and nginx appends
(`$proxy_add_x_forwarded_for`), so the API receives `X-Forwarded-For: <client>, 192.168.9.1`
on a socket from `127.0.0.1`. With the default limit the app resolves the **edge's**
address for every visitor: one IP for all traffic, so geo, ASN, `usage_type` and every
velocity counter become meaningless while still looking populated.

Correct configuration (in `tg.env`):

```
ForwardedHeaders__TrustedProxyCidrs__0=127.0.0.1/32
ForwardedHeaders__TrustedProxyCidrs__1=192.168.9.1/32
ForwardedHeaders__ForwardLimit=2
Edge__Provider=None
```

Trust and limit must be raised **together**. The limit only says how far the middleware
may walk; it still stops at the first address that is not in `TrustedProxyCidrs`, which is
what makes a client-supplied `X-Forwarded-For` unusable for spoofing. Verify both
properties after any change to the chain:

```bash
K=<site key>
curl -s -o /dev/null "https://<site>/tg/p.gif?k=$K"                                  # honest
curl -s -o /dev/null -H "X-Forwarded-For: 8.8.8.8" "https://<site>/tg/p.gif?k=$K"    # spoof

curl -s "http://127.0.0.1:8123/?user=tg&password=<pw>&database=telemetry_guard" --data-binary \
  "SELECT kind, IPv6NumToString(ip), country, asn_type FROM tg_events
   ORDER BY timestamp DESC LIMIT 2 FORMAT TSVWithNames"
```

Both rows must show **your** address: not `192.168.9.1` (chain too short) and not
`8.8.8.8` (forged header believed).

`Edge:Provider` stays `None` because the edge is OPNsense, not Cloudflare — the `X-TG-*`
edge-signal intake and the embedded Cloudflare CIDR merge (INT-05) must not be enabled
here, and TLS-fingerprint features stay null as a result.

## nginx

```nginx
location ^~ /tg/ {
    proxy_pass         http://127.0.0.1:5120/;
    proxy_http_version 1.1;
    proxy_set_header   Host              $host;
    proxy_set_header   X-Real-IP         $remote_addr;
    proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header   X-Forwarded-Proto $scheme;
    proxy_connect_timeout 2s;
    proxy_read_timeout    10s;
    proxy_intercept_errors off;
}
```

`^~` is required, not stylistic: a WordPress vhost carries a regex location for static
assets (`~* \.(…|js|…)$`), and **regex locations outrank plain prefix locations**. Written
as `location /tg/`, the request for `/tg/sdk/tg.js` matches the asset regex instead and is
served from disk as a 404 — the tag loads nothing and no telemetry is collected, with
everything else looking healthy.

Short timeouts are also deliberate: telemetry must never hold up a page render.

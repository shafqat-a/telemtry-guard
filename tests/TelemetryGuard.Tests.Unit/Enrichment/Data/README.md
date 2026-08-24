# iplegence test fixture

`iplegence-fixture.mmdb` (4 KB) is a hand-built database in the **exact record layout**
of iplegence's `Superior-IP.mmdb` — `database_type: iplegence-Superior-IP`, nested
`country` / `continent` / `city` / `location` / `subdivisions` / `postal` / `asn` /
`traits`, English names only, empty fields and false traits omitted (see
`iplegence/internal/schema/record.go`, `ToMMDBType`).

It exists so `IplegenceProviderTests` can assert the trait→flag and usage_type→`AsnType`
mappings deterministically without shipping the real ~130 MB daily build. The same decode
path is also verified against a real `dist/Superior-IP.mmdb` by
`RealIplegenceDatabaseTests` (opt-in, `IPLEGENCE_MMDB`).

## Rows

| Prefix | What it covers |
|---|---|
| `8.8.8.0/24` | full geo (city, coordinates, timezone, subdivision, postal) + ASN 15169; `usage_type=hosting` (peeringdb) with **no** prefix flags — the type alone must drive `AsnType` and `IsDatacenter` |
| `24.48.0.0/16` | `usage_type=residential` (peeringdb), no flags — a known-clean row |
| `45.32.0.0/16` | `is_hosting_provider` + `usage_type=hosting` (prefix_flag), ASN 20473 |
| `104.16.0.0/13` | `is_cdn` + `usage_type=hosting` on ASN 13335 — proves CDN wins over Datacenter |
| `185.220.101.0/24` | `is_tor_exit_node` + `is_anonymous` |
| `51.15.0.0/16` | `is_public_proxy` + `is_anonymous_vpn` + `is_anonymous` |
| `203.0.113.0/24` | `is_relay`, outside the embedded Apple egress CIDRs — proves the trait alone sets `IsPrivateRelay` |
| `208.54.0.0/16` | `usage_type=mobile` (asn_name) — the classification the pre-usage_type dataset could not express |
| `44.224.0.0/11` | `usage_type=education` on ASN 16509, which **is** in the embedded datacenter seed — proves the inferred type outranks the seed, and that the row is not reported as a datacenter |
| `149.101.0.0/16` | `usage_type=government` (asn_name) |
| `194.60.0.0/16` | `usage_type=business` (peeringdb) |
| `196.201.0.0/16` | a row with **no** `usage_type` at all — `AsnType.Unknown`, the ~54% case |
| `2001:db8::/32` | IPv6 row, ASN 64512 (not in the seed), untyped |

Any address outside these prefixes (e.g. `9.9.9.9`) decodes as "not found" — every
dataset-derived field null, which is NOT the same as "clean".

## Regenerating

`iplegence-fixture.gen.go` is the generator. It is **not** part of any build — Go is not
a runtime dependency of this repo (D1); it is only used to author the binary fixture,
because MMDB writers exist in Go/Perl and not in .NET. Run it from a scratch directory:

```bash
mkdir /tmp/fixgen && cp iplegence-fixture.gen.go /tmp/fixgen/main.go && cd /tmp/fixgen
go mod init fixgen && go get github.com/maxmind/mmdbwriter@v1.0.0
go run . iplegence-fixture.mmdb
```

Then copy the result back over this directory's copy and re-run the unit tests.

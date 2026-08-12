---
id: SDK-08
title: SDK bundle delivery (/sdk/tg.js route and snippet URL contract)
phase: 1
workstream: sdk
depends_on: [SDK-01, API-01]
size: S
spec_refs: [D22, D2, D1, "§11.3"]
detail_level: full
---

# SDK-08: SDK bundle delivery (/sdk/tg.js route and snippet URL contract)

## Objective

Give tenants a real URL to load the snippet from — the deliverable D22 promises (`<script src="https://cdn.../tg.js">`) that SDK-01 explicitly does not host. MVP decision (this task pins it): the **API host origin-serves the built bundle** at `GET /sdk/tg.js` (latest) and `GET /sdk/tg-<version>.js` (version-pinned), with cache headers implementing D22's two delivery modes; Cloudflare fronting (INT-05) supplies the CDN layer in production. Without this task Phase 1's "SDK + web-pixel mode" is not integrable — the pixel path has a URL (API-03's `/p.gif`) but the JS bundle is homeless.

## Spec context (self-contained)

- **D22 — delivery is "latest from CDN" by default** (detection fixes propagate in minutes) **plus a version-pinned URL** for tenants requiring change control. SDK-01 already emits both artifacts (`dist/tg.js`, `dist/tg-<version>.js`); this task serves them. The spec defers *where* they live — MVP answer: the API origin, fronted by Cloudflare (INT-05). Object storage / a dedicated CDN bucket is deliberately postponed to the Azure deployment work (P2-04) — one origin, one TLS story, zero new infrastructure now.
- **Snippet URL contract (pinned here, referenced by docs and SDK-06's generator):**
  - default: `https://<api-host>/sdk/tg.js`
  - change-controlled: `https://<api-host>/sdk/tg-<version>.js`
- **Cache semantics implement D22:** latest → `Cache-Control: public, max-age=300, stale-while-revalidate=60` (a detection fix reaches every tenant page within ~5 minutes through Cloudflare); pinned → `Cache-Control: public, max-age=31536000, immutable` (the content of a versioned file never changes — SDK-01 bumps `package.json` versions instead).
- **Zero-config bootstrap synergy (SDK-02):** the SDK defaults its beacon `endpoint` to the **origin of its own `script src`**. Serving the bundle from the API origin therefore makes the minimal snippet — no `data-endpoint` at all — work out of the box.
- **D1:** static files served by ASP.NET Core; no Node server anywhere near production. **D2:** the artifact is a static IIFE bundle; this task adds no runtime code to it.
- Local dev (FND-02): the API runs on the host via `dotnet run` (it is deliberately *not* a compose service), so `http://localhost:<api-port>/sdk/tg.js` works as soon as the SDK has been built once — this is also what SDK-06's bot generator probes as `{target}/sdk/tg.js`.

## Prerequisites

- **SDK-01**: `npm run build` produces `TelemetryGuard.Sdk/dist/tg.js`, `dist/tg-<version>.js` (+ `.map` files).
- **API-01**: `TelemetryGuard.Api` host and `Program.cs` middleware pipeline exist (this task adds a static-file mapping to it; coordinate placement with API-01's middleware-order table — static files go early, before tenant resolution, since `/sdk/*` is tenant-agnostic).
- FND-03's CI workflow exists (conditional edits only, as SDK-01 step 9 does).

## Implementation steps

1. **Artifact hand-off — MSBuild copy target** in `TelemetryGuard.Api/TelemetryGuard.Api.csproj`:

   ```xml
   <ItemGroup>
     <SdkBundle Include="..\TelemetryGuard.Sdk\dist\tg*.js;..\TelemetryGuard.Sdk\dist\tg*.js.map" />
   </ItemGroup>
   <Target Name="CopySdkBundle" BeforeTargets="Build" Condition="@(SdkBundle) != ''">
     <Copy SourceFiles="@(SdkBundle)" DestinationFolder="$(MSBuildProjectDirectory)\wwwroot\sdk" SkipUnchangedFiles="true" />
   </Target>
   ```

   The condition makes backend-only builds succeed when the SDK has never been built (`/sdk/*` then 404s — honest and harmless in dev). Add `TelemetryGuard.Api/wwwroot/sdk/` to `.gitignore` (built artifact, never committed — same rule as `dist/`).

2. **Route mapping** in `TelemetryGuard.Api/Program.cs`: serve **only** the `wwwroot/sdk` physical directory at request path `/sdk` via `UseStaticFiles` with a scoped `PhysicalFileProvider` (no general wwwroot serving; no directory browsing), registered **before** rate limiting and tenant resolution. In `OnPrepareResponse`:
   - filename matches `^tg-\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?\.js(\.map)?$` → `Cache-Control: public, max-age=31536000, immutable`;
   - otherwise (`tg.js`, `tg.js.map`) → `Cache-Control: public, max-age=300, stale-while-revalidate=60`;
   - always: `Content-Type: text/javascript; charset=utf-8` for `.js` (`application/json` for `.map`), `Access-Control-Allow-Origin: *`, `Cross-Origin-Resource-Policy: cross-origin`, `X-Content-Type-Options: nosniff`.

3. **CI ordering (conditional edit, FND-03's `.github/workflows/ci.yml`):** in the `sdk` job, upload `TelemetryGuard.Sdk/dist/` as an artifact (`upload-artifact`, name `sdk-dist`) after the size gate, if no such step exists. No download step is needed yet — CI does not build a deployable API image (containerizing is P2-04, which must copy `wwwroot/sdk` into the image; leave a one-line note in that workflow comment).

4. **Document the snippet** in `TelemetryGuard.Sdk/README.md` under "Installing":

   ```html
   <!-- default: latest, fixes propagate within minutes (D22) -->
   <script async src="https://<api-host>/sdk/tg.js" data-site-key="YOUR_SITE_KEY"></script>
   <!-- change-controlled alternative -->
   <script async src="https://<api-host>/sdk/tg-0.1.0.js" data-site-key="YOUR_SITE_KEY"></script>
   ```

   Note explicitly: no `data-endpoint` needed when loading from the API origin (SDK-02's default); cross-origin hosting requires `data-endpoint`.

5. Verify locally: `cd TelemetryGuard.Sdk && npm run build`, then `dotnet run --project TelemetryGuard.Api` and curl the acceptance checks below.

## Files to create or modify

- `TelemetryGuard.Api/TelemetryGuard.Api.csproj` (modify — `CopySdkBundle` target)
- `TelemetryGuard.Api/Program.cs` (modify — scoped `/sdk` static-file mapping + headers)
- `.gitignore` (modify — `TelemetryGuard.Api/wwwroot/sdk/`)
- `.github/workflows/ci.yml` (modify — `sdk-dist` artifact upload in the `sdk` job, only if missing)
- `TelemetryGuard.Sdk/README.md` (modify — snippet URL contract / install docs)
- `tests/TelemetryGuard.Tests.Unit/Api/SdkDeliveryTests.cs` (create — `WebApplicationFactory` header/404 assertions)

## Acceptance criteria

- With the SDK built and the API running: `curl -sI http://localhost:<api-port>/sdk/tg.js` → `200`, `Content-Type: text/javascript; charset=utf-8`, `Cache-Control: public, max-age=300, stale-while-revalidate=60`; body is byte-identical to `TelemetryGuard.Sdk/dist/tg.js`.
- `curl -sI .../sdk/tg-0.1.0.js` → `200` with `Cache-Control: public, max-age=31536000, immutable`; the `.map` files are served with their bundle's cache policy.
- A fixture page loading `<script src="http://localhost:<api-port>/sdk/tg.js" data-site-key="k1">` **without** `data-endpoint` sends `/i/init` and `/i` to the API origin (proves the zero-config install path).
- With `wwwroot/sdk` absent (fresh clone, no SDK build): `dotnet build` and startup succeed; `/sdk/tg.js` → `404`; no exception.
- `/sdk/` (directory), `/sdk/nonexistent.js`, and traversal shapes (`/sdk/..%2fappsettings.json`) → `404`; nothing outside the sdk directory is reachable through this mapping.
- Requests to `/sdk/*` never touch tenant resolution or rate-limit state keyed per tenant (route is mapped before those middlewares — assert middleware order per API-01's table).
- CI `sdk` job uploads the `sdk-dist` artifact.

## Testing

- Unit/host tests via `WebApplicationFactory<Program>` with a temp `wwwroot/sdk` fixture: header matrix (latest vs pinned vs map), 404 cases, byte-identity. The zero-config fixture-page check is manual now and folds into SDK-06's rig when convenient (its generator already probes `{target}/sdk/tg.js`).

## Out of scope / guardrails

- **No Node server, ever** (D1) — delivery is static files from the .NET host; `e2e/serve.mjs` and any npm tooling stay dev-only.
- **Never serve `tg.js` (latest) as immutable** — that breaks D22's instant-fix propagation; **never serve pinned files with short TTLs** — that defeats the change-control promise. The two cache policies in step 2 are the contract.
- **No registry or third-party CDN publishing in MVP** — Cloudflare in front of the API origin (INT-05) is the CDN layer; revisit object storage/CDN split in P2-04, not here.
- Do not inline any tenant-specific data into the served bundle — per-tenant config rides script-tag attributes only (one artifact for all tenants, or CDN caching is pointless).
- Do not widen the static mapping beyond the `sdk/` directory, and do not commit built bundles to git.
- Bundle content is SDK-01..05's business — this task must not modify `dist/` output, only move and serve it.

---
id: SDK-01
title: SDK project, esbuild bundle, size gate
phase: 1
workstream: sdk
depends_on: []
size: M
spec_refs: [D1, D2, D22, "§8 solution shape"]
detail_level: full
---

# SDK-01: SDK project, esbuild bundle, size gate

## Objective

Create the npm/TypeScript project for the TelemetryGuard client SDK in `TelemetryGuard.Sdk/`, with a strict TypeScript 5 setup, an esbuild pipeline that produces a minified ES2019 IIFE bundle at `dist/tg.js` plus a version-pinned copy (`dist/tg-<version>.js`), and a size-gate script that fails the build when the gzipped bundle exceeds 30 KB. Also un-gate the `sdk` job in the CI workflow created by FND-03 (if it exists yet).

## Spec context (self-contained)

- **D2 — Client SDK is TypeScript + esbuild, the one exception to ".NET everywhere".** Browsers only run JavaScript. TS compiles to a **static IIFE bundle** — no server-side scripting runtime is introduced. Plain TS, **no framework**: the snippet loads on third-party tenant landing pages and must stay **~20–30 KB gzipped**. It embeds **FingerprintJS OSS** and **Botd** (both from FingerprintJS), plus custom listeners added by later tasks.
- **D1 — No server-side scripting languages** (Python/Node banned from the backend). Node.js used here is *build/test tooling only* and never runs as a production server. This is permitted; a Node production server is not.
- **D22 — Delivery is "latest from CDN" by default** so detection fixes propagate instantly, **plus a version-pinned URL** for tenants requiring change control. Therefore every build must emit both `dist/tg.js` (latest) and `dist/tg-<version>.js` (pinned), where `<version>` comes from `package.json`. (Serving these files to tenants — the `/sdk/tg.js` route and the snippet URL contract — is **SDK-08**; this task only produces the artifacts.)
- **§8 solution shape:** the SDK lives in `TelemetryGuard.Sdk/` at the repo root ("TypeScript source + esbuild → static bundle artifact"). It is *not* a .NET project and is *not* added to `TelemetryGuard.sln`.
- Allowed runtime dependencies: **exactly two** — `@fingerprintjs/fingerprintjs` and `@fingerprintjs/botd`. Nothing else may appear under `dependencies`.

## Prerequisites

None (this task has no depends_on). If FND-01 (solution scaffold) or FND-03 (CI workflow at `.github/workflows/ci.yml`) have already run, do not disturb their files except for the single CI un-gating edit described in step 8.

## Implementation steps

1. Create the directory `TelemetryGuard.Sdk/` at the repo root with this layout:

   ```
   TelemetryGuard.Sdk/
   ├── package.json
   ├── tsconfig.json
   ├── .gitignore
   ├── README.md
   ├── scripts/
   │   ├── build.mjs
   │   └── check-size.mjs
   └── src/
       ├── index.ts        entry point (placeholder bootstrap; replaced by SDK-02)
       ├── types.ts        shared wire-type skeleton (extended by SDK-02/03/04)
       └── global.d.ts     ambient declarations (__TG_VERSION__)
   ```

2. `TelemetryGuard.Sdk/package.json` — exactly:

   ```json
   {
     "name": "@telemetryguard/sdk",
     "version": "0.1.0",
     "private": true,
     "description": "TelemetryGuard client SDK: behavioral timing, fingerprint, botd, honeypot and storage-age telemetry shipped via sendBeacon.",
     "type": "module",
     "scripts": {
       "build": "node scripts/build.mjs",
       "size": "node scripts/check-size.mjs",
       "typecheck": "tsc --noEmit",
       "test": "echo \"no tests yet (added in SDK-06)\" && exit 0"
     },
     "dependencies": {
       "@fingerprintjs/fingerprintjs": "^4.5.1",
       "@fingerprintjs/botd": "^1.9.1"
     },
     "devDependencies": {
       "typescript": "^5.5.4",
       "esbuild": "^0.24.0"
     }
   }
   ```

3. `TelemetryGuard.Sdk/tsconfig.json`:

   ```json
   {
     "compilerOptions": {
       "target": "ES2019",
       "lib": ["ES2019", "DOM", "DOM.Iterable"],
       "module": "ESNext",
       "moduleResolution": "bundler",
       "strict": true,
       "noUncheckedIndexedAccess": true,
       "noImplicitOverride": true,
       "noFallthroughCasesInSwitch": true,
       "isolatedModules": true,
       "noEmit": true,
       "skipLibCheck": true
     },
     "include": ["src/**/*.ts"]
   }
   ```

   esbuild does the emitting; `tsc --noEmit` is the type gate.

4. `TelemetryGuard.Sdk/.gitignore`:

   ```
   node_modules/
   dist/
   ```

5. `TelemetryGuard.Sdk/src/global.d.ts`:

   ```ts
   declare const __TG_VERSION__: string;
   ```

   `src/types.ts` (skeleton, extended later):

   ```ts
   /** Base shape of every SDK event. t = ms since performance.timeOrigin (integer). */
   export interface TgEvent {
     e: string;
     t: number;
     [key: string]: unknown;
   }
   ```

   `src/index.ts` — a placeholder that **statically imports both runtime deps** so the size measurement in this task is real (SDK-02 replaces this file with the actual bootstrap; SDK-04 wires the libraries properly):

   ```ts
   import * as FingerprintJS from '@fingerprintjs/fingerprintjs';
   import { load as loadBotd } from '@fingerprintjs/botd';

   (() => {
     try {
       // Placeholder bootstrap — replaced by SDK-02. Keeps both libs in the
       // bundle so the SDK-01 size measurement reflects the real payload.
       (window as unknown as Record<string, unknown>).__tgv = __TG_VERSION__;
       void FingerprintJS;
       void loadBotd;
     } catch {
       /* the SDK must never throw on a host page */
     }
   })();
   ```

6. `TelemetryGuard.Sdk/scripts/build.mjs`:

   ```js
   import { build } from 'esbuild';
   import { readFileSync, copyFileSync, writeFileSync, mkdirSync } from 'node:fs';

   const pkg = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
   mkdirSync('dist', { recursive: true });

   const result = await build({
     entryPoints: ['src/index.ts'],
     bundle: true,
     format: 'iife',
     target: 'es2019',
     minify: true,
     sourcemap: true,
     metafile: true,
     outfile: 'dist/tg.js',
     define: { __TG_VERSION__: JSON.stringify(pkg.version) },
     banner: { js: `/* TelemetryGuard SDK v${pkg.version} */` },
     logLevel: 'info'
   });

   writeFileSync('dist/meta.json', JSON.stringify(result.metafile));
   // D22: version-pinned copy alongside "latest"
   copyFileSync('dist/tg.js', `dist/tg-${pkg.version}.js`);
   copyFileSync('dist/tg.js.map', `dist/tg-${pkg.version}.js.map`);
   console.log(`built dist/tg.js and dist/tg-${pkg.version}.js`);
   ```

   The `metafile` output (`dist/meta.json`) is required: SDK-05 uses it to prove its module stays under 2 KB.

7. `TelemetryGuard.Sdk/scripts/check-size.mjs`:

   ```js
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
   ```

8. **Measure, then decide on botd chunking.** Run `npm ci && npm run build && npm run size`. Record raw + gzip sizes in `TelemetryGuard.Sdk/README.md` under a "Bundle size" heading (a small table: date, version, raw bytes, gzip bytes, decision).
   - **If gzip ≤ 30 KB** (expected: FingerprintJS ≈ 10 KB gz, Botd ≈ 8 KB gz, leaving headroom): keep the single static bundle. Write in the README: "Decision: single bundle; botd statically imported; measured X KB gzip."
   - **If gzip > 30 KB**: split botd into a lazily loaded second chunk. Because IIFE format does not support esbuild code-splitting, do it with two builds in `build.mjs`: (a) main bundle **without** the botd import; (b) a second `build()` call with entry `src/botd-chunk.ts` (`import { load } from '@fingerprintjs/botd'; (window as any).__tgBotd = load;`) → `dist/tg-botd.js` + versioned copy, also IIFE. The main bundle later loads it by injecting `<script src="<own script origin+path>/tg-botd.js">` and awaiting `window.__tgBotd`. Gate `tg.js` at 30 KB and print (not gate) the chunk size. Document the measured numbers and the decision in the README either way — SDK-04's implementer reads it.

9. **Un-gate the FND-03 `sdk` CI job.** If `.github/workflows/ci.yml` exists and contains a job named `sdk` gated off (e.g. `if: false` with a comment referencing SDK-01), remove the gate so the job runs on push/PR with steps equivalent to:

   ```yaml
   sdk:
     runs-on: ubuntu-latest
     defaults: { run: { working-directory: TelemetryGuard.Sdk } }
     steps:
       - uses: actions/checkout@v4
       - uses: actions/setup-node@v4
         with: { node-version: 20, cache: npm, cache-dependency-path: TelemetryGuard.Sdk/package-lock.json }
       - run: npm ci
       - run: npm run typecheck
       - run: npm run build
       - run: npm run size
       - run: npm test
   ```

   Adapt to the workflow's existing structure rather than replacing it wholesale. **If the workflow file does not exist yet** (FND-03 not merged), skip this step and leave a note in `TelemetryGuard.Sdk/README.md`: "CI: un-gate the sdk job in .github/workflows/ci.yml (see SDK-01 step 9)."

10. Commit `package-lock.json` (generated by `npm ci`/`npm install`) — CI needs it for `cache-dependency-path`.

## Files to create or modify

- `TelemetryGuard.Sdk/package.json` (create)
- `TelemetryGuard.Sdk/package-lock.json` (create, generated)
- `TelemetryGuard.Sdk/tsconfig.json` (create)
- `TelemetryGuard.Sdk/.gitignore` (create)
- `TelemetryGuard.Sdk/README.md` (create — bundle-size table + botd decision)
- `TelemetryGuard.Sdk/scripts/build.mjs` (create)
- `TelemetryGuard.Sdk/scripts/check-size.mjs` (create)
- `TelemetryGuard.Sdk/src/index.ts` (create)
- `TelemetryGuard.Sdk/src/types.ts` (create)
- `TelemetryGuard.Sdk/src/global.d.ts` (create)
- `.github/workflows/ci.yml` (modify — only the `sdk` job gate, only if the file exists)

## Acceptance criteria

- `cd TelemetryGuard.Sdk && npm ci` succeeds with no missing peer warnings that break install.
- `npm run typecheck` exits 0.
- `npm run build` produces `dist/tg.js`, `dist/tg.js.map`, `dist/tg-0.1.0.js`, `dist/tg-0.1.0.js.map`, and `dist/meta.json`; `dist/tg.js` begins with the banner comment and is a single self-executing IIFE (no `import`/`export`/`require` in the output).
- `npm run size` exits 0 and prints raw and gzip byte counts; manually confirm gzip ≤ 30720 bytes.
- `grep -c "es2019" dist/tg.js` is not required, but the bundle must not contain optional chaining helpers that break ES2019 targets — verify `node -e "new Function(require('fs').readFileSync('dist/tg.js','utf8'))"` does not throw.
- `package.json` `dependencies` contains exactly `@fingerprintjs/fingerprintjs` and `@fingerprintjs/botd`.
- `TelemetryGuard.Sdk/README.md` documents measured sizes and the static-vs-lazy botd decision.
- If `.github/workflows/ci.yml` existed: the `sdk` job is no longer gated and runs the steps of step 9 (verify with `git diff`).

## Testing

- No unit-test framework in this task (test infra arrives in SDK-06). Verification is the command sequence above.
- Sanity-run the bundle in a browser-less check: `node -e "global.window={};new Function(require('fs').readFileSync('TelemetryGuard.Sdk/dist/tg.js','utf8'))()"` must not throw (the try/catch in the IIFE guarantees this).

## Out of scope / guardrails

- **No runtime/bootstrap logic** beyond the placeholder — session, transport, collectors are SDK-02/03/04/05.
- **No framework, no polyfills, no extra runtime deps.** `dependencies` stays at exactly the two FingerprintJS packages. Dev-tooling deps go in `devDependencies` only.
- **Node is build tooling only** (D1). Never add an npm `start`/server script that would run Node as a service; the backend is .NET end-to-end. The bundle output is a static artifact.
- **Do not add the SDK to `TelemetryGuard.sln`** or create a `.csproj` in this folder.
- **Do not publish to any registry** (`"private": true` stays).
- The 30 KB gzip gate is a hard constraint (D2) — never raise the limit to make a build pass; shrink the bundle (lazy botd chunk) instead.
- Keep `dist/` out of git; CDN/hosting of the artifact is owned by **SDK-08** (API-host `/sdk/` route + cache-header/URL contract) — not in this task.
- Do not touch any `.github/workflows` job other than the `sdk` job gate.

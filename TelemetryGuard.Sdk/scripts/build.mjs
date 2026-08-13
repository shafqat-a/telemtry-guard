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

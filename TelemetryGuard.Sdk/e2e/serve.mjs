/**
 * SDK-06 e2e mock server — TEST INFRA ONLY (D1: the real endpoints are .NET,
 * API-04). Plain node:http, no frameworks. Never grows product features.
 *
 * Routes:
 *   GET  /dist/*      static files from TelemetryGuard.Sdk/dist/
 *   GET  /fixtures/*  static files from TelemetryGuard.Sdk/e2e/fixtures/
 *   GET  /i/init      {nonce, storageTs, storageSig}; records issued values per sid
 *   POST /i           captures {receivedAt, raw, parsed, headers}; responds 204
 *   POST /decide      SDK-07 mock: pops the next scripted response from the
 *                     queue (empty queue -> {"action":"allow"}); records the
 *                     body + headers into the captured store (kind:'decide').
 *                     A scripted entry {"status":N} replies with that HTTP
 *                     status; {"delayMs":N} delays the reply (both for the
 *                     fail-open scenarios).
 *   POST /__decide-script  replaces the /decide queue with the posted JSON array
 *   POST /__submitted      form target: records (kind:'submitted'), responds 204
 *   GET  /__captured  captured beacons as JSON (test-only introspection)
 *   POST /__reset     clears captured beacons + the /decide queue (inits kept)
 *   GET  /__inits     issued /i/init responses as JSON (test-only introspection)
 */
import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { randomBytes } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SDK_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PORT = 4599;

/** @type {Array<{receivedAt:number, raw:string, parsed:unknown, headers:import('node:http').IncomingHttpHeaders}>} */
const captured = [];
/** @type {Array<{sid:string, k:string, nonce:string, storageTs:number, storageSig:string, at:number}>} */
const inits = [];
/** SDK-07: scripted /decide responses, consumed FIFO. @type {Array<Record<string, unknown>>} */
const decideScript = [];
let lastTs = 0; // guarantees strictly increasing (hence distinct) storageTs values

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.map': 'application/json; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
};

function cors(res) {
  res.setHeader('Access-Control-Allow-Origin', '*');
}

function json(res, status, body) {
  cors(res);
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify(body));
}

async function serveStatic(res, baseDir, rel) {
  // No path traversal out of the base dir.
  const file = path.normalize(path.join(baseDir, rel));
  if (!file.startsWith(baseDir + path.sep)) {
    res.writeHead(403).end('forbidden');
    return;
  }
  try {
    const data = await readFile(file);
    cors(res);
    res.writeHead(200, {
      'Content-Type': MIME[path.extname(file)] ?? 'application/octet-stream',
      'Cache-Control': 'no-store',
    });
    res.end(data);
  } catch {
    res.writeHead(404).end('not found');
  }
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url ?? '/', `http://localhost:${PORT}`);
    const p = url.pathname;

    if (req.method === 'GET' && p.startsWith('/dist/')) {
      await serveStatic(res, path.join(SDK_ROOT, 'dist'), p.slice('/dist/'.length));
      return;
    }
    if (req.method === 'GET' && p.startsWith('/fixtures/')) {
      await serveStatic(
        res,
        path.join(SDK_ROOT, 'e2e', 'fixtures'),
        p.slice('/fixtures/'.length)
      );
      return;
    }
    if (req.method === 'GET' && p === '/i/init') {
      const sid = url.searchParams.get('sid') ?? '';
      const k = url.searchParams.get('k') ?? '';
      const nonce = 'e2e-nonce-' + randomBytes(8).toString('hex');
      const storageTs = (lastTs = Math.max(Date.now(), lastTs + 1));
      const storageSig = 'e2e-sig-' + randomBytes(8).toString('hex');
      inits.push({ sid, k, nonce, storageTs, storageSig, at: Date.now() });
      json(res, 200, { nonce, storageTs, storageSig });
      return;
    }
    if (req.method === 'POST' && p === '/i') {
      const raw = await readBody(req);
      let parsed = null;
      try {
        parsed = JSON.parse(raw);
      } catch {
        /* keep raw; tests will fail loudly on parsed === null */
      }
      captured.push({ receivedAt: Date.now(), raw, parsed, headers: req.headers });
      cors(res);
      res.writeHead(204).end();
      return;
    }
    if (req.method === 'POST' && p === '/decide') {
      const raw = await readBody(req);
      let parsed = null;
      try {
        parsed = JSON.parse(raw);
      } catch {
        /* keep raw; tests fail loudly on parsed === null */
      }
      captured.push({
        receivedAt: Date.now(),
        raw,
        parsed,
        headers: req.headers,
        kind: 'decide',
        url: req.url,
      });
      const next = decideScript.shift();
      if (next && typeof next.status === 'number' && next.status !== 200) {
        cors(res);
        res.writeHead(next.status, {
          'Content-Type': 'application/json; charset=utf-8',
        });
        res.end(JSON.stringify({ error: 'scripted failure' }));
        return;
      }
      const body =
        next && typeof next.action === 'string'
          ? next.turnstileSiteKey !== undefined
            ? { action: next.action, turnstileSiteKey: next.turnstileSiteKey }
            : { action: next.action }
          : { action: 'allow' }; // empty queue -> allow
      const delayMs = next && typeof next.delayMs === 'number' ? next.delayMs : 0;
      if (delayMs > 0) {
        setTimeout(() => json(res, 200, body), delayMs);
      } else {
        json(res, 200, body);
      }
      return;
    }
    if (req.method === 'POST' && p === '/__decide-script') {
      const raw = await readBody(req);
      let arr = null;
      try {
        arr = JSON.parse(raw);
      } catch {
        /* handled below */
      }
      if (!Array.isArray(arr)) {
        json(res, 400, { error: 'expected a JSON array' });
        return;
      }
      decideScript.length = 0;
      decideScript.push(...arr);
      cors(res);
      res.writeHead(204).end();
      return;
    }
    if (req.method === 'POST' && p === '/__submitted') {
      const raw = await readBody(req);
      captured.push({
        receivedAt: Date.now(),
        raw,
        parsed: null,
        headers: req.headers,
        kind: 'submitted',
      });
      cors(res);
      res.writeHead(204).end();
      return;
    }
    if (req.method === 'GET' && p === '/__captured') {
      json(res, 200, captured);
      return;
    }
    if (req.method === 'POST' && p === '/__reset') {
      captured.length = 0;
      decideScript.length = 0;
      cors(res);
      res.writeHead(204).end();
      return;
    }
    if (req.method === 'GET' && p === '/__inits') {
      json(res, 200, inits);
      return;
    }
    res.writeHead(404).end('not found');
  } catch (err) {
    res.writeHead(500).end(String(err));
  }
});

server.listen(PORT, () => {
  console.log(`[serve.mjs] e2e mock listening on http://localhost:${PORT}`);
});

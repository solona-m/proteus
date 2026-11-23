/**
 * The static-assets layer in front of the Worker: wrangler.toml's [assets], what may live in static/, and _headers.
 *
 * A request that matches a file in static/ is answered by Cloudflare without running the Worker (and costs no Worker
 * request); anything else falls through to the Worker. So the failures that matter here are a file that SHADOWS a
 * Worker path, a setting that turns misses into an asset 404 page, and `run_worker_first`, which would bill every
 * request again. No network, no wrangler, no account.
 */
import { readFile, readdir, mkdtemp, mkdir, writeFile, rm, access } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import worker from '../src/index.js';
import { LANGS, mirrorPathFor } from '../src/render.js';
import { prepare } from '../scripts/deploy.mjs';

const WORKER = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const STATIC = join(WORKER, 'static');

let pass = 0, fail = 0;
const t = (name, ok, detail = '') => {
  if (ok) { pass++; console.log(`  ok   ${name}`); }
  else { fail++; console.log(`  FAIL ${name}${detail ? ': ' + detail : ''}`); }
};

/** `key = "value"` lines of one [section], comments and blank lines skipped. Enough for this file; no TOML dep. */
function tomlSection(text, name) {
  const out = {};
  let inside = false;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.replace(/#.*$/, '').trim();
    if (!line) continue;
    if (line.startsWith('[')) { inside = line === `[${name}]`; continue; }
    if (!inside) continue;
    const m = /^([A-Za-z_]+)\s*=\s*(.+)$/.exec(line);
    if (m) out[m[1]] = m[2].trim().replace(/^"(.*)"$/, '$1');
  }
  return out;
}

/** Cloudflare's _headers format: an unindented URL pattern, then indented `Name: value` lines. */
function parseHeaders(text) {
  const rules = [];
  for (const raw of text.split(/\r?\n/)) {
    if (!raw.trim() || raw.trim().startsWith('#')) continue;
    if (!/^\s/.test(raw)) { rules.push({ path: raw.trim(), headers: {} }); continue; }
    const m = /^\s+([^:]+):\s*(.*)$/.exec(raw);
    if (m && rules.length) rules.at(-1).headers[m[1].trim().toLowerCase()] = m[2].trim();
  }
  return rules;
}

console.log('wrangler.toml [assets]:');
{
  const toml = await readFile(join(WORKER, 'wrangler.toml'), 'utf8');
  const assets = tomlSection(toml, 'assets');
  // [AC-static-repo-json-1] [AC-static-repo-json-3] [EDGE-static-repo-json-4] [EDGE-static-repo-json-7]
  t('directory is ./static', assets.directory === './static', JSON.stringify(assets));
  t('html_handling is none (exact paths only: no /repo.json/ or index.html rewrites)', assets.html_handling === 'none');
  t('not_found_handling is none (a miss reaches the Worker, never an asset 404 page)', assets.not_found_handling === 'none');
  t('exactly those three keys, no binding', JSON.stringify(Object.keys(assets).sort())
    === JSON.stringify(['directory', 'html_handling', 'not_found_handling']));
  const code = toml.split(/\r?\n/).map((l) => l.replace(/#.*$/, '')).join('\n');
  t('run_worker_first appears nowhere outside comments', !code.includes('run_worker_first'));
  t('no account_id committed (CI and the owner pass CLOUDFLARE_ACCOUNT_ID)', !/^\s*account_id\s*=/m.test(code));
}

console.log('static/ holds only what it should:');
{
  // [AC-static-repo-json-3] [EDGE-static-repo-json-9]
  const tracked = execFileSync('git', ['ls-files', '--', 'static'], { cwd: WORKER, encoding: 'utf8' })
    .split('\n').filter(Boolean);
  t('git tracks exactly static/_headers', JSON.stringify(tracked) === JSON.stringify(['static/_headers']),
    JSON.stringify(tracked));

  const dir = await mkdtemp(join(tmpdir(), 'proteus-assets-'));
  const staticCopy = join(dir, 'static');
  await mkdir(staticCopy);
  await writeFile(join(staticCopy, '_headers'), await readFile(join(STATIC, '_headers')));
  await writeFile(join(dir, 'repo.json'), '[{"InternalName":"Proteus"}]');
  await prepare({ staticDir: staticCopy, repoJsonPath: join(dir, 'repo.json') });
  const after = (await readdir(staticCopy)).sort();
  t('after prepare: exactly _headers and repo.json, no dotfiles',
    JSON.stringify(after) === JSON.stringify(['_headers', 'repo.json']), JSON.stringify(after));
  await rm(dir, { recursive: true, force: true });
}

console.log('no static file shadows a Worker path:');
{
  // [AC-static-repo-json-3] [EDGE-static-repo-json-9] With html_handling = "none" an asset matches only its exact
  // path. _headers itself is config, never served. So the question is whether any Worker path names a file here.
  const workerPaths = [
    '/', '/README.md', '/TROUBLESHOOTING.md', '/For%20Creators.md', '/TERMS.md', '/icon.png',
    '/milieu', '/milieu/', '/milieu/README.md', '/milieu/icon.png',
    ...LANGS.flatMap(({ code }) => [`/${code}`, `/${code}/`, mirrorPathFor(code)]),
    '/uvmaps-v1/bibo_to_gen3_transfer.tif', '/effects-v2/sky.jpeg', '/matcaps-v3/Gem.1.png',
    '/v2611.1839.0.0/latest.zip', '/testing-309/latest.zip', '/camera-tools/v2609.12.0.0/latest.zip',
    '/pickles/aiscan-v1/ffmpeg.exe', '/milieu/v2611.40.0.0/latest.zip', '/milieu/backdrops-v2/moon.atex',
  ];
  const served = new Set((await readdir(STATIC)).filter((f) => f !== '_headers').map((f) => `/${f}`));
  served.add('/repo.json'); // generated at deploy time; counted as present
  const hits = workerPaths.filter((p) => served.has(decodeURIComponent(p)));
  t(`none of ${workerPaths.length} Worker paths names a static file`, hits.length === 0, hits.join(', '));
}

console.log('_headers:');
let assetHeaders;
{
  // [AC-static-repo-json-1] [AC-static-repo-json-7] [EDGE-static-repo-json-21]
  const rules = parseHeaders(await readFile(join(STATIC, '_headers'), 'utf8'));
  t('one rule, for /repo.json', rules.length === 1 && rules[0].path === '/repo.json', JSON.stringify(rules));
  assetHeaders = rules[0]?.headers ?? {};
  t('exactly the three headers', JSON.stringify(Object.keys(assetHeaders).sort())
    === JSON.stringify(['cache-control', 'content-type', 'x-proteus-mirror']), JSON.stringify(assetHeaders));
  t('content type application/json; charset=utf-8', assetHeaders['content-type'] === 'application/json; charset=utf-8');
  t('max-age 300, as the proxy had', assetHeaders['cache-control'] === 'public, max-age=300');
  t('marked x-proteus-mirror: asset (the Worker says raw/github)', assetHeaders['x-proteus-mirror'] === 'asset');
}

console.log('asset and Worker fallback look the same to Dalamud:');
{
  // [EDGE-static-repo-json-24]
  globalThis.caches = { default: { match: async () => undefined, put: async () => {} } };
  globalThis.fetch = async () => new Response('[]', { status: 200 });
  const res = await worker.fetch(new Request('https://dl.example.com/repo.json'), {}, { waitUntil() {} });
  t('content-type matches', res.headers.get('content-type') === assetHeaders['content-type'],
    `${res.headers.get('content-type')} vs ${assetHeaders['content-type']}`);
  t('cache-control matches', res.headers.get('cache-control') === assetHeaders['cache-control'],
    `${res.headers.get('cache-control')} vs ${assetHeaders['cache-control']}`);
}

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);

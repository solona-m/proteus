/**
 * Routing and caching tests for the asset mirror, against stubbed Cloudflare globals.
 *
 * The important half is the REJECTED list. The request path is what builds the upstream GitHub URL,
 * so a route pattern that is looser than it looks turns this worker into an open proxy for any file
 * on github.com — a failure that is invisible in normal use, because every legitimate request keeps
 * working exactly as before.
 *
 * Run with `npm test` from worker/. No network, no wrangler, no account needed.
 */
import worker from '../src/index.js';

let fetched = [];
let fetchOpts = [];
const store = new Map();

globalThis.caches = {
  default: {
    async match(req) { return store.get(new URL(req.url).pathname)?.clone(); },
    async put(req, res) { store.set(new URL(req.url).pathname, res); },
  },
};
globalThis.fetch = async (url, opts) => {
  fetched.push(url);
  fetchOpts.push(opts ?? {});
  if (url.includes('missing')) return new Response('nope', { status: 404 });
  return new Response('BODY', {
    status: 200,
    headers: { 'content-type': 'application/octet-stream', 'content-length': '4', etag: '"abc"' },
  });
};
const ctx = { waitUntil: (p) => p };

let pass = 0, fail = 0;

async function check(name, path, expectStatus, expectOrigin) {
  fetched = [];
  fetchOpts = [];
  const res = await worker.fetch(new Request('https://dl.example.com' + path), {}, ctx);
  const okStatus = res.status === expectStatus;
  const okOrigin = expectOrigin === undefined
    || (expectOrigin === null ? fetched.length === 0 : fetched[0] === expectOrigin);
  if (okStatus && okOrigin) { pass++; console.log(`  ok   ${name}`); }
  else {
    fail++;
    console.log(`  FAIL ${name}\n       status ${res.status} (want ${expectStatus})` +
                `\n       origin ${fetched[0] ?? '(none)'}\n       want   ${expectOrigin}`);
  }
}

const GH = 'https://github.com/solona-m/proteus/releases/download';

console.log('accepted paths:');
await check('uv map', '/uvmaps-v1/bibo_to_gen3_transfer.tif', 200,
  `${GH}/uvmaps-v1/bibo_to_gen3_transfer.tif`);
await check('effect (dotted name)', '/effects-v1/hello.kitty.png', 200,
  `${GH}/effects-v1/hello.kitty.png`);
await check('stable plugin zip', '/v2608.309.0.0/latest.zip', 200,
  `${GH}/v2608.309.0.0/latest.zip`);
await check('testing plugin zip', '/testing-309/latest.zip', 200,
  `${GH}/testing-309/latest.zip`);

console.log('rejected paths (must never reach an origin):');
await check('traversal', '/uvmaps-v1/../../../etc/passwd', 404, null);
await check('absolute-ish', '//evil.com/x.tif', 404, null);
await check('unknown tag', '/random-tag/file.tif', 404, null);
await check('wrong extension for tag', '/uvmaps-v1/payload.exe', 404, null);
await check('effect with wrong ext', '/effects-v1/payload.sh', 404, null);
await check('bare file', '/latest.zip', 404, null);
await check('nested', '/uvmaps-v1/sub/dir/f.tif', 404, null);
await check('zip under uvmaps tag', '/uvmaps-v1/latest.zip', 404, null);
// Effect assets are uploaded with spaces already replaced by dots (upload-effects.yml), so a name
// with a real space is never legitimate and must not be forwarded.
await check('space in effect name', '/effects-v1/hello kitty.png', 404, null);

console.log('readme endpoint:');
{
  for (const p of ['/', '/README.md']) {
    fetched = [];
    const res = await worker.fetch(new Request('https://dl.example.com' + p), {}, ctx);
    const body = await res.text();
    const ok = res.status === 200
      && res.headers.get('content-type')?.startsWith('text/markdown')
      && body.includes('Proteus asset mirror')
      // Static content must never build an upstream URL — that is what keeps it outside the
      // open-proxy surface the route patterns above exist to contain.
      && fetched.length === 0;
    if (ok) { pass++; console.log(`  ok   ${p} serves the README`); }
    else {
      fail++;
      console.log(`  FAIL ${p}: status ${res.status}, type ${res.headers.get('content-type')}, ` +
                  `len ${body.length}, origin ${fetched[0] ?? '(none)'}`);
    }
  }

  // HEAD must not carry a body, but must still report the same headers.
  const head = await worker.fetch(
    new Request('https://dl.example.com/', { method: 'HEAD' }), {}, ctx);
  const headBody = await head.text();
  if (head.status === 200 && headBody === '') { pass++; console.log('  ok   HEAD / has no body'); }
  else { fail++; console.log(`  FAIL HEAD /: status ${head.status}, body len ${headBody.length}`); }

  // The README is the one MUTABLE thing this worker serves, so it must not inherit the assets'
  // immutable one-year TTL — a stale year-old copy of the docs would be worse than none.
  const res = await worker.fetch(new Request('https://dl.example.com/'), {}, ctx);
  const cc = res.headers.get('cache-control') ?? '';
  if (!cc.includes('immutable') && /max-age=(\d+)/.test(cc) && +RegExp.$1 <= 3600) {
    pass++; console.log(`  ok   short, mutable cache-control (${cc})`);
  } else { fail++; console.log(`  FAIL cache-control on README: ${cc}`); }
}

console.log('origin failures pass through:');
// The plugin's retry and fallback logic branches on the real status (404 vs 429), so the worker must
// not replace it with one of its own.
await check('404 from origin', '/uvmaps-missing/x.tif', 404);

// A failure must never be cached, at the edge or in the browser. The tag for a release that has not
// been published yet 404s until the moment it is published; caching that answer would keep the mirror
// denying an asset that exists, with no signal that anything is wrong.
{
  fetched = [];
  const res = await worker.fetch(
    new Request('https://dl.example.com/uvmaps-missing/x.tif'), {}, ctx);
  const cc = res.headers.get('cache-control') ?? '';
  const opts = fetchOpts[0]?.cf ?? {};
  const byStatus = opts.cacheTtlByStatus ?? {};
  // Every non-2xx bucket must be 0 (do not cache); only success is worth keeping.
  const nonSuccessCached = Object.entries(byStatus)
    .filter(([range]) => !range.startsWith('2'))
    .some(([, ttl]) => ttl !== 0);
  const ok = cc.includes('no-store')
    && opts.cacheTtl === undefined            // a bare cacheTtl has no per-status control
    && byStatus['200-299'] > 0
    && !nonSuccessCached;
  if (ok) { pass++; console.log('  ok   origin failures are not cached'); }
  else {
    fail++;
    console.log(`  FAIL failure caching: cache-control "${cc}", cf ${JSON.stringify(opts)}`);
  }
}

console.log('method guard:');
{
  const res = await worker.fetch(
    new Request('https://dl.example.com/uvmaps-v1/x.tif', { method: 'POST' }), {}, ctx);
  if (res.status === 405) { pass++; console.log('  ok   POST rejected'); }
  else { fail++; console.log(`  FAIL POST got ${res.status}`); }
}

console.log('caching:');
{
  store.clear();
  await worker.fetch(new Request('https://dl.example.com/uvmaps-v1/c.tif'), {}, ctx);
  fetched = [];
  await worker.fetch(new Request('https://dl.example.com/uvmaps-v1/c.tif'), {}, ctx);
  if (fetched.length === 0) { pass++; console.log('  ok   second request served from cache'); }
  else { fail++; console.log(`  FAIL second request hit origin: ${fetched[0]}`); }

  // The whole point of keying the cache on the path alone: a resuming client sends a Range header,
  // and keying on the request would give every byte range its own entry — a guaranteed miss exactly
  // when re-pulling 128 MB is most expensive.
  fetched = [];
  await worker.fetch(new Request('https://dl.example.com/uvmaps-v1/c.tif',
    { headers: { Range: 'bytes=0-1' } }), {}, ctx);
  if (fetched.length === 0) { pass++; console.log('  ok   ranged request reuses the cached object'); }
  else { fail++; console.log(`  FAIL ranged request re-fetched origin: ${fetched[0]}`); }
}

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);

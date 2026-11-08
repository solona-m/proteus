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
import worker, { pickLanguage } from '../src/index.js';
import { renderMarkdown, stripTags } from '../src/render.js';

let fetched = [];
let fetchOpts = [];
const store = new Map();

// Keyed on pathname + search, because that is what Cloudflare's Cache API does. Keying on pathname
// alone — as this stub first did — silently merges the HTML and markdown variants of a document and
// makes the variant-bleed test unable to fail, which is the one bug the cache key exists to prevent.
globalThis.caches = {
  default: {
    async match(req) {
      const u = new URL(req.url);
      return store.get(u.pathname + u.search)?.clone();
    },
    async put(req, res) {
      const u = new URL(req.url);
      store.set(u.pathname + u.search, res);
    },
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
    || (expectOrigin === null ? fetched.length === 0 : stripEpoch(fetched[0]) === expectOrigin);
  if (okStatus && okOrigin) { pass++; console.log(`  ok   ${name}`); }
  else {
    fail++;
    console.log(`  FAIL ${name}\n       status ${res.status} (want ${expectStatus})` +
                `\n       origin ${fetched[0] ?? '(none)'}\n       want   ${expectOrigin}`);
  }
}

// Release-asset subrequests carry the ORIGIN_EPOCH cache-buster; the exact value is not the point,
// only that the path and tag reach the right upstream, so it is stripped before comparing.
const GH = 'https://github.com/solona-m/proteus/releases/download';
const CAM = 'https://github.com/solona-m/camera-tools-ffxiv/releases/download';
const PKL = 'https://github.com/solona-m/Pickles-Playlist-Editor/releases/download';
const MIL = 'https://github.com/solona-m/milieu/releases/download';
const stripEpoch = (u) => (u ?? '').replace(/\?e=\d+$/, '');

console.log('accepted paths:');
await check('uv map', '/uvmaps-v1/bibo_to_gen3_transfer.tif', 200,
  `${GH}/uvmaps-v1/bibo_to_gen3_transfer.tif`);
await check('effect (dotted name)', '/effects-v1/hello.kitty.png', 200,
  `${GH}/effects-v1/hello.kitty.png`);
await check('matcap (dotted name)', '/matcaps-v1/Gold.Metal.png', 200,
  `${GH}/matcaps-v1/Gold.Metal.png`);
await check('stable plugin zip', '/v2608.309.0.0/latest.zip', 200,
  `${GH}/v2608.309.0.0/latest.zip`);
await check('testing plugin zip', '/testing-309/latest.zip', 200,
  `${GH}/testing-309/latest.zip`);

// A second plugin, served from a second repo. The assertions that matter are which upstream each
// path resolves to: a prefixed path must never reach Proteus's repo, and Proteus's bare paths must
// keep resolving exactly as before — they are hardcoded in every shipped build of the plugin.
console.log('camera tools (second origin repo):');
await check('camera tools stable zip', '/camera-tools/v2609.12.0.0/latest.zip', 200,
  `${CAM}/v2609.12.0.0/latest.zip`);
await check('camera tools testing zip', '/camera-tools/testing-12/latest.zip', 200,
  `${CAM}/testing-12/latest.zip`);
// Same tag shape, no prefix: still Proteus, not camera tools.
await check('bare tag still resolves to proteus', '/v2609.12.0.0/latest.zip', 200,
  `${GH}/v2609.12.0.0/latest.zip`);
// The prefix is not a general escape hatch into another repo's release assets.
await check('camera tools rejects other files', '/camera-tools/v1.0/payload.exe', 404, null);
await check('camera tools rejects bad tag', '/camera-tools/random/latest.zip', 404, null);
await check('camera tools rejects nesting', '/camera-tools/v1.0/sub/latest.zip', 404, null);
await check('camera tools prefix alone', '/camera-tools/latest.zip', 404, null);
// Proteus's asset tags do not leak through the prefixed route into the camera tools repo.
await check('camera tools rejects uvmaps tag', '/camera-tools/uvmaps-v1/x.tif', 404, null);

// Milieu: its own prefix; plugin zips and the bundled scene textures only.
console.log('milieu (fourth origin repo):');
await check('milieu stable zip', '/milieu/v2611.40.0.0/latest.zip', 200, `${MIL}/v2611.40.0.0/latest.zip`);
await check('milieu testing zip', '/milieu/testing-40/latest.zip', 200, `${MIL}/testing-40/latest.zip`);
await check('milieu bundled scene', '/milieu/backdrops-v2/moon.atex', 200, `${MIL}/backdrops-v2/moon.atex`);
await check('milieu rejects other files', '/milieu/v1.0/payload.exe', 404, null);
await check('milieu rejects jpeg scenes', '/milieu/backdrops-v2/moon.jpeg', 404, null);
await check('milieu rejects bad tag', '/milieu/random/latest.zip', 404, null);
await check('milieu rejects nesting', '/milieu/backdrops-v2/sub/moon.atex', 404, null);
await check('milieu rejects uppercase stem', '/milieu/backdrops-v2/Moon.atex', 404, null);
await check('milieu prefix alone', '/milieu/latest.zip', 404, null);
await check('milieu rejects proteus asset tags', '/milieu/uvmaps-v1/x.tif', 404, null);
await check('bare backdrops tag is not milieu', '/backdrops-v2/moon.atex', 404, null);
await check('milieu 40-char stem accepted', `/milieu/backdrops-v2/${'a'.repeat(40)}.atex`, 200, `${MIL}/backdrops-v2/${'a'.repeat(40)}.atex`);
await check('milieu 41-char stem rejected', `/milieu/backdrops-v2/${'a'.repeat(41)}.atex`, 404, null);
await check('milieu rejects dotted stem', '/milieu/backdrops-v2/moon.night.atex', 404, null);
await check('milieu rejects hyphenated stem', '/milieu/backdrops-v2/living-room.atex', 404, null);
await check('milieu rejects atex under a zip tag', '/milieu/v1.0/moon.atex', 404, null);
await check('milieu rejects zip under a backdrops tag', '/milieu/backdrops-v2/latest.zip', 404, null);
await check('milieu rejects over-long backdrops tag', '/milieu/backdrops-v1234567/moon.atex', 404, null);
await check('milieu rejects traversal', '/milieu/backdrops-v2/../../etc/passwd', 404, null);
await check('milieu rejects encoded traversal', '/milieu/backdrops-v2/%2e%2e/moon.atex', 404, null);
await check('milieu rejects encoded slash', '/milieu/backdrops-v2%2Fmoon.atex', 404, null);

// A third repo: Pickles Playlist Editor's AI-score assets, under its own prefix.
// [AC-ai-assets-download-9] [EDGE-ai-assets-download-22] [EDGE-ai-assets-download-24] (Pickles plan ids)
console.log('pickles (third origin repo):');
for (const f of ['ffmpeg.exe', 'sonics-beta-120s.onnx', 'sonics-beta-120s.onnx.data']) {
  await check(`pickles ${f}`, `/pickles/aiscan-v1/${f}`, 200, `${PKL}/aiscan-v1/${f}`);
}
{
  const res = await worker.fetch(new Request('https://dl.example.com/pickles/aiscan-v1/ffmpeg.exe'), {}, ctx);
  const cc = res.headers.get('cache-control') ?? '';
  if (res.status === 200 && cc.includes('immutable') && cc.includes('max-age=31536000')) {
    pass++; console.log(`  ok   pickles asset is immutable for a year (${cc})`);
  } else { fail++; console.log(`  FAIL pickles cache-control: ${res.status} ${cc}`); }
}
await check('proteus bare tag unaffected by pickles', '/v2609.13.0.0/latest.zip', 200, `${GH}/v2609.13.0.0/latest.zip`);
await check('camera tools unaffected by pickles', '/camera-tools/v2609.13.0.0/latest.zip', 200, `${CAM}/v2609.13.0.0/latest.zip`);
for (const [name, p] of [
  ['pickles no prefix', '/aiscan-v1/ffmpeg.exe'],
  ['pickles dll', '/pickles/aiscan-v1/x.dll'],
  ['pickles zip', '/pickles/aiscan-v1/x.zip'],
  ['pickles app zip', '/pickles/v2.11.14/latest.zip'],
  ['pickles nesting', '/pickles/aiscan-v1/sub/ffmpeg.exe'],
  ['pickles traversal', '/pickles/aiscan-v1/../ffmpeg.exe'],
  ['pickles encoded traversal', '/pickles/aiscan-v1/%2e%2e/ffmpeg.exe'],
  ['pickles encoded slash', '/pickles/aiscan-v1/%2e%2e%2fffmpeg.exe'],
  ['pickles upper-case tag', '/pickles/AISCAN-V1/ffmpeg.exe'],
  ['pickles upper-case prefix', '/Pickles/aiscan-v1/ffmpeg.exe'],
  ['pickles bad tag', '/pickles/aiscan-x/ffmpeg.exe'],
  ['pickles other kind of tag', '/pickles/uvmaps-v1/x.tif'],
  ['pickles tag under camera tools', '/camera-tools/aiscan-v1/ffmpeg.exe'],
  ['pickles no file', '/pickles/aiscan-v1/'],
  ['pickles space in name', '/pickles/aiscan-v1/ff mpeg.exe'],
]) await check(name, p, 404, null);
{
  // An upstream 404 (e.g. a release not published yet) must not be cached: the next request goes back to GitHub.
  fetched = [];
  const first = await worker.fetch(new Request('https://dl.example.com/pickles/aiscan-v1/missing.exe'), {}, ctx);
  const cc = first.headers.get('cache-control') ?? '';
  fetched = [];
  await worker.fetch(new Request('https://dl.example.com/pickles/aiscan-v1/missing.exe'), {}, ctx);
  if (first.status === 404 && cc.includes('no-store') && fetched.length === 1) {
    pass++; console.log('  ok   pickles upstream 404 not cached');
  } else { fail++; console.log(`  FAIL pickles upstream 404: ${first.status} ${cc}, refetched ${fetched.length}`); }
}
{
  // A matcap release not published yet 404s; publishing it later must just work, so the 404 is never cached.
  fetched = [];
  const first = await worker.fetch(new Request('https://dl.example.com/matcaps-v1/missing.png'), {}, ctx);
  const cc = first.headers.get('cache-control') ?? '';
  fetched = [];
  await worker.fetch(new Request('https://dl.example.com/matcaps-v1/missing.png'), {}, ctx);
  if (first.status === 404 && cc.includes('no-store') && fetched.length === 1) {
    pass++; console.log('  ok   matcap upstream 404 not cached');
  } else { fail++; console.log(`  FAIL matcap upstream 404: ${first.status} ${cc}, refetched ${fetched.length}`); }
}

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
// Matcaps are PNG only and, like effects, uploaded with spaces already replaced by dots (upload-matcaps.yml).
await check('matcap wrong ext', '/matcaps-v1/payload.jpg', 404, null);
await check('space in matcap name', '/matcaps-v1/Gold Metal.png', 404, null);
await check('matcap nested', '/matcaps-v1/sub/Gold.Metal.png', 404, null);

console.log('the worker README is not published:');
{
  for (const method of ['GET', 'HEAD']) {
    fetched = [];
    const res = await worker.fetch(new Request('https://dl.example.com/mirror.md', { method }), {}, ctx);
    const body = await res.text();
    const ok = res.status === 404 && !body.includes('Proteus asset mirror') && fetched.length === 0;
    if (ok) { pass++; console.log(`  ok   ${method} /mirror.md is a 404`); }
    else { fail++; console.log(`  FAIL ${method} /mirror.md: status ${res.status}, fetched ${fetched.join(',')}`); }
  }
}

console.log('milieu readme (the page for new Milieu users):');
{
  const MILIEU_README = 'https://raw.githubusercontent.com/solona-m/milieu/test/README.md';
  for (const p of ['/milieu', '/milieu/', '/milieu/README.md']) {
    store.clear();
    fetched = [];
    const res = await worker.fetch(new Request('https://dl.example.com' + p), {}, ctx);
    const ok = res.status === 200 && fetched[0] === MILIEU_README
      && res.headers.get('content-type')?.startsWith('text/markdown');
    if (ok) { pass++; console.log(`  ok   ${p} proxies Milieu's README`); }
    else { fail++; console.log(`  FAIL ${p}: status ${res.status}, fetched ${fetched[0]}`); }
  }
  store.clear();
  fetched = [];
  const html = await worker.fetch(new Request('https://dl.example.com/milieu', { headers: { accept: 'text/html' } }), {}, ctx);
  const page = await html.text();
  const ok = html.headers.get('content-type')?.startsWith('text/html') && /<title>[^<]*Milieu/.test(page) && !/<title>[^<]*Proteus/.test(page) && page.includes('href="/milieu/icon.png"');
  if (ok) { pass++; console.log('  ok   /milieu renders for a browser, titled Milieu, with its own icon'); }
  else { fail++; console.log(`  FAIL /milieu html: ${html.headers.get('content-type')} ${page.slice(0, 200)}`); }
}

{
  store.clear();
  fetched = [];
  const res = await worker.fetch(new Request('https://dl.example.com/milieu/icon.png'), {}, ctx);
  const ok = res.status === 200 && fetched[0] === 'https://raw.githubusercontent.com/solona-m/milieu/test/Milieu/images/icon.png'
    && res.headers.get('content-type') === 'image/png';
  if (ok) { pass++; console.log('  ok   /milieu/icon.png proxies the Milieu icon'); }
  else { fail++; console.log(`  FAIL /milieu/icon.png: ${res.status} ${fetched[0]}`); }
}

console.log('project readme (proxied, and the front door):');
{
  const PROJECT = 'https://raw.githubusercontent.com/solona-m/proteus/main/README.md';
  for (const p of ['/README.md', '/']) {
    store.clear();
    fetched = [];
    const res = await worker.fetch(new Request('https://dl.example.com' + p), {}, ctx);
    const ok = res.status === 200
      && fetched[0] === PROJECT
      && res.headers.get('content-type')?.startsWith('text/markdown');
    if (ok) { pass++; console.log(`  ok   ${p} -> the project README`); }
    else {
      fail++;
      console.log(`  FAIL ${p}: status ${res.status}, upstream ${fetched[0]}, ` +
                  `type ${res.headers.get('content-type')}`);
    }
  }

  // `/` is an alias, not a second entry: it must share the canonical cache slot rather than fetching
  // and storing the same document twice.
  store.clear();
  await worker.fetch(new Request('https://dl.example.com/README.md'), {}, ctx);
  fetched = [];
  await worker.fetch(new Request('https://dl.example.com/'), {}, ctx);
  if (fetched.length === 0) { pass++; console.log('  ok   / reuses the /README.md cache entry'); }
  else { fail++; console.log(`  FAIL / re-fetched: ${fetched[0]}`); }
}

console.log('fixed-upstream proxies (manifest + icon):');
{
  const RAW = 'https://raw.githubusercontent.com';
  for (const [path, upstream, ctype] of [
    ['/repo.json', `${RAW}/solona-m/plugins/main/repo.json`, 'application/json'],
    ['/icon.png', `${RAW}/solona-m/proteus/main/Proteus/images/icon.png`, 'image/png'],
  ]) {
    store.clear();
    fetched = [];
    fetchOpts = [];
    const res = await worker.fetch(new Request('https://dl.example.com' + path), {}, ctx);
    const cc = res.headers.get('cache-control') ?? '';
    const ttl = /max-age=(\d+)/.exec(cc)?.[1];
    const ok = res.status === 200
      && fetched[0] === upstream
      && res.headers.get('content-type')?.startsWith(ctype)
      // Both are MUTABLE. Inheriting the assets' immutable year would hide a new release from every
      // client for that whole year — the worst possible failure for the manifest specifically.
      && !cc.includes('immutable')
      && +ttl <= 86400;
    if (ok) { pass++; console.log(`  ok   ${path} -> raw.githubusercontent (${cc})`); }
    else {
      fail++;
      console.log(`  FAIL ${path}: status ${res.status}, upstream ${fetched[0]}, ` +
                  `type ${res.headers.get('content-type')}, cc "${cc}"`);
    }
  }

  // Second request must come from cache, not from raw.githubusercontent — the whole point, given the
  // manifest is re-fetched by every client on every launch.
  fetched = [];
  await worker.fetch(new Request('https://dl.example.com/icon.png'), {}, ctx);
  if (fetched.length === 0) { pass++; console.log('  ok   repeat request served from cache'); }
  else { fail++; console.log(`  FAIL repeat hit upstream: ${fetched[0]}`); }

  // The map is an exact whole-pathname lookup, so nothing adjacent reaches the second origin host.
  for (const p of ['/repo.json/x', '/x/repo.json', '/REPO.JSON', '/repo.json.bak']) {
    fetched = [];
    const res = await worker.fetch(new Request('https://dl.example.com' + p), {}, ctx);
    const reachedRaw = (fetched[0] ?? '').includes('raw.githubusercontent');
    if (res.status === 404 && !reachedRaw) { pass++; console.log(`  ok   ${p} rejected`); }
    else { fail++; console.log(`  FAIL ${p}: status ${res.status}, upstream ${fetched[0]}`); }
  }

  // Traversal normalises to an allowlisted path BEFORE matching, and serving that path's own resource
  // is correct — the upstream is a hardcoded literal, so no request can redirect it elsewhere. This
  // asserts the invariant that actually matters: whatever the path, the upstream is one of two fixed
  // URLs or nothing.
  for (const p of ['/icon.png/../repo.json', '/a/b/../../repo.json']) {
    fetched = [];
    const res = await worker.fetch(new Request('https://dl.example.com' + p), {}, ctx);
    const upstream = fetched[0] ?? '(none)';
    const FIXED = [
      '(none)',
      'https://raw.githubusercontent.com/solona-m/plugins/main/repo.json',
      'https://raw.githubusercontent.com/solona-m/proteus/main/Proteus/images/icon.png',
    ];
    if (FIXED.includes(upstream)) { pass++; console.log(`  ok   ${p} -> fixed upstream only (${res.status})`); }
    else { fail++; console.log(`  FAIL ${p} reached ${upstream}`); }
  }
}

console.log('markdown rendering + content negotiation:');
{
  const HTML = { Accept: 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8' };
  const MD = '# Proteus\n\nSee [Troubleshooting](TROUBLESHOOTING.md) and [Creators](For%20Creators.md).\n' +
             'Also [elsewhere](docs/other.md) and [ext](https://example.com/x).\n' +
             'And [queried](docs/other.md?plain=1#L3).\n\n' +
             '| Column | What |\n|---|---|\n| On | Enables it. |\n';

  const mdHandler = () => new Response(MD, {
    status: 200,
    headers: { 'content-type': 'text/plain; charset=utf-8' },
  });

  async function get(path, headers) {
    fetched = [];
    const res = await worker.fetch(
      new Request('https://dl.example.com' + path, { headers }), {}, ctx);
    return { res, body: await res.text() };
  }

  // A browser gets a real page.
  store.clear();
  globalThis.fetch = async (url) => { fetched.push(url); return mdHandler(); };
  {
    const { res, body } = await get('/', HTML);
    const ok = res.status === 200
      && res.headers.get('content-type')?.startsWith('text/html')
      // The front door negotiates on language as well, so it varies on both. Cloudflare's own Cache
      // API ignores Vary — this header is for downstream caches; the key is what protects us here.
      && res.headers.get('vary') === 'Accept, Accept-Language'
      && body.includes('<h1')
      && body.includes('<table')          // the README is mostly tables; GFM must be on
      && body.includes('<title>Proteus</title>')   // title from the first heading
      && body.includes('href="/icon.png"');         // Proteus pages keep Proteus's own favicon
    if (ok) { pass++; console.log('  ok   Accept: text/html -> rendered page'); }
    else {
      fail++;
      console.log(`  FAIL html: ${res.status} ${res.headers.get('content-type')} ` +
                  `vary=${res.headers.get('vary')} h1=${body.includes('<h1')} ` +
                  `table=${body.includes('<table')} title=${/<title>[^<]*<\/title>/.exec(body)?.[0]}`);
    }
  }

  // Tools keep getting the source, byte for byte. This is the existing contract.
  store.clear();
  {
    const { res, body } = await get('/README.md', { Accept: '*/*' });
    const ok = res.headers.get('content-type')?.startsWith('text/markdown') && body === MD;
    if (ok) { pass++; console.log('  ok   Accept: */* -> unchanged markdown source'); }
    else { fail++; console.log(`  FAIL raw: ${res.headers.get('content-type')}, identical=${body === MD}`); }
  }

  // THE bug the cache key exists to prevent. Cloudflare's Cache API ignores Vary, so if both variants
  // shared a key, whoever arrived first would decide what everyone else got.
  store.clear();
  {
    await get('/README.md', HTML);                       // populate the HTML variant first
    const { res, body } = await get('/README.md', { Accept: '*/*' });
    const ok = res.headers.get('content-type')?.startsWith('text/markdown')
      && body === MD && !body.includes('<h1');
    if (ok) { pass++; console.log('  ok   html and raw occupy separate cache entries'); }
    else { fail++; console.log(`  FAIL variant bleed: ${res.headers.get('content-type')}, html-ish=${body.includes('<h1')}`); }
  }

  // Relative links must land somewhere real: mirrored docs stay here, everything else goes to GitHub
  // so a doc added upstream later degrades to a working link instead of a 404.
  store.clear();
  {
    const { body } = await get('/', HTML);
    const checks = [
      ['mirrored, plain', 'href="/TROUBLESHOOTING.md"'],
      ['mirrored, encoded space', 'href="/For%20Creators.md"'],
      ['unmirrored -> GitHub', 'href="https://github.com/solona-m/proteus/blob/main/docs/other.md"'],
      ['absolute untouched', 'href="https://example.com/x"'],
      // Everything after the path travels with it. Rewriting only the pathname would quietly turn
      // ?plain=1 into a different page than the one the link was written for.
      ['query and hash preserved',
        'href="https://github.com/solona-m/proteus/blob/main/docs/other.md?plain=1#L3"'],
    ];
    for (const [label, needle] of checks) {
      if (body.includes(needle)) { pass++; console.log(`  ok   link ${label}`); }
      else { fail++; console.log(`  FAIL link ${label}: expected ${needle}`); }
    }
  }

  // The two newly mirrored documents, including the percent-encoded form a browser actually sends.
  for (const [path, upstream] of [
    ['/TROUBLESHOOTING.md', 'https://raw.githubusercontent.com/solona-m/proteus/main/TROUBLESHOOTING.md'],
    ['/For%20Creators.md', 'https://raw.githubusercontent.com/solona-m/proteus/main/For%20Creators.md'],
  ]) {
    store.clear();
    const { res } = await get(path, { Accept: '*/*' });
    if (res.status === 200 && fetched[0] === upstream) {
      pass++; console.log(`  ok   ${path} -> mirrored`);
    } else { fail++; console.log(`  FAIL ${path}: ${res.status}, upstream ${fetched[0]}`); }
  }

  // Encoded and literal-space forms are ONE document, not two cache entries and two upstream fetches.
  store.clear();
  await get('/For%20Creators.md', { Accept: '*/*' });
  {
    const { res } = await get('/For Creators.md', { Accept: '*/*' });
    if (res.status === 200 && fetched.length === 0) {
      pass++; console.log('  ok   encoded and literal space share one entry');
    } else { fail++; console.log(`  FAIL space forms diverged: ${res.status}, refetched ${fetched[0]}`); }
  }

  // A malformed escape must 404, not throw out of the worker.
  {
    fetched = [];
    const res = await worker.fetch(new Request('https://dl.example.com/%E0%A4%A'), {}, ctx);
    if (res.status === 404 && fetched.length === 0) {
      pass++; console.log('  ok   malformed percent-escape 404s without throwing');
    } else { fail++; console.log(`  FAIL malformed escape: ${res.status}, upstream ${fetched[0]}`); }
  }

  // Restore the shared stub for the suites below.
  globalThis.fetch = async (url, opts) => {
    fetched.push(url);
    fetchOpts.push(opts ?? {});
    if (url.includes('missing')) return new Response('nope', { status: 404 });
    return new Response('BODY', {
      status: 200,
      headers: { 'content-type': 'application/octet-stream', 'content-length': '4', etag: '"abc"' },
    });
  };
  store.clear();
}

console.log('language negotiation:');
{
  const HTML = { Accept: 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8' };
  const RAW = 'https://raw.githubusercontent.com/solona-m/proteus/main/';

  // Each stand-in document names its own language in the body, so a test can tell which one came
  // back. The nav block is the real thing: it must be STRIPPED and replaced by the generated
  // switcher, and its relative links — written for a checkout — must resolve to this host.
  const doc = (code, nav, tail = '') =>
    `# Proteus\n\n<!--i18n-->\n${nav}\n<!--/i18n-->\n\nBODY-${code}\n${tail}`;
  const ROOT = doc('en', '**English** · [日本語](docs/README.ja.md) · [Deutsch](docs/README.de.md)');
  const DE = doc('de', '[English](../README.md) · [日本語](README.ja.md) · **Deutsch**',
    '\nSiehe [Fehlerbehebung](../TROUBLESHOOTING.md) und [Ersteller](../For%20Creators.md).\n');

  globalThis.fetch = async (url) => {
    fetched.push(url);
    const body = url === `${RAW}README.md` ? ROOT
      : url === `${RAW}docs/README.de.md` ? DE
        : url.startsWith(`${RAW}docs/README.`) ? doc(url.slice(-5, -3), 'x')
          : 'MISSING';
    return new Response(body, {
      status: 200,
      headers: { 'content-type': 'text/plain; charset=utf-8' },
    });
  };

  async function get(path, headers) {
    fetched = [];
    const res = await worker.fetch(
      new Request('https://dl.example.com' + path, { headers }), {}, ctx);
    return { res, body: await res.text() };
  }

  const t = (name, ok, detail = '') => {
    if (ok) { pass++; console.log(`  ok   ${name}`); }
    else { fail++; console.log(`  FAIL ${name}${detail ? ': ' + detail : ''}`); }
  };

  // The header alone decides, on the rendered variant of `/`. This is the whole feature.
  for (const [header, want] of [
    ['de-DE,de;q=0.9,en;q=0.8', 'de'],
    ['zh-TW,zh-Hans;q=0.9', 'zh'],              // one Chinese translation; primary subtag wins
    ['en;q=0.2,de;q=0.9', 'de'],                // q-order, not header order
    ['it-IT,it;q=0.9', 'en'],                   // unshipped -> the English default
    ['*', 'en'],
  ]) {
    store.clear();
    const { res, body } = await get('/', { ...HTML, 'Accept-Language': header });
    t(`Accept-Language "${header}" -> ${want}`,
      body.includes(`BODY-${want}`) && body.includes(`<html lang="${want}">`)
      && res.headers.get('content-language') === want,
      `body ${/BODY-\w+/.exec(body)?.[0]}, lang ${/<html lang="(\w+)"/.exec(body)?.[1]}`);
  }

  // No preference at all still works.
  store.clear();
  {
    const { body } = await get('/', HTML);
    t('no Accept-Language -> English', body.includes('BODY-en'));
  }

  // Negotiation is for BROWSERS. curl and scripts asked for the source and still get the English
  // source, byte for byte — the existing contract, unchanged.
  store.clear();
  {
    const { res, body } = await get('/README.md', { Accept: '*/*', 'Accept-Language': 'de' });
    t('raw markdown ignores Accept-Language',
      body === ROOT && res.headers.get('content-type')?.startsWith('text/markdown'),
      `identical=${body === ROOT}`);

    // ...and says so. Claiming to vary on a header it ignores makes every shared cache downstream
    // keep one identical copy per distinct Accept-Language string it sees.
    t('raw markdown does not claim to vary on language',
      res.headers.get('vary') === 'Accept', `vary=${res.headers.get('vary')}`);
  }

  // The rendered variant of the same path does vary, and must still say so.
  store.clear();
  {
    const { res } = await get('/README.md', HTML);
    t('rendered README varies on language',
      res.headers.get('vary') === 'Accept, Accept-Language', `vary=${res.headers.get('vary')}`);
  }

  // A pinned path does not negotiate, so it does not vary either.
  store.clear();
  {
    const { res } = await get('/de/README.md', HTML);
    t('pinned language paths do not vary on language',
      res.headers.get('vary') === 'Accept', `vary=${res.headers.get('vary')}`);
  }

  // A per-language path means exactly what it says, whatever the browser prefers — otherwise a
  // shared link would show the recipient something other than what the sharer saw.
  store.clear();
  {
    const { body } = await get('/de/README.md', { ...HTML, 'Accept-Language': 'ja' });
    t('/de/README.md is pinned against Accept-Language', body.includes('BODY-de'),
      `${/BODY-\w+/.exec(body)?.[0]}`);
  }

  // Same document, shorter names. `/de` without the trailing slash is what a person actually types
  // and what survives being pasted around; a 404 there reads as "that language does not exist".
  for (const short of ['/de/', '/de']) {
    store.clear();
    await get('/de/README.md', HTML);
    const { res, body } = await get(short, HTML);
    t(`${short} reuses the /de/README.md cache entry`,
      res.status === 200 && fetched.length === 0 && body.includes('BODY-de'),
      `status ${res.status}, refetched ${fetched[0] ?? '(none)'}`);
  }

  // THE bug the language part of the cache key exists to prevent: Cloudflare's Cache API ignores
  // Vary, so one key for every language would serve whoever arrived first to everybody after.
  store.clear();
  await get('/', { ...HTML, 'Accept-Language': 'de' });
  {
    const { body } = await get('/', { ...HTML, 'Accept-Language': 'ja' });
    t('languages occupy separate cache entries', body.includes('BODY-ja'),
      `got ${/BODY-\w+/.exec(body)?.[0]}`);
  }

  // The switcher: one entry per shipped language, the current one marked, and the markdown nav it
  // replaces gone — two rows of the same eight languages would be worse than none.
  store.clear();
  {
    const { body } = await get('/de/README.md', HTML);
    t('switcher is rendered', body.includes('<nav class="langs"'));
    t('switcher links to pinned paths', body.includes('href="/ja/README.md"')
      && body.includes('href="/en/README.md"'));
    t('current language is marked, not linked',
      /<span aria-current="page" lang="de">Deutsch<\/span>/.test(body)
      && !body.includes('href="/de/README.md"'));
    t('one entry per shipped language', (body.match(/hreflang="/g) ?? []).length === 7);
    t('markdown nav is stripped', !body.includes('<!--i18n')
      && (body.match(/日本語/g) ?? []).length === 1,
      `${(body.match(/日本語/g) ?? []).length} copies of the Japanese entry`);
  }

  // Links in a translated document are written relative to docs/, so they only land anywhere real if
  // they are resolved against the document's own path first.
  store.clear();
  {
    const { body } = await get('/de/README.md', HTML);
    for (const [label, needle] of [
      ['../TROUBLESHOOTING.md -> /TROUBLESHOOTING.md', 'href="/TROUBLESHOOTING.md"'],
      ['../For%20Creators.md -> /For%20Creators.md', 'href="/For%20Creators.md"'],
    ]) t(`link ${label}`, body.includes(needle));
  }

  // A document with no translations gets no switcher: it would promise pages that do not exist.
  store.clear();
  {
    const { body } = await get('/TROUBLESHOOTING.md', HTML);
    t('untranslated documents render without a switcher', !body.includes('<nav class="langs"'));
  }

  // The parser itself, at the edges.
  for (const [header, want] of [
    ['fr-CA', 'fr'],
    ['de;q=0', null],              // q=0 is "explicitly not this one"
    ['*', null],
    ['it', null],
    ['', null],
    [null, null],
  ]) {
    t(`pickLanguage(${JSON.stringify(header)}) -> ${want}`, pickLanguage(header) === want,
      `got ${pickLanguage(header)}`);
  }

  // Restore the shared stub for the suites below.
  globalThis.fetch = async (url, opts) => {
    fetched.push(url);
    fetchOpts.push(opts ?? {});
    if (url.includes('missing')) return new Response('nope', { status: 404 });
    return new Response('BODY', {
      status: 200,
      headers: { 'content-type': 'application/octet-stream', 'content-length': '4', etag: '"abc"' },
    });
  };
  store.clear();
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

// Heading ids. The plugin's header band links into README sections by these ids, in every language, so
// they must be exactly what GitHub generates — and marked, left alone, generates none.
console.log('heading ids:');
{
  const html = renderMarkdown(
    '# Proteus\n\n#### Color Editor\n\n##### Part switches\n\n#### スタジオ\n\n##### Éditeur de couleurs\n\n' +
    '#### 색상 편집기\n\n### Rows\n\n### Rows\n\n#### `Proteus/x` & **bold**\n');
  for (const [label, needle] of [
    ['plain', '<h4 id="color-editor">'],
    ['spaces to hyphens', '<h5 id="part-switches">'],
    ['CJK kept', '<h4 id="スタジオ">'],
    ['accented, lower-cased', '<h5 id="éditeur-de-couleurs">'],
    ['Hangul with a space', '<h4 id="색상-편집기">'],
    ['first duplicate unsuffixed', '<h3 id="rows">'],
    ['second duplicate suffixed', '<h3 id="rows-1">'],
    ['markup and punctuation dropped', '<h4 id="proteusx--bold">'],
  ]) {
    if (html.includes(needle)) { pass++; console.log(`  ok   heading id ${label}`); }
    else { fail++; console.log(`  FAIL heading id ${label}: expected ${needle}`); }
  }
  // Nested and broken tags leave no tag behind.
  for (const [input, want] of [
    ['x <<b>b> y', 'x b> y'],
    ['<scr<script>ipt>a', 'ipt>a'],
    ['<b><i>bold</i></b>', 'bold'],
    ['plain', 'plain'],
  ]) {
    const got = stripTags(input);
    if (got === want && !/<[^>]*>/.test(got)) { pass++; console.log(`  ok   stripTags ${JSON.stringify(input)}`); }
    else { fail++; console.log(`  FAIL stripTags ${JSON.stringify(input)}: got ${JSON.stringify(got)}, want ${JSON.stringify(want)}`); }
  }
}

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);

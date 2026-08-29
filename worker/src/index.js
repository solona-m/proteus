/**
 * Proteus asset mirror.
 *
 * An edge cache in front of this repo's GitHub release assets. GitHub throttles anonymous
 * release-asset downloads, and Proteus pulls two 128 MB UV transfer maps plus a starter effect
 * library on first run — enough traffic, across enough installs, to keep running into that throttle.
 * GitHub stays the origin and the source of truth; this only caches.
 *
 * Every path it serves is IMMUTABLE (the tag is part of the path and a tag's assets are never
 * replaced — see .github/workflows/upload-uvmaps.yml), which is what makes a one-year TTL safe.
 *
 * MUST be deployed on a custom domain in a Cloudflare zone. A *.workers.dev deployment cannot use
 * the Cache API or cf.cacheEverything, so it would proxy every byte from GitHub uncached and make
 * the problem it exists to solve strictly worse.
 */

import README from './readme.generated.js';

const OWNER = 'solona-m';
const REPO = 'proteus';

// Served at / and /README.md so the host explains itself instead of answering a bare 404. Inlined at
// build time (scripts/embed-readme.mjs) rather than fetched, so it always describes THIS deployment
// and the page has no runtime dependency on anything.
//
// Short TTL, unlike the assets: the README is the one mutable thing here, and it is small enough that
// re-fetching it every few minutes costs nothing.
const README_TTL = 300;

// Anchored, and each segment is a tight character class rather than a wildcard: the path decides
// which upstream URL we build, so a loose pattern here would be an open proxy for any GitHub file.
// Kept in sync with ProteusAssets.BaseUrls / UVMapDownloadService / DefaultEffectsDownloadService.
// Paths are `/<tag>/<file>` and map 1:1 onto `releases/download/<tag>/<file>` — the tag already says
// what kind of asset it is, so there is no category segment to keep in sync with the client. That is
// deliberate: it lets ProteusAssets.BaseUrls be nothing more than `MirrorBase + tag + "/"`, and a
// mirror URL that drifts from an origin URL is a bug that only shows up in production.
//
// The first capture is the tag VERBATIM — no normalisation — so what is asked for is what is fetched.
// Stable releases tag `v2608.309.0.0`, testing builds tag `testing-309`.
const ROUTES = [
  /^\/(uvmaps-[a-z0-9.-]{1,32})\/([A-Za-z0-9_.-]{1,80}\.tif)$/,
  // No space in the class: effect assets are uploaded with spaces already replaced by dots, because
  // GitHub rewrites spaces in asset names (see upload-effects.yml).
  /^\/(effects-[a-z0-9.-]{1,32})\/([A-Za-z0-9_.-]{1,80}\.(?:png|jpe?g))$/,
  /^\/(v[0-9][0-9.]{0,30}|testing-[0-9]{1,10})\/(latest\.zip)$/,
];

const YEAR = 31536000;

/**
 * Bump to invalidate Cloudflare's own edge cache of the UPSTREAM fetches.
 *
 * There are two caches here: `caches.default`, which this worker writes explicitly and only ever on a
 * 200, and Cloudflare's cache of the `fetch()` subrequest, keyed by the origin URL. The second one is
 * not reachable from Worker code — `caches.default.delete()` does not clear it — so a bad entry there
 * can only be removed by purging the zone from the dashboard, or by changing the URL.
 *
 * That is not hypothetical: an earlier revision cached upstream responses with a bare `cacheTtl` of one
 * year, which applied to a 404 as readily as to an asset. Every effects-v1 asset was requested through
 * the mirror before that release was published, so all eleven 404s were pinned for a year — the tag
 * went live and the mirror kept insisting the files did not exist. `cacheTtlByStatus` stops NEW
 * failures being cached; this epoch is what releases the ones already stuck.
 *
 * Appended as a query parameter, which GitHub ignores on a release-download URL (verified: the asset
 * returns 200 with or without it) but which Cloudflare treats as a different cache key.
 */
const ORIGIN_EPOCH = '2';

/**
 * Exact paths proxied to a fixed upstream. Deliberately a lookup of whole pathnames rather than a
 * pattern: these point at raw.githubusercontent.com, a second origin host, and the safety of the
 * route table above rests on the path never being able to name an arbitrary upstream. A literal map
 * cannot, no matter what is thrown at it.
 *
 * These matter more than their size suggests. GitHub throttles on REQUEST COUNT, not bytes, and the
 * plugin manifest is re-fetched by every Dalamud client on every startup and every list refresh —
 * per user, forever. That is a far higher request rate than the release assets, which are fetched
 * once per install. The icon is fetched by anyone who opens the plugin installer at all.
 *
 * Both are MUTABLE, so neither can take the assets' immutable one-year TTL: the manifest changes on
 * every release, and a stale one would hide a new version from everybody for the length of the TTL.
 */
const STATIC_PROXIES = {
  '/repo.json': {
    origin: 'https://raw.githubusercontent.com/solona-m/plugins/main/repo.json',
    type: 'application/json; charset=utf-8',
    // Short: this is how a client learns a release exists. Long enough to collapse the startup
    // stampede, short enough that a new release is visible almost immediately.
    ttl: 300,
  },
  '/icon.png': {
    origin: 'https://raw.githubusercontent.com/solona-m/proteus/main/Proteus/images/icon.png',
    type: 'image/png',
    ttl: 86400,
  },
};

export default {
  async fetch(request, _env, ctx) {
    if (request.method !== 'GET' && request.method !== 'HEAD') {
      return new Response('Method not allowed', { status: 405, headers: { Allow: 'GET, HEAD' } });
    }

    const url = new URL(request.url);

    // Static, and it never builds an upstream URL — so it cannot participate in the open-proxy risk
    // the route patterns below exist to contain.
    if (url.pathname === '/' || url.pathname === '/README.md') {
      return new Response(request.method === 'HEAD' ? null : README, {
        status: 200,
        headers: {
          'content-type': 'text/markdown; charset=utf-8',
          'cache-control': `public, max-age=${README_TTL}`,
          'x-proteus-mirror': 'readme',
        },
      });
    }

    // Fixed-upstream proxies (the plugin manifest and its icon). Cached at the edge with their own
    // short TTLs, and re-asked on any failure so a transient upstream error is not pinned.
    const stat = STATIC_PROXIES[url.pathname];
    if (stat) {
      const cache = caches.default;
      const key = new Request(`${url.origin}${url.pathname}`, { method: 'GET' });

      let hit = await cache.match(key);
      if (!hit) {
        const upstream = await fetch(stat.origin, {
          redirect: 'follow',
          cf: {
            cacheEverything: true,
            cacheTtlByStatus: { '200-299': stat.ttl, '300-399': 0, '400-499': 0, '500-599': 0 },
          },
        });
        if (!upstream.ok) {
          return new Response(`Upstream ${upstream.status}`, {
            status: upstream.status,
            headers: { 'cache-control': 'no-store' },
          });
        }
        hit = new Response(upstream.body, {
          status: 200,
          headers: {
            'content-type': stat.type,
            'cache-control': `public, max-age=${stat.ttl}`,
          },
        });
        ctx.waitUntil(cache.put(key, hit.clone()));
      }

      const out = new Response(request.method === 'HEAD' ? null : hit.body, hit);
      out.headers.set('x-proteus-mirror', 'raw');
      return out;
    }

    let tag = null;
    let file = null;
    for (const rx of ROUTES) {
      const m = rx.exec(url.pathname);
      if (m) { tag = m[1]; file = m[2]; break; }
    }
    if (!tag) return new Response('Not found', { status: 404 });

    // Cache key deliberately ignores the query string AND the Range header. Keying on the incoming
    // request would give every distinct byte range its own cache entry, so a resuming client would
    // miss the cache exactly when it can least afford to re-pull 128 MB.
    const cacheKey = new Request(`${url.origin}${url.pathname}`, { method: 'GET' });
    const cache = caches.default;

    let hit = await cache.match(cacheKey);

    if (!hit) {
      const origin =
        `https://github.com/${OWNER}/${REPO}/releases/download/${tag}/${encodeURIComponent(file)}` +
        `?e=${ORIGIN_EPOCH}`;

      // Always fetch the WHOLE object, never the client's range: the cache needs a complete 200 to
      // store, and Cloudflare slices ranges out of it afterwards.
      //
      // cacheTtlByStatus, NOT a bare cacheTtl. A plain `cacheTtl: YEAR` gives no per-status control, so
      // a 404 for a tag that has not been published YET can be cached as though it were an asset — and
      // then the mirror keeps answering 404 long after the release exists, which is unrecoverable
      // without a manual purge. Only 2xx is worth keeping; every failure must be re-asked next time.
      const upstream = await fetch(origin, {
        redirect: 'follow',                       // github.com -> objects.githubusercontent.com
        cf: {
          cacheEverything: true,
          cacheTtlByStatus: { '200-299': YEAR, '300-399': 0, '400-499': 0, '500-599': 0 },
        },
      });

      if (!upstream.ok) {
        // Pass the origin's status through unchanged so the plugin's own fallback and retry logic
        // sees the real reason (404 vs 429) instead of a status this worker invented.
        return new Response(`Upstream ${upstream.status}`, {
          status: upstream.status,
          headers: { 'cache-control': 'no-store' },
        });
      }

      // Copy through only the headers the origin actually sent. Setting content-length or etag to an
      // empty string is not the same as omitting them — an empty content-length in particular is a
      // malformed response, and it would reach a client that is resuming a 128 MB download.
      const headers = new Headers({
        'content-type': upstream.headers.get('content-type') ?? 'application/octet-stream',
        'cache-control': `public, max-age=${YEAR}, immutable`,
        'accept-ranges': 'bytes',
      });
      for (const h of ['content-length', 'etag', 'last-modified']) {
        const v = upstream.headers.get(h);
        if (v) headers.set(h, v);
      }

      const body = new Response(upstream.body, { status: 200, headers });

      // waitUntil so the store completes even after we have answered this request.
      ctx.waitUntil(cache.put(cacheKey, body.clone()));
      hit = body;
    }

    // Re-issue through the cache so Cloudflare slices the 206 itself. On a cold request the entry is
    // usually not stored yet (the put above is still in flight), so this misses and we fall through to
    // returning the whole object with a 200. That is safe rather than merely tolerable: the plugin's
    // downloader explicitly treats "asked for a range, got a 200" as "this server ignores Range" and
    // restarts the file from zero instead of appending to its partial. See ResilientDownloader.
    if (request.headers.has('range')) {
      const ranged = await cache.match(request);
      if (ranged) return ranged;
    }

    const out = new Response(request.method === 'HEAD' ? null : hit.body, hit);
    out.headers.set('x-proteus-mirror', 'github');
    return out;
  },
};

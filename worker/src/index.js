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
import { renderMarkdown, LANGS, docPathFor, mirrorPathFor } from './render.js';

const OWNER = 'solona-m';
const REPO = 'proteus';

// A second plugin served by the same mirror. Only its release zips are hosted here — its listing
// lives in the same repo.json this worker already proxies, and it ships no icon or documents, so it
// needs nothing in STATIC_PROXIES. REPO stays the default everywhere else: rawUrl and the document
// table below are Proteus's own docs, not a shared surface.
const CAMERA_TOOLS_REPO = 'camera-tools-ffxiv';

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
//
// Each entry carries the repo its paths resolve against, because the mirror now fronts two plugins.
// Proteus's paths are the bare `/<tag>/<file>` forms and MUST stay that way: ProteusAssets.BaseUrls
// builds them as `MirrorBase + tag + "/"` in every shipped build, so adding a prefix to them would
// break every copy of the plugin already installed. A second plugin therefore gets a prefix segment
// of its own rather than the existing routes being generalised.
const ROUTES = [
  { rx: /^\/(uvmaps-[a-z0-9.-]{1,32})\/([A-Za-z0-9_.-]{1,80}\.tif)$/, repo: REPO },
  // No space in the class: effect assets are uploaded with spaces already replaced by dots, because
  // GitHub rewrites spaces in asset names (see upload-effects.yml).
  { rx: /^\/(effects-[a-z0-9.-]{1,32})\/([A-Za-z0-9_.-]{1,80}\.(?:png|jpe?g))$/, repo: REPO },
  { rx: /^\/(v[0-9][0-9.]{0,30}|testing-[0-9]{1,10})\/(latest\.zip)$/, repo: REPO },
  // Camera Tools. The prefix cannot collide with the route above it: that one requires the first
  // segment to start `v<digit>` or `testing-<digit>`, which `camera-tools` does not.
  {
    rx: /^\/camera-tools\/(v[0-9][0-9.]{0,30}|testing-[0-9]{1,10})\/(latest\.zip)$/,
    repo: CAMERA_TOOLS_REPO,
  },
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
 * A document's raw URL in this repo, built from its path in a CHECKOUT.
 *
 * Encoded here rather than at each call site so the paths in the table below can stay readable and
 * match what render.js resolves links to — "For Creators.md", not "For%20Creators.md".
 */
const rawUrl = (repoPath) =>
  `https://raw.githubusercontent.com/${OWNER}/${REPO}/main/${encodeURI(repoPath)}`;

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
  // The PROJECT readme — install instructions, the tab-by-tab guide — not this worker's own docs.
  // Proxied rather than inlined so it tracks the repo without a redeploy; it is documentation whose
  // whole value is being current, unlike the mirror's own notes, which must describe the deployment
  // actually serving them.
  //
  // `negotiate` is what makes `/` answer a German browser in German — see pickLanguage. It applies to
  // the RENDERED variant only; the markdown source stays English here, and a reader or a script that
  // wants a specific language asks for `/de/README.md`, which never negotiates.
  '/README.md': {
    origin: rawUrl('README.md'),
    type: 'text/markdown; charset=utf-8',
    ttl: 900,
    markdown: true,
    lang: 'en',
    docPath: 'README.md',
    negotiate: true,
  },
  // The two documents the README links to. Mirrored so those links resolve to rendered pages here
  // rather than bouncing the reader back to GitHub halfway through the install instructions.
  //
  // No `lang`: these two are English-only, and a switcher on them would promise translations that do
  // not exist. A reader following a link out of a translated README lands on English and can see that
  // for themselves.
  '/TROUBLESHOOTING.md': {
    origin: rawUrl('TROUBLESHOOTING.md'),
    type: 'text/markdown; charset=utf-8',
    ttl: 900,
    markdown: true,
    docPath: 'TROUBLESHOOTING.md',
  },
  // Stored DECODED, because the lookup decodes the path — a browser sends "For%20Creators.md".
  '/For Creators.md': {
    origin: rawUrl('For Creators.md'),
    type: 'text/markdown; charset=utf-8',
    ttl: 900,
    markdown: true,
    docPath: 'For Creators.md',
  },

  // One pinned path per shipped language, `/ja/README.md` and friends, including `/en/README.md`.
  // These are the paths the switcher links to and the ones every translated README's own links
  // resolve to. They are PINNED: whatever the browser's Accept-Language says, the path decides, so a
  // link someone shares shows the reader what the sharer saw.
  ...Object.fromEntries(LANGS.map(({ code }) => [mirrorPathFor(code), {
    origin: rawUrl(docPathFor(code)),
    type: 'text/markdown; charset=utf-8',
    ttl: 900,
    markdown: true,
    lang: code,
    docPath: docPathFor(code),
  }])),
};

/**
 * Alias paths — same entry, different name. Keeps `/` meaning "tell me about Proteus", and gives each
 * language the short `/ja` and `/ja/` forms as well as `/ja/README.md`.
 *
 * BOTH slash forms, deliberately. `/ja` is what a person types, what survives being pasted into
 * Discord, and what is left after someone trims the address bar — and a bare 404 there reads as "that
 * language does not exist" rather than "you missed a character". They are aliases rather than a
 * redirect so the short form costs no extra round trip and shares one cache entry.
 */
const STATIC_ALIASES = {
  '/': '/README.md',
  ...Object.fromEntries(LANGS.flatMap(({ code }) =>
    [[`/${code}`, mirrorPathFor(code)], [`/${code}/`, mirrorPathFor(code)]])),
};

/**
 * Picks a shipped language out of an Accept-Language header, or null for "no opinion we can honour".
 *
 * Matches on the PRIMARY SUBTAG only: `zh-TW`, `zh-Hans-CN` and plain `zh` all land on `zh`, because
 * one Chinese translation is what exists and answering a Taiwanese reader in Simplified beats
 * answering them in English. `q=0` means "explicitly not this one" and is dropped rather than ranked.
 *
 * Ties keep header order, which is the order the reader put their languages in.
 */
export function pickLanguage(header) {
  if (!header) return null;

  const ranked = header.split(',')
    .map((part, i) => {
      const [tag, ...params] = part.trim().split(';');
      const q = params.map((p) => /^\s*q\s*=\s*([0-9.]+)\s*$/i.exec(p)).find(Boolean);
      return { code: tag.trim().toLowerCase().split('-')[0], q: q ? Number(q[1]) : 1, i };
    })
    .filter((e) => e.code && Number.isFinite(e.q) && e.q > 0)
    .sort((a, b) => (b.q - a.q) || (a.i - b.i));

  // `*` means "anything"; there is nothing to prefer, so fall through to the default.
  const hit = ranked.find((e) => LANGS.some((l) => l.code === e.code));
  return hit ? hit.code : null;
}

export default {
  async fetch(request, _env, ctx) {
    if (request.method !== 'GET' && request.method !== 'HEAD') {
      return new Response('Method not allowed', { status: 405, headers: { Allow: 'GET, HEAD' } });
    }

    const url = new URL(request.url);

    // A browser asks for text/html and gets a rendered page; curl and every script send */* and keep
    // getting the markdown source, so nothing that consumes these URLs programmatically changes.
    const wantsHtml = (request.headers.get('accept') ?? '').includes('text/html');

    // This worker's OWN documentation — deploy steps, path scheme, caching caveats. Inlined at build
    // time rather than proxied so it always describes the deployment serving it, and static, so it
    // never builds an upstream URL and cannot participate in the open-proxy risk the route patterns
    // below exist to contain. The PROJECT readme is a different document, served from `/` via
    // STATIC_PROXIES.
    if (url.pathname === '/mirror.md') {
      // docPath is where this file lives in the REPO — worker/README.md — so its relative links
      // ("wrangler.toml", "../Proteus/Services/ProteusAssets.cs") resolve the way a checkout would.
      const body = wantsHtml
        ? renderMarkdown(README, 'Proteus asset mirror', { docPath: 'worker/README.md' })
        : README;
      return new Response(request.method === 'HEAD' ? null : body, {
        status: 200,
        headers: {
          'content-type': wantsHtml ? 'text/html; charset=utf-8' : 'text/markdown; charset=utf-8',
          'cache-control': `public, max-age=${README_TTL}`,
          vary: 'Accept',
          'x-proteus-mirror': 'readme',
        },
      });
    }

    // Fixed-upstream proxies (the plugin manifest and its icon). Cached at the edge with their own
    // short TTLs, and re-asked on any failure so a transient upstream error is not pinned.
    // Decoded, so "/For%20Creators.md" (what a browser sends) and a literal space are one entry.
    // A malformed escape must fall through to the 404 below rather than throw out of the worker.
    let decodedPath = url.pathname;
    try { decodedPath = decodeURIComponent(url.pathname); } catch { /* keep raw; it will not match */ }

    const statPath = STATIC_ALIASES[decodedPath] ?? decodedPath;
    const stat = STATIC_PROXIES[statPath];
    if (stat) {
      const cache = caches.default;
      const renderHtml = wantsHtml && stat.markdown === true;

      // Language negotiation, for the rendered variant of `/` and `/README.md` only. A reader who
      // typed nothing and expressed a preference only through their browser gets that preference;
      // everything a URL states explicitly — `/de/README.md`, or the markdown source — is left alone,
      // so nothing that consumes these paths programmatically changes.
      //
      // English is left on the un-suffixed key rather than redirected to the `/en/` entry: the bytes
      // are identical, and one cache entry for the overwhelmingly common case beats two.
      const picked = renderHtml && stat.negotiate === true
        ? pickLanguage(request.headers.get('accept-language'))
        : null;
      const entry = picked && picked !== 'en' ? (STATIC_PROXIES[mirrorPathFor(picked)] ?? stat) : stat;
      const variant = entry === stat ? '' : `&lang=${entry.lang}`;

      // The variant is part of the KEY, not just a Vary header. Cloudflare's Cache API keys on URL
      // alone and ignores Vary, so one key for both variants would serve whichever arrived first to
      // everyone after — HTML to curl, raw markdown to every browser, or one reader's language to
      // all the others, at random.
      const key = new Request(
        `${url.origin}${encodeURI(statPath)}${renderHtml ? '?view=html' : ''}${variant}`,
        { method: 'GET' });

      let hit = await cache.match(key);
      if (!hit) {
        const upstream = await fetch(entry.origin, {
          redirect: 'follow',
          cf: {
            cacheEverything: true,
            cacheTtlByStatus: { '200-299': entry.ttl, '300-399': 0, '400-499': 0, '500-599': 0 },
          },
        });
        if (!upstream.ok) {
          return new Response(`Upstream ${upstream.status}`, {
            status: upstream.status,
            headers: { 'cache-control': 'no-store' },
          });
        }

        // Rendered once per TTL and cached, rather than per request: marked on a 400-line document is
        // cheap but not free, and the free plan allows 10 ms of CPU per request.
        const body = renderHtml
          ? renderMarkdown(await upstream.text(), 'Proteus',
            { lang: entry.lang ?? null, docPath: entry.docPath })
          : upstream.body;

        hit = new Response(body, {
          status: 200,
          headers: {
            'content-type': renderHtml ? 'text/html; charset=utf-8' : entry.type,
            'cache-control': `public, max-age=${entry.ttl}`,
            // Advertised for downstream caches that do honour Vary, even though Cloudflare's own
            // Cache API does not — which is why the variant is in the key above as well.
            //
            // `&& renderHtml`, because that is the condition negotiation itself is gated on: the raw
            // markdown of /README.md is English whatever the reader's locale, and claiming otherwise
            // would make every shared cache downstream store one identical copy per distinct
            // Accept-Language string it sees — effectively per user.
            vary: stat.negotiate === true && renderHtml ? 'Accept, Accept-Language' : 'Accept',
            // Only on the documents that have a language. repo.json and icon.png do not.
            ...(entry.lang ? { 'content-language': entry.lang } : {}),
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
    let repo = null;
    for (const route of ROUTES) {
      const m = route.rx.exec(url.pathname);
      if (m) { tag = m[1]; file = m[2]; repo = route.repo; break; }
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
        `https://github.com/${OWNER}/${repo}/releases/download/${tag}/${encodeURIComponent(file)}` +
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

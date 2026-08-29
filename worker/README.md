# Proteus asset mirror

A Cloudflare Worker that edge-caches this repo's GitHub release assets. GitHub throttles anonymous
release-asset downloads; this puts a cache in front so repeat downloads are served by Cloudflare.
**GitHub stays the origin and the source of truth — this worker only caches.**

It is **optional**. With `ProteusAssets.MirrorBase` empty the plugin fetches straight from GitHub and
behaves exactly as it always did. The mirror is a preference, never a dependency: GitHub always stays
in the source list behind it, so a mirror outage degrades to a slower download, not a broken plugin.

---

## Setup

### 0. What you need

- A domain **on a Cloudflare zone** (any plan, Free is fine). Not optional, and not a `*.workers.dev`
  subdomain: workers.dev cannot use the Cache API or `cf.cacheEverything`, so a workers.dev
  deployment would proxy every byte from GitHub uncached — strictly worse than no mirror at all.
  If you don't have one: register a domain anywhere, then Cloudflare dashboard → **Add a site** and
  follow the nameserver instructions. Roughly $10/year and about 15 minutes of DNS propagation.
- Node.js, for `wrangler`.

### 1. Point the config at your domain

Edit [`wrangler.toml`](wrangler.toml) and uncomment/fill the `routes` block:

```toml
routes = [
  { pattern = "dl.YOURDOMAIN.com", custom_domain = true }
]
```

`dl.` is a convention, not a requirement — any hostname on a zone you own works.

**Use `custom_domain = true`, not a `zone_name` route.** The difference bites:

```toml
# WRONG for a hostname that has no DNS record yet:
routes = [ { pattern = "dl.YOURDOMAIN.com/*", zone_name = "YOURDOMAIN.com" } ]
```

A plain route only *attaches* the worker to a hostname that already resolves through Cloudflare — it
does not create the DNS record. Point one at a bare subdomain and `wrangler deploy` reports success,
prints your route, and every request then fails with `curl: (6) Could not resolve host`, which reads
like a broken worker rather than a missing record. A custom domain creates the DNS record and the
binding together. Note it takes **no `/*`** — a custom domain is a hostname, not a path pattern.

### 2. Deploy

```sh
cd worker
npm install
npm test               # routing/caching tests — no network or account needed
npx wrangler login     # opens a browser once
npm run deploy         # NOT `npx wrangler deploy` — see "Paths" below
```

### 3. Verify before you point the plugin at it

Do this now, not after shipping — a mirror that returns the wrong thing is worse than none.

**PowerShell** (what you get by default on Windows):

```powershell
# NOT $HOST — that is a reserved PowerShell automatic variable. And quote every URL: unquoted,
# PowerShell parses the "/" in $dl/uvmaps-v1/... as the division operator.
# `curl.exe`, not `curl`: bare `curl` is an alias for Invoke-WebRequest and ignores these flags.
$dl = "https://dl.YOURDOMAIN.com"

# (a) It serves the real file — the hash is the one pinned in UVMapDownloadService.MapFiles.
curl.exe -sL "$dl/uvmaps-v1/bibo_to_gen3_transfer.tif" -o map.tif
(Get-FileHash map.tif -Algorithm SHA256).Hash.ToLower()
# expect: 155e736ddfb78448552968cdac7cd32f76012c83d5488058387e9fc53bd61cba
Remove-Item map.tif

# (b) It actually caches. Run twice — the second must say HIT.
curl.exe -sI "$dl/uvmaps-v1/bibo_to_gen3_transfer.tif" | Select-String cf-cache-status
curl.exe -sI "$dl/uvmaps-v1/bibo_to_gen3_transfer.tif" | Select-String cf-cache-status

# (c) Range resume works. The plugin depends on this after a dropped connection.
# -L so the same command also works when pointed straight at github.com, which redirects.
curl.exe -sL -r 0-1023 -o NUL -w "%{http_code} %{size_download}`n" "$dl/uvmaps-v1/bibo_to_gen3_transfer.tif"
# expect: 206 1024   (see "Range on a cold cache" below if you get 200)

# (d) It is not an open proxy.
curl.exe -s -o NUL -w "%{http_code}`n" "$dl/uvmaps-v1/../../../etc/passwd"   # expect 404
curl.exe -s -o NUL -w "%{http_code}`n" "$dl/not-a-real-tag/thing.tif"        # expect 404
```

**bash / zsh:**

```sh
dl=https://dl.YOURDOMAIN.com

# (a) It serves the real file. 134217960 bytes, and the hash is the one pinned in
#     UVMapDownloadService.MapFiles.
curl -sL $dl/uvmaps-v1/bibo_to_gen3_transfer.tif | sha256sum
# expect: 155e736ddfb78448552968cdac7cd32f76012c83d5488058387e9fc53bd61cba

# (b) It actually caches. Run twice — the second must say HIT.
curl -sI $dl/uvmaps-v1/bibo_to_gen3_transfer.tif | grep -i cf-cache-status
curl -sI $dl/uvmaps-v1/bibo_to_gen3_transfer.tif | grep -i cf-cache-status

# (c) Range resume works. The plugin depends on this after a dropped connection.
# -L so the same command also works when pointed straight at github.com, which redirects.
curl -sL -r 0-1023 -o /dev/null -w '%{http_code} %{size_download}\n' \
  $dl/uvmaps-v1/bibo_to_gen3_transfer.tif
# expect: 206 1024   (see "Range on a cold cache" below if you get 200)

# (d) It is not an open proxy.
curl -s -o /dev/null -w '%{http_code}\n' $dl/uvmaps-v1/../../../etc/passwd   # expect 404
curl -s -o /dev/null -w '%{http_code}\n' $dl/not-a-real-tag/thing.tif        # expect 404
```

### 4. Switch the plugin over

In [`Proteus/Services/ProteusAssets.cs`](../Proteus/Services/ProteusAssets.cs):

```csharp
public const string MirrorBase = "https://dl.YOURDOMAIN.com/";
```

Trailing slash required. Then rebuild; the plugin will try the mirror first and fall back to GitHub
on any failure. Confirm in `dalamud.log` that the fetch URL is now your host.

### 5. (Optional) Move Dalamud's own download too

This is the lowest-value step — the plugin zip is ~1.6 MB — and the only one with **no client-side
fallback**, since Dalamud fetches exactly one URL. In the separate `solona-m/plugins` repo, point
`repo.json`'s `DownloadLinkInstall` / `DownloadLinkUpdate` / `DownloadLinkTesting` at
`https://dl.YOURDOMAIN.com/<tag>/latest.zip`, where `<tag>` is `v2608.309.0.0` or `testing-309`.

---

## Paths

`/<tag>/<file>` maps 1:1 onto `releases/download/<tag>/<file>`. The tag already identifies the asset
type, so there is no category segment for the client and the worker to disagree about.

| Path | Origin |
|---|---|
| `/uvmaps-v1/bibo_to_gen3_transfer.tif` | `releases/download/uvmaps-v1/…` |
| `/effects-v1/hello.kitty.png` | `releases/download/effects-v1/…` |
| `/v2608.309.0.0/latest.zip` | `releases/download/v2608.309.0.0/…` |
| `/` and `/README.md` | the project README, from `raw.githubusercontent.com` |
| `/TROUBLESHOOTING.md`, `/For%20Creators.md` | the docs the README links to |
| `/mirror.md` | this file |

### Documents render for browsers, stay raw for tools

The four document paths negotiate on `Accept`:

- `Accept: text/html` (any browser) → a rendered, styled HTML page
- anything else (`curl`, scripts, `*/*`) → the markdown source, byte for byte

Nothing needs a different URL, and nothing that consumes these programmatically changed.

Two details that are easy to get wrong if this is ever refactored:

- **The variant is part of the cache key** (`?view=html`), not just a `Vary: Accept` header.
  Cloudflare's Cache API keys on URL alone and *ignores* `Vary` — so a single key for both variants
  would serve whichever arrived first to everyone afterwards, HTML to `curl` or markdown to every
  browser, at random. `Vary` is still set, for downstream caches that do honour it.
- **Paths are looked up decoded**, so `/For%20Creators.md` and a literal space are one document rather
  than two cache entries and two upstream fetches. A malformed escape falls through to a 404 instead
  of throwing.

Relative links in the rendered output are rewritten: a link to a document this mirror serves stays
here, anything else goes to `github.com/solona-m/proteus/blob/main/…`, so a doc added upstream later
degrades to a working link rather than a 404.

Rendering uses `marked`, which does **not** sanitise — raw HTML in the source passes through. Every
document here comes from `solona-m/proteus`, which anyone who could inject into already has commit
access to the plugin, so this is a trust argument rather than a safety one. Do not point the renderer
at markdown from anywhere else without adding a sanitiser.

This file is inlined into the worker at build time by
[`scripts/embed-readme.mjs`](scripts/embed-readme.mjs), so `/mirror.md` always describes the
deployment serving it and has no runtime dependency. **That means deploying with `npm run deploy`, not
`npx wrangler deploy`** — the plain wrangler command skips the `predeploy` hook that regenerates it,
and would ship whatever text was embedded last.

The project docs are the opposite: proxied live from the repo with a 15-minute TTL, because their
whole value is being current and they should not need a worker redeploy to update. Neither is
immutable, so none of them take the assets' one-year TTL.

Every one is immutable, which is what makes the one-year TTL safe: a tag's assets are never replaced,
and the upload workflows refuse to overwrite an existing release. Revised content gets a new tag.

Each route is anchored with a tight character class. The path chooses the upstream URL, so a loose
pattern here would make this an open proxy for any file on GitHub.

## Notes

**Range on a cold cache.** On the very first request for an object the cache entry is still being
written, so a `Range` request falls through and gets the whole object with a `200`. That is safe, not
merely tolerable: `ResilientDownloader` treats "asked for a range, got a 200" as "this server ignores
Range" and restarts the file rather than appending to its partial. Re-run check (c) once the object is
cached and it will return `206` — this was observed exactly so on first deploy: `200 134217960` cold,
`206 1024` warm.

**A fresh hostname stays NXDOMAIN locally for a while.** If you queried the name *before* deploying,
your resolver has cached the negative answer and `curl` will keep failing with
`(6) Could not resolve host` even after the record exists. `Resolve-DnsName <host> -Server 1.1.1.1`
will show the truth. Either wait out the negative TTL or test through it:

```powershell
curl.exe -s --resolve dl.YOURDOMAIN.com:443:<cloudflare-ip> -o NUL -w "%{http_code}`n" https://dl.YOURDOMAIN.com/...
```

**Cost and scale.** Free-plan Workers cover 100k requests/day, and objects up to 512 MB are cacheable
(the maps are 128 MB). The plugin-side fix — maps in the config directory, effects out of the plugin
zip — is what keeps this in ordinary territory: steady-state is roughly `new installs × 267 MB`
instead of the ~12 TB that the pre-fix re-download loop was generating. Serving that much in binaries
through a free zone is what [Cloudflare ToS §2.8](https://www.cloudflare.com/terms/) discourages, so
do not treat the mirror as a licence to skip the plugin-side fix.

**If it outgrows this.** Point the origin at an R2 bucket (zero egress fees). No plugin change is
needed — the plugin only ever knows the `dl.*` URL.

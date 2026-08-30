/**
 * Markdown -> a complete, self-contained HTML document.
 *
 * Everything is inline: no external stylesheet, no web font, no CDN. This worker exists to cut the
 * number of requests Proteus makes people send, and a page that pulls a font from another host would
 * quietly hand some of that back — one extra request per page view, to a third party.
 */
import { marked } from 'marked';

/** ProteusStyle.Accent — Vector4(0.949, 0.467, 0.071). The plugin's identity colour. */
const ACCENT = '#F27712';

/**
 * The logo's sweep, sampled from Proteus/images/icon.png rather than invented: yellow through amber
 * to deep red. Echoes the header band the plugin draws across the top of its own window.
 */
const SWEEP = '#F0D830, #D89000, #D81800';

/**
 * The languages the README is translated into, in the order the switcher lists them.
 *
 * Same eight codes as LocSetup.Shipped — the plugin's UI and its README travel together, and a
 * language in one list but not the other is the kind of drift nobody notices until a reader lands on
 * a switcher entry that 404s. `name` is the language's name IN that language: someone looking for
 * their own language cannot read "Japanese".
 */
export const LANGS = [
  { code: 'en', name: 'English' },
  { code: 'ja', name: '日本語' },
  { code: 'de', name: 'Deutsch' },
  { code: 'fr', name: 'Français' },
  { code: 'zh', name: '简体中文' },
  { code: 'ko', name: '한국어' },
  { code: 'es', name: 'Español' },
  { code: 'ru', name: 'Русский' },
];

/** The repo path each translation lives at. English is the root README; the rest sit under docs/. */
export const docPathFor = (code) => (code === 'en' ? 'README.md' : `docs/README.${code}.md`);

/** The mirror path each translation is served from. `/en/README.md` is English PINNED — see below. */
export const mirrorPathFor = (code) => `/${code}/README.md`;

/**
 * Documents this mirror serves itself, as repo path -> mirror path. A link to one of these stays here
 * instead of bouncing to GitHub.
 *
 * README.md maps to `/en/README.md`, NOT to `/`. `/` and `/README.md` negotiate on Accept-Language,
 * so a link there would bounce a reader who deliberately clicked "English" straight back into their
 * browser's language. The per-language paths are the only ones that mean exactly what they say.
 */
const MIRRORED = new Map([
  ['README.md', '/en/README.md'],
  ['TROUBLESHOOTING.md', '/TROUBLESHOOTING.md'],
  ['For Creators.md', '/For Creators.md'],
  ['mirror.md', '/mirror.md'],
  ...LANGS.filter((l) => l.code !== 'en').map((l) => [docPathFor(l.code), mirrorPathFor(l.code)]),
]);

const BLOB = 'https://github.com/solona-m/proteus/blob/main/';

const escapeHtml = (s) =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

/**
 * The language switcher, delimited so it is one unmistakable thing to find and replace.
 *
 * On GitHub — where nothing can generate a switcher — the block is an ordinary line of links, and the
 * comments are invisible. Here it is cut out and replaced by the rendered switcher below, so the page
 * never shows the same row of languages twice.
 */
const NAV_RX = /<!--\s*i18n\s*-->[\s\S]*?<!--\s*\/i18n\s*-->\s*/;

/**
 * Sends relative links somewhere that exists.
 *
 * A relative link is written for a CHECKOUT, not for this host, so it has to be resolved against the
 * document it appears in before it means anything: `../README.md` in `docs/README.ja.md` is the root
 * readme, and the same text in the root readme is one directory above the repo. Resolving first and
 * looking up second is what lets the translations use ordinary relative links that also work on
 * GitHub and in a clone.
 *
 * Anything we do NOT mirror has to go to GitHub, otherwise a document added upstream later turns into
 * a 404 here with nothing to explain why.
 */
export function resolveHref(href, docPath = 'README.md') {
  if (/^(?:[a-z][a-z0-9+.-]*:|\/\/|#)/i.test(href)) return href;   // absolute, protocol-relative, anchor

  // A leading slash in these documents means "repo root", not "server root" — there is nothing above
  // the repo to address — so it is dropped and the rest resolved from the root.
  const rooted = href.startsWith('/');
  const base = new URL('https://r/' + encodeURI(rooted ? '' : docPath));

  let target;
  try { target = new URL(href.replace(/^\//, ''), base); } catch { return href; }

  let repoPath = target.pathname.replace(/^\//, '');
  try { repoPath = decodeURIComponent(repoPath); } catch { /* leave as-is */ }

  // Everything after the path comes along. Reading only `pathname` would silently drop a query —
  // `?plain=1` on a GitHub link, say — and turn a working link into a subtly different one, which is
  // the exact failure this function exists to prevent.
  const suffix = target.search + target.hash;

  const mirror = MIRRORED.get(repoPath);
  if (mirror) return encodeURI(mirror) + suffix;

  return BLOB + encodeURI(repoPath) + suffix;
}

const CSS = `
:root{color-scheme:light dark;
  --accent:${ACCENT};
  --bg:#fbfaf8;--fg:#23201d;--muted:#6b635b;--rule:#e3ded7;--card:#fff;--code:#f2efea}
@media (prefers-color-scheme:dark){:root{
  --bg:#171513;--fg:#e8e3dc;--muted:#a09488;--rule:#332e29;--card:#1e1b18;--code:#221f1c}}
*{box-sizing:border-box}
body{margin:0;padding:2.5rem 1.25rem 5rem;background:var(--bg);color:var(--fg);
  font:16px/1.65 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;
  -webkit-text-size-adjust:100%}
main{max-width:46rem;margin:0 auto}
h1,h2,h3,h4{line-height:1.25;font-weight:650;margin:2.2em 0 .6em}
h1{margin-top:0;font-size:2.1rem;letter-spacing:-.02em}
/* The logo's sweep, as a rule under the title. */
h1::after{content:"";display:block;height:4px;width:100%;margin-top:.5rem;border-radius:2px;
  background:linear-gradient(90deg,${SWEEP})}
h2{font-size:1.5rem;padding-bottom:.3em;border-bottom:1px solid var(--rule)}
h3{font-size:1.2rem}
h4{font-size:1rem;color:var(--muted);text-transform:uppercase;letter-spacing:.06em}
p,ul,ol{margin:0 0 1.1em}
li{margin:.3em 0}
a{color:var(--accent);text-decoration:none}
a:hover{text-decoration:underline}
strong{font-weight:650}
hr{border:0;border-top:1px solid var(--rule);margin:2.5em 0}
blockquote{margin:1.4em 0;padding:.85em 1.1em;background:var(--card);
  border-left:3px solid var(--accent);border-radius:0 6px 6px 0;color:var(--muted)}
blockquote p:last-child{margin-bottom:0}
code{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:.88em;
  background:var(--code);padding:.15em .4em;border-radius:4px}
pre{background:var(--code);padding:1em;border-radius:8px;overflow-x:auto}
pre code{background:none;padding:0;font-size:.85em}
/* Tables are most of this README, so they get the most attention. */
.tw{overflow-x:auto;margin:0 0 1.4em}
table{border-collapse:collapse;width:100%;font-size:.94em}
th,td{text-align:left;padding:.55em .8em;border-bottom:1px solid var(--rule);vertical-align:top}
th{font-weight:650;background:var(--card);white-space:nowrap}
tbody tr:last-child td{border-bottom:0}
img{max-width:100%}
/* Language switcher. Wraps rather than scrolls: eight short words fit two lines on a phone, and a
   reader hunting for their own language should see all of them at once. */
.langs{display:flex;flex-wrap:wrap;gap:.35rem .5rem;margin:0 0 2em;font-size:.9rem}
.langs a,.langs span{padding:.15em .55em;border-radius:999px;border:1px solid var(--rule)}
.langs a{color:var(--muted)}
.langs a:hover{color:var(--accent);border-color:var(--accent);text-decoration:none}
.langs span[aria-current]{color:var(--bg);background:var(--accent);border-color:var(--accent);
  font-weight:650}
`;

/** Pulls the document title out of its first heading, so the tab is named rather than generic. */
function firstHeading(md) {
  const m = /^#{1,2}\s+(.+?)\s*$/m.exec(md);
  return m ? m[1].replace(/[`*_]/g, '').trim() : null;
}

/**
 * The rendered switcher. Every entry points at a PINNED per-language path, including the one for the
 * language being read: whatever a reader clicks, they get what it says, and a reload keeps it.
 */
function switcherHtml(current) {
  const items = LANGS.map(({ code, name }) => code === current
    ? `<span aria-current="page" lang="${code}">${escapeHtml(name)}</span>`
    : `<a href="${mirrorPathFor(code)}" hreflang="${code}" lang="${code}">${escapeHtml(name)}</a>`);
  return `<nav class="langs" aria-label="Language">${items.join('')}</nav>`;
}

/**
 * Renders one document.
 *
 * `docPath` is the document's path IN THE REPO, and it matters: relative links are resolved against
 * it (see resolveHref), so rendering `docs/README.ja.md` while claiming to be the root readme sends
 * every one of its links one directory too high.
 *
 * `lang` marks the page for screen readers and for the browser's own line-breaking and font
 * selection, and picks out the current entry in the switcher. Passing it is also what turns the
 * switcher on; documents with no translations render without one.
 *
 * NOTE: marked does not sanitise, and passes raw HTML in the source straight through. Every document
 * rendered here comes from solona-m/proteus, whose only raw HTML is the i18n nav markers stripped
 * below, and which nobody can edit without already being able to ship plugin code — so this is a
 * trust argument, not a safety one. Do not point this at markdown from anywhere else without adding
 * a sanitiser first.
 */
export function renderMarkdown(md, fallbackTitle = 'Proteus', opts = {}) {
  const { lang = null, docPath = 'README.md' } = opts;

  const renderer = new marked.Renderer();
  const baseLink = renderer.link.bind(renderer);
  renderer.link = (token) => baseLink({ ...token, href: resolveHref(token.href ?? '', docPath) });

  // Wrapped so a wide table scrolls inside its own box instead of forcing the whole page sideways
  // on a phone.
  const baseTable = renderer.table.bind(renderer);
  renderer.table = (token) => `<div class="tw">${baseTable(token)}</div>`;

  const title = firstHeading(md) ?? fallbackTitle;
  const source = md.replace(NAV_RX, '');
  const body = marked.parse(source, { gfm: true, breaks: false, renderer });

  // Placed where the markdown nav was — just under the title — rather than above it, so the page
  // still opens with its own name.
  //
  // A FUNCTION replacer, not a string: String.replace interprets `$&`, `$'` and `$1` in a string
  // replacement, so a language name or path containing a dollar sign would splice a copy of the
  // page's own HTML into the nav. "No language name has a `$` in it" is not a guard worth relying on.
  const withNav = lang === null
    ? body
    : (body.includes('</h1>')
      ? body.replace('</h1>', () => '</h1>\n' + switcherHtml(lang))
      : switcherHtml(lang) + body);

  return `<!doctype html>
<html lang="${lang ?? 'en'}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>${escapeHtml(title)}</title>
<link rel="icon" href="/icon.png">
<style>${CSS}</style>
</head>
<body><main>${withNav}</main></body>
</html>
`;
}

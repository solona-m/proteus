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

/** Documents this mirror serves itself. A link to one of these stays here instead of bouncing to GitHub. */
const MIRRORED = new Set(['README.md', 'TROUBLESHOOTING.md', 'For Creators.md', 'mirror.md']);

const BLOB = 'https://github.com/solona-m/proteus/blob/main/';

const escapeHtml = (s) =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

/**
 * Sends relative links somewhere that exists.
 *
 * A relative link is written for a checkout, not for this host — `TROUBLESHOOTING.md` resolves
 * against the mirror root and happens to work only because we mirror that file. Anything we do NOT
 * mirror has to go to GitHub, otherwise a document added upstream later turns into a 404 here with
 * nothing to explain why.
 */
export function resolveHref(href) {
  if (/^(?:[a-z][a-z0-9+.-]*:|\/\/|#)/i.test(href)) return href;   // absolute, protocol-relative, anchor

  const clean = href.replace(/^\.\//, '').replace(/^\//, '');
  let decoded = clean;
  try { decoded = decodeURIComponent(clean); } catch { /* leave as-is */ }

  const [pathPart, hash] = decoded.split('#');
  if (MIRRORED.has(pathPart)) return '/' + encodeURI(pathPart) + (hash ? '#' + hash : '');

  return BLOB + encodeURI(decoded);
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
`;

/** Pulls the document title out of its first heading, so the tab is named rather than generic. */
function firstHeading(md) {
  const m = /^#{1,2}\s+(.+?)\s*$/m.exec(md);
  return m ? m[1].replace(/[`*_]/g, '').trim() : null;
}

/**
 * Renders one document.
 *
 * NOTE: marked does not sanitise, and passes raw HTML in the source straight through. Every document
 * rendered here comes from solona-m/proteus, which contains no raw HTML and which nobody can edit
 * without already being able to ship plugin code — so this is a trust argument, not a safety one.
 * Do not point this at markdown from anywhere else without adding a sanitiser first.
 */
export function renderMarkdown(md, fallbackTitle = 'Proteus') {
  const renderer = new marked.Renderer();
  const baseLink = renderer.link.bind(renderer);
  renderer.link = (token) => baseLink({ ...token, href: resolveHref(token.href ?? '') });

  // Wrapped so a wide table scrolls inside its own box instead of forcing the whole page sideways
  // on a phone.
  const baseTable = renderer.table.bind(renderer);
  renderer.table = (token) => `<div class="tw">${baseTable(token)}</div>`;

  const body = marked.parse(md, { gfm: true, breaks: false, renderer });
  const title = firstHeading(md) ?? fallbackTitle;

  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>${escapeHtml(title)}</title>
<link rel="icon" href="/icon.png">
<style>${CSS}</style>
</head>
<body><main>${body}</main></body>
</html>
`;
}

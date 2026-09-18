/**
 * Proteus usage statistics — the receiving end of the opt-in feature counts.
 *
 * A separate worker from the asset mirror on purpose: the mirror holds nothing and should stay that way,
 * and this one's whole job is holding something. Keeping them apart also keeps what PRIVACY.md describes
 * in one small file.
 *
 * What it stores is exactly what a report carries and nothing about the request. It never reads
 * CF-Connecting-IP, User-Agent or any other header, and wrangler.toml switches Workers Logs off, so the
 * IP address that necessarily reaches Cloudflare in transit is not written anywhere by this code.
 *
 * Routes:
 *   POST   /v1/report          one day's counts from one install -> 204
 *   DELETE /v1/install/<uuid>  erase everything that install ever sent -> 204
 * Anything else is a 404.
 */

import { FEATURES } from './features.js';

const FEATURE_SET = new Set(FEATURES);

/** A report is a few hundred bytes; anything much bigger is not one. */
export const MAX_BODY = 4096;

/** Per-feature, per-day ceiling. A real day never comes close; this only stops a garbage row. */
export const MAX_COUNT = 100000;

/** How old a report's day may be. The client drops pending counts older than a week. */
const MAX_AGE_DAYS = 8;

/** Rows older than this are deleted by the daily cron. Stated in PRIVACY.md — change both together. */
export const RETENTION = '-13 months';

// Lower-case canonical form only: the client always sends Guid.ToString("D"), and accepting other
// spellings would let one install appear as several.
const UUID_RX = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const DAY_RX = /^\d{4}-\d{2}-\d{2}$/;
const VERSION_RX = /^[0-9][0-9.]{0,30}$/;
const LANG_RX = /^[a-z]{2,3}$/;

const noContent = () => new Response(null, { status: 204 });
const bad = (why) => new Response(why, { status: 400, headers: { 'content-type': 'text/plain' } });
const notFound = () => new Response('Not found', { status: 404 });

/**
 * Checks a parsed report and returns either `{ row }` ready to insert or `{ error }`.
 * Exported for the tests, which exercise the rules without a database.
 */
export function validateReport(body, now = new Date()) {
  if (body === null || typeof body !== 'object' || Array.isArray(body)) return { error: 'not an object' };

  // Exactly these fields. An unexpected one is refused rather than ignored, so a client can never start
  // sending something PRIVACY.md does not list without it failing loudly here first.
  const allowed = ['v', 'id', 'day', 'version', 'build', 'lang', 'counts'];
  for (const k of Object.keys(body)) if (!allowed.includes(k)) return { error: `unexpected field ${k}` };

  const { v, id, day, version, build, lang, counts } = body;
  if (v !== 1) return { error: 'unsupported version' };
  if (typeof id !== 'string' || !UUID_RX.test(id)) return { error: 'bad id' };

  if (typeof day !== 'string' || !DAY_RX.test(day)) return { error: 'bad day' };
  const dayMs = Date.parse(`${day}T00:00:00Z`);
  if (!Number.isFinite(dayMs)) return { error: 'bad day' };
  const ageDays = (now.getTime() - dayMs) / 86400000;
  // -1: a client a few hours ahead of UTC is already on tomorrow's date.
  if (ageDays < -1 || ageDays > MAX_AGE_DAYS) return { error: 'day out of range' };

  if (typeof version !== 'string' || !VERSION_RX.test(version)) return { error: 'bad version' };
  if (!Number.isInteger(build) || build < 0 || build > 1000000) return { error: 'bad build' };
  if (typeof lang !== 'string' || !LANG_RX.test(lang)) return { error: 'bad lang' };

  if (counts === null || typeof counts !== 'object' || Array.isArray(counts)) return { error: 'bad counts' };
  const clean = {};
  for (const [k, n] of Object.entries(counts)) {
    if (!FEATURE_SET.has(k)) return { error: `unknown feature ${k}` };
    if (!Number.isInteger(n) || n < 1) return { error: `bad count for ${k}` };
    clean[k] = Math.min(n, MAX_COUNT);
  }

  return { row: { id, day, version, build, lang, counts: JSON.stringify(clean) } };
}

async function handleReport(request, env) {
  // Content-Length first, so an oversized body is refused before it is read; then the real length,
  // because the header is optional and can lie.
  const declared = Number(request.headers.get('content-length') ?? '0');
  if (declared > MAX_BODY) return new Response('Too large', { status: 413 });
  const text = await request.text();
  if (text.length > MAX_BODY) return new Response('Too large', { status: 413 });

  let body;
  try { body = JSON.parse(text); } catch { return bad('not json'); }

  const { row, error } = validateReport(body);
  if (error) return bad(error);

  await env.DB.prepare(
    'INSERT INTO reports (install_id, day, version, build, lang, counts, received) ' +
    "VALUES (?1, ?2, ?3, ?4, ?5, ?6, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))")
    .bind(row.id, row.day, row.version, row.build, row.lang, row.counts)
    .run();

  return noContent();
}

async function handleDelete(id, env) {
  if (!UUID_RX.test(id)) return bad('bad id');
  // 204 whether or not anything matched: the answer to "is my data gone?" is yes either way, and a
  // different status would let anyone probe which IDs exist.
  await env.DB.prepare('DELETE FROM reports WHERE install_id = ?1').bind(id).run();
  return noContent();
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname === '/v1/report') {
      if (request.method !== 'POST')
        return new Response('Method not allowed', { status: 405, headers: { Allow: 'POST' } });
      return handleReport(request, env);
    }

    const del = /^\/v1\/install\/([^/]+)$/.exec(url.pathname);
    if (del) {
      if (request.method !== 'DELETE')
        return new Response('Method not allowed', { status: 405, headers: { Allow: 'DELETE' } });
      return handleDelete(del[1], env);
    }

    return notFound();
  },

  /** Daily retention sweep. */
  async scheduled(_event, env) {
    // `received`, not `day`: it is the server's own clock, so a client cannot keep a row alive longer
    // by claiming a later date.
    await env.DB.prepare(
      "DELETE FROM reports WHERE received < strftime('%Y-%m-%dT%H:%M:%SZ', 'now', ?1)")
      .bind(RETENTION)
      .run();
  },
};

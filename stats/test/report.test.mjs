/**
 * Tests for the stats worker, against a fake D1 that records every statement.
 *
 * The important half is the REFUSED list: the allow-list is what keeps this endpoint from storing
 * anything PRIVACY.md does not describe, and a rule that is looser than it looks fails silently —
 * every legitimate report keeps working exactly as before.
 *
 * Run with `npm test` from stats/. No network, no wrangler, no account needed.
 */
import worker, { validateReport, MAX_BODY, MAX_COUNT, RETENTION } from '../src/index.js';

let statements = [];
const env = {
  DB: {
    prepare(sql) {
      const st = { sql, args: [] };
      return {
        bind(...args) { st.args = args; return this; },
        async run() { statements.push(st); return { success: true }; },
      };
    },
  },
};

let pass = 0, fail = 0;
function ok(name, cond, detail = '') {
  if (cond) pass++;
  else { fail++; console.log(`FAIL ${name}${detail ? ` — ${detail}` : ''}`); }
}

const today = new Date().toISOString().slice(0, 10);
const ID = '0f8fad5b-d9cb-469f-a165-70867728950e';
const good = () => ({
  v: 1, id: ID, day: today, version: '2609.1.0.0', build: 923, lang: 'en',
  counts: { composite: 12, studio_open: 1 },
});

async function post(body, headers = {}) {
  statements = [];
  const text = typeof body === 'string' ? body : JSON.stringify(body);
  return worker.fetch(new Request('https://stats.example.com/v1/report',
    { method: 'POST', body: text, headers: { 'content-type': 'application/json', ...headers } }), env);
}

// ── accepted ──────────────────────────────────────────────────────────────────────────────────────
{
  const res = await post(good());
  ok('valid report -> 204', res.status === 204, `got ${res.status}`);
  ok('valid report -> one insert', statements.length === 1 && statements[0].sql.startsWith('INSERT'));
  const args = statements[0]?.args ?? [];
  ok('insert binds only report fields', args.length === 6 && args[0] === ID && args[5] === '{"composite":12,"studio_open":1}',
    JSON.stringify(args));
}
{
  const r = good(); r.counts = {};
  const res = await post(r);
  ok('empty counts (active day, nothing used) -> 204', res.status === 204);
}
{
  const r = validateReport({ ...good(), counts: { composite: MAX_COUNT * 5 } });
  ok('count clamped', r.row && JSON.parse(r.row.counts).composite === MAX_COUNT);
}

// ── refused ───────────────────────────────────────────────────────────────────────────────────────
const refused = [
  ['unknown feature', { ...good(), counts: { keylogger: 1 } }],
  ['extra field', { ...good(), character: 'Some Name' }],
  ['uppercase id', { ...good(), id: ID.toUpperCase() }],
  ['non-uuid id', { ...good(), id: 'player-1234' }],
  ['wrong schema version', { ...good(), v: 2 }],
  ['zero count', { ...good(), counts: { composite: 0 } }],
  ['fractional count', { ...good(), counts: { composite: 1.5 } }],
  ['string count', { ...good(), counts: { composite: '3' } }],
  ['counts is array', { ...good(), counts: ['composite'] }],
  ['day far in past', { ...good(), day: '2020-01-01' }],
  ['day in future', { ...good(), day: '2999-01-01' }],
  ['malformed day', { ...good(), day: 'yesterday' }],
  ['free-text version', { ...good(), version: 'hello world' }],
  ['free-text lang', { ...good(), lang: 'English (my name is x)' }],
  ['negative build', { ...good(), build: -1 }],
  ['array body', [good()]],
];
for (const [name, body] of refused) {
  const res = await post(body);
  ok(`refused: ${name}`, res.status === 400, `got ${res.status}`);
  ok(`refused: ${name} writes nothing`, statements.length === 0);
}
{
  const res = await post('{not json');
  ok('refused: not json', res.status === 400);
}
{
  const r = good(); r.version = '1' + '.1'.repeat(MAX_BODY);
  const res = await post(r);
  ok('refused: oversized body -> 413', res.status === 413, `got ${res.status}`);
  ok('refused: oversized body writes nothing', statements.length === 0);
}

// ── delete ────────────────────────────────────────────────────────────────────────────────────────
{
  statements = [];
  const res = await worker.fetch(new Request(`https://stats.example.com/v1/install/${ID}`, { method: 'DELETE' }), env);
  ok('delete -> 204', res.status === 204);
  ok('delete binds the id', statements.length === 1 && statements[0].sql.startsWith('DELETE') && statements[0].args[0] === ID);
}
{
  statements = [];
  const res = await worker.fetch(new Request('https://stats.example.com/v1/install/not-an-id', { method: 'DELETE' }), env);
  ok('delete bad id -> 400', res.status === 400);
  ok('delete bad id writes nothing', statements.length === 0);
}

// ── everything else ───────────────────────────────────────────────────────────────────────────────
for (const [method, path, status] of [
  ['GET', '/v1/report', 405],
  ['GET', `/v1/install/${ID}`, 405],
  ['GET', '/', 404],
  ['POST', '/v1/reports', 404],
]) {
  const res = await worker.fetch(new Request('https://stats.example.com' + path, { method }), env);
  ok(`${method} ${path} -> ${status}`, res.status === status, `got ${res.status}`);
}

// ── retention ─────────────────────────────────────────────────────────────────────────────────────
{
  statements = [];
  await worker.scheduled({}, env);
  ok('cron deletes by received age', statements.length === 1
    && statements[0].sql.includes('DELETE FROM reports WHERE received <')
    && statements[0].args[0] === RETENTION);
}

console.log(`${pass} passed, ${fail} failed`);
if (fail > 0) process.exit(1);

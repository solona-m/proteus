/**
 * scripts/deploy.mjs: the repo.json it ships, the refusals that keep the version tag honest, and the cleanup that
 * keeps a stale copy from ever reaching a later raw `wrangler deploy`. No network, no wrangler, no account.
 */
import { mkdtemp, writeFile, readFile, access, rm, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import {
  validateRepoJson, prepare, deploy, dev, deployArgs, wranglerCommand, parseArgs, CONTENTS_URL,
} from '../scripts/deploy.mjs';

let pass = 0, fail = 0;
const t = (name, ok, detail = '') => {
  if (ok) { pass++; console.log(`  ok   ${name}`); }
  else { fail++; console.log(`  FAIL ${name}${detail ? ': ' + detail : ''}`); }
};
const exists = (p) => access(p).then(() => true, () => false);
const rejects = (p) => p.then(() => false, () => true);

const SHA = '0123456789abcdef0123456789abcdef01234567';
const FIXTURE = Buffer.from('[{"InternalName":"Proteus","Name":"Proteus — ünïcødé"}]\n', 'utf8');

async function scratch() {
  const dir = await mkdtemp(join(tmpdir(), 'proteus-deploy-'));
  const staticDir = join(dir, 'static');
  await mkdir(staticDir);
  await writeFile(join(staticDir, '_headers'), '/repo.json\n');
  const repoJson = join(dir, 'repo.json');
  await writeFile(repoJson, FIXTURE);
  return { dir, staticDir, repoJson, target: join(staticDir, 'repo.json') };
}

// A git stub: answers by the first argument; records every call.
const stubGit = ({ porcelain = '', sha = SHA, remotes = '  origin/test\n' } = {}) => {
  const calls = [];
  const git = (args) => {
    calls.push(args.join(' '));
    if (args[0] === 'status') return porcelain;
    if (args[0] === 'rev-parse') return `${sha}\n`;
    if (args[0] === 'branch') return remotes;
    throw new Error(`unexpected git ${args.join(' ')}`);
  };
  return { git, calls };
};

console.log('validateRepoJson:');
// [AC-static-repo-json-5] [EDGE-static-repo-json-3]
for (const [label, text] of [['empty', ''], ['not JSON', '{oops'], ['an object', '{"a":1}'], ['an empty array', '[]'],
  ['an array holding null', '[null]'], ['an array of numbers', '[1]'], ['an array of arrays', '[[]]']]) {
  let threw = false;
  try { validateRepoJson(text); } catch { threw = true; }
  t(`rejects ${label}`, threw);
}
t('accepts a repo.json-shaped array', validateRepoJson(FIXTURE.toString('utf8')).length === 1);

console.log('prepare:');
{
  // [EDGE-static-repo-json-2] [EDGE-static-repo-json-3] A stale copy is removed before anything else, so a failed
  // fetch or an invalid file leaves nothing behind to ship.
  for (const [label, opts] of [
    ['failing fetch', { fetchImpl: async () => new Response('no', { status: 502 }) }],
    ['invalid content', { fetchImpl: async () => new Response('[]', { status: 200 }) }],
    ['invalid file', { repoJsonPathContent: '{"not":"an array"}' }],
  ]) {
    const s = await scratch();
    await writeFile(s.target, '[{"InternalName":"STALE"}]');
    if (opts.repoJsonPathContent) await writeFile(s.repoJson, opts.repoJsonPathContent);
    const threw = await rejects(prepare({
      staticDir: s.staticDir, repoJsonPath: opts.repoJsonPathContent ? s.repoJson : undefined, fetchImpl: opts.fetchImpl,
    }));
    t(`${label}: throws and leaves no repo.json`, threw && !(await exists(s.target)));
    await rm(s.dir, { recursive: true, force: true });
  }

  // [AC-static-repo-json-6] [EDGE-static-repo-json-18] With no --repo-json the owner's deploy reads plugins main
  // through the contents API, never raw.githubusercontent.com (whose CDN lags a fresh commit).
  {
    const s = await scratch();
    const calls = [];
    await prepare({
      staticDir: s.staticDir,
      fetchImpl: async (url, init) => {
        calls.push({ url, accept: init?.headers?.accept, timed: init?.signal instanceof AbortSignal });
        return new Response(FIXTURE);
      },
    });
    t('the fetch carries a timeout signal', calls[0]?.timed === true);
    t('fetches once from the contents API with the raw media type',
      calls.length === 1 && calls[0].url === CONTENTS_URL
        && calls[0].url.startsWith('https://api.github.com/repos/solona-m/plugins/contents/repo.json')
        && calls[0].accept === 'application/vnd.github.raw' && !calls[0].url.includes('raw.githubusercontent'),
      JSON.stringify(calls));
    t('writes what it fetched', (await readFile(s.target)).equals(FIXTURE));
    await rm(s.dir, { recursive: true, force: true });
  }

  // [AC-static-repo-json-4] CI passes its checkout of plugins main; the asset is that file, byte for byte.
  {
    const s = await scratch();
    const bytes = await prepare({ staticDir: s.staticDir, repoJsonPath: s.repoJson, fetchImpl: () => { throw new Error('no fetch'); } });
    t('--repo-json copies exact bytes (non-ASCII, trailing newline)',
      (await readFile(s.target)).equals(FIXTURE) && bytes.equals(FIXTURE));
    await rm(s.dir, { recursive: true, force: true });
  }
}

console.log('deploy:');
{
  // [AC-static-repo-json-6] [EDGE-static-repo-json-19] A dirty worker/ would make the tag lie about what is live.
  {
    const s = await scratch();
    const { git } = stubGit({ porcelain: ' M src/index.js\n' });
    let spawned = false;
    const threw = await rejects(deploy({ workerDir: s.dir, staticDir: s.staticDir, repoJsonPath: s.repoJson, git,
      spawn: async () => { spawned = true; return 0; } }));
    t('refuses a dirty tree without spawning wrangler', threw && !spawned);
    await rm(s.dir, { recursive: true, force: true });
  }

  // [AC-static-repo-json-6] [EDGE-static-repo-json-17] An unpushed commit could never be checked out by CI.
  {
    const s = await scratch();
    const { git } = stubGit({ remotes: '' });
    let spawned = false;
    const threw = await rejects(deploy({ workerDir: s.dir, staticDir: s.staticDir, repoJsonPath: s.repoJson, git,
      spawn: async () => { spawned = true; return 0; } }));
    t('refuses a HEAD no remote branch contains', threw && !spawned);
    await rm(s.dir, { recursive: true, force: true });
  }

  // [AC-static-repo-json-4] [AC-static-repo-json-6] The version is tagged with the commit, through node + wrangler.js.
  {
    const s = await scratch();
    const { git, calls } = stubGit();
    let seen;
    const code = await deploy({ workerDir: s.dir, staticDir: s.staticDir, repoJsonPath: s.repoJson, git,
      spawn: async (cmd, args, opts) => { seen = { cmd, args, opts, present: await exists(s.target) }; return 7; } });
    const digest = createHash('sha256').update(FIXTURE).digest('hex').slice(0, 12);
    t('spawns node with wrangler.js, deploy --tag <sha> --message', seen
      && seen.cmd === process.execPath
      && seen.args[0].replaceAll('\\', '/').endsWith('node_modules/wrangler/bin/wrangler.js')
      && JSON.stringify(seen.args.slice(1)) === JSON.stringify(
        ['deploy', '--tag', SHA, '--message', `proteus ${SHA.slice(0, 7)} repo.json sha256:${digest}`])
      && seen.opts.cwd === s.dir, JSON.stringify(seen));
    t('repo.json is in place while wrangler runs', seen?.present === true);
    // The refusals above pass because the stub answers by verb; this pins the exact questions asked of git, so a
    // check that stopped asking the right one (e.g. `branch -r` without `--contains HEAD`) fails here.
    t('asks git exactly: worker/ status, HEAD, remote branches containing HEAD', JSON.stringify(calls)
      === JSON.stringify(['status --porcelain -- .', 'rev-parse HEAD', 'branch -r --contains HEAD']), JSON.stringify(calls));
    t("wrangler's exit code passes through", code === 7);
    await rm(s.dir, { recursive: true, force: true });
  }

  // [AC-static-repo-json-6] [EDGE-static-repo-json-2] [EDGE-static-repo-json-25] Nothing is left behind, whatever
  // wrangler did: a later raw `npx wrangler deploy` then ships no repo.json, and the Worker fallback answers.
  for (const [label, spawn] of [
    ['success', async () => 0],
    ['wrangler failure', async () => 1],
    ['spawn throws', async () => { throw new Error('ENOENT'); }],
  ]) {
    const s = await scratch();
    const { git } = stubGit();
    await deploy({ workerDir: s.dir, staticDir: s.staticDir, repoJsonPath: s.repoJson, git, spawn }).catch(() => {});
    t(`deploy cleans up after ${label}`, !(await exists(s.target)));
    await rm(s.dir, { recursive: true, force: true });
  }
  {
    const s = await scratch();
    let args;
    await dev({ workerDir: s.dir, staticDir: s.staticDir, repoJsonPath: s.repoJson, extraArgs: ['--port', '8799'],
      spawn: async (_cmd, a) => { args = a; return 0; } });
    t('dev runs wrangler dev and cleans up when it exits',
      args?.[1] === 'dev' && args.includes('8799') && !(await exists(s.target)));
    await rm(s.dir, { recursive: true, force: true });
  }
  // [EDGE-static-repo-json-2] [EDGE-static-repo-json-25] dev cleans up whatever wrangler did, like deploy.
  for (const [label, spawn] of [['wrangler failure', async () => 1], ['spawn throws', async () => { throw new Error('ENOENT'); }]]) {
    const s = await scratch();
    await dev({ workerDir: s.dir, staticDir: s.staticDir, repoJsonPath: s.repoJson, spawn }).catch(() => {});
    t(`dev cleans up after ${label}`, !(await exists(s.target)));
    await rm(s.dir, { recursive: true, force: true });
  }

  // [AC-static-repo-json-6] Ctrl+C before wrangler starts reaches no child: the deploy must not ship anyway.
  for (const [label, run] of [
    ['deploy', (s, spawn) => deploy({ workerDir: s.dir, staticDir: s.staticDir, repoJsonPath: s.repoJson,
      git: stubGit().git, spawn, cancelled: () => true })],
    ['dev', (s, spawn) => dev({ workerDir: s.dir, staticDir: s.staticDir, repoJsonPath: s.repoJson, spawn,
      cancelled: () => true })],
  ]) {
    const s = await scratch();
    let spawned = false;
    const threw = await rejects(run(s, async () => { spawned = true; return 0; }));
    t(`${label} cancelled before wrangler starts: no spawn, no repo.json left`, threw && !spawned && !(await exists(s.target)));
    await rm(s.dir, { recursive: true, force: true });
  }

  // [AC-static-repo-json-6] A commit id git didn't give as 40 lowercase hex would make a tag CI refuses.
  {
    const s = await scratch();
    let spawned = false;
    const threw = await rejects(deploy({ workerDir: s.dir, staticDir: s.staticDir, repoJsonPath: s.repoJson,
      git: stubGit({ sha: 'fatal: not a git repository' }).git, spawn: async () => { spawned = true; return 0; } }));
    t('refuses a commit id that is not 40-hex', threw && !spawned);
    await rm(s.dir, { recursive: true, force: true });
  }
}

console.log('helpers:');
t('deployArgs is deterministic', JSON.stringify(deployArgs({ sha: SHA, repoJsonBytes: FIXTURE }))
  === JSON.stringify(deployArgs({ sha: SHA, repoJsonBytes: FIXTURE })));
t('wranglerCommand runs through node', wranglerCommand('/w')[0] === process.execPath);
t('parseArgs reads --repo-json', parseArgs(['deploy', '--repo-json', 'x.json']).repoJsonPath === 'x.json');
t('parseArgs reads --repo-json=<path> (not passed on to wrangler)',
  parseArgs(['dev', '--repo-json=x.json']).repoJsonPath === 'x.json' && parseArgs(['dev', '--repo-json=x.json']).extraArgs.length === 0);
for (const [label, argv] of [
  ['unknown deploy arguments', ['deploy', '--dry-run']],
  ['--repo-json without a path', ['deploy', '--repo-json']],
  ['--repo-json followed by a flag', ['dev', '--repo-json', '--port']],
  ['an empty --repo-json=', ['deploy', '--repo-json=']],
  ['an unknown command', ['publish']],
  ['no command', []],
]) {
  let threw = false;
  try { parseArgs(argv); } catch { threw = true; }
  t(`parseArgs refuses ${label}`, threw);
}
t('parseArgs passes dev arguments through to wrangler',
  JSON.stringify(parseArgs(['dev', '--port', '1']).extraArgs) === JSON.stringify(['--port', '1']));

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);

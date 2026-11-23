/**
 * The one way to deploy proteus-assets (and to run it locally), for the owner and for solona-m/plugins' CI.
 *
 *   node scripts/deploy.mjs prepare [--repo-json <path>]   write a validated static/repo.json
 *   node scripts/deploy.mjs deploy  [--repo-json <path>]   prepare, then `wrangler deploy` tagged with this commit
 *   node scripts/deploy.mjs dev     [--repo-json <path>] [wrangler dev args...]
 *
 * /repo.json is a Workers static asset (static/, see wrangler.toml), so it is served without running the Worker and
 * costs no Worker request. A static asset only changes on a deploy, which is why every deploy takes a fresh copy:
 * from --repo-json (CI passes its checkout of plugins main), or else from the GitHub contents API, which unlike
 * raw.githubusercontent.com does not lag a fresh commit by minutes.
 *
 * The copy lives only for the length of the deploy or dev session and is deleted afterwards, whatever happened. A
 * leftover copy is what a raw `npx wrangler deploy` would ship; with none, the Worker's own /repo.json proxy answers.
 *
 * The version is tagged with the Proteus commit (`--tag`, stored as the `workers/tag` annotation). The plugins repo's
 * workflow reads that tag to redeploy EXACTLY the code that is live with a new repo.json, so the tag must name a
 * commit that is on GitHub and describes the deployed tree: hence the clean-tree and pushed-commit refusals.
 */
import { spawn as nodeSpawn, execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFile, rm, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const WORKER_DIR = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const STATIC_DIR = join(WORKER_DIR, 'static');
export const CONTENTS_URL = 'https://api.github.com/repos/solona-m/plugins/contents/repo.json?ref=main';

/** Throws unless the text is a JSON array with at least one entry: what Dalamud expects a plugin repo to be. */
export function validateRepoJson(text) {
  let parsed;
  try { parsed = JSON.parse(text); } catch (e) { throw new Error(`repo.json is not valid JSON: ${e.message}`); }
  if (!Array.isArray(parsed) || parsed.length === 0) throw new Error('repo.json is not a non-empty JSON array');
  return parsed;
}

/**
 * Deletes static/repo.json, then writes a fresh, validated copy, byte for byte. On any failure it throws and leaves
 * no file behind, so nothing stale can ship.
 */
export async function prepare({ staticDir = STATIC_DIR, repoJsonPath, fetchImpl = globalThis.fetch } = {}) {
  const target = join(staticDir, 'repo.json');
  await rm(target, { force: true });

  let bytes;
  if (repoJsonPath) {
    bytes = await readFile(repoJsonPath);
  } else {
    const res = await fetchImpl(CONTENTS_URL, {
      headers: { accept: 'application/vnd.github.raw', 'user-agent': 'proteus-assets-deploy' },
    });
    if (!res.ok) throw new Error(`fetching repo.json from the GitHub contents API failed: ${res.status}`);
    bytes = Buffer.from(await res.arrayBuffer());
  }

  validateRepoJson(bytes.toString('utf8'));
  await writeFile(target, bytes);
  return bytes;
}

/** wrangler run through node itself: spawning wrangler.cmd on Windows fails without a shell. */
export const wranglerCommand = (workerDir = WORKER_DIR) =>
  [process.execPath, join(workerDir, 'node_modules', 'wrangler', 'bin', 'wrangler.js')];

export function deployArgs({ sha, repoJsonBytes }) {
  const digest = createHash('sha256').update(repoJsonBytes).digest('hex').slice(0, 12);
  return ['deploy', '--tag', sha, '--message', `proteus ${sha.slice(0, 7)} repo.json sha256:${digest}`];
}

const defaultGit = (workerDir) => (args) =>
  execFileSync('git', args, { cwd: workerDir, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });

const defaultSpawn = (cmd, args, opts) => new Promise((ok, fail) => {
  const child = nodeSpawn(cmd, args, { stdio: 'inherit', ...opts });
  child.on('error', fail);
  child.on('exit', (code) => ok(code ?? 1));
});

/** Refuses an untaggable tree, then prepares, deploys and cleans up. Resolves to wrangler's exit code. */
export async function deploy({
  workerDir = WORKER_DIR, staticDir = join(workerDir, 'static'), repoJsonPath, fetchImpl,
  git = defaultGit(workerDir), spawn = defaultSpawn,
} = {}) {
  // Ignored files (static/repo.json, node_modules) never show in porcelain output.
  if (git(['status', '--porcelain', '--', '.']).trim() !== '') {
    throw new Error('worker/ has uncommitted changes: commit them first, so the version tag describes what is deployed');
  }
  const sha = git(['rev-parse', 'HEAD']).trim();
  if (!/^[0-9a-f]{40}$/.test(sha)) throw new Error(`unexpected commit id from git: ${sha}`);
  if (git(['branch', '-r', '--contains', 'HEAD']).trim() === '') {
    throw new Error(`HEAD ${sha.slice(0, 7)} is on no remote branch: push it first, or the plugins repo's ` +
      'redeploy cannot check out the live commit');
  }

  try {
    const repoJsonBytes = await prepare({ staticDir, repoJsonPath, fetchImpl });
    const [cmd, bin] = wranglerCommand(workerDir);
    return await spawn(cmd, [bin, ...deployArgs({ sha, repoJsonBytes })], { cwd: workerDir });
  } finally {
    await rm(join(staticDir, 'repo.json'), { force: true });
  }
}

/** `wrangler dev` with the asset in place, removed again when dev exits. */
export async function dev({
  workerDir = WORKER_DIR, staticDir = join(workerDir, 'static'), repoJsonPath, fetchImpl, extraArgs = [],
  spawn = defaultSpawn,
} = {}) {
  try {
    await prepare({ staticDir, repoJsonPath, fetchImpl });
    const [cmd, bin] = wranglerCommand(workerDir);
    return await spawn(cmd, [bin, 'dev', ...extraArgs], { cwd: workerDir });
  } finally {
    await rm(join(staticDir, 'repo.json'), { force: true });
  }
}

export function parseArgs(argv) {
  const [command, ...rest] = argv;
  let repoJsonPath;
  const extraArgs = [];
  for (let i = 0; i < rest.length; i++) {
    if (rest[i] === '--repo-json') {
      repoJsonPath = rest[++i];
      if (!repoJsonPath) throw new Error('--repo-json needs a path');
    } else extraArgs.push(rest[i]);
  }
  if (!['prepare', 'deploy', 'dev'].includes(command)) throw new Error('usage: deploy.mjs prepare|deploy|dev [--repo-json <path>]');
  if (command !== 'dev' && extraArgs.length) throw new Error(`unknown arguments: ${extraArgs.join(' ')}`);
  return { command, repoJsonPath, extraArgs };
}

async function main() {
  const { command, repoJsonPath, extraArgs } = parseArgs(process.argv.slice(2));
  if (command === 'prepare') {
    await prepare({ repoJsonPath });
    return 0;
  }
  // Ctrl+C, a closed console or a polite kill reach wrangler too; stay alive until it exits so the cleanup in
  // `finally` runs. A hard kill cannot be caught: the leftover copy is then deleted by the next prepare/deploy/dev.
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP']) process.on(signal, () => {});
  return command === 'deploy' ? deploy({ repoJsonPath }) : dev({ repoJsonPath, extraArgs });
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main().then((code) => process.exit(code), (e) => { console.error(`deploy.mjs: ${e.message}`); process.exit(1); });
}

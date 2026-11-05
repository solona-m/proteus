# Adversarial QA in game: the test harness

Proteus runs inside FFXIV, so QA reaches it through the **QA test harness**: a small
HTTP API inside the plugin, driven with `curl`. It exists only in local builds (CI
compiles it out; see `docs/adr/0002-qa-harness-compile-out-and-threat-model.md`),
listens on IPv4 loopback only, and needs a per-session token.

## Before you start

1. The user must have the game running, be logged in, and have the build
   dev-loaded from `E:\repos\Proteus\Proteus\bin\release\win-x64` (AGENTS.md Rule 5).
   You cannot launch the game or log in.
2. The build must contain the harness. The build prints
   `Proteus test harness: compiled IN`; the check command is
   `dotnet test Proteus.Tests/Proteus.Tests.csproj -c Release --filter "FullyQualifiedName~HarnessBuildFlagTests"`.
   `compiled OUT` locally means the shell has `CI=true` (or `GITHUB_ACTIONS=true`)
   set — a Blocker; report it.
3. **Ask the user to type `/proteus harness on`** in game chat. Only the user can
   turn it on: a `harness` line in the dev-command file is refused. It then stays
   on across plugin reloads until `/proteus harness off`.
4. **Not listening is a Blocker**, never a reason to work around it: no session
   file, a refused connection, or `/health` failing → stop and report.

## Finding the port and token

The session file is
`$APPDATA/XIVLauncher/pluginConfigs/Proteus/test-harness.json`
(`XIVLauncherCN` on the Chinese client):

```json
{"port":51234,"token":"…","pid":1234,"build":1411,"buildStamp":"11-01 14:59:12","session":"a1b2c3d4e5f6","startedUtc":"…"}
```

Every request: `http://127.0.0.1:<port>` (use `127.0.0.1`, not `localhost` — the
listener is IPv4 only) with `Authorization: Bearer <token>`. Never put the token in
a URL. After a plugin reload the port, token and session all change: **re-read the
file after a 401 or a refused connection.** A new `session` also means the log
cursor started again from 1.

```bash
F="$APPDATA/XIVLauncher/pluginConfigs/Proteus/test-harness.json"
PORT=$(python -c "import json;print(json.load(open(r'$F'))['port'])")
TOKEN=$(python -c "import json;print(json.load(open(r'$F'))['token'])")
H="Authorization: Bearer $TOKEN"
curl -s -H "$H" http://127.0.0.1:$PORT/health
```

Browsers are refused (any `Origin` header gets 403); so is a `Host` other than
`127.0.0.1:<port>` or `localhost:<port>`.

## Endpoints

Errors are always `{"error":{"code","message"}}`. A `503 framework-timeout` means the
game's framework did not run the request within 10 s (a loading screen): wait and
retry — abandoned work never runs later.

| Route | What it does |
|---|---|
| `GET /health` | `{ok, harness, session, build, buildStamp, penumbra, loggedIn, pluginEnabled}` |
| `GET /state` | The live character (race, tribe, gender, body type, drawn race code), the player collection's id, every drawn model (`slot`, `loaded`, `gamePath`, `resolved`, `modDir`; `slot:-1` = Penumbra resolves it but it is not drawn), the compositor (`phase`: `boot-hold`/`compositing`/`pending`/`idle`, last result, last refresh), `redraws` (`count`, `lastAtUtc`), `autoRefit` (`busy`, `pending`), `shaderUpgrade` (below), and the current `job`. No names. |
| `GET /log?since=<n>&max=<m>` | Plugin log lines after cursor `n` (`max` default 200, cap 2000): `{session, next, oldest, dropped, lines:[{seq,text}]}`. Pass `next` back as `since`. `dropped:true` = lines were lost to the ring. |
| `GET /screenshot?ui=true\|false` | The game screen as PNG (`ui=false` = before Dalamud's UI). 429 while another is taken; 503 `capture-timeout` when the game is minimised or not rendering. |
| `POST /actions/refit` | `{"mod":"<Penumbra folder name>","bodies":["rue","tbse@women"],"remake":false}` → 202 `{job, modDir, state}`. Runs the Studio's "Make every size" with the Studio's current settings, after backing the mod up; then reloads the mod and redraws. |
| `POST /actions/restore` | `{"job":"<refit job id>"}`, or `{"mod":"<folder>"}`, or `{"mod":"<folder>","backup":"<backupId>"}` → 202. |
| `POST /actions/shader-upgrade` | `{"enabled":true\|false}` and/or `{"maskGloss":"inverse-specular"\|"low"\|"mid"}` → 200 `{enabled, maskGloss}`. Switches the gear shader upgrade as its Settings checkbox does; `maskGloss` picks how the diffuse form's derived mask fills its roughness channel, for this session only (never saved). 400 `invalid-field` for an unknown value, a wrong type or no field. |
| `GET /jobs/{id}` | `{id, kind, modDir, state: queued\|running\|done\|failed\|cancelled, progress, backup, reloaded, redrawRequested, redrawObserved, autoRefitFollowed, result, restored, error}` |

`mod` is the **folder name** Penumbra keeps the mod in (case-insensitive), never its
display name. Body keys are the size set's (`neolithe, bibo, rue, yab, yab-buff, tre,
rosaline, tbse, tbse-x`); `<men's key>@women` makes a women's mod's men's versions.

Poll a job until `endedAt` is set:

```bash
JOB=$(curl -s -H "$H" -H "Content-Type: application/json" -d '{"mod":"My Outfit","bodies":["rue"]}' \
      http://127.0.0.1:$PORT/actions/refit | python -c "import json,sys;print(json.load(sys.stdin)['job'])")
curl -s -H "$H" http://127.0.0.1:$PORT/jobs/$JOB
```

Job `error.code` is one of `legacy-mod`, `framework-timeout`, `backup-too-large`,
`insufficient-space`, `backup-failed`, `run-failed`, `restore-failed`, `cancelled`,
`after-change-skipped`, `reload-failed` (Penumbra refused the reload: the files are as reported, the game may not
match them until Penumbra reloads its mods — tell the user) — or `internal`, which is a harness bug: report it as a
finding.

Only one job runs at a time (409 `busy`), across `off`/`on` too: `on` is refused while
a cancelled job is still stopping. A **plugin reload** mid-job is not covered — the old
instance's job may still be writing while the new one listens — so never reload the
plugin while a job runs; wait for its `endedAt`. Refit and restore are refused with 409
`auto-refit-busy` while auto refit is working.

## You must back up and restore every mod you touch

**Never edit a mod's files yourself — only through the harness.** Every refit backs
the mod up before it writes, and **you must restore every mod you refitted before
you finish**, whatever the outcome:

- **Restore by job:** `{"job":"<refit job id>"}` puts the mod back as it was just
  before that refit. To undo several refits, restore newest first, or restore the
  first refit's job.
- **Restore by mod alone:** `{"mod":"<folder>"}` restores the *oldest* refit backup
  of this harness session. That is the original **only when
  `restored.olderSessionBackups` is empty**; otherwise an earlier session (before a
  plugin reload) also backed the mod up — restore the right one by its explicit
  backup id. `restored.skippedNewer` lists this session's newer backups it passed
  over.
- Every restore first takes a **pre-restore snapshot** of the mod as it is, so a
  restore never loses anything. If it fails partway (`restore-failed`), the snapshot
  is named in the error (and `restored.files`/`deleted` are null: how far it got is
  not known); **retry** — it simply runs again.
- `restored.changedSinceJob: true` warns that something else changed the mod after
  the refit (null = not known). It never refuses the restore.
- An incomplete backup (a crash mid-copy) is never restored: `409 backup-incomplete`.
- **What restore does not cover:** collection selections that pointed at an option
  the refit added (they are left pointing at an option that is gone), and Proteus's
  own state outside the mod (auto-refit records). Tell the user if you changed a
  selection.

**Where backups live:** `$APPDATA/XIVLauncher/pluginConfigs/Proteus/test-harness/backups/<modDir>/<backupId>/`.
The harness never deletes or overwrites a backup; the user may delete them by hand.

## Waiting for the game

- **Gear shader upgrade.** `/state` `shaderUpgrade` is
  `{enabled, busy, waiting: none|penumbra|managed-mod, awaitingRedraw, maskGloss,
  upgraded:[{gamePath, source, copy, form: index-mask|diffuse, mask:{gamePath, copy, width, height}|null, loaded}],
  left:[{gamePath, source, reason: old-table|unsupported-form|unreadable|outranked|unresolved|compositor|write-failed}],
  peeks:{trustedFirstTry, settled, failedOpen}, reload:{inPlaceLoadsMaterials, inPlace, redraw, gaveUp},
  masks:{memoHits, decodes, pruned, missingBeforeApply}, lastAppliedUtc}`.
  After switching it or equipping, wait for `shaderUpgrade.busy == false` (and about 2 s for its verify walk)
  before you assert `loaded`. Copies live in the managed mod's `ShaderUpgrade/` folder; the worn mod's own
  folder must hash the same before and after.
- After a refit or restore, wait for the job to end, then for `/state`
  `autoRefit.busy == false` and `compositor.phase == "idle"` before you assert
  anything or take a screenshot.
- `redrawRequested` only says a redraw was asked for; `redrawObserved` says one
  arrived; `redraws.count` in `/state` counts them.
- `autoRefitFollowed: true` means auto refit woke up after the redraw — and may have
  written into worn mods, including this one. **Prefer testing on mods the
  character is not wearing**, and expect a worn mod to be refitted again after a
  restore.
- Known limitation: a refit cancelled before its worker started reloads nothing (it
  wrote nothing).

## Personal data

- `/log` scrubs the Windows user name and every collection and character name seen
  this session, as whole words. Penumbra's built-in `Default` collection is not
  scrubbed, names under 3 characters are not, and **other players' names are not**.
  A collection named like a body key or a common word (`Rue`) masks that word in
  the log too.
- **Screenshots show the game HUD, chat, party lists and name plates** even with
  `ui=false` (that only hides Dalamud's UI).
- Keep screenshots and log excerpts in your **scratchpad only**. Never put names,
  log excerpts with names, or screenshots into commits, tasks, issues, memory or
  reports; describe what they showed in words.

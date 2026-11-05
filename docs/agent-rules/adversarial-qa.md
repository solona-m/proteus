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
3. The harness is normally already on: once the user types `/proteus harness on`
   in game chat it stays on across plugin reloads until `/proteus harness off`.
   Only the user can turn it on (a `harness` line in the dev-command file is
   refused) — ask only when `/health` does not answer.
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
| `POST /actions/shader-upgrade` | `{"enabled":true\|false}` and/or `{"maskGloss":"inverse-specular"\|"low"\|"mid"}` → 200 `{enabled, maskGloss}`; `{"undoWorn":true}` (optionally `"decline":false`) or `{"upgradeWornNow":true}` → 202 `{started}`. Switches the gear shader upgrade as its Settings checkbox does; `maskGloss` picks how the diffuse form's derived mask fills its roughness channel, for this session only (never saved) and only for files written after it; `undoWorn` / `upgradeWornNow` run the two Settings buttons. 409 `busy` while a run or auto refit is working; 400 `invalid-field` for an unknown field or value, a wrong type, no field, `decline` without `undoWorn`, or both runs at once. |
| `GET /hair-port?mod=<folder>&hair=<hair model game path>` | Read-only: what the Studio's "Port to all races" lists for that hairstyle — `{modDir, hair, source, sourceRace, id, otherSexNote, rows:[{race:"c0101", state: open\|covered\|ported\|stale\|no-style\|other-sex-authored, otherSex, ownVersion, ownSkeleton, tickable, defaultTicked, declined, stateText, warning}], ports:[{race, targetGamePath, sourceGamePath, group, option, file, auto, est, skeletonFrom, portVersion}]}`. `hair` may use either slash and any case; a port of ours stands for the author's hairstyle it was made from (`source`). `warning` is set only on `ported`/`stale` rows. 404 `hair-not-in-mod`, 409 `legacy-mod`, 409 `mod-unreadable`. |
| `POST /actions/hair-port` | `{"mod":"<folder>","hair":"<hair model game path>","races":["c0101","c0401"]}` → 202 `{job, modDir, state}`: the Studio's **Port** button for those races (leave `races` out for the Studio's default ticks), after a backup; then reloads the mod and redraws. `{"mod","hair","remove":true}` is the **Remove ports** button. Only races the Studio lets you tick are ported; the rest come back in `result.skippedRaces` (`not-listed`, `covered`, `ported`, `no-style`, `other-sex-authored`, `own-version`, `own-skeleton`). Job kinds `hair-port` / `hair-remove`; `result` = `{action, summary: ported\|nothing-to-port\|nothing-ported\|removed\|nothing-to-remove, resultLine, source, asked, skippedRaces, ported:[{race, group, option, est, skeletonFrom}], stripped, refused:[{race, reason}], warnings, current, saved}` (remove: `removed, message, remaining`). No backup is taken when nothing would be written. 400 `invalid-field` for a bad `hair`, `races` that are not playable `cNNNN` codes, `remove` other than `true`, or both; 409 `hair-port-busy` (below). |
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

```bash
curl -s -H "$H" -H "Content-Type: application/json" \
     -d '{"mod":"My Hair","hair":"chara/human/c0201/obj/hair/h0005/model/c0201h0005_hir.mdl","races":["c0101"]}' \
     http://127.0.0.1:$PORT/actions/hair-port
```

A hair job's `summary` is `ported` when it hair-ported at least one race (`hair-ported` in your notes means that).

Job `error.code` is one of `legacy-mod`, `mod-unreadable`, `hair-not-in-mod`, `framework-timeout`, `backup-too-large`,
`insufficient-space`, `backup-failed`, `run-failed`, `restore-failed`, `cancelled`,
`after-change-skipped`, `reload-failed` (Penumbra refused the reload: the files are as reported, the game may not
match them until Penumbra reloads its mods — tell the user) — or `internal`, which is a harness bug: report it as a
finding.

Only one job runs at a time (409 `busy`), across `off`/`on` too: `on` is refused while
a cancelled job is still stopping. A **plugin reload** mid-job is not covered — the old
instance's job may still be writing while the new one listens — so never reload the
plugin while a job runs; wait for its `endedAt`. Refit, restore and hair port are refused with 409
`auto-refit-busy` while auto refit is working.

`hair-port-busy` (409): a **hair port** is refused while the hair watcher examines the worn hairstyle, runs a check, or
owes one (`/state hairPort.busy`) — retry; `busy` flickers after every redraw, and an owed check can hold for about 30 s.
A **refit or restore** gets it only for a hair mod while the watcher is examining or checking right now
(`hairPort.worn.busy`, `hairPort.check.running`); an owed check (`check.owed`) **never blocks a restore**, and a
garment mod never waits on the hair watcher.

Reasons (`refused[].reason`, `stateText`, `resultLine`, `otherSexNote`) are in the game's language: assert on the
codes (`state`, `skippedRaces[].reason`, `summary`).

## You must back up and restore every mod you touch

**Never edit a mod's files yourself — only through the harness.** Every refit and hair
port backs the mod up before it writes, and **you must restore every mod you refitted before
you finish** — and every mod you hair-ported (a `hair-port` or `hair-remove` job id restores it too) — whatever the
outcome:

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

- **Gear shader upgrade — it WRITES INTO THE WORN MODS.** `/state` `shaderUpgrade` is
  `{enabled, busy, waiting: none|penumbra, awaitingRedraw, maskGloss,
  upgraded:[{gamePath, modDir, file, form: index-mask|diffuse}],
  left:[{gamePath, source, reason: old-table|unsupported-form|unreadable|compositor|write-failed|legacy-mod|mixed-specular|specular-from-other-mod|unsupported-placement|proteus-pack|declined}],
  masks:{memoHits, decodes}, lastRun:{kind: upgrade|undo-worn|upgrade-now, modsChanged, filesChanged, restored, kept, refused, reloaded, redrawRequested, atUtc}}`.
  After switching it on or equipping, wait for `shaderUpgrade.busy == false` before you assert. **Clean up everything
  you caused:** hash every file of each mod you will touch first; afterwards, while wearing each upgraded piece, POST
  `{"enabled":false,"undoWorn":true,"decline":false}` — switching the setting off in the SAME body, or the undo's own
  redraw walks and upgrades the pieces again — and check every non-manifest file hashes as before, the mod's redirects
  are as before, and no `Shader Upgrade/` folder, `Proteus/shaderupgrade.json` or `Proteus/shaderupgrade-backup/`
  remains. This undo deletes the gear shader upgrade's OWN backups as it restores them: that is the feature's undo, not
  the harness's refit backups, which ADR 0002 says the harness never deletes. `upgraded[].mask` names the Form B mask
  (`{gamePath, file}`).
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
- **Hair ports — the plugin's own hair path writes too.** `/state` `hairPort` is `{autoFit, earless, busy,
  worn:{gamePath, modDir, ported, portsInMod, busy, message, failed, earless, warning},
  check:{running, owed, done, total, race, message, rows:[{id, modDir, targetGamePath, state}], lastFitted}}`. After
  any hair-port, hair-remove, refit or restore job, the job's reload makes Penumbra report the mod edited, and the hair
  watcher owes a check of that mod (`check.owed`) about 400 ms later; the redraw also makes it look at the worn
  hairstyle. Either may add ports of the player's own race (`ports[].auto == true` in `GET /hair-port`) when the mod
  is on in the player's collection and "Fit hair automatically" is on — even right after a restore. So: wait for
  `check.running == false` and `worn.busy == false` (and `check.owed == false` where it clears), `GET /hair-port`
  again, and tell `"auto": true` ports (the plugin's) from yours (`"auto": false`) before you assert; restore even
  while a check is owed, then look for `auto` ports again. **Prefer mods that are off in the player's collection** for
  file-level checks, and hairstyles the character is not wearing.
- **What a hair-port QA run cannot verify** — say so as UNVERIFIED for the user: that the automatic path never
  starts a port across the sexes, remakes a Studio port after an author update, or leaves an undone one alone (they
  need the character, the author's files or Settings); and anything visual of a race or sex the character is not (a
  screenshot shows the character only). Check files, the record and the rows through the routes; screenshot only a
  port of the character's own race while they wear that hairstyle.
- **What restore does not cover for hair:** the hair watcher's session memory (its refusal memo and remembered
  warnings), and ports the automatic path makes after the restore's reload.

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

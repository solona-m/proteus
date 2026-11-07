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
3. The harness is **always on** on the owner's machine (`/proteus harness on`
   stays on across plugin reloads), so don't ask for it — read the session file.
   Only the user can turn it on (a `harness` line in the dev-command file is
   refused), so if it is off there, that is a Blocker to report. On any other
   machine (a contributor's), ask the user to type `/proteus harness on`.
   Another session may be running in-game QA at the same time: never overwrite the
   dev-loaded DLL except to load the build you are about to test (AGENTS.md Rule 5).
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
retry — abandoned work never runs later. A `503 too-many-connections` means more than
four requests were open at once: retry when one finishes; it is never a busy run
(`409 busy`). A body that repeats a field is refused: `400 bad-json`.

| Route | What it does |
|---|---|
| `GET /health` | `{ok, harness, session, build, buildStamp, penumbra, loggedIn, pluginEnabled}` |
| `GET /state` | The live character (race, tribe, gender, body type, drawn race code), the player collection's id, every drawn model (`slot`, `loaded`, `gamePath`, `resolved`, `modDir`; `slot:-1` = Penumbra resolves it but it is not drawn), the compositor (`phase`: `boot-hold`/`compositing`/`pending`/`idle`, last result, last refresh), `redraws` (`count`, `lastAtUtc`), `autoRefit` (`busy`, `pending`), `shaderUpgrade` and `variantRepoint` (below), the current `job`, `gear` (the ten slots in order: `slot`, `item`, `dyes`, and `set`/`variant` only for a Glamourer custom id; nulls when Glamourer cannot be read) and `harnessChanges` (`stopping`, and this session's recorded equips `gear[]` with `original`, `applied`, `requested`, `verified`, and holds `holds[]` with `before`). No names. |
| `GET /log?since=<n>&max=<m>` | Plugin log lines after cursor `n` (`max` default 200, cap 2000): `{session, next, oldest, dropped, lines:[{seq,text}]}`. Pass `next` back as `since`. `dropped:true` = lines were lost to the ring. |
| `GET /screenshot?ui=true\|false` | The game screen as PNG (`ui=false` = before Dalamud's UI). 429 while another is taken; 503 `capture-timeout` when the game is minimised or not rendering. |
| `POST /actions/refit` | `{"mod":"<Penumbra folder name>","bodies":["rue","tbse@women"],"remake":false}` → 202 `{job, modDir, state}`. Runs the Studio's "Make every size" with the Studio's current settings, after backing the mod up; then reloads the mod and redraws. |
| `POST /actions/restore` | `{"job":"<refit job id>"}`, or `{"mod":"<folder>"}`, or `{"mod":"<folder>","backup":"<backupId>"}` → 202. |
| `POST /actions/shader-upgrade` | `{"enabled":true\|false}` and/or `{"maskGloss":"inverse-specular"\|"low"\|"mid"}` → 200 `{enabled, maskGloss}`; `{"undoWorn":true}` (optionally `"decline":false`) or `{"upgradeWornNow":true}` → 202 `{started}`. Switches the gear shader upgrade as its Settings checkbox does; `maskGloss` picks how the diffuse form's derived mask fills its roughness channel, for this session only (never saved) and only for files written after it; `undoWorn` / `upgradeWornNow` run the two Settings buttons. 409 `busy` while a run or auto refit is working; 400 `invalid-field` for an unknown field or value, a wrong type, no field, `decline` without `undoWorn`, or both runs at once. |
| `POST /actions/variant-repoint` | `{"enabled":true\|false}` → 200 `{enabled}`; `{"undoWorn":true}` (optionally `"decline":false`) or `{"repointWornNow":true}` → 202 `{started}`. Switches "Gear variants" as its Settings checkbox does; `undoWorn` / `repointWornNow` run the two Settings buttons. 409 `busy` while a run, the shader upgrade or auto refit is working; 400 `invalid-field` for an unknown field, a wrong type, no field, `decline` without `undoWorn`, or both runs at once. |
| `GET /hair-port?mod=<folder>&hair=<hair model game path>` | Read-only: what the Studio's "Port to all races" lists for that hairstyle — `{modDir, hair, source, sourceRace, id, otherSexNote, rows:[{race:"c0101", state: open\|covered\|ported\|stale\|no-style\|other-sex-authored, otherSex, ownVersion, ownSkeleton, tickable, defaultTicked, declined, stateText, warning}], ports:[{race, targetGamePath, sourceGamePath, group, option, file, auto, est, skeletonFrom, portVersion}]}`. `hair` may use either slash and any case; a port of ours stands for the author's hairstyle it was made from (`source`). `warning` is set only on `ported`/`stale` rows. 404 `hair-not-in-mod`, 409 `legacy-mod`, 409 `mod-unreadable`. |
| `POST /actions/hair-port` | `{"mod":"<folder>","hair":"<hair model game path>","races":["c0101","c0401"]}` → 202 `{job, modDir, state}`: the Studio's **Port** button for those races (leave `races` out for the Studio's default ticks), after a backup; then reloads the mod and redraws. `{"mod","hair","remove":true}` is the **Remove ports** button. Only races the Studio lets you tick are ported; the rest come back in `result.skippedRaces` (`not-listed`, `covered`, `ported`, `no-style`, `other-sex-authored`, `own-version`, `own-skeleton`). Job kinds `hair-port` / `hair-remove`; `result` = `{action, summary: ported\|nothing-to-port\|nothing-ported\|removed\|nothing-to-remove, resultLine, source, asked, skippedRaces, ported:[{race, group, option, est, skeletonFrom}], stripped, refused:[{race, reason}], warnings, current, saved}` (remove: `removed, message, remaining`). No backup is taken when nothing would be written. 400 `invalid-field` for a bad `hair`, `races` that are not playable `cNNNN` codes, `remove` other than `true`, or both; 409 `hair-port-busy` (below). |
| `POST /actions/equip` | `{"slot":"Body","item":12345}` or `{"slot":"Body","set":317,"variant":2}` → 200 `{slot, sent, item, set, variant, dyes, previous, original}` (`readBack:false` when the worn item could not be read back). Equips through Glamourer for this session only (no lock, no design). Slots: Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, RFinger, LFinger (any case). `set`/`variant` equip that model even when no item uses it. 400 `invalid-field` / `item-invalid`; 409 `busy`, `auto-refit-busy`, `not-logged-in`, `carrier-slot`, `glamourer-refused`; 503 `glamourer-unavailable`, `harness-stopping`. |
| `POST /actions/mod-settings` | `{"mod":"<folder>","enabled":false}` and/or `"priority":1000` → 200 `{mod, collection, enabled, priority, before, redrawRequested}`. Holds the mod in the player's collection with a Penumbra **temporary** setting (its options kept), never saved. 409 `held-elsewhere` when another plugin (Glamourer, design binding) holds it; 404 `mod-not-found`; 400 `managed-mod`; 409 `no-collection`, `penumbra-refused`; 503 `penumbra-unavailable`, `harness-stopping`. |
| `POST /actions/mod-settings` with `"permanent":true` | `{"mod":"<Proteus mod folder>","enabled":false,"permanent":true}` → 200 `{mod, collection, permanent:true, enabled, priority, before:{enabled,priority}, redrawRequested}`. Writes a **Proteus** mod's own enabled/priority (not a hold), recorded for put-back (see **Design bindings**). 409 `not-a-proteus-mod`, `penumbra-refused`; 503 `record-failed`. |
| `POST /actions/put-back` | `{}` → 200 `{gear:[{slot,result}], holds:[{mod,result}], sweep, redrawRequested, studio:[{mod,files,panel,code,kept}], binding}`. Undoes every equip and hold of this session, then ends the Studio session (see *The Studio*), then the binding session (`binding` is null when there is none: see **Design bindings**). 409 `busy` only while a design apply is settling. |
| `GET /bindings?mod=<folder>&group=<g>&option=<o>&design=<guid>&names=1` | Read-only design-binding state: `{enabled, followsAutomation, restoresCharacterMods, unequipUnset, echoWindowOpen, echoMsLeft, active:{id, fields}, storeSha256, bindings:[{id, capturedUtc, mods, sha256}], designs:[{id, bound}], mod, design}`. With `mod` (and `group`+`option`): `{mod, governed, presetDriven, live:[{row,diffuse,emissive}], stored:[…], settings:{enabled,priority,options,temporary}}`; with `design`: its applied `fields`. Names appear only with `names=1`, scrubbed. 400 `invalid-field`; 404 `mod-not-found`, `design-not-found`; 503 `glamourer-unavailable`. |
| `POST /actions/glamourer-apply` | `{"design":"<guid>"}` (optionally `"allowAutoWriters":true`) → 200 `{design, activeBefore, activeAfter, finalizedSeen, settled, echoWindowOpen, echoMsLeft}`. Applies a Glamourer design to the character through Glamourer's own `ApplyDesign` IPC, as a user or plugin does (Proteus sees `finalized=DesignApplied`), and answers once its deferred work has settled. 409 `busy`, `auto-refit-busy`, `shader-upgrade-busy`, `variant-repoint-busy`, `hair-port-busy`, `not-logged-in`, `presets-pinned`, `put-back-first`, `auto-writers-on`, `model-change`, `glamourer-refused`; 404 `design-not-found`; 503 `glamourer-unavailable`, `record-failed`, `harness-stopping`. |
| `POST /actions/binding-edit` | `{"mod","group","option","row":<n>,"diffuse":"#RRGGBB"}` sets one live colour row (sub-row A) of a mod the active binding captured, through the colour editor's path; `{"mod","group","option","clear":true}` clears that option's live override, through **Reset to defaults**' (live-only) path. Leave `group`/`option` out for a simple mod's top level. → 200 `{mod, group, option, governed, live}`. Never writes the binding store or the mod's files. 409 `binding-off`, `no-active-binding`, `not-bound`, `preset-driven`, `no-override-rows`, `busy`; 404 `row-not-found`, `mod-not-found`. |
| `GET /studio` | The Studio's state: `open {modDir,file,gamePath}`, `window`, `tool`, `mode` (parts/polygons/all), `view`, `settings {mirror,adjacent,moveSkin}`, `effective {mirrorNow,adjacentNow,skinMovesNow,followReady}`, `parts [{label,material,island,skin,locked,moves}]`, `chosen`, `all {seeds,movingRows,rows,includesSkin,pivot}`, `pivot`, `help`, `edit {dirty,canUndo,worstMm,saveOwed,editVersion}`, `status`, `harnessOpened`, `session`. Positions are model space, metres. |
| `POST /actions/studio-open` | `{"mod":"<folder>","model":"<file or game path>","show":true}` → 200 the `/studio` body. Opens the model as picking it does; `show` brings the window up on the Studio tab. Refused: `managed-mod`, `mod-not-found`, `model-not-in-mod` (the message lists the models), `model-ambiguous` (give the file), `legacy-mod`, `studio-has-edits`, `studio-busy`, `put-back-first` (a session is already recorded), `busy`, `auto-refit-busy`, `harness-stopping`. |
| `POST /actions/studio-set` | Any of `{"tool":"move\|rotate\|scale","mode":"parts\|polygons\|all","view":"character\|model","mirror":b,"adjacent":b,"moveSkin":b,"unlockAll":true,"lock":[label],"unlock":[label],"part":"<label>"}`, applied in that order → 200 `{applied, locks:[{label,result}], chosen, state}`. Refused: `invalid-field`, `wrong-tool` (locks outside Move, Rotate or Scale), `no-studio-session`, `studio-changed`, `dragging`, `busy`, `auto-refit-busy`, `harness-stopping`. |
| `POST /actions/studio-drag` | One of `{"offsetMm":[x,y,z]}` (Move), `{"axis":"x\|y\|z" or [x,y,z],"degrees":d}` (Rotate), `{"factor":f}` / `{"factors":[x,y,z]}` (Scale), `{"undo":true}` → 200 `{kind, moved, nodes, worstMm, pivot, saved, status, state}`. Runs the gizmo's own drag about its pivot and saves at once. Refused: `invalid-field`, `wrong-tool`, `nothing-to-move`, `nothing-to-undo`, `no-studio-session`, `studio-changed`, `dragging`, `busy`, `auto-refit-busy`, `harness-stopping`. |
| `GET /matcaps` | `[?refresh=1][&retry=1][&mod=<folder>&model=<file or game path>]` → 200 `{scanned, scanning, images:[{name,path,source: global\|proteus}], pack:{state: idle\|downloading\|ready\|failed, have, total, detail}, libraries:[{mod,name,website,group,texture,options:[{name}],selected,enabled,inherited,heldBy,providers}], model}`; with `mod`+`model`, `model` is `{model, gamePath, images:[{name,fromMod,fromPack}], parts:[{label,material,skin,onMatcap,tickable,horn,island,alphaTested}] (on a face only `horn` parts are `tickable`), siblings:[{file,linesUp}], group (the apply group: what an Undo here restores with it), entries:[{name,gamePaths,meshes,source,state: ok\|superseded\|edited}], materialRoom}`. `refresh=1` rescans every mod. Asking starts the download of Proteus's own matcaps (first use, as showing the pane does) into the plugin's config folder — never a mod, nothing to put back; `retry=1` re-arms a failed or finished download (QA only). `pack` is reported even with Penumbra unavailable (in the 503 body). 400 `invalid-field`; 404 `mod-not-found`, `model-not-in-mod`; 409 `model-ambiguous`, `legacy-mod`; 503 `penumbra-unavailable`, `studio-unavailable`. |
| `POST /actions/matcap-image` | `{"name":"qa-<x>.png","color":"#RRGGBB"}` → 200 `{name, path}`. Writes a generated 256×256 shaded sphere into the global matcap folder; put-back deletes it while unchanged (`matcap.images`: `deleted`, `changed`, `gone`). 400 `invalid-field`; 409 `exists`; 503 `harness-stopping`. |
| `POST /actions/matcap` | Apply `{"mod","model","parts":[labels],"image":"<name>"[,"pack":true]}` (an image name is looked up in the mod's folder, then the global one, then Proteus's own; `pack: true` takes Proteus's only) or `{…,"library":"<folder>","group","option","confirmEnable":b}`; change `{"mod","model","entry":"<material name>",<image or library fields>}`; `{"mod","model","undo":true}`; `{"mod","model","forget":true}` → 202 `{job, modDir, state}` (kinds `matcap`, `matcap-change`, `matcap-undo`, `matcap-forget`), after a whole-mod backup. `result` = `{action, summary: applied\|changed\|undone\|forgotten\|refused\|nothing-to-undo, code, resultLine, material, gamePaths, files:[{file,meshesAdded,lod0Cut}], materials:[{name,gamePaths,normal,alphaThreshold}], drawn:{line,ours,throughFace} (Apply only: the panel's drawn-file line, null when the written file draws), siblingsSkipped, librarySetting}`. Every successful job, Forget included, ends in the reload and redraw. A library that is off needs `confirmEnable` (`needs-confirm` otherwise, nothing written). Refusal codes: `legacy-mod`, `imported-piece`, `not-target`, `no-horns`, `not-horn`, `skin-part`, `already-matcap`, `regenerated-model` (earless copies only; refits and hair ports take a matcap — an Apply on a hair port ends `resultLine` with the "kept as it is" sentence), `model-layout`, `material-cap`, `not-square`, `image-unreadable`, `no-template`, `edited`, `later-feature`, `cross-kind-change`, `unknown-part\|image\|library\|option`. Request refusals: 400 `invalid-field`; 409 `busy`, `auto-refit-busy`, `studio-busy` (never flushed); 503 `harness-stopping`, `studio-unavailable`. |
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
(The one exception is inside the mod: the Studio's own `meshvolume-backup`, which a
harness Studio drag created and put-back's Undo saved changes removes — see
*The Studio*.)

## You must put back every equip and hold

Equips and holds change the shared character for everyone, so:

- **Call `POST /actions/put-back` before you finish**, check every entry, and retry
  any `failed` one (only failures stay recorded). Results: gear `restored` /
  `changed` / `failed`; holds `cleared` / `changed` / `gone` / `failed`.
- `changed` means something else took the slot or the mod over since (the player, a
  design, automation, a Proteus carrier, another plugin's hold): the harness left it
  alone. Tell the user.
- Holds are Penumbra temporary settings, never saved to the collection. A mod
  another plugin already holds answers `held-elsewhere`; the harness never touches
  that hold. A slot holding a Proteus invisible carrier answers `carrier-slot`.
- Auto refit and the gear shader upgrade still react to what you equip, and
  put-back does not undo *their* writes: hash the worn mods first and clean up as
  the shader upgrade section says.
- Confirm what is **drawn** in `/state.models`, not `/state.gear`: Glamourer's
  restricted-gear protection can draw a different model than the worn item.
- **One in-game QA at a time when you equip or hold.** Before each assert, check
  that `/state.harnessChanges` lists only your entries and that `/state.session` is
  unchanged. A changed session means the plugin reloaded, and put-back already ran
  at unload: set your state up again and report it.
- `off` and plugin unload put back on their own (an unload off the framework thread
  cannot, and logs what it left).
- Start with a smoke check: one equip and put-back of one slot, one hold and
  put-back, with `/state` read before and after each.

## Design bindings

`GET /bindings`, `POST /actions/glamourer-apply`, `POST /actions/binding-edit` and
`/actions/mod-settings` with `"permanent": true` let you drive design binding as a
player does: apply a Glamourer design, edit a live colour, change a Proteus mod's
own setting. The harness never writes the binding store (`design_bindings.json`) or
a mod's files: there is no harness Update, and **Reset to defaults**' rewrite of the
mod's metadata is not driven (only its live half, `clear`). Those stay UNVERIFIED for
the user, and so do automation, zone changes and relogin (no route drives them).

- **An IPC apply is not quite a UI apply.** Glamourer marks the state's source
  `IpcManual` instead of `Manual`, and may not re-assert a design's associated mod
  temporary settings. Temporary settings a restore cleared (another plugin's, with
  "Restore every mod on the character" on) are not put back by the harness: their
  owner re-asserts them on its next apply. Report a difference that may come from
  this as such, not as a Proteus defect.

- **Record first.** Before the session's first change that outlives the game (a
  design apply, whose restore writes Proteus mods' permanent settings, or a
  `permanent` write), the harness writes the Before values to
  `test-harness/backups/<session>/binding.json`: the Glamourer state, the carriers,
  the live binding state and every configured mod's own setting in the collection. If it cannot,
  the change is refused with 503 `record-failed`. Put-back is the undo; the record is
  for recovering by hand if put-back never ran (a crash, an unload off the framework
  thread). A later binding session in the same harness session writes
  `binding-2.json`, `binding-3.json`…: none is overwritten. With "Restore every mod on
  the character" on, a design apply's restore writes other mods' own settings too;
  every mod whose own setting changed is recorded and put back (back to inheriting
  when it had no setting of its own). Never copy the record's content into a report.
- **Refusals.** A design apply is refused while a per-mod preset is pinned
  (`presets-pinned`: the apply would drop it for good), while harness equips are
  recorded (`put-back-first`: put back, then apply, then equip), when the character's
  collection changed since the session began (`collection-changed`, also for
  `permanent`: put back first), when the design
  would change race, gender, clan or body type (`model-change`), and while any
  automatic writer is switched on: auto refit, the gear shader upgrade, Gear
  variants, Fit hair automatically or Auto Hat Compat (`auto-writers-on`). Those
  write into worn mods after the apply and put-back cannot undo that. Pass
  `"allowAutoWriters": true` only when you will hash the worn mods first and clean up
  as the shader upgrade, Gear variants, hair and auto refit sections say; with Fit
  hair automatically on, a design that changes the hairstyle is still
  `model-change`.
- **Settling.** The apply answers only once the compositor and every automatic
  writer are quiet (at most the redraw wait; `settled:false` otherwise: wait and
  re-read). While it settles, every change route and put-back answer 409 `busy`:
  wait for the apply's answer before sending anything else.
- **The echo window.** For 2 s after a restore Proteus ignores the next design
  apply as its own echo (`echoWindowOpen`, `echoMsLeft` in the apply's answer and
  `/bindings`). Between any two applies wait until `echoWindowOpen` is false (at
  least 2.5 s) and `compositor.phase` is `idle`, or the second apply is swallowed and
  your check fails, or passes, for the wrong reason.
- **Put-back** stops the restore's deferred work first, then puts the Glamourer
  state back (carrier slots left alone) and re-reads it: `glamourer` is `restored`,
  `partial` (something, such as advanced dyes, did not come back), `changed`
  (someone applied something since: left alone), `failed` or `none`. Only after
  `restored` (or `none`) does it write back each `permanent` setting and the `live`
  binding state, so the character never wears one look with another's binding:
  after `partial` or `failed` they are `skipped-inconsistent` and stay recorded for the
  next put-back; after `changed` they are `skipped-inconsistent` and dropped for good
  (the log names the record holding their Before values: tell the user). Each is
  restored only while it is still what the harness left (`changed` otherwise). Retry
  once; report what is left. `/state.harnessChanges.binding` shows the session (null
  when none).
- **Script** (AC-binding-reapply). Read `/bindings` first and note `active`,
  `storeSha256` and each `sha256`; pick a governed mod M with live rows
  (`?mod=M&group=G&option=O`). (1) `binding-edit` a row to `#FF00FF`, then
  `glamourer-apply` the active design: `/log` shows `finalized=DesignApplied` and
  "re-applied — restoring its binding", `live` equals `stored`, `storeSha256`
  unchanged. (2) `permanent` a captured Proteus mod off, re-apply: it is back on. (3)
  edit, `equip` a slot, hold another mod: the edit stays, no "re-applied" line, then
  put back. (4) an unbound design whose fields don't overlap the active one's:
  "another design applied over", the edit stays. (5) edit, `clear`, re-apply: `live`
  equals `stored`. Finish with put-back and check `storeSha256`, each `sha256`,
  `active` and `live` equal what you read first.

Example (a variant case): hold the competing mod off, equip the model, assert, put
back:

```bash
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"mod":"Equinox","enabled":false}' http://127.0.0.1:$PORT/actions/mod-settings
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"slot":"Body","set":317,"variant":2}' http://127.0.0.1:$PORT/actions/equip
# ... wait for the compositor and auto refit to be idle, assert in /state.models ...
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{}' http://127.0.0.1:$PORT/actions/put-back
```

## The Studio (Move, Rotate, Scale)

The Studio routes drive the Studio panel through the code its controls run: a drag
goes through the gizmo's own begin, place and finish, and is **saved into the mod**
at once (the brush save, which keeps the author's original in the mod's
`Proteus/meshvolume-backup`). So:

- **Pick a mod with no saved Studio edits** (`studio-has-edits` otherwise), and not
  one the user is editing: `studio-busy` means the Studio has their drag, stroke or
  unsaved edit, which the harness never flushes. Wear it if you want screenshots.
- **Hash the mod's files before you start, and check them after put-back.**
- Put-back closes the harness's model without saving, puts the files back with the
  Studio's own **Undo saved changes**, restores the Studio's settings, locks and the
  model that was open, and closes the window if `show` opened it. It reverts only
  what is provably the harness's: every file the Studio record lists must be one a
  harness save wrote and still hash as it wrote it, and the model must be untouched in
  the Studio. Otherwise it reports `files: changed` / `panel: changed` with the
  reason (`hash-changed`, `not-harness-saved`, `model-edited`, `save-owed`,
  `drag-open`, `applying-sizes`) and touches nothing; tell the user, who can press
  Undo saved changes. `failed` with `still-patched` is retried by the next put-back;
  `blocked:<feature>` means a later feature (hat fitting, part switches) changed the
  same model: undo that first, most recent first, as the Studio says, then put back
  again.
- A session left recorded (`changed` or `failed`) keeps refusing a second
  `studio-open` (`put-back-first`), and design binding's apply, until a put-back
  ends it or the harness is switched `off`.
- If the user opens another model meanwhile, or edits the harness's model (a
  brush, a save owed, a file that no longer hashes as the harness wrote it),
  `studio-set` and `studio-drag` answer `studio-changed` and change nothing; put
  back then. Put-back still reverts the harness's files when it may, reloads that
  mod by name, and leaves the user's panel as it is.
- `moveSkin:true` answers before the skin binding is built: poll `GET /studio` until
  `effective.followReady` and `effective.skinMovesNow` are true before a drag that
  should carry skin (until then skin stays put, as in the panel).
- Locks (`lock`, `unlock`, `unlockAll`) are set only under Move, Rotate or Scale
  (`wrong-tool` otherwise): Body size's holds are never touched.
- A drag whose save fails answers `saved: false` and is taken back at once, so it
  is never written into the mod later.
- After a drag, wait as after a refit: `autoRefit.busy == false` and
  `compositor.phase == "idle"`. Auto refit and the hair watcher react to the save,
  and put-back does not undo their writes.
- Drive it from PowerShell with JSON bodies read from a file (Git Bash rewrites
  path-like arguments).

All-mode recipe: open with `show`, `{"tool":"move","mode":"all"}`, lock one part,
read `all` and `effective` (mirror and adjacent off), drag `{"offsetMm":[0,20,0]}`,
check `moved`, `nodes` and a screenshot, `{"tool":"rotate"}` and
`{"axis":"y","degrees":30}`, `{"tool":"scale"}` and `{"factor":1.2}`,
`{"undo":true}`, then put back and compare the hashes. Still UNVERIFIED after
that: only the visual judgement of the handle against a whole garment on a posed
character, and the radio row in German and Russian (screenshot both for the user).

## Matcaps

The matcap routes run the Studio's Matcaps tool through the panel's own entry
points, after a whole-mod backup. A library's option is a **permanent** collection
setting of a third-party mod: its Before is written to the session record first and
put back at put-back (in `matcap.settings`) while it is still what the harness left.

- **Restore every job** (`POST /actions/restore {"job"}`) and **put back** when done;
  a seeded `qa-` image is deleted then too.
- The first steps are the in-game gates; do them before anything else:
  - **G2 (looks right):** read `/state` and its character `code` (`c1401` …, never
    the customize byte). Seed an image (`matcap-image`), apply it to a gear part, a
    hair mod's part and a lace or fringe part (alpha-cut), screenshot each. Au Ra
    horns are **face** parts (see the plan's AM-7): with a non-Au Ra character, or no
    installed face mod with horns, the in-game half of horns is **UNVERIFIED**;
    Viera ears likewise need a Viera character.
  - **G2 horns with a face overlay:** with an Au Ra character, put an asymmetric face
    overlay on and apply a matcap to the horns, in both orders (overlay first, then
    matcap; matcap first, then overlay), then Undo with the overlay still on. After
    each composite, wait with no other action and confirm the overlay is still
    doubled: a fresh `face uv: … -> facelr_…` line in `/log`, no unresolved
    "resolves to our own output … refusing" line, and a face screenshot (not just
    the horns). A non-horn face part must refuse `not-horn`; a face with no horns
    `no-horns`.
  - **G3 (library missing):** apply a library option, then hold the library **off**
    (`/actions/mod-settings`) and screenshot and read `/log`. If more than the
    matcap parts disappear (the whole model, or errors), stop and report: the plan
    re-plans library mode.
  - **G4 (names resolve):** a gear `_mcap…` material must draw; a missing look
    after apply is a Blocker to report, not to work around.
- `needs-confirm` is the panel's confirmation: send `confirmEnable:true` only for a
  library you would switch on by hand, and say what else it turns on (the message
  lists it).
- `studio-busy` means the user has an unsaved Studio edit: never work around it.

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
- **Gear variants — it WRITES INTO THE WORN MODS' MANIFESTS.** `/state` `variantRepoint` is
  `{enabled, busy, waiting: none|penumbra, awaitingRedraw,
  worn:[{modDir|null, item: "e6255 Body", variant, effectiveMaterialId, loadedFolder: "v0001"|null}],
  repointed:[{modDir, item, materialId, folders:[int], variants:[int], legacyImcVariants:[int]}],
  left:[{modDir, item, reason: several-variants|mod-imc|other-mod-material|no-imc|meta-unreadable|outranked|legacy-mod|proteus-pack|declined|write-failed}],
  lastRun:{kind: repoint|repoint-now|undo-worn, modsChanged, foldersWritten, removed, kept, refused, reloaded, redrawRequested, nothingRecorded, atUtc}}`.
  It writes NO Imc entries: it copies the mod's own `…/v{N}/<name>` material redirects to `…/v{M}/<name>` (same file)
  inside the same options and containers, appended after their existing keys. `legacyImcVariants` lists Imc entries an
  older build (1428) wrote; they are still undone.
  `loadedFolder` is the material folder the game actually loaded for the piece (from the resource map), the real-game
  cross-check of `effectiveMaterialId`. **It is ON by default**, so it may already have written into a worn mod before
  you start: to take a "before" reading, first POST `{"enabled":false,"undoWorn":true,"decline":false}` while wearing
  the piece, check `repointed` is empty and wait for a redraw. After switching it on or equipping, wait for
  `variantRepoint.busy == false` and a redraw before you assert. **Clean up everything you caused:** copy the mod's
  `meta.json` and hash its other files first; afterwards, while wearing each repointed piece, POST
  `{"enabled":false,"undoWorn":true,"decline":false}`, check the manifest is as before (every container's `Files`,
  `DefaultData.Manipulations`) and no `Proteus/variantrepoint.json` remains (unless a case repointed a copy by hand: undo keeps such released keys recorded), then switch it back on (`{"enabled":true}`, the user's default) unless the user
  had it off.
  **Case A (a game-served variant, e.g. Satin - Sleepwear's e0050 Body, which ships only `v0003`, worn as the
  Coliseum Shawl, variant 1):** take the "before" reading as above (`loadedFolder` `v0001`, the game's material on
  the mod's model), then switch it on and check `repointed` lists folder 1, each container holding
  `.../v0003/mt_c0201e0050_top_a.mtrl` gained `.../v0001/...` -> the same file appended after its keys, no Imc
  manipulation was added, and the v0001 material now resolves into the mod.
  **Case B (custom-named material shipped only under `v0001`, e.g. Elegy - Clara Lingerie's e0051 `_top_b`, worn as the
  Striped Summer Top, variant 2):** take the "before" reading as above — wear the piece and record `loadedFolder` and a
  screenshot with the feature off and nothing repointed. If `loadedFolder` is already `v0001` (the game fell back for
  the missing material), STOP and report it as a finding: "wrong" must narrow to "a vanilla file exists under the worn
  folder" and EDGE-imc-all-variants-33 flips. Otherwise switch it on and check `repointed` lists the worn folder in
  `folders`, the worn `v{M}` material now resolves into the mod (`loadedFolder` stays `v{M}`, its file the mod's), and the
  piece is drawn.
  **Case C (a competing model replacement, EDGE-imc-all-variants-41):** "moonfire lace - by solona" (e0317 Body, ships
  only `v0001`) worn as variant 2 with Rose, Sweetheart Lingerie and Equinox enabled (each also replaces the e0317 model
  and ships its own `v0002` material). With the "before" reading taken as above (`loadedFolder` `v0002`, Rose's
  material on moonfire lace's model), switch it on: `left` no longer lists it as `other-mod-material`, `repointed`
  names it with `folders` [2], every moonfire lace container holding `…/v0001/mt_c0201e0317_top_b.mtrl` gained
  `…/v0002/…` → the same file, no Imc manipulation was added, and the v0002 material now resolves into moonfire lace
  (`loadedFolder` stays `v0002`).
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

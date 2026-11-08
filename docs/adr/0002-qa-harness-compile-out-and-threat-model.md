# ADR 0002: The QA test harness is compiled out of every CI build, and is loopback + token only

## Status

Accepted

## Context

Adversarial QA (the ai-workflow `adversarial-qa` agent) drives a feature through
whatever surface it exposes, with Bash and curl. Proteus has no such surface: it
runs inside the game, and until now every in-game check was the user's
("UNVERIFIED"). The QA test harness (`Proteus/TestHarness/`, guide in
`docs/agent-rules/adversarial-qa.md`) gives QA one — a small HTTP API inside the
plugin that reads state, logs and the screen, and can refit and restore a mod,
and for the session equip gear on the character and hold mod settings.

That is a local control port inside a game client, able to write into users'
Penumbra mods. It must never reach a user, and while it runs on the developer's
machine it must not be reachable by anything but the QA agent.

The release workflows build with exactly the canonical local command,
`dotnet build Proteus/Proteus.csproj -c Release` (AGENTS.md Rule 1), so neither
the configuration nor a command-line flag can tell a release build from a local
one.

## Decision

**Compiled out, decided by the CI environment.** `Directory.Build.props` sets the
MSBuild property `ProteusTestHarness` to true, then forces it false when `CI` or
`GITHUB_ACTIONS` is `true` (GitHub Actions sets both). It drives the
`PROTEUS_TEST_HARNESS` define and `Compile Remove="TestHarness\**"` in
`Proteus.csproj` and `Proteus.Tests.csproj`. Every CI build — stable and testing
releases, CI tests, CodeQL — leaves the harness out. The build prints
`Proteus test harness: compiled IN|OUT`.

**Checked on the artifact.** Both release workflows fail before the release step
if `Proteus.dll`, loose or inside `latest.zip`, contains the string
`Proteus.TestHarness`, or if the zip has no `Proteus.dll`. For a testing release
this is the only gate (`test.yml` runs for `main` only). `HarnessReleaseGuardTests`
pins the step; `HarnessBuildFlagTests` asserts no harness types whenever `CI` or
`GITHUB_ACTIONS` is set at test time, and is the local check that a build copied
to the game has it.

**Off until the user turns it on.** A dev-loaded build only (`IsDev`), started by
the user typing `/proteus harness on` — never from the dev-command file. An
autostart marker keeps it on across a rebuild-and-reload until `off`.

**Threat model.** IPv4 loopback socket only (`TcpListener(IPAddress.Loopback, 0)`,
never `HttpListener`'s host routing); a fresh 32-byte token per session, accepted
only as `Authorization: Bearer`, compared in constant time, never logged; any
`Origin` header refused (browser CSRF); `Host` must be `127.0.0.1:<port>` or
`localhost:<port>` (DNS rebinding); no CORS headers; hard limits on headers, body,
time and connections. The token keeps out browsers, other users and remote hosts.
A process running as the same Windows user can read the session file and is out
of scope — it could equally edit the mods directly.

**Writes are undoable.** Every refit backs the mod up first; every restore
snapshots the mod as it is before writing; the harness never deletes or
overwrites a backup.

*Amended 2026-11-05:* equips (through Glamourer, no lock) and mod holds are
recorded per session and put back on request, on `off` and on unload. Holds are
Penumbra temporary settings under the harness's own key, never saved to a
collection. A stop off the framework thread cannot put back, and logs what it left.

*Amended 2026-11-06:* the harness can also apply a Glamourer design (Glamourer's own
`ApplyDesign` IPC, created inside the harness, never in `Interop/`), edit or clear a
live design-binding colour override, and write a Proteus mod's permanent
enabled/priority. A design apply's restore can write any mod's own settings (with
"Restore every mod on the character" on, other mods' too): every mod whose own
setting changed is recorded and put back, options included, and back to inheriting
when it had no setting of its own. Other plugins' temporary settings a restore clears
are not put back (their owner re-asserts them). All of it is put back with the same
"still what the harness left" checks, Glamourer state first; the permanent settings and the live binding state go
back only once the Glamourer state has (otherwise `skipped-inconsistent`). Their
durability class differs from a refit's: the undo is held in memory, and before the
session's first change that outlives the game the Before values are written to
`test-harness/backups/<session>/binding.json` for recovery by hand, with no replay.
The harness never writes the binding store or a mod's files on this path. The
release guard checks a second marker, `HarnessSuppressApplySignals`, a member of the
harness-only `DesignBindingService` partial.
It can also drive the Studio: its routes (`/studio`, `studio-open`, `studio-set`,
`studio-drag`) drive the Studio panel through its own controls' code, and a drag
writes into a mod through the Studio's brush save. They open only a mod with no
saved Studio edits, never flush the user's edit, and keep one session at a time.
At put-back the harness restores the model files and deletes the
`meshvolume-backup` (and `meshvolume.json` record) its own saves created, through
the Studio's "Undo saved changes" — an exception to "never deletes a backup" — but
only when every file the record lists was written by a harness save and still
hashes as the harness last wrote it, and the model is untouched in the Studio;
otherwise it leaves everything and reports `changed`. The panel's settings, locks
and open model are restored. The panel's harness code is a partial compiled out
with the rest.
It can also drive the Studio's Matcaps tool (`/matcaps`, `matcap-image`, `matcap`).
A matcap job writes into a mod through the panel's own entry points, after a
whole-mod backup like a refit's, and refuses while the user has a Studio edit
pending (it never flushes it). Three things are new on this path: it writes a
**third-party** library mod's permanent option and enabled state in the player's
collection — the Before is written to the session record first, and put-back
restores it only while it is still what the harness left (the binding ledger's
rule); it writes and later deletes test images of its own (`qa-*.png`) in the
global matcap folder, leaving any one changed since; and matcap Undo and Forget
delete or retire the `matcap-backup` files they restore from or no longer need — a
second, narrow exception to "never deletes a backup", backed by the job's
whole-mod backup. `GET /matcaps` also starts (and with `retry=1` re-arms) the
download of Proteus's own matcaps into the plugin's config directory — the same
first-use download the pane makes; it writes no mod and no setting, so there is
nothing to put back.

**Amendment (2026-11-07, matcap click preview).** `matcap-preview` runs the Matcaps
pane's thumbnail click on the Studio session's model: it switches the tool, ticks
parts and asks for the preview. It writes no mod and no collection setting; what it
puts on is a Penumbra temporary mod on the player's collection (the live brush's own,
one at a time) redirecting the model and its new materials and textures to private
files in `%TEMP%`, a library option's texture as a copy rather than the library's
file. Put-back ends it with the Studio session, as do the panel's own end paths, and
the files are swept at the next load after a crash. The matcap job's separate change
form (`entry`) is gone: Apply on parts that already have a matcap changes it, as the
pane's Apply does.

**Amendment (2026-11-07, idle disk I/O checks).** Four more abilities, so QA can
check what Proteus reads from the drive while nothing changes:
- `boot-rearm` re-arms the compositor's boot probe as a zone change does. It writes
  nothing of its own; with a Proteus mod enabled the probe runs a normal boot
  composite, which rewrites only Proteus's own output mod.
- `sidecar-edit` writes one colour row of a Proteus mod's own sidecar
  (`Proteus/metadata.json`) through Proteus's own save. It backs the file up and
  writes a ledger (`backups/<session>/sidecar/<mod>/`) before the first save, refuses
  a mod with an unsaved user edit, and holds one lock across the edit and each
  restore. Put-back (on or off the framework thread) restores the file only while
  it still hashes as the harness wrote it, and deletes a `metadata.default.json`
  only when the harness's save created it, it is unchanged and the file was
  restored — a third narrow exception to "never deletes". A crash leaves the ledger:
  the next start reports it and restores nothing by itself.
- `mod-settings` takes another collection: still a Penumbra temporary hold under the
  harness key, never saved; put-back's sweep covers every collection Penumbra lists.
  `/state` lists collection ids only, never names.
- `auto-refit` switches auto refit for the session in memory only. It is never
  written to the configuration, so no save can keep it; put-back or unload clears
  it, and put-back lists any mod a refit wrote into while it stood (those writes are
  not undone by the harness).

**Amendment (2026-11-08, shell output failures).** Three more abilities, so QA can
drive issue sweep D's recovery flows on demand:
- `output-lock` either holds read-only handles (no write or delete sharing) on up
  to 32 existing files of Proteus's **own** output mod for at most 120 s, or makes
  the next N (at most 20) moves of matching output files fail in memory. It writes
  nothing; handles die with the process and are released at expiry, on release, at
  put-back, on `off` and on unload.
- `shell-fault` leaves one shell texture redirect out of Proteus's **own** manifest
  at publish. Healed before Proteus's automatic redraw (rebuilt from the live
  manifest with its manipulations kept, recorded as a publish) or cleared by
  put-back or `off` with a recomposite (an unload clears it without one); a crash loses it with the process
  and the next composite publishes in full.
- `auto-redraw` switches the compositor's auto redraw for the session in memory
  only, never written to the configuration; put-back or unload clears it.

## Consequences

Easier: QA can test the plugin in a running game unattended once the user turns
the harness on, and nothing about it can ship.

Harder: CodeQL never scans the harness (CI compiles it out), so its parser,
auth, dispatch and backup code get a careful human/agent review instead. A
developer shell that exports `CI=true` silently builds without it — the build
line and the check command say so. A global `-p:ProteusTestHarness=true` would
override the switch; no workflow passes one, and the release guard would catch
it.

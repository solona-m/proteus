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

## Consequences

Easier: QA can test the plugin in a running game unattended once the user turns
the harness on, and nothing about it can ship.

Harder: CodeQL never scans the harness (CI compiles it out), so its parser,
auth, dispatch and backup code get a careful human/agent review instead. A
developer shell that exports `CI=true` silently builds without it — the build
line and the check command say so. A global `-p:ProteusTestHarness=true` would
override the switch; no workflow passes one, and the release guard would catch
it.

# Project rules — code-critic

Project-specific extension of the AI Workflow plugin's `code-critic` skill.
The skill reads this file on every review — ad-hoc and in the `/feature`
workflow — and applies each rule at the severity it states, alongside its
base standards. The skill itself is project-agnostic and plugin-owned;
**this file is owned by the project** (the plugin never overwrites it —
later `/init-workflow` runs only add content you confirm), and it is the
file that accretes over time: every time an agent produces bad output the
base standards didn't
catch, add a rule here — or, better, encode it as a build-enforced test.

Guidance for good rules:

- One bullet per rule, each stating the rule AND its severity
  (`FAIL` / `NEEDS_DECISION`). Add subsections (Persistence, Security,
  Privacy, View Layer, …) as the list grows.
- Name the exact symbol/file/pattern, so the reviewer can grep for it.
- State the failure mode, not just the prohibition ("otherwise X is
  silently wrong").
- Scope to production code vs test code if the two differ.
- Say whether the rule is diff-scoped or repo-wide.

**Build-enforced rules.** When a rule is mechanically checkable, prefer
encoding it as an architecture/fitness test over prose here — tests don't
drift and the human never re-verifies them. For any rule that IS backed by a
test, the reviewer's job is only to check the diff does not WEAKEN the
enforcement (deleting/disabling the test, adding an unexplained exemption, or
restructuring code out of the test's scan scope) — a weakened enforcement is
`FAIL`. List which rules are build-enforced so the reviewer doesn't re-derive
them by hand.

Build-enforced: the localization rule (`Proteus.Tests/LocalizationTests.cs`:
keys, placeholders and format specifiers match English across every locale,
code keys match `en.json`) and the supply-chain rule
(`Proteus.Tests/SupplyChainTests.cs`).

## Rules

### Penumbra mods

- **Never author Penumbra v3.** Writes go through `PenumbraModMeta` at
  `SingleFileVersion` (4); a v3 folder is refused (`IsLegacyFolder` /
  `LegacyFolderException`), never written or upgraded in place. Otherwise the
  mod is half-migrated and Penumbra misreads it. `FAIL`. Diff-scoped. The one
  exception: `PenumbraModMeta.MigrateWorkCopy`, which converts a work copy
  outside Penumbra's mod folder whole, for the headless size set; it refuses
  anything under that folder.
- **Append option groups, never insert or reorder them.** Penumbra keeps a
  user's settings by group *position*, so inserting a group shifts every
  later group's saved selection onto the wrong group. `FAIL`. Diff-scoped.
- **Exports and diagnostic dumps go to the Desktop** (`DesktopFolder`), never
  into a Penumbra mod folder, where Penumbra would pick them up as mod
  content. `FAIL`. Diff-scoped.

### Threading and interop

- **Penumbra/Glamourer IPC events fire on the caller's thread.** A handler that
  touches game state, ImGui or the compositor must hop to the framework thread
  (as `PenumbraBridge` does) first; otherwise it races the game and crashes
  intermittently. `FAIL`. Diff-scoped, `Proteus/Interop/` and event handlers.

### Mesh edits

- **Refit moves positions only — never normals** (or tangents) in
  `BodyRetarget*` / `RefitModService` / `Proteus.Refit`. Rewritten normals
  change the garment's shading. `FAIL`. Diff-scoped. One exception, by the
  user's decision: body-skin vertices the refit's nipple smoothing
  (`SmoothCoveredNipples`) moves are reshaded, else the flattened skin still
  shades as a nipple. Garment and cloth normals never.
- **A mesh change must be checked for validity, not just smoothness** — a
  folded or degenerate triangle shows in game as a hole. A diff that adds or
  changes a mesh pass without a test asserting no folded/degenerate triangles
  on its output is `NEEDS_DECISION`.

### Localization

- **Every user-facing string goes through `Strings`/`ProteusLoc.Localize` (never
  CheapLoc's own `Loc.Localize`, which can return `"#key"` once inlined) with a
  literal key and is added to `en.json` and all 7 translations** (de, es, fr,
  ja, ko, ru, zh) — translated, not an English copy. An English copy passes the
  tests but ships untranslated. Build-enforced for keys/placeholders; English
  copies in a non-English file are `FAIL` by hand. Diff-scoped.

### Tests

- **No local paths in tests.** Real game/mod data is read via `LocalData` and
  the test is marked `[LocalDataFact]`, so it skips on CI and on other
  machines. A hard-coded `E:\`/`C:\` path or user folder in
  `Proteus.Tests/` is `FAIL`. Diff-scoped.

### Native

- **`proteus_bcn.dll` is built without `/arch:AVX2`** (baseline x64). An AVX2
  build crashes on CPUs without it. `FAIL` if a diff adds it. Diff-scoped,
  `Proteus/native/`.

### Supply chain

- **NuGet versions are plain and match the lock file; actions are pinned to a
  commit SHA with a `# vX.Y.Z` comment.** A floating (`2.*`) or ranged
  PackageReference, a `uses:` on a tag or branch, or a `packages.lock.json`
  `resolved`/`contentHash` change in a diff that only pins versions is
  `FAIL`: a tag or a float can change the code a release builds without any
  diff here. Only the read-only `codeql.yml` may run on a push to `test`.
  Build-enforced by `SupplyChainTests`. Repo-wide.

### Commits

- **No real name and no Claude attribution in commits**; the author is
  Solona. `FAIL`. Diff-scoped (commits on the branch).

## Privacy anchors

The skill's base privacy rules (deletion by design, public-surface whitelist,
no personal data in logs/URLs) are generic; these anchors bind them to this
codebase. Fill per project:

- **Sensitive categories**: character names, world names, and local file
  paths containing the Windows user name. No compliance doc.
- **Public surfaces**: the Cloudflare worker (`worker/src/index.js`,
  `dl.solona.info`) serves public release assets and the README; it takes no
  user data. Everything else runs locally inside the game client.
- **Identifier exemptions**: mod names, item names and game file paths
  (`chara/...`) are public by design.
- **Privacy tests**: `Proteus.Tests/LogAnonymizerTests.cs` covers the log
  anonymizer used by `LogExportService`. Anything new written to an exported
  log or a shared file (presets, exported mods) needs a by-hand check that it
  doesn't carry character names or local paths.

## Checklist

One checklist item per rule in the *Rules* section above — keep the two in
sync. The reviewer evaluates these as part of the skill's Review Checklist.
Tag items "(if applicable)" when they only apply to certain diffs.

- [ ] No Penumbra v3 written; v3 folders refused, not edited (if applicable)
- [ ] Option groups appended, never inserted or reordered (if applicable)
- [ ] Exports/dumps go to the Desktop, not a mod folder (if applicable)
- [ ] IPC event handlers hop to the framework thread before touching game state (if applicable)
- [ ] Refit changes positions only, not normals/tangents (if applicable)
- [ ] Mesh passes have a validity test (no folded/degenerate triangles) (if applicable)
- [ ] New strings localized in all 8 locale files, translated; LocalizationTests not weakened (if applicable)
- [ ] No local paths in tests; real data via LocalData + [LocalDataFact] (if applicable)
- [ ] Native shim not built with /arch:AVX2 (if applicable)
- [ ] NuGet versions plain and matching the lock file; actions SHA-pinned; SupplyChainTests not weakened (if applicable)
- [ ] Commits authored as Solona, no real name, no Claude attribution

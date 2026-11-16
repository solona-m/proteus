# Clean-room rewrite of `XivLiveMesh/PbdFile.cs`

Instructions for the agents that replace the Meddle-derived racial deformer reader with code Solona owns
outright, so Proteus can move off the AGPL-3.0 (see `docs/LICENSE-DRAFT.md`).

## Why

`XivLiveMesh/PbdFile.cs` is ported from Meddle (PassiveModding, AGPL-3.0): `Meddle.Utils/Files/PbdFile.cs`
and `Meddle.Utils/RaceDeformer.cs`, by way of SkinTattoo @bc3663a. While it is in the plugin, the plugin
must stay AGPL. Solona can either get the Meddle authors' written permission (ask first — if they agree,
none of this is needed) or replace the file with an independent implementation. This document is the
second route.

A clean-room rewrite has two people who never meet in the code: one who reads the old code and writes down
only the facts (the file format and the maths), and one who has never seen the old code and writes new code
from those facts. The facts of a file format aren't protected by copyright; the way someone wrote the code
can be. Keeping the two roles apart is what shows the new code wasn't copied.

## The rules for both agents

- Work on a branch off `test`, in its own worktree, through the `/feature` pipeline (`AGENTS.md`, Rule 3).
- Never open, quote or paraphrase code from: Meddle, SkinTattoo, Ktisis, Brio, DragAndDropTexturing,
  TexTools / xivModdingFramework, Penumbra's or Glamourer's deformer code, or any other GPL/AGPL source.
  Don't search the web for "pbd parser" code either. Agent B also never opens the old `PbdFile.cs` (below).
- Keep a log: every file and URL each agent opened, committed as `docs/pbd-clean-room-log.md`. It is the
  evidence that the rule was kept.

## Agent A — the spec writer (may read the old code)

**Goal:** write `docs/pbd-format-spec.md`: a plain-language description of the `human.pbd` format and of
what the three public methods compute. No code, no pseudo-code that mirrors the old code's structure, no
copied identifiers beyond the public API below, no comments lifted from the old file.

**May read:** `XivLiveMesh/PbdFile.cs`, `Proteus.Tests/PbdFileTests.cs` and the other tests that use it,
its callers in `Proteus/` and `XivLiveMesh/` (Proteus's own code), and a real `chara/xls/boneDeformer/human.pbd`
from the game, via `LocalData` (never a hard-coded path).

**The spec covers, as facts:**

1. **File layout.** Byte order; the count at the start; the table of per-race entries (what each field
   holds, its width and signedness); the table of tree links (parent / first child / next sibling / entry
   index, and the "no parent" value); where each race's deformer block starts.
2. **A deformer block.** Its bone count; the table of name offsets (width, signedness — they are UNSIGNED:
   NIRÆ's 600-odd-bone deformer puts names more than 32 KB past the block start — and what they are relative
   to); how names are terminated and encoded; the padding before the matrices; each matrix's 12 floats and
   how they map onto a 4×4 transform; what happens to a name that appears twice.
3. **Bad input.** What the reader must tolerate: an entry whose offset is 0, negative or past the end of the
   file; two entries sharing a block; a link index or parent out of range.
4. **The race chain.** Given a race the model was authored for and a race it is drawn on: how the tree is
   walked from the target towards the source, when the answer is "no bending" (same race, either race 0),
   when it is "no chain" (a race not in the file, a broken link, the source never reached, a loop), and the
   order the deformers apply in.
5. **One bone through a chain.** For each deformer in order: use the bone's own matrix if listed, otherwise
   its nearest listed ancestor's (ancestors come from a caller-supplied parent lookup), otherwise nothing;
   how the results combine; the depth guard; and the separate yes/no question "did any deformer list this
   bone or an ancestor".
6. **Conventions.** System.Numerics uses row vectors: state exactly which way round the stored matrix is
   and the multiplication order, with one worked example from a real file (a bone, its stored floats, the
   resulting transform applied to a sample point).
7. **Test vectors.** A handful of real inputs and expected outputs Agent B can check against: for chosen
   race pairs, the chain length; for chosen bones, the resolved matrix (to 6 decimal places).

Agent A writes no implementation and hands over only `docs/pbd-format-spec.md`.

## Agent B — the implementer (never sees the old code)

**Start in a fresh context**, in a worktree where the old file has already been deleted (the main agent
deletes `XivLiveMesh/PbdFile.cs` in a commit before B starts). B must not run `git log -p`, `git show`,
`git blame` or `git diff` on that path, nor read any earlier revision of it, nor open the files listed in
the rules above.

**May read:** `docs/pbd-format-spec.md`, this document, the callers in `Proteus/` and `XivLiveMesh/` (to see
how the API is used), and the tests.

**Write** a new `XivLiveMesh/PbdFile.cs` with this public surface, unchanged, so no caller changes:

```csharp
namespace XivLiveMesh;

public sealed class PbdFile
{
    public PbdFile(ReadOnlySpan<byte> data);

    // The deformers that take a model authored for `from` onto a `to` skeleton, in the order they apply;
    // empty when no bending is needed or none is defined.
    public IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> Chain(ushort from, ushort to);

    // One bone's deformation through a whole chain, as a single matrix.
    public static Matrix4x4 Resolve(IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> chain, string bone,
                                    Func<string, string?> parentOf);

    // Whether any deformer in the chain lists the bone or one of its ancestors.
    public static bool Lists(IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>> chain, string bone,
                             Func<string, string?> parentOf);
}
```

Header: `// SPDX-License-Identifier: LicenseRef-Proteus` (or the AGPL line if the new licence isn't in force
yet), `// Copyright (c) Solona`, and one line saying it was written from `docs/pbd-format-spec.md`. No
"ported from" line.

## Proving it behaves the same

Run, from the worktree's own build (never overwrite the game's dev-load DLL just to test):

```
dotnet test Proteus.Tests/Proteus.Tests.csproj -c Release --filter "(FullyQualifiedName~PbdFileTests|FullyQualifiedName~RacialModelBakeTests|FullyQualifiedName~RacialRoute|FullyQualifiedName~OtherRaceWearerTests|FullyQualifiedName~OtherRaceRefit|FullyQualifiedName~HairPort|FullyQualifiedName~HairScalpPushTests|FullyQualifiedName~SkeletonRemapTests|FullyQualifiedName~LiveMeshTests|FullyQualifiedName~KeepEndsLocalTests|FullyQualifiedName~UvBakeStep0LocalTests|FullyQualifiedName~UvTransferMapsLocalTests)"
```

Also run the golden baselines (`GoldenTests`, `GoldenCrossBodyLocalTests`, `SkinTuckGoldenTests`) once,
because this file decides where every other-race vertex lands. The `[LocalDataFact]` tests only run on the
owner's machine, where the real game files are: they matter most here, so the run has to happen there.

Add Agent A's test vectors to `PbdFileTests` as new tests. A difference anywhere is a bug in the new code or
the spec — fix it from the spec, never by looking at the old file.

## Paperwork once it passes

- `XivLiveMesh/NOTICE.md`: remove the `PbdFile.cs` row from *Third-party sources*.
- `THIRD_PARTY_NOTICES.txt`: remove the Meddle entry.
- Commit `docs/pbd-format-spec.md` and `docs/pbd-clean-room-log.md` with the code: together they are the
  record of how it was written.
- The merge commit says it is a clean-room replacement and names the two stages.
- Have the lawyer reviewing `docs/LICENSE-DRAFT.md` look at this record too.

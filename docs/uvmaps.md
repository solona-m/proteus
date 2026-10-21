# UV transfer maps

Proteus moves overlay art between body texture layouts with **transfer maps**: 4096×4096 16-bit RGBA TIFFs, one per
direction. In a map named `A_to_B_transfer.tif`, the pixel at (x, y) of layout **B** stores where the same point of the
body is on layout **A**: red is that point's u and green its v, scaled to 0–65535. Alpha says whether the pixel has a
point at all. `UVRemapService` reads them.

| Release tag | Files | Made from | Downloaded |
|---|---|---|---|
| `uvmaps-v1` | `bibo_to_gen3`, `gen3_to_bibo` | LooseTextureCompilerCore (MIT), uncompressed, 128 MB each | by every install on first run |
| `uvmaps-v2` | `bibo_to_tbse`, `tbse_to_bibo` | baked here from the Bibo+ and TBSE body meshes, Deflate, ~12 MB each | the first time a man's body material meets a woman's art |

`Proteus/uvmaps/releases.json` lists which files each tag publishes. The upload workflow reads it, and a test checks it
against `UVMapDownloadService`'s pinned lists. A tag's assets never change: a revised map gets a new tag.

## The bibo↔TBSE pair

### What it is for

A woman's overlay that reaches a man is drawn in his own layout instead of a women's one. That means TBSE's layout, or
the men's vanilla layout, which is the right half of TBSE's sheet. Such overlays are saved as bibo or have no saved
layout and name women's body materials: imports that list every race, and Create-tab packs that list only women's
races, which are carried onto the man's own body materials. Their gear and mask shells are carried the same way. A
man's own art is never moved. Art saved as gen3 or gen2 on a man counts as his own. The bibo↔TBSE pair is the only map
used for this.

The same maps match bodies for a refit across the sexes (Body size → **Refit for**): a women's body in the Bibo+ or game
layout is matched point by point to a men's body in the TBSE or game layout, one map hop, never through gen3. Measured
on real garments (`CrossSexRefitLocalTests`): 98–99.5% of the skin lands, 0 astray, p99 shift 27–42 mm. A women's top for
men is not offered: 115 of Sheer Elegance's 10,436 cloth faces turn over as the bust flattens.

Nothing of a woman's art is drawn on a man until the maps are on disk. It is never pasted in her layout. The maps are
downloaded the first time a man needs them, and the mod says it is waiting meanwhile. Each mod's **Overlay on men**
switch, on by default, keeps its women's art off men altogether.

### How it was made

```
Proteus.Refit --bake-uvmaps --game "<FFXIV>\game\sqpack" --bibo-root "<Penumbra>\Bibo+" --tbse-root "<Penumbra>\The Body SE" --out-dir Proteus\uvmaps
```

1. The bake reads the bodies' files by path (`UvBakeInputs`):
   - **Bibo+ v3.0.9:** Nude Small chest; Type A, B and C and SFW Undies Small legs; Small Clothes hands and feet.
   - **TBSE v3.0.0:** `common/1` chest, the `p2/step 6- smallclothes options` legs (SFW, Gen A–D), `common/3` hands and
     `common/7` feet.
2. TBSE is bent from a man's proportions to a woman's by the game's own racial deformer (`human.pbd`, c0101→c0201,
   `RacialModelBake`). A body mod's own bones (earring nodes, IVCS physics bones) are bent as the standard bone they
   stand in for (`UvBakeBones`).
3. Each destination texel is rasterised to its point on the destination body. That point is projected along a smoothed
   normal onto the source body, only onto triangles of the same or a neighbouring body region, and the source uv found
   there is written.
4. Where a body is the destination, its legs are every smallclothes option at once, so whichever option a player wears
   has a map.

The maps contain no geometry, weights or textures from either mod, only a source coordinate per texel, as the
LooseTextureCompilerCore maps do. Publishing them was the user's decision.

### Provenance

| | SHA-256 |
|---|---|
| `bibo_to_tbse_transfer.tif` (11 798 374 bytes) | `1ad198ed137abf5882ee0e38b3b91d7eb01771af35f7be2cd4eaf41d975e32a0` |
| `tbse_to_bibo_transfer.tif` (12 911 330 bytes) | `701964922589185c3ed4d4b1af824f84bf0c2dd4552488a8e7383a08ce345c23` |
| TBSE `common/1/c0101e0000_top.mdl` | `20bab704895ec6fa32914495dafa77708d5b89b0a9e6abf1d3043656bab1d8d0` |
| Bibo+ `Breasts - Small Clothes/Nude - Small/…/c0201e0000_top.mdl` | `04831b60c8faff1b0b6e2f4ab7eec1465afd2f6ee24292c1aa24b1cf6c415a5d` |

The bake's JSON report lists every input's SHA-256, and `UvTransferMapsLocalTests.Rebake_reproduces_committed_maps`
re-bakes and compares the result byte for byte. The bake can't run on CI, because it needs the two mods and the game.
If either mod changes its UVs, the maps must be baked again and published under a new tag.

### Quality (2026-10-20)

| Check | Gate | bibo→TBSE | TBSE→bibo |
|---|---|---|---|
| Coverage | ≥ 99% | 100% | 100% |
| Folds, sternum / under-bust / lower belly / inner thigh | ≤ 1% | ≤ 0.4% | ≤ 0.2% |
| Round-trip mismatch (gross) | ≤ 2.6%¹ | 2.0% | 1.5% |
| Folds overall | ≤ 1.2%² | 1.1% | 1.1% |
| Fingertips, toes | ≤ 10 mm | 1–2 mm | 1–3 mm |
| Sternum, shoulders, inner thighs | ≤ 15 mm | 6–14 mm | 6–14 mm |
| Under-bust, lower belly | ≤ 32 / 46 mm² | 26–31 / 45 mm | 27 / 23 mm |

A compressed map loads in about 0.56 s, against 0.23 s for an uncompressed v1 map: 2.5×, accepted by the user (gate 3×).
It loads once a session, only on a man who needs it, and is a tenth the download.

¹ No worse than the shipped bibo↔gen3 maps, which measure 2.0–2.6% by the same yardstick.
² Accepted by the user over the plan's 0.5% and 15 mm, where a woman's body and a man's differ in shape (breast, belly,
crotch) rather than the map in placement.

Art over the chest and crotch stretches, because the anatomy differs.

The identity check bakes two bodies that share one layout onto each other (Rue+→Bibo+, TBSE-X→TBSE), to show how far
geometric matching strays from authored uvs where shapes differ. It measures p50 2.7–4.5 px and p95 30–66 px at 4096.
The user accepted that after checking tattoos in game: the gate is now p50 ≤ 5 and p95 ≤ 68, against the plan's 4 and 16.
A dot on Bibo+'s sternum lands 27 px (about 6.5 mm) from TBSE's own; the bound is 30 px.

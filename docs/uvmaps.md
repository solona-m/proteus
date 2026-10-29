# UV transfer maps

Proteus moves overlay art between body texture layouts with **transfer maps**: 4096×4096 16-bit RGBA TIFFs, one per
direction. In a map named `A_to_B_transfer.tif`, the pixel at (x, y) of layout **B** stores where the same point of the
body is on layout **A**: red is that point's u and green its v, scaled to 0–65535. Alpha says whether the pixel has a
point at all. `UVRemapService` reads them.

| Release tag | Files | Made from | Downloaded |
|---|---|---|---|
| `uvmaps-v1` | `bibo_to_gen3`, `gen3_to_bibo` | LooseTextureCompilerCore (MIT), uncompressed, 128 MB each | by every install on first run |
| `uvmaps-v2` | `bibo_to_tbse`, `tbse_to_bibo` | baked here from the Bibo+ and TBSE body meshes, Deflate, ~12 MB each | superseded by v3 |
| `uvmaps-v3` | `bibo_to_tbse`, `tbse_to_bibo` | v2's bake with the nipples matched (see [Nipples](#nipples)) | the first time a man needs them; installs holding v2 upgrade in the background |

`Proteus/uvmaps/releases.json` lists which files each tag publishes. The upload workflow reads it, and a test checks it
against `UVMapDownloadService`'s pinned lists. A tag's assets never change: a revised map gets a new tag. The repository
holds v3 under the same file names, so v2 cannot be rebuilt from HEAD: it needs commit `d8c549c` or earlier.

**Upgrading v2.** The men's pair is held to its pinned size (the first set is not: an older revision there is worth more
than 256 MB fetched again). When both men's files are on disk and either is not at its pinned size, the current files are
fetched beside them as `*.staged`, in the background, with no pill and no recomposite; the old pair keeps drawing. The next
start swaps the staged files in, in the download service's constructor, before anything reads a map, and only when every
file of the pair is then current, so the pair is never half new and half old. A crash mid-swap is finished at the start
after (rolled forward from `.staged`, or back from `.prev`). A failed fetch keeps the old pair and writes
`men-upgrade.failed` (a UTC time); it is not tried again for 24 h. A lone men's file is no upgrade: the on-demand download
fetches what is missing or not current straight to its place.

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
men turns the most cloth over as the bust flattens (Sheer Elegance: 115 of 10,436 faces); it is saved all the same, with the
count shown in the warning colour. Only a match under 90% of the body is refused.

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
5. The nipples are matched (v3, below).

The maps contain no geometry, weights or textures from either mod, only a source coordinate per texel, as the
LooseTextureCompilerCore maps do. Publishing them was the user's decision.

### Nipples

**Which map does what.** `bibo_to_tbse` carries a woman's overlay onto a man (`ConvertSheet`), and is the body match of a
man's garment refit for a woman. `tbse_to_bibo` is the body match of a woman's garment refit for a man
(`UvConverter("bibo", "tbse")`, `BodyCorrespondence`), and carries TBSE art onto bibo.

**What v2 did.** A woman's breast stands forward of a man's chest and lower: a ray cast from TBSE's nipple meets the
breast above Bibo+'s. Measured on v2 (2026-10-28): a dot on Bibo+'s nipple landed 29.7 mm below TBSE's (114 px at 4096)
on a man's overlay, 34.7 mm below by the refit match (133 px), and TBSE art came back 36.6 mm high. The error was the
nipple's own: the clavicle row was +17 mm, the sternum +13 mm. The Sheer Elegance top refit for TBSE put the cloth over
the nipples 29.1 mm under his.

**What v3 does.**
- `UvBakeNipples.Find`: each body's nipples are the tips of the modelled bumps (the vertex standing furthest out of the
  skin within 1.5 cm, along the smoothed normal), on forward-facing skin (normal z > 0.7) within 2 cm of the front of
  its side — a large breast's overhang rim and its cleavage stand out as much — TBSE's after the bend. They sit on each
  body's painted areola: Bibo+ 4.1 texture px, TBSE 2.6–3.2 px from the dark centroid of `bibo_mid_base.tex` /
  `c0101b0001_b_d.tex`. They are the layout's: Bibo+ Medium, Large and X-Large put them 7.2, 1.8 and 10.3 px (at 4096)
  from Small's (X-Large's modelled nipple sits 5 mm from where the shared skin draws its areola); TBSE's Highlander
  chest and TBSE-X 0 px; Rue+ 10.3 px.
- `ChestField` (`bibo_to_tbse`): TBSE's points are moved before they are cast, by a sum of Wendland C2 bumps (reach
  14 cm) solved so each nipple lands on Bibo+'s, while pins stay still: the sternum, the clavicle row over each nipple,
  each flank, and a row under the creases where the plain projection has caught up (12.5 cm under TBSE's crease). It fades
  to nothing inside the clavicle and lower rows; outside its reach the map is v2's, texel for texel. Checked before
  baking: no triangle squeezed under a fifth of its area (least 0.29), pins held, nothing off the torso moved past 3 mm,
  no overshoot or push the wrong way, the nipples clear of the fade.
- `tbse_to_bibo` is, on the chest, that map's inverse (`UvTransferBake.InvertInto`): cast from Bibo+'s breast onto a flat
  chest, by ray or by nearness, the rays cross and the map folds (61% / 33% of the nipple disc). Only the front of TBSE's
  torso is inverted (the field's reach wraps round the flanks, where TBSE's side islands land on Bibo+'s front too), and
  only onto Bibo+'s torso, found apart from the drawing: not facing back (a breast's underside takes TBSE's chest),
  within the field's band and reach. Elsewhere it is v2's, texel for texel. Where the inverse meets v2's map the two
  disagree by what the v2 pair always did (a step of up to 9 mm on TBSE's body), so it is eased into v2's over 2 cm
  inside its edge (`UvTransferBake.Feather`): on the sheet where both point into one of TBSE's uv islands, on the body —
  read back on the inverse's island — where they point into two. Measured on TBSE's body, neighbours either side of that
  edge land p99 1.9 mm apart (v2 1.4), at most 3.2 (v2 2.8): gated at 1.25 × v2's, never under 2 mm.
- Only the nipples are matched. Matching the under-bust creases as well pulled a man's shirt refit for a woman into the
  fold under her breast (the lumberjack shirt: 49 faces turned over, against 8). The upper chest keeps v2's offset; the
  sternum dot lands 27.0 px from TBSE's, as with v2.
- The under-bust landmark is now the crease (`UvBakeNipples.Crease`). The old point, the front at the middle spine's
  height, sat at Bibo+'s nipple height (1.227 against 1.226 m); it is reported as `underBustOld` and not gated. The
  crease is bounded by v2's own error there, rounded up, never under 15 mm.

| v3 (2026-10-28) | bibo→TBSE | TBSE→bibo |
|---|---|---|
| Nipple landmark (v2) | 1.0 mm (50) | 0.0 mm (42.6) |
| Nipple dot, compositor / refit path | 1.0–1.1 px; men's vanilla 1.3 px | 0.3 px; men's vanilla 0.3–0.4 px |
| TBSE art back on bibo | | 3.2–3.6 px (v2 123) |
| Crease (v2) | 35 mm (51) | 43 mm (50) |
| Nipple disc (3 cm): coverage / folds | 100% / 0.01% | 100% / 0.08% |
| Chest distortion p99 where corrected, area / anisotropy (v2 there) | 4.28 / 5.64 (3.52 / 4.69) | 9.74 / 16.25 (10.67 / 22.51) |
| At the correction's edge, neighbours apart on the body: p99 / max (v2) | 0.95 / 1.3 mm (same) | 1.9 / 3.2 mm (1.4 / 2.8) |
| Folds, round trip gross | 1.09%, 2.0% | 0.96%, 1.47% |
| Sheer Elegance top for TBSE: cloth over the nipples; cloth faces turned over | | +5.9 / +5.6 mm (v2 −29.1); 143 (v2 101) |

The chest's distortion is gated against v2's own there: v2 is no yardstick of what is right on the chest (it squeezed a
man's under-bust onto a sliver of the breast), so each map is measured on its own and the two compared. The overlay map
may be 1.25 × v2's, and is about 22% over it (p99), the price of bringing a breast's nipple onto a flat chest; the
inverse may be no worse than v2's at all, and is under it. A refit across the sexes says its folds and is not refused
for them: the Sheer Elegance top turns 42 more faces over than with v2 (143 against 101; 122–151 at earlier builds),
its bust now over his nipples instead of 3 cm under them.

**Refit again.** A garment refit across the sexes keeps the fit it was saved with. Women's tops refit for men before
v3 keep the old fit at the bust: refit them again. Men's art from women's overlays updates by itself once the new maps
are on disk. Release note:

> Women's overlays and tops now line up with the nipples on men (TBSE and men's vanilla) instead of sitting about 3 cm
> low. Overlays update by themselves once the new maps have downloaded and the game has restarted; women's tops refit
> for men before this version keep the old fit — refit them again.

### Provenance

| | SHA-256 |
|---|---|
| `bibo_to_tbse_transfer.tif` v3 (11 834 120 bytes) | `559efe148403b12c168f20b41dd614952708933f4404fedc98c7ab79a6a36ca1` |
| `tbse_to_bibo_transfer.tif` v3 (13 027 942 bytes) | `820fd4c1cb38930698a12daba7bd624ddc43a5e417d53436b34a165d14a6f227` |
| `bibo_to_tbse_transfer.tif` v2 (11 798 374 bytes) | `1ad198ed137abf5882ee0e38b3b91d7eb01771af35f7be2cd4eaf41d975e32a0` |
| `tbse_to_bibo_transfer.tif` v2 (12 911 330 bytes) | `701964922589185c3ed4d4b1af824f84bf0c2dd4552488a8e7383a08ce345c23` |
| TBSE `common/1/c0101e0000_top.mdl` | `20bab704895ec6fa32914495dafa77708d5b89b0a9e6abf1d3043656bab1d8d0` |
| Bibo+ `Breasts - Small Clothes/Nude - Small/…/c0201e0000_top.mdl` | `04831b60c8faff1b0b6e2f4ab7eec1465afd2f6ee24292c1aa24b1cf6c415a5d` |

The bake's JSON report lists every input's SHA-256, and `UvTransferMapsLocalTests.Rebake_reproduces_committed_maps`
re-bakes and compares the result byte for byte. The bake can't run on CI, because it needs the two mods and the game.
If either mod changes its UVs, the maps must be baked again and published under a new tag.

### Quality (v2, 2026-10-20)

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

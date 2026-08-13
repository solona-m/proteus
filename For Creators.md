
## For Mod Authors

### How It Works

EZ Mode to just start creating: https://github.com/solona-m/substance-proteus-packager will give you one click mod publishing. Install the mod. Set your colorset rows in the /proteus editor and reexport the mod from penumbra.

Proteus scans for Penumbra mods that contain a `Proteus/` subfolder. At composite time it:

1. Resolves which of your character's textures are active (respecting all other mods in your load order).
2. Loads those textures as a base.
3. Alpha-composites your overlay PNGs on top.
4. Writes the result to Proteus's own internal managed mod and reloads it via Penumbra.

Your mod does **not** need any Penumbra file redirects for the composited textures — Proteus handles that automatically.

### Sidecar Structure

Inside your Penumbra mod folder, create a `Proteus/` subfolder:

```
YourMod/
  meta.json               ← Penumbra mod metadata: default option + any
                            option groups (you already have this)
  Proteus/
    metadata.json         ← Proteus sidecar — required
    OptionA/
      diffuse.png
      normal.png
    OptionB/
      diffuse.png
```

### metadata.json

#### Minimal example — unconditional overlay

```json
{
  "FormatVersion": 1,
  "Name": "My Tattoo",
  "Author": "YourName",
  "Overlays": [
    {
      "MaterialGamePath": "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
      "Diffuse": "overlays/diffuse.png",
      "Normal":  "overlays/normal.png"
    }
  ],
  "ColorTableRows": [
    { "Row": 16, "SubRowA": { "Diffuse": "#FFFFFF", "Emissive": 0.0 } }
  ]
}
```

#### Overlay descriptor fields

| Field | Required | Description |
|-------|----------|-------------|
| `MaterialGamePath` | Yes | The game path of the `.mtrl` file this overlay targets. Proteus reads the material to find the actual texture game paths. |
| `Diffuse` | No | Path to your diffuse overlay PNG, relative to the `Proteus/` folder. |
| `Normal` | No | Path to your normal map overlay PNG. Alpha-composited onto the base normal. |
| `Mask` | No | Path to your mask/specular overlay PNG. |
| `Index` | No | Path to your index texture PNG. Enables per-region coloring. See below. |
| `GenerateDiffuse` | No | Only affects **normal-only** overlays (a `Normal` with no `Diffuse`). Defaults to `true`. Set `false` to apply the normal (and any mask) **without** synthesizing a diffuse tint on the skin. Ignored when a `Diffuse` is present. See [Normal-only overlays](#normal-only-overlays). |
| `SkinToneMask` | No | `0`–`1`. How strongly to keep the character's skin tone out of this overlay (so an opaque overlay looks the same on any skin tone). Omitted = full masking (the default). Set `0` to let skin tone show through fully — use for tattoos/decals that should take the skin's color. See [Skin-tone masking](#skin-tone-masking). |

All paths are relative to the `Proteus/` folder. Subfolders and spaces in names are fine.

#### ColorTableRows

Color table rows control how Proteus tints and illuminates the overlay. Rows are numbered 1–16 to match FFXIV's colorset numbering. Any row not specified defaults to white diffuse and zero emissive (pass-through).

```json
"ColorTableRows": [
  {
    "Row": 16,
    "SubRowA": { "Diffuse": "#FF8844", "Emissive": 0.0 },
    "SubRowB": { "Diffuse": "#FFFFFF", "Emissive": 0.0 }
  }
]
```

- **Diffuse**: hex color (`#RRGGBB` or `#RGB`). Multiplied against the overlay pixel. White = natural colors. Black = invisible. Any other color tints.
- **Emissive**: glow intensity 0–1. Skin cannot glow, so any row with emissive > 0 promotes the overlay to a cloth layer — a shell with its own material — and it renders there. The promotion happens automatically, whether you declare it here or the user sets it in the editor.

Users can override these values at any time from the Proteus status window. Their changes are written back to your `metadata.json` inside their local mod installation.

#### Normal-only overlays

If you provide a `Normal` but no `Diffuse`, Proteus by default **generates a diffuse tint** using the normal's **blue channel** as opacity and Row 16's color. This means:
- The normal detail is applied only where the blue channel has value.
- A matching tint (Row 16's diffuse color — white by default) is applied to the skin diffuse in those same pixels.
- No extra files needed — just ship the normal PNG.

This is ideal for lace, fabric texture detail, or lingerie overlays where you want normal map detail to follow the shape of the garment and color the skin to match.

##### Disabling the auto-diffuse — `"GenerateDiffuse": false`

Some normal-only overlays should change **only** the normal (and mask) and leave the skin's diffuse color untouched — for example a wetness effect (normal + mask) or pure surface relief. Set `"GenerateDiffuse": false` on the overlay to skip the generated diffuse:

```json
{
  "MaterialGamePath": "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
  "Normal": "Wet/normal.png",
  "Mask":   "Wet/mask.png",
  "GenerateDiffuse": false
}
```

With the flag off, Proteus applies your normal and mask over the base textures and does **not** lighten or recolor the skin diffuse. The flag defaults to `true` (existing mods are unaffected) and is ignored when a `Diffuse` is present — in that case your diffuse is composited directly.

> A `Mask`-only overlay (no `Diffuse` **and** no `Normal`) is also supported: the mask PNG's own alpha defines where it applies. Useful for effects carried entirely in the mask/multi map, like wetness specular.

### Skin-tone masking

The skin shader (`skin.shpk`, used by Bibo+ bodies) multiplies the diffuse by the character's **skin tone**. Because Proteus composites your overlay into that diffuse, an opaque overlay would otherwise be darkened/tinted by skin tone — most visible as a bright/white overlay turning beige on darker skin. Proteus masks the skin tone out of opaque overlay pixels so they render at their authored color on any skin tone. The masking is automatically scaled by pixel brightness (bright pixels are fully de-tinted; dark pixels are left alone, since skin tone is invisible on dark color and masking it would slightly increase shine).

Most overlays want this and need no setting. Use `SkinToneMask` when an overlay should instead **take the skin's color** — a tattoo, freckles, blush, a decal, or anything that sits *on* the skin rather than covering it:

```json
{
  "MaterialGamePath": "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
  "Diffuse": "Tattoo/diffuse.png",
  "SkinToneMask": 0
}
```

`0` lets skin tone through fully; `1` (or omitting it) is full masking; values between blend. Users also have a global "Skin-tint suppression" slider in `/proteus` that scales this — your `SkinToneMask: 0` always wins (the skin tone is never masked for that overlay).

### Index Textures

An index texture lets different regions of your overlay use different color table rows. This is how you support recolorable multi-region overlays (e.g. separate colors for bow, lace, and ribbon on the same stocking).

**Channel encoding:**

| Channel | Meaning |
|---------|---------|
| Red | Which color table row pair to use. Value ÷ 17 → row index 0–15. So red=0 → row 1, red=17 → row 2, …, red=255 → row 16. |
| Green | Blend between sub-row A and sub-row B within that pair. 255 = 100% A, 0 = 100% B, 128 = 50/50. |

Pixels not mapped to a row that exists in `ColorTableRows` use the default white pass-through.

Create your index texture as you would for any gear mod.

### Penumbra Option Groups

If your mod has multiple options (style variants, independent pieces, etc.) you need both a Penumbra group in `meta.json` **and** the matching `OptionGroups` in your Proteus `metadata.json`. The `PenumbraGroupName` must exactly match the group's `Name`.

**Penumbra `meta.json`** — since Penumbra's FileVersion 4 the whole mod layout lives in this one file at the mod root. Groups are entries in the `Groups` array (they used to be separate `group_001_style.json` files), and the default option is the `DefaultData` object (it used to be `default_mod.json`):
```json
{
  "FileVersion": 4,
  "Identifier": "a1b2c3d4-0000-4000-8000-000000000001",
  "Name": "My Stockings",
  "Author": "YourName",
  "Description": "",
  "Version": "1.0",
  "Website": "",
  "ModTags": [],
  "DefaultData": { "Files": {}, "FileSwaps": {}, "Manipulations": [] },
  "Groups": [
    {
      "Type": "Single",
      "Id": "a1b2c3d4-0000-4000-8000-000000000002",
      "Name": "Style",
      "Description": "",
      "Image": "",
      "Page": 0,
      "DefaultSettings": 0,
      "Options": [
        { "Id": "a1b2c3d4-0000-4000-8000-000000000003", "Name": "Roses",   "Description": "", "Files": {}, "FileSwaps": {}, "Manipulations": [] },
        { "Id": "a1b2c3d4-0000-4000-8000-000000000004", "Name": "Stripes", "Description": "", "Files": {}, "FileSwaps": {}, "Manipulations": [] }
      ]
    }
  ]
}
```

`"Type": "Single"` means only one option is active at a time. The options list just needs the names — all texture work is handled by Proteus, so `Files` stays empty. `Id` values are GUIDs Penumbra uses to keep a user's saved selections stable across mod updates; keep them the same when you re-export.

**Group order is the `Groups` array order.** Where two groups overlay the same skin, the one earlier in the array wins. (Before FileVersion 4 this was the `group_NNN` filename number — same meaning, new home. Proteus still reads the old layout for mods that were never migrated.)

The easiest way to get all this right is to build the group in Penumbra's own mod editor, or let the Substance Painter packager write it for you.

**Proteus metadata.json:**
```json
{
  "FormatVersion": 1,
  "Name": "My Stockings",
  "Author": "YourName",
  "OptionGroups": [
    {
      "PenumbraGroupName": "Style",
      "Options": [
        {
          "Name": "Roses",
          "Overlays": [
            {
              "MaterialGamePath": "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
              "Diffuse": "Roses/diffuse.png",
              "Normal":  "Roses/normal.png",
              "Index":   "Roses/index.png"
            }
          ],
          "ColorTableRows": [
            { "Row": 16, "SubRowA": { "Diffuse": "#FFFFFF" } }
          ]
        },
        {
          "Name": "Stripes",
          "Overlays": [
            {
              "MaterialGamePath": "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
              "Diffuse": "Stripes/diffuse.png"
            }
          ],
          "ColorTableRows": [
            { "Row": 16, "SubRowA": { "Diffuse": "#FFFFFF" } }
          ]
        }
      ]
    }
  ]
}
```

Each option can have its own `ColorTableRows`. If an option omits `ColorTableRows`, it inherits the top-level `ColorTableRows` if present.

#### Independent toggleable pieces

To let users enable pieces independently (e.g. top and bottom separately), use **two separate groups**, each with a `"None"` first option:

```json
"OptionGroups": [
  {
    "PenumbraGroupName": "Bra",
    "Options": [
      { "Name": "None", "Overlays": [] },
      { "Name": "Bra",  "Overlays": [ { ... } ] }
    ]
  },
  {
    "PenumbraGroupName": "Panties",
    "Options": [
      { "Name": "None",    "Overlays": [] },
      { "Name": "Panties", "Overlays": [ { ... } ] }
    ]
  }
]
```

Create a matching Penumbra group JSON for each group.

### Simple Unconditional Overlay

If your mod has no options at all, use the top-level `Overlays` field instead of `OptionGroups`. The overlays apply unconditionally whenever the mod is enabled.

### Masks

Masks let users **carve away** parts of your overlays so the skin underneath (or a lower-priority mod) shows through — for example, a bodysuit that can hide its sleeves, gloves, or a chest panel.

Masks are **convention-based** — there is nothing to add to `metadata.json`. You need two things:

1. A Penumbra **multi-select** group named exactly **`Masks`** (a `Groups` entry in `meta.json` with `"Type": "Multi"`).
2. A `Proteus/Masks/` subfolder containing one **grayscale PNG per option**, named to match the option exactly: option `Sleeves` → `Proteus/Masks/Sleeves.png`.

```
YourMod/
  meta.json                ← contains a Multi group named "Masks"
  Proteus/
    metadata.json
    Masks/
      Sleeves.png
      Chest.png
```

How a mask image is read — a mask sets the overlay's opacity **explicitly**, using two channels:

- **RGB (grayscale) = the target opacity.** Where the mask takes effect, the overlay's coverage is *set to* this value: black (0) → fully transparent (skin shows), white (255) → fully opaque, grays → that exact opacity. It's an explicit set, not a fade of the existing coverage — so a white patch can **add** opacity, forcing an opaque band even where the overlay was sheer.
- **Alpha = how strongly the target is applied.** White alpha (255) → fully apply the target opacity above; black alpha (0) → the mask does nothing there and the overlay keeps its own coverage; grays blend between the two. Think of alpha as "where this mask has any say at all," and RGB as "what opacity it forces there."

So to punch a clean hole, paint the hole region **alpha = white, RGB = black**; to force a patch fully opaque, paint it **alpha = white, RGB = white**; leave everywhere else **alpha = black**.

- **A mask only acts where the overlay is already visible.** The added opacity is gated by the overlay's own coverage: where the overlay is fully transparent (above where a stocking ends, or the holes of a fishnet) the mask has no effect — it can boost a sheer area to opaque, but it can never paint opacity onto bare skin. You don't need to carefully avoid those areas in your mask.
- A mask applies to **every overlay in the same mod** (all groups/options), at full UV resolution. Author your mask in the same UV space as your overlays.
- When a user selects **several masks at once**, masks **earlier in the group's `Options` list win** where they overlap — the top mask sets the opacity in its alpha region, and lower masks only show through where the higher one's alpha leaves room.

Because `Masks` is just a Penumbra group, the user's selection is saved and restored by Glamourer designs automatically, and toggling a mask re-composites immediately.

### Distributing Your Mod

Pack your mod folder as a `.zip` and rename the extension to `.pmp`. Penumbra imports `.pmp` files directly. Include everything in the mod root:

```
YourMod.pmp (rename from .zip)
  meta.json               ← including "Groups", if you have option groups
  Proteus/
    metadata.json
    OptionA/
      diffuse.png
      ...
```

### Sample Mods

The `samples/` directory in this repository contains two ready-to-study examples:

- **ExampleOverlayMod** — simple unconditional overlay with diffuse, normal, and mask.
- **MultiOptionOverlayMod** — single-select style picker plus an independently toggleable piece, demonstrating `OptionGroups`, per-option `ColorTableRows`, index textures, and the normal-only overlay pattern.

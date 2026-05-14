# Proteus

Proteus is a Dalamud plugin for FFXIV that composites overlay textures onto your character's skin and equipment at runtime. Mod authors ship small PNG overlays alongside their Penumbra mods; Proteus blends them onto the base textures every time you change options, without touching the original mod files.

**Requires:** [Penumbra](https://github.com/xivdev/Penumbra)

If you need help, please join https//discord.gg/solona and ask in the #help channel.

---

## For Users

### Installation

Add this repo to your experimental tab under /xlplugins https://raw.githubusercontent.com/solona-m/plugins/main/repo.json
Save, then find Proteus in the main /xlplugins window.

Install some overlay mods made for proteus, chose your options and your character will update.


### Status Window

Open the status window with `/proteus`.

The status window lists every Penumbra mod that contains a Proteus sidecar.

| Column | What it does |
|--------|-------------|
| Checkbox | Enable or disable Proteus compositing for that mod. |
| Mod name | The mod's display name. |
| Pri | Priority within Proteus's composite stack. Lower numbers go first (bottom layer). Drag to change; Ctrl-click to type. |
| Colors | Opens the color editor for that mod. |
| Overlays | How many overlay descriptors are active for the current option selection. |

The bottom of the window shows the result of the last composite: how many textures were patched, how many mods contributed, and when it last ran.

Click **Refresh** to force a re-composite manually. Proteus also re-composites automatically whenever you change a Penumbra option or mod setting.

### Color Editor

Click **Colors** next to a mod to open its color editor. This lets you tint the overlay and control emissive glow on a per-region basis without editing any files.

The editor shows up to 16 color table rows. Rows map to regions defined by the mod's index texture (if it has one). Row 16 is always the fallback color used when there is no index texture.

Each row has two sub-rows:
- **A** — applies where the index texture's green channel is 255.
- **B** — applies where the green channel is 0. Values in between blend smoothly.

For each sub-row:
- **Diffuse** (color swatch) — multiplicative tint applied to the overlay. White (`#FFFFFF`) shows the overlay's natural colors. Any other color tints it. The user can recolor a plain grayscale stocking by picking a color here.
- **Emissive** (0–1 slider) — how strongly the overlay glows. Requires `skin.shpk` (most Bibo+ skin materials). The emissive color defaults to a warm gold; it can be authored per-mod in the metadata.

Changes are saved immediately to the mod's `metadata.json` and trigger a re-composite.

---

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
  meta.json               ← Penumbra mod metadata (you already have this)
  default_mod.json        ← Penumbra default option (you already have this)
  group_001_style.json    ← Penumbra option group (if your mod has options)
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
- **Emissive**: glow intensity 0–1. When any row has emissive > 0, Proteus also patches the material's shader key and color table to enable the emissive pass.

Users can override these values at any time from the Proteus status window. Their changes are written back to your `metadata.json` inside their local mod installation.

#### Normal-only overlays

If you provide a `Normal` but no `Diffuse`, Proteus automatically generates a white diffuse using the normal's **blue channel** as opacity. This means:
- The normal detail is applied only where the blue channel has value.
- A matching white tint is applied to the skin diffuse in those same pixels.
- No extra files needed — just ship the normal PNG.

This is ideal for lace, fabric texture detail, or lingerie overlays where you want normal map detail to follow the shape of the garment without a separate diffuse mask.

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

If your mod has multiple options (style variants, independent pieces, etc.) you need both a Penumbra group JSON at the mod root **and** the matching `OptionGroups` in your Proteus `metadata.json`. The `PenumbraGroupName` must exactly match the `Name` in the group JSON.

**Penumbra group JSON** (`group_001_style.json`):
```json
{
  "Version": 0,
  "Name": "Style",
  "Description": "",
  "Image": "",
  "Page": 0,
  "Priority": 0,
  "Type": "Single",
  "DefaultSettings": 0,
  "Options": [
    { "Name": "Roses",   "Description": "", "Files": {}, "FileSwaps": {}, "Manipulations": [] },
    { "Name": "Stripes", "Description": "", "Files": {}, "FileSwaps": {}, "Manipulations": [] }
  ]
}
```

`"Type": "Single"` means only one option is active at a time. The options list in the Penumbra JSON just needs the names — all texture work is handled by Proteus, so `Files` stays empty.

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

### Distributing Your Mod

Pack your mod folder as a `.zip` and rename the extension to `.pmp`. Penumbra imports `.pmp` files directly. Include everything in the mod root:

```
YourMod.pmp (rename from .zip)
  meta.json
  default_mod.json
  group_001_style.json    ← only if you have option groups
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

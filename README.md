# Proteus

Proteus is a Dalamud plugin for FFXIV that composites overlay textures onto your character's skin and equipment at runtime. Mod authors ship small PNG overlays alongside their Penumbra mods; Proteus blends them onto the base textures every time you change options, without touching the original mod files.

**Requires:** [Penumbra](https://github.com/xivdev/Penumbra)

If you need help, please join https//discord.gg/solona and ask in the #help channel. This is still new but I'll work to fix any bugs asap!

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
- **Opacity** (-100 to 100 slider) — 0 is the mod default. -100 is transparent. 100 is fully opaque

Changes are saved immediately to the mod's `metadata.json` and trigger a re-composite.

---

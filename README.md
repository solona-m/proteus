# Proteus

Proteus is a Dalamud plugin for FFXIV that composites overlay textures onto your character's skin and equipment at runtime. Mod authors ship small PNG overlays alongside their Penumbra mods; Proteus blends them onto the base textures every time you change options, without touching the original mod files.

Overlays can render two ways: painted into your skin, or as a **second skin** — a copy of your body's mesh drawn as gear, so an overlay can use sphere maps, metalness and animated glow that skin materials can't do.

**Requires:** [Penumbra](https://github.com/xivdev/Penumbra) and [Glamourer](https://github.com/Ottermandias/Glamourer)

Glamourer is what lets Proteus refresh your character without the redraw flicker, bind overlay setups to your designs, and host second skins on an invisible item.

If you need help, please look at this [Troubleshooting Guide](TROUBLESHOOTING.md).
Then, join https://discord.gg/solona and ask in the #help channel. This is still new but I'll work to fix any bugs asap!

If you're a creator and want to make mods for Proteus, read the [Creator's Guide](For%20Creators.md).

---

## For Users

### Installation

Add this repo to your experimental tab under /xlplugins https://raw.githubusercontent.com/solona-m/plugins/main/repo.json
Save, then find Proteus in the main /xlplugins window.

Install some overlay mods made for Proteus, choose your options and your character will update.

### Status Window

Open the status window with `/proteus`. It has four tabs, and the last composite's result (textures patched, mods used, how long ago) always shows along the bottom.

#### Mods

Lists every Penumbra mod that contains a Proteus sidecar. Click any column header to sort by it.

| Column | What it does |
|--------|-------------|
| On | Enable or disable Proteus compositing for that mod. |
| Mod | The mod's display name. Click it to jump to the mod in Penumbra. |
| Pri | Priority within Proteus's composite stack. Lower numbers go first (bottom layer). Drag to change; Ctrl-click to type. |
| Colors | Opens the color editor for that mod. |
| Bodies | Which body types to synthesize this mod for — All bodies, bibo+gen3 (default), or Off. |
| Skindent | Ambient-occlusion shadow and normal indent for this mod's strap edges. "Pack" follows what the mod asked for; On/Off overrides it. |

Click **Refresh** to force a re-composite manually. Proteus also re-composites automatically whenever you change a Penumbra option or mod setting, change gear, or change race/body.

#### Bindings

Ties your whole Proteus setup — which mods are on, their priorities and options, and all their colors — to a Glamourer design. Tick **Bind Proteus state to Glamourer designs** to turn it on.

Saving a design captures the current Proteus state against it. Applying that design later restores it. Colors and layer settings are restored as a live overlay, so the mod's own files are never rewritten.

While a binding is active, edits in the color editor preview immediately but are **not** saved until you press **Update binding** — which folds everything currently on screen back into that design.

#### Create

Authors a basic overlay mod without leaving the game. Give it a name, an author, and pick at least one texture (diffuse, mask, normal, or index). The material target auto-fills from the body you're currently wearing; you can pick another equipped material from the dropdown or type a path by hand. Proteus writes a new Penumbra mod and opens it.

Texture slots the chosen material can't actually use are greyed out.

#### Settings

| Setting | What it does |
|---------|-------------|
| Enabled | Master switch. Off clears Proteus's output and redraws you without it. |
| Disable auto redraw | Stop Proteus refreshing your character after a composite. |
| In-place reload | Refresh textures through Glamourer instead of a full redraw, avoiding the despawn/respawn flicker. On by default. |
| Enable Compression | Block-compress baked textures, cutting them to about a quarter of their size on disk and in VRAM. On by default. |
| Sharp alpha | Experimental. Keeps sphere maps and metalness working in gpose, at the cost of harder edges on sheer fabrics. |
| Host on invisible glasses | Lets the second skin ride the facewear slot so your rings stay free. |
| Host on the Emperor's New Ring | Fallback host when nothing you're wearing can carry the second skin. Never takes a ring you're already wearing. |
| Skin-tint suppression | How strongly overlays resist being tinted by your skin tone. |
| Ambient occlusion / Shadow softness / Skindenting | Global strength of the contact shadow and normal indent around strap edges. |
| Texture cache (MB) | How much decoded texture data to keep in memory between composites. |
| Hide Connector Meshes | Skips a body's joint reinforcement rings on the second skin. Only needed for Neolithe. |

Three buttons here are worth knowing about:

- **Restore changed accessory** — forces a full redraw if a second skin ever gets stuck on a ring or bracelet after disabling or swapping.
- **Clear texture cache** — use when a texture edit isn't showing up, e.g. you re-exported an overlay at the same size.
- **Glow Effect Textures** — opens the folder Proteus reads animated-glow scroll maps from. Drop images in it and they appear in every gear overlay's Effect dropdown. Hover the button to see the full path.

### Color Editor

Click **Colors** next to a mod to open its color editor in its own window. This lets you tint overlays, control glow, and set material properties per region without editing any files.

Each active overlay option gets its own tab along the top, ordered by how they stack. Drag a tab to restack it. If the mod uses masks, a **Masks** tab is pinned at the top — masks always render over everything else.

#### Rendering mode

Proteus works out how each overlay should render from the features you actually use, and shows the result as a **Rendering as** badge:

- **Skin (painted)** — composited into your skin. The default.
- **Cloth** — a second skin using sphere maps, metalness or specular.
- **Animated glow** — a second skin with a scrolling glow effect.

You don't have to pick — setting a sphere map turns it into Cloth on its own. If you need to force it, open **Advanced** and pin a mode. **Reset to defaults** there restores the mod's authored settings.

#### Rows

The editor shows up to 16 color table rows. Rows map to regions defined by the mod's index texture (if it has one). Row 16 is the fallback color used when there is no index texture. Rows the index texture never selects are dimmed.

Press **Glow** on any sub-row to light that region up on your character, so you can find which row controls what.

Each row has two sub-rows:
- **A** — applies where the index texture's green channel is 255.
- **B** — applies where the green channel is 0. Values in between blend smoothly.

For each sub-row:
- **Diffuse** (color swatch) — multiplicative tint applied to the overlay. White (`#FFFFFF`) shows the overlay's natural colors. Any other color tints it. You can recolor a plain grayscale stocking by picking a color here.
- **Emissive** (0–1 slider) — how strongly the overlay glows, with its own color.
- **Opacity** (-100 to 100 slider) — 0 is the mod default. -100 is transparent. 100 is fully opaque.
- **Sphere map / Metalness / Roughness / Specular** — available on Cloth. Setting any of them switches the overlay to a second skin.

Rows and sub-rows can be copied and pasted between each other.

Changes apply on screen immediately and re-composite about a second after you stop editing. They're saved to the mod's `metadata.json` — unless a design binding is active, in which case they belong to that design until you press **Update binding**.

### Acknowledgements
Thank you so much to Sebby for teaching me about how to use pixel-based image mapping instead of baking and for releasing the baked maps under the MIT license via the loose texture compiler.

---

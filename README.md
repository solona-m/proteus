# Proteus

<!--i18n-->
**English** · [日本語](docs/README.ja.md) · [Deutsch](docs/README.de.md) · [Français](docs/README.fr.md) · [简体中文](docs/README.zh.md) · [한국어](docs/README.ko.md) · [Español](docs/README.es.md) · [Русский](docs/README.ru.md)
<!--/i18n-->

Proteus is a Dalamud plugin for FFXIV that composites overlay textures onto your character's skin and equipment at runtime. Mod authors ship small PNG overlays alongside their Penumbra mods; Proteus blends them onto the base textures every time you change options, without touching the original mod files. Proteus can import Proteus-enabled pmp files, onion overlay omp files, and Atramentum Luminis glow tattoos.

Overlays can render two ways: painted into your skin, or as a **second skin** — a copy of your body's mesh drawn as gear, so an overlay can use sphere maps, metalness and animated glow that skin materials can't do.

- **Wear mods without giving up a gear slot.** A second skin has to be drawn as an item, but Proteus hides it on something you aren't using — invisible glasses, or a ring you don't have on, or appends it to your equipped accessories — so your actual glamour is untouched. There's nothing to set up; it picks a host on its own and never takes an item you're wearing.
- **Add toggles to any part of any mod, not just Proteus ones.** When a mod welds a bow, a collar or a strap into geometry its author never made optional, the **Toggles** tab can split that piece out and give it a real switch.


If you need help, please look at this [Troubleshooting Guide](TROUBLESHOOTING.md).
Then, join https://discord.gg/solona and ask in the #help channel. This is still new but I'll work to fix any bugs asap!

If you're a creator and want to make mods for Proteus, read the [Creator's Guide](For%20Creators.md).

---

## For Users

### Installation

Add this repo to your experimental tab under /xlplugins https://dl.solona.info/repo.json
Save, then find Proteus in the main /xlplugins window.

> Already installed from `raw.githubusercontent.com/solona-m/plugins/main/repo.json`? That still
> works and always will, but the new url will be more reliable and not subject to github throttles.

Install some overlay mods made for Proteus, choose your options and your character will update.

### Status Window

Open the status window with `/proteus`. It has seven tabs, and the last composite's result (textures patched, mods used, how long ago) always shows along the bottom.

#### Mods

Lists every Penumbra mod that contains a Proteus sidecar. Click any column header to sort by it.

| Column | What it does |
|--------|-------------|
| On | Enable or disable Proteus compositing for that mod. |
| Mod | The mod's display name. Click it to jump to the mod in Penumbra. |
| Pri | Priority within Proteus's composite stack. Lower numbers go first (bottom layer). Drag to change; Ctrl-click to type. |
| Colors | Opens the color editor for that mod. |
| Skindent | Ambient-occlusion shadow and normal indent for this mod's strap edges. "Pack" follows what the mod asked for; On/Off overrides it. |

Click **Refresh** to force a re-composite manually. Proteus also re-composites automatically whenever you change a Penumbra option or mod setting, change gear, or change race/body.

#### Bindings

Ties your whole Proteus setup — which mods are on, their priorities and options, and all their colors — to a Glamourer design. Tick **Bind Proteus state to Glamourer designs** to turn it on.

Saving a design captures the current Proteus state against it. Applying that design later restores it. Colors and layer settings are restored as a live overlay, so the mod's own files are never rewritten.

While a binding is active, edits in the color editor preview immediately but are **not** saved until you press **Update binding** — which folds everything currently on screen back into that design.

#### Create

Authors a basic overlay mod without leaving the game. Give it a name, an author, and pick at least one texture (diffuse, mask, normal, or index). The material target auto-fills from the body you're currently wearing; you can pick another equipped material from the dropdown or type a path by hand. Proteus writes a new Penumbra mod and opens it.

Texture slots the chosen material can't actually use are greyed out.

#### Import

Takes a mod pack and converts it to a Proteus mod. Three types are supported:

**Regular Penumbra mods (`.pmp`)** — wear parts of a normal gear mod without using a gear slot, and get the advanced colour-table features on top.

It stays an ordinary Penumbra mod: Penumbra still owns whether it's on and which of its options are selected. What changes is that its pieces are drawn on Proteus's carrier item instead of on a real equipment slot, so your glamour is untouched.

The useful side effect is that **you can wear several of its options at once**. Normally two options in the same group both claim the same model path and the game can only show one, so a pack physically can't offer "this piece *and* that piece" — after importing, each selected piece is added on its own.

- Pieces arrive switched **off**. Tick the ones you want in Penumbra afterwards; nothing is worn until you do.
- A pack that is *already* a Proteus mod is installed exactly as its author built it. Nothing is converted.
- Skin is removed during import. This is ideal for acccessories like jewelry, piercings and jackets. If you import a shirt, the shirt will only fit if your equipped chest slot is the same size.

**Onion overlay packs (`.omp`)** — wear its layers as Proteus overlays you can recolour and restack, make glow, etc.

A pack that ships the same artwork in several UV layouts (bibo, gen3, vanilla) becomes a single-select **Body UV** group in Penumbra, pre-set to the layout matching the body you're wearing, so only one composites at a time. Layer opacity is baked into the image; a layer with a blend mode other than Normal is skipped and said so, because Proteus composites alpha-over only. Onion's own option groups and race filters aren't imported.

**Atramentum Luminis glow tattoos (`.ttmp2`)** — wear the glow as a Proteus overlay you can recolour and dim, with no shader mod needed.

Atramentum Luminis packs hide their glow in a texture's alpha channel, and without that shader mod installed they render nothing at all. Proteus reads the glow out and rebuilds it as an ordinary overlay: the panels the artist marked become a second skin, and the artwork itself drives an animated-glow material, so the neon keeps its own colours per pixel. The **Glow** dial in Colors then does what you'd expect, and you can bind the whole thing to a design like any other overlay.

- The pack's own body texture comes in too, as a separate **Author's skin** option, and it's on by default — it carries the parts of a tattoo that don't glow, and it keeps your own skin tone rather than the author's. Untick it in Penumbra if you only want the glow.
- Proteus recognises bibo and gen3 outright. For any other body it paints onto the one you're wearing without resizing, and says so; the **Body** picker overrides it if the pack was made for something else.
- There's no race or sex filter, so the mod paints any character on a body with the same material. Turn it off in Penumbra for characters it wasn't painted for.
- Eye glow isn't imported today, but message if you're interested.

#### Export

Saves one of your Proteus mods as a Penumbra mod pack (`.pmp`) to share. Pick the mod from the dropdown, press **Export**, and choose where to put it — the file name is filled in from the mod name, and the dialog opens on your desktop the first time and wherever you saved last after that.

The pack is a straight copy of the mod folder, so nothing is lost: options, colour tables, masks, glow effects and gear layers all come along, and the recipient's Proteus picks it up as soon as Penumbra installs it. Disabled mods can be exported too.

#### Toggles

Takes a piece of geometry out of a mod's model and puts it behind an on/off switch — a bow, a collar, a strap that the author welded into an always-on mesh. This works on **any** mod you have installed, not just Proteus ones.

The switch is written into the mod itself as an ordinary Penumbra option, so it shows up in that mod's own settings and **keeps working with Proteus turned off**.

Pick a mod, then one of its models. The parts of that model are listed with their triangle counts, and shown in a viewport beside them — click a piece to switch it on or off, drag to turn the model, shift-drag to move it, scroll to zoom. Tick the parts one switch should hide, give it a name, and press **Make a switch from the ticked parts**. Queue up as many as you want, then **Write the switches into the mod**.

Things worth knowing:

- **Ten switches per item.** That's the game's limit, not Proteus's. If an author has already used them all, the tab says so and won't let you add more.
- **Equipment and accessories only.** There's nothing to attach a switch to on other model types.
- **Parts the author already made optional can't take a second switch**, and the tab marks them.
- **It's reversible.** The original models are kept, so **Undo — restore the original models** puts the mod back exactly as it was and removes the option group.
- If an item has several model files whose parts are arranged differently, Proteus edits only the ones the switch lands on correctly and tells you which it left alone, rather than guessing and hitting the wrong geometry.

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

#### Advanced

Below the rows, **Advanced** holds the settings that apply to the whole mod rather than to one row:

| Setting | What it does |
|---------|-------------|
| Force render mode | Pins Skin / Cloth / Animated glow instead of letting the features pick. **Back to auto** releases it. |
| Bodies | Which body types this mod is baked onto — **All bodies** (sibling body bibo↔gen3/Eve, plus vanilla gen2), **bibo+gen3** (the sibling body only — the default), or **Off**. Applies to the whole mod, and it's a global setting: design bindings don't capture it. |
| Reset to defaults | Restores this option's colors, glow and mode to the settings Proteus first recorded for the mod. Hold Ctrl to arm it. |

If a mod has no active option there are no colors to show, but **Advanced** still appears so **Bodies** stays reachable.

#### Rows

The editor shows up to 16 color table rows. Rows map to regions defined by the mod's index texture (if it has one). Row 16 is the fallback color used when there is no index texture. Rows the index texture never selects are dimmed.

Press **Glow** on any sub-row to light that region up on your character, so you can find which row controls what.

Each row has two sub-rows:
- **A** — applies where the index texture's green channel is 255.
- **B** — applies where the green channel is 0. Values in between blend smoothly.

For each sub-row:
- **Diffuse** (color swatch) — multiplicative tint applied to the overlay. White (`#FFFFFF`) shows the overlay's natural colors. Any other color tints it. You can recolor a plain grayscale stocking by picking a color here.
- **Emissive** (0–1 slider) — how strongly the overlay glows, with its own color. Skin can't glow, so setting this switches the overlay to a cloth layer, the same way a sphere map does.
- **Opacity** (-100 to 100 slider) — 0 is the mod default. -100 is transparent. 100 is fully opaque.
- **Sphere map / Metalness / Roughness / Specular** — available on Cloth. Setting any of them switches the overlay to a second skin.

Rows and sub-rows can be copied and pasted between each other.

Changes apply on screen immediately and re-composite about a second after you stop editing. They're saved to the mod's `metadata.json` — unless a design binding is active, in which case they belong to that design until you press **Update binding**.

### Acknowledgements
Thank you so much to Sebby for teaching me about how to use pixel-based image mapping instead of baking and for releasing the baked maps under the MIT license via the loose texture compiler.

---

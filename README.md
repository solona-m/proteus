# Proteus

<!--i18n-->
**English** · [日本語](docs/README.ja.md) · [Deutsch](docs/README.de.md) · [Français](docs/README.fr.md) · [简体中文](docs/README.zh.md) · [한국어](docs/README.ko.md) · [Español](docs/README.es.md) · [Русский](docs/README.ru.md)
<!--/i18n-->

Proteus is a Dalamud plugin for FFXIV that offers the following capabilities:

- **Composite overlay textures.** Adds overlays onto your character's skin and equipment at runtime, allowing you to toggle them on and off like regular mods. Overlays are baked to all compatible UVs such as bibo/gen3/gen2/vanilla.
- **Have gear refit itself as you put it on.** Pick your body and size once per Penumbra collection, and chest, legs, hands and feet gear, and accessories that cover the body, such as a jacket on a necklace, are refitted to it the moment you wear them. 
- **Studio.** Provides blender-like tools to manipulate meshes right on your character to fix clipping, add wind effects, and batch upscale gear to many bodies in seconds.
- **Wear mods without giving up a gear slot.** A second skin has to be drawn as an item, but Proteus hides it on something you aren't using — invisible glasses, or a ring you don't have on, or appends it to your equipped accessories — so your actual glamour is untouched. There's nothing to set up; it picks a host on its own and never takes an item you're wearing.
- **Add toggles to any part of any mod, not just Proteus ones.** When a mod welds a bow, a collar or a strap into geometry its author never made optional, the **Studio** tab can split that piece out and give it a real switch.
- **Wear hair mods made for another race.** Many hair mods are made for one race only, so on anyone else the game shows its own hairstyle instead. Proteus bends the mod's hairstyle onto your race's head and adds it to the same mod, physics included, so it just works.
- **Make modded hair fit under hats.** Proteus presses the hair a hat would cover flat against your head, so a hat stops going straight through it. It's on by default and only acts while you're wearing a hat.


If you need help, please look at this [Troubleshooting Guide](TROUBLESHOOTING.md).
Then, join https://discord.gg/solona and ask in the #help channel. This is still new but I'll work to fix any bugs asap!

If you're a creator and want to make mods for Proteus, read the [Creator's Guide](For%20Creators.md).

How AI was and wasn't used to make Proteus (There is no AI used at runtime in your game): [AI declaration](AI-DECLARATION.md)

---

## For Users

### Installation

Add this repo to your experimental tab under /xlplugins https://dl.solona.info/repo.json
Save, then find Proteus in the main /xlplugins window.

> Already installed from `raw.githubusercontent.com/solona-m/plugins/main/repo.json`? That still
> works and always will, but the new url will be more reliable and not subject to github throttles.

Install some overlay mods made for Proteus, choose your options and your character will update.

### Chat commands

| Command | What it does |
|---------|-------------|
| `/proteus` | Opens or closes the status window. |
| `/proteus config` | Opens the status window on Settings. |
| `/proteus refresh` | Recomposites now, the same as the **Refresh** button. Handy in a macro. |
| `/proteus refresh full` | Rebuilds everything first, then recomposites, the same as Shift-clicking **Refresh**. Slower than a plain refresh. |
| `/proteus refresh silent` | Either of the above without the chat message. `full` and `silent` can be combined. |

### Status Window

Open the status window with `/proteus`. It has seven tabs, and the last composite's result (textures patched, mods used, how long ago) always shows along the bottom.

After an update, a **What's new** window opens once to show what changed. It doesn't open again until there's something new.

#### Mods

Lists every Penumbra mod that contains a Proteus sidecar. Click any column header to sort by it. Hover a mod's name to see the picture its author shipped with it.

| Column | What it does |
|--------|-------------|
| On | Enable or disable Proteus compositing for that mod. |
| Mod | The mod's display name. Click it to jump to the mod in Penumbra. |
| Pri | Priority within Proteus's composite stack. Lower numbers go first (bottom layer). Drag to change; Ctrl-click to type. |
| Preset | The saved look this mod is wearing. Pick another to switch to it without opening the color editor. Shows — for a mod that has no presets. |
| Colors | Opens the color editor for that mod. Skindent, the contact shadow and indent at strap edges, is set there under **Effects**. |

Click **Refresh** to force a re-composite manually, or Shift-click it to rebuild everything first. Proteus also re-composites automatically whenever you change a Penumbra option or mod setting, change gear, or change race/body.

#### Studio

Edits the models of **any** mod you have installed, not just Proteus ones, including the garments of packs you imported. You can reshape clothing so your body stops poking through it, move or resize pieces of it, refit it to another body, paint where the wind sways it, or split a piece out behind its own on/off switch. Every change is written into the mod's own files, so it **keeps working with Proteus turned off** and travels with the mod if you export it.

Pick a mod, then one of its models. The tab opens on the chest piece you're wearing (or your legs if there's none), and mods and models you're wearing are listed first, in green. Clicking a garment on your character opens its mod and model, vanilla or modded. Every list in the tab can be searched: type part of a name to narrow it. When an item ships a separate model per race, each one is named with its race so you can tell them apart.

The tools are on the left and the chosen tool's settings beside them, with the model's parts behind a button at the top of the settings: click it to open the part list as a column beside them, pushing the model right so it stays in view. Tick **Show model view** for a third column with the model on the right.

**Vanilla gear** you're wearing is listed too, marked *(vanilla)*. It opens as it is, under every tool, and nothing is saved until you change it: your first change copies it into a mod of its own named after the item, *<Item> — Proteus edit*, and later changes go there too. Delete that mod in Penumbra and it stays gone; opening the piece again doesn't bring it back. With **Body size** chosen, saving a refit makes a mod of its own instead, see [Body size](#body-size).

The tools:

| Tool | What it does |
|------|-------------|
| Toggle Parts | Picks pieces of the model and gives them an on/off switch. See [Part switches](#part-switches) below. |
| Pull out | Pushes the surface outwards, so a body that pokes through a piece of clothing is covered again. Cloth moves straight away from the skin beneath it. |
| Push in | The same brush in reverse, for clothing that stands too far off the body. |
| Relax | Smooths the surface: lumps the clothing came with, or a pull that came out rough. Like 3ds Max's relax it shrinks, so curves flatten and cloth can sink toward the body. |
| Smooth | Irons out lumps and ripples **without** shrinking: unlike Relax, curves keep their shape and cloth stays off the body. **Strength** sets how quickly it works. |
| Grab | Press on the cloth and drag: the cloth around the brush follows the mouse like rubber, strongest in the middle, with no hard edge. Brush size sets how much cloth comes along, and Ctrl+Z takes a grab back. |
| Bridge | Paint across a hollow, like the cleft between cheeks or a crease, to stretch the cloth straight over it instead of following the body down into it. It only ever lifts. |
| Wind | Paints how much the game's wind sways the garment, shown in red. Hold Ctrl, or set **Amount** to 0%, to erase. |
| Move | Drags a part, or a few polygons, with arrows: one arrow moves it along that axis, a square across that plane. See [Moving, turning and scaling](#moving-turning-and-scaling) below. |
| Rotate | Turns a part about its centre: drag a coloured ring to turn it about that axis, or the outer ring to turn it about your view. |
| Scale | Grows, shrinks or stretches a part about its centre: drag a coloured handle to stretch it along that axis, or press on the part and drag right to grow it, left to shrink it. |
| Body size | Refits the garment to another body size, or to another body mod. See [Body size](#body-size) below. |

##### Brushing

- **You paint straight onto your character**, and a garment shows each stroke as you paint it. Hold **Alt** to move the camera. Tick **Show model view** to paint on the model in the window instead: drag from the background to turn it, shift-drag to move it, scroll to zoom.
- **Brush size** goes down to 1 mm. `[` and `]` change it, and Shift gives finer steps. The effect is strongest in the middle and fades to nothing at the edge; the brush's reach is drawn as a rainbow on the cloth so you can see it. Brushes work at the same pace at any frame rate. For **Pull out** and **Push in**, **Strength** is how far the middle of the brush moves the surface for each quarter second you hold it; for the other brushes it's how quickly they work. Small is usually right: clothing only has to clear the body by a fraction of a millimetre, and you can always paint the same place again.
- **Limit** is the furthest the brushes may move any point from where the mod's author put it. Painting stops at the limit, so a held brush can't balloon the garment. Raise it for a bigger change.
- **Mirror left/right** paints both sides at once. Move, Rotate and Scale mirror too.
- **Skin never moves.** To keep a brush off anything else, lock it: Shift-click the part on your character or the model, or untick it in the part list. A locked part is also hidden, on the model and on your character, so clicks pass through it to what's underneath — lock a skirt to reach the underwear beneath it. Seams welded to a locked part hold too.
- **Hair works too.** Hair, face, ears and tail redraw when you let go, rather than showing the stroke as you paint.
- **Apply to other sizes** copies the edit onto the mod's other files for the same garment, matched by where they sit on it.

##### Saving and undo

- Each stroke saves into the mod a moment after you let go. The first save copies the original model to `Proteus/meshvolume-backup/` inside the mod.
- **Undo stroke** (or Ctrl+Z) takes back the last stroke. **Start over** discards every stroke on this model.
- **Undo saved changes** (hold Ctrl or Shift and click) puts every model the brush changed in this mod back exactly as its author made it.
- Part switches, hat fitting and the brush each keep their own backup. If more than one has changed the same model, undo the most recent first. Proteus tells you if you try another order.
- A model's body sliders can't always follow an edit. When some points couldn't move with it, Proteus says how many, and turning that slider on may bring the clipping back in places.
- Most modded meshes have no wind channel. The first wind stroke to save adds one, and sets the garment's materials so the wind can move it. Materials that come from the game rather than the mod can't be set.
- **Friends using a sync plugin see your edits.** Once a stroke has settled and saved, people paired with you through a sync plugin such as PlayerSync or Lightless receive the edited garment within a few seconds, while the Studio stays open. Strokes still in progress stay private. If you've locked parts, they reappear a few seconds after you stop working so the whole garment can be shared; hovering or painting hides them again.

##### Moving, turning and scaling

**Move**, **Rotate** and **Scale** work on a chosen piece of the garment rather than painting. Click a part on your character or in the model view, or pick it from the part list, then drag the handles at its centre. Hold **Alt** to move the camera while you work on your character. Each change saves when you let go, and **Undo move** takes the last one back.

- **Whole parts or Polygons.** By default you move whole parts. Switch to **Polygons** to work on a few polygons instead: click one to select it, Shift-click to add or remove one, and **Grow** or **Shrink** the selection a ring at a time. Choosing a part in the list selects all of its polygons, and **Clear** empties the selection.
- **Move adjacent parts** lets the cloth around what you're moving follow it, less the further away it is, so the garment bends instead of tearing. **Falloff** sets how far that reaches. With polygons, the falloff is measured along the surface, so only cloth joined to your selection follows: a separate piece that merely sits close by stays put. With **Move adjacent parts** off, only your selection moves, though points it shares exactly with a neighbour still move so seams stay closed.
- **Mirror left/right** moves, turns or scales the matching cloth on the other side of the body the mirrored way, at the same time. A piece that is its own mirror, such as a whole bra or a choker, moves as one and stays centred, so sideways moves and turns are left out for it. Points with no match on the other side still move on their own side, and one **Undo move** takes back both sides.
- **Skin and locked parts never move.** Unlock a part under a brush to move it.
- A moved part still follows the bones it was made for, so a part moved far from them can bend oddly in poses.

##### Body size

**Body size** refits a garment made for one body onto another: a different size of the same body mod (Neolithe XS to L), or a different body mod altogether (Neolithe to Rue, TBSE to TBSE-X).

**Both body mods must be installed in Penumbra**: the one the garment was made for and the one you're refitting it onto. They don't need to be turned on. Proteus measures how far to move each point of the garment by comparing the two bodies' own model files, so it needs both on disk. The size the garment was made for also has to be one of that mod's options.

1. Open the garment's model in the Studio and choose **Body size**. The first time, a few tips show; they come back when they change.
2. Under **Made for**, pick the body mod the garment was made for, then its size. Proteus guesses the size for you and says how sure it is.
3. Under **Refit onto**, pick the body mod and one or more sizes. These start on the body you're wearing. Tick several sizes to make them all in one run.
4. Press **Refit and preview** to see the result on your character. When it looks right, press **Save as a new option** (or **Save *N* sizes as new options**). Nothing is kept until you save.
5. **Save into** chooses where it goes. **This mod** adds it to the garment's own mod, by default in the group that already holds the author's sizes (**Save to group** changes that), but the author's next update will remove it. **A new mod** keeps it in a mod of its own that survives updates. Either way it keeps working with Proteus turned off.

Mods Proteus makes, for refits and for edited vanilla gear, are filed together under a **Proteus Upscales** folder in Penumbra's mod list.

Things worth knowing:

- **Use the new body's skin** is on by default: the garment's own skin for each part of the body being resized is replaced with the new body's, so it matches the rest of you. Untick it to resize the skin the garment came with instead, which keeps the author's push-up or skindent shaping.
- **Remove hidden skin**, on by default, leaves out the new skin where the author deleted theirs, usually under cloth. Turn it off if a refit leaves holes. It only applies while **Use the new body's skin** is on.
- **Add the new body's piercings**, off by default, brings the new body's piercings in with its skin, where the garment shows that skin. Nails always come along. It only applies while **Use the new body's skin** is on.
- **Push cloth out of the body** pushes out all buried cloth, not just what the refit buried. Use it if the body shows through, for example stockings with their own leg skin. Off by default, because authors often hide cloth under skin on purpose. For the game's own tops it is always on while **Use the new body's skin** is on, as the automatic refit does it.
- **Keep shape** moves rigid pieces (rings, buckles, chains) whole instead of bending them. Small closed pieces start ticked. A piece that would sink into the new body bends with it instead.
- **Shoes and gloves keep their own feet and hands.** With **Keep its own feet** (or **hands**) ticked, the default, the foot stays exactly as its author made it, heel and all, and only the leg the shoe reaches is refitted, sized by the legs (the arm by the chest, for gloves). The feet go along by themselves. A shoe that reaches no leg keeps its shape and only has its skin redrawn, saved as "*Body* — skin". Untick it to refit the whole shoe as before.
- **Pieces that cover more than one part**, like a long dress over the legs, take a size for each part under **Other body parts**. Choose **None (leave this part out)** to skip one.
- **Untick a part** in the part list to keep it exactly where the author put it.
- **Gear the game doesn't ship for your race**, such as pieces made only for men, can still be refitted: Proteus first makes a model for your race from the one that exists. Gear made only for your race, such as Au Ra's Auri set, is refitted in the Midlander shape the bodies are made for, the way the game bends it, and saved back for your race.
- **Refit for** another race of the garment's sex. When the body mod you refit onto has a body for another race, such as TBSE-X's **M Highlander**, **Refit for** lists it. Picking it makes that race's own model from this one the way the game bends it, refits it onto that race's body, and saves it where that race loads it, whoever you're playing. This model stays as it is. It works the other way too: a mod with only a Highlander model can be refitted for Midlanders, and every race of that sex with no model of its own for the item then wears that refit as well. If the mod already has its own model for that race, Proteus says so and offers to open it; refit it anyway and the saved refit takes that model's place while it is on. The automatic refit on equip still keeps the author's model for that race; Studio is where you replace it.
- **Vanilla gear** can be refitted the same way. It has no mod to save into, so saving makes a new mod with the refit and an **Original** option.
- Touch up afterwards with the other brushes, like **Pull out**, and use **Move** for rigid pieces. Refit from the author's size nearest the one you want — the size set below does that for you.
- **Between body mods**, the garment also takes on the new body's bone weights where the two bodies differ, so it moves with the new body's physics. Where they agree, the author's weighting is kept. Skirt chains and other bones of the garment's own are never changed.
- **The two bodies' skin must share a texture layout**, or use one Proteus can convert (bibo, gen3 and gen2). Men's and women's bodies are refitted onto each other only through **Refit for**, below.
- **The other sex** can be picked under **Refit for** too. A Midlander chest or legs garment can be refitted for Midlanders of the other sex when the body mod you refit onto has bodies of that sex. Pick the body mod the garment was made for under **Made for body**. The bodies are matched through the bibo↔TBSE body maps, so only women's bodies in the Bibo+ or game layout pair with men's bodies in the TBSE or game layout. A refit is never refused for folded faces: how many turned over is shown beside it in the warning colour, since a turned-over face shows as a hole in game, so check those places before you share it. Every race that falls through to the saved model with no model of its own wears it too. The auto refit never crosses the sexes; the size set does, for a women's mod (see below). The game's own gear crosses the sexes for women's legs only: they can be refitted for Midlander men from the **Vanilla body**. The game has its own men's model of most items, so the panel says the saved refit replaces it while its new mod is on, and offers to open the game's men's model instead.
- **Bones the race's skeleton lacks** don't stop a **Refit for**. A garment rigged to a women's physics skeleton (YAS) can be refitted for men, onto IVCS or the game's skeleton: each bone the race's skeleton doesn't have is moved onto the nearest one it has, such as YAS's buttock bones onto IVCS's. The panel lists every bone it moved; those parts then move with that bone instead of on their own. Bones only your collection's custom skeleton has are kept, and the panel warns that anyone without that skeleton sees those parts out of place.
- **Shape keys are lost** when the skin is replaced or the garment moves between body mods. Proteus tells you when that happens.
- **Remove the last refit** takes a saved size back out of the mod.
- Only the highest level of detail is refitted, so the old size can show at a great distance.

##### Every size for other bodies

Below the one-garment tool, **Every size for other bodies** does a whole mod at once. Tick the body mods you want, press **Add the sizes**, and every size of those bodies is refitted from the author's size nearest it — the outfit's other pieces in that option from the same size — and saved beside the author's own sizes as "Rue S", "YAB Buff L" and so on.

- **Use a body's YAS version where it has one** gives Rue, YAB and Tre their Yet Another Skeleton sizes. A piece already weighted to YAS always gets YAS sizes.
- **Include shoes and gloves**, on by default, sizes the mod's shoes and gloves too, keeping their own feet and hands: shoes get the legs' sizes ("Rue WC", "YAB SC"), gloves one option per body. A shoe or glove in the same size group as the outfit's chest or legs joins the sizes that piece made, so one choice dresses the whole outfit. One that reaches no leg or arm only has its skin redrawn, and only where the new body draws skin another way. One sized in a group with a piece it can't follow — gloves beside legs, or a shoe and gloves with no chest — is left alone, with a note.
- **Add the new bodies' piercings**, off by default, brings each body's piercings into the sizes it makes. Nails always come along. Sizes already made change only when made again.
- Sizes the mod already has are left alone. Tick **Make again the sizes Proteus added before** to redo the ones Proteus made; the author's own sizes are never touched. If you ran it with an earlier version of Proteus, tick **Make again** once: those sizes were cut from the author's first size.
- It runs in the background and can take a few minutes on a big mod. **Stop** keeps the sizes already saved. When it's done, it lists anything it had to leave out and why.
- **TBSE (for men)** and **TBSE-X (for men)** are offered for a women's mod. Each of its women's chest and legs pieces gets a men's version, refitted for Midlander men through the bibo↔TBSE body maps (they download the first time they're needed) from the author's size whose body, bent to a man's shape, lies nearest his. They're saved in a group of their own, **Men's body**, whose default is Original, so the women's sizes stay as they are; every men's race with no model of its own for the item wears them too. Shoes and gloves aren't refitted across the sexes, a piece the author already made for men keeps theirs, and sizes made on gen3 bodies aren't used. Where the author's options are variants of one size, such as SFW and NSFW, men get one version, and **Check in game** says which it was made from; it also lists each version's turned-over faces and moved bones. A version that matches under 90% of the body is left out. These ticks are kept apart from TBSE and TBSE-X for men's mods.
- Only bodies for the races the mod dresses are offered. A body has to be installed in Penumbra to get its sizes. The game's own gear has no mod to add sizes to, so this doesn't appear for it.

##### Part switches

**Toggle Parts** takes a piece of geometry out of a mod's model and puts it behind an on/off switch: a bow, a collar, a strap that the author welded into an always-on mesh.

The switch is written into the mod itself as an ordinary Penumbra option, so it shows up in that mod's own settings.

The parts of the model are listed with their triangle counts. Click a piece on your character or in the model view to tick it. Tick the parts one switch should hide, give it a name, and press **Make a switch from the ticked parts**. Queue up as many as you want, then press **Write the switches into the mod**.

Things worth knowing:

- **Ten switches per item.** That's the game's limit, not Proteus's. If an author has already used them all, the tab says so and won't let you add more.
- **Equipment and accessories only.** There's nothing to attach a switch to on other model types.
- **A part the author already switches can take yours too.** The two stack: the part shows only when both are on.
- **Imported packs take switches too.** A garment from a pack you imported through **Import** gets its switch the same way.
- **It's reversible.** The original models are kept, so **Undo — restore the original models** puts the mod back exactly as it was and removes the option group.
- If an item has several model files whose parts are arranged differently, Proteus edits only the ones the switch lands on correctly and tells you which it left alone, rather than guessing and hitting the wrong geometry.

#### Bindings

Ties your whole Proteus setup — which mods are on, their priorities and options, and all their colors — to a Glamourer design. **Bind Proteus state to Glamourer designs** is on by default.

Saving a design captures the current state against it: every mod on your character, not just Proteus ones, plus all Proteus colors. Applying that design later restores it. Colors and layer settings are restored as a live overlay, so the mod's own files are never rewritten. Each binding lists the mods it restores; **Apply** restores one straight away without going through Glamourer, and **Unbind** forgets it without touching the design.

While a binding is active, edits in the color editor preview immediately but are **not** saved until you press **Update** beside that design on the Bindings tab — which folds everything currently on screen back into it.

| Option | What it does |
|--------|-------------|
| Follow Glamourer automation | Restores a binding when Glamourer's automation applies its design on a gearset or job change. On by default. It only ever restores, never clears. |
| Restore every mod on the character | Also restores the other mods that were on your character when the design was saved, switches off the ones that weren't, and raises the design's mods above anything they conflict with. Off by default: only Proteus mods are restored. Other mods are held with Penumbra temporary settings, so your collection itself isn't changed, and Penumbra's banner on a held mod lets you drop that one hold. |
| Unequip slots the design leaves unset | When you apply a bound design, empties the gear and glasses slots it doesn't set and switches off imported packs it didn't capture, so pieces of the previous look don't carry over. Off by default. Not used for automation, and weapons and the slot hosting a second skin are left alone. |

A design saved before bindings recorded the whole character is marked **Proteus mods only**. Apply it and press **Update** to capture the rest.

#### Create

Authors a basic overlay mod without leaving the game. Give it a name, an author, and pick at least one texture (diffuse, mask, normal, or index). The material target auto-fills from the body you're currently wearing; you can pick another equipped material from the dropdown or type a path by hand. Proteus writes a new Penumbra mod and opens it.

Texture slots the chosen material can't actually use are greyed out.

A body skin target also covers the same body on every other race that shares its texture layout, so the mod follows you through a race change. The tab shows how many materials that comes to. Women's bodies share one layout and men's another; Lalafell and male Hrothgar have their own and aren't included.

Tick **Make this art glow** to turn a plain diffuse — art on a transparent background — into a glowing tattoo, with no hand-written metadata. The glow's colour comes from the picture itself, per pixel, so there's nothing else to set. Two behaviours:

- **Always glow** — the tattoo is there in daylight and glows day and night. Kept gentle on purpose: this shader adds one flat colour across the whole tattoo rather than following the picture, so a strong value bleaches the art in daylight. For a brighter glow at night without that, raise **Glow** in Colors and set **Fades in light**.
- **Only in the dark** — nothing in daylight, glowing in an unlit room, the way Atramentum Luminis tattoos behaved. The art fades as the light on you rises and takes its own surface with it, so there's skin and nothing else in the bright.

Skin can't emit, so a glowing overlay renders on a second skin rather than being painted into yours — which is why the checkbox is only available on a skin or face target, and only once you've picked a diffuse.

#### Import

Takes a mod pack and converts it to a Proteus mod. Three types are supported — and it also accepts a shared preset:

**Regular Penumbra mods (`.pmp`)** — wear parts of a normal gear mod without using a gear slot, and get the advanced colour-table features on top. To convert a mod you've already installed in Penumbra, pick the `meta.json` in its folder instead of a pack file. Packs in Penumbra's older format are upgraded by Penumbra first, then converted.

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

**Presets (`.ptp`)** — a look someone shared for a mod you already have.

A preset isn't a mod, so nothing is installed: Proteus reads which mod it was made for, offers to add it to that one, and says so if you don't have it under that name — pick the right mod yourself and it goes there instead. It's saved, not worn; wear it from that mod's Presets section in Colors when you want it.

#### Export

Saves one of your Proteus mods as a Penumbra mod pack (`.pmp`) to share. Pick the mod from the dropdown, press **Export**, and choose where to put it — the file name is filled in from the mod name, and the dialog opens on your desktop the first time and wherever you saved last after that.

The pack is a straight copy of the mod folder, so nothing is lost: options, colour tables, masks, glow effects and gear layers all come along, and the recipient's Proteus picks it up as soon as Penumbra installs it. Disabled mods can be exported too.

#### Settings

| Setting | What it does |
|---------|-------------|
| Enabled | Master switch. Off clears Proteus's output, redraws you without it, disables the managed "Proteus" mod in Penumbra and stops design bindings. Every Proteus feature stands down. |
| Auto redraw | Lets Proteus keep up on its own: it recomposites after zoning, gear changes and redraws, then reloads your character so you see the result. Off makes Proteus mostly manual. Your look stays on, but an edit won't show until something redraws you. |
| Auto-raise mod priority | When another mod is confirmed to be overriding a skin texture Proteus composites into, raises Proteus's Penumbra priority above it and says so in chat. |
| In-place reload | Refresh textures through Glamourer instead of a full redraw, avoiding the despawn/respawn flicker. On by default. |
| Enable Compression | Block-compress baked textures, cutting them to about a quarter of their size on disk and in VRAM. Off by default: it's the slowest thing Proteus does, and on an older PC it can hold your look back for a minute or more per change. Turn it on only if you're short of video memory. On a PC too slow for it, Proteus leaves textures uncompressed even with it ticked, and says why. |
| Sharp alpha | Experimental. Keeps sphere maps and metalness working in gpose, at the cost of harder edges on sheer fabrics. |
| Texture cache (MB) | How much decoded texture data to keep in memory between composites. |
| Hide redundant body meshes | Skips skin the second skin would otherwise draw twice — joint reinforcement rings a neighbouring part already covers, and spare copies of a region. On by default, safe on any body. |
| Host on invisible glasses | Lets the second skin ride the facewear slot so your rings stay free. |
| Skin-tint suppression | How strongly overlays resist being tinted by your skin tone. |
| Ambient occlusion / Shadow softness / Skindenting | Global strength of the contact shadow and normal indent around strap edges. |
| React to the scene's light | Lets colour rows marked **Fades in light** dim as the light on you rises. Off, every glow burns at full brightness everywhere. |
| Set the light level by hand / Light level | Ignores the scene and uses the slider instead. It's the quickest way to see a dark-only glow without waiting for dusk, and the right setting for gpose. |
| Background glow | The soft ember glow drifting behind the window. On by default. |
| Reduce motion | Holds the window still: hover effects land at once, and the background glow and the pulse on active mods stop moving. |

Four buttons here are worth knowing about:

- **Copy Logs** — runs a full refresh and saves everything Proteus logs while it runs to a text file on your desktop, with your Windows user name removed. Attach that file when you report a problem.
- **Restore changed accessory** — forces a full redraw if a second skin ever gets stuck on a ring or bracelet after disabling or swapping.
- **Clear texture cache** — use when a texture edit isn't showing up, e.g. you re-exported an overlay at the same size.
- **Glow Effect Textures** — opens the folder Proteus reads animated-glow scroll maps from. Drop images in it and they appear in every gear overlay's Effect dropdown. Hover the button to see the full path.

##### Hair

The **Hair** section fits hair mods to you in two ways: hair made for another race onto your race's head, and hair under hats. Like the Studio, both edit the hair mod's own files, so they keep working with Proteus turned off and travel with the mod if you export it.

**Hair made for another race.** Many hair mods are made for one race only, so on any other race the game shows its own hairstyle instead. With **Fit hair mods made for another race** on, Proteus bends the mod's hairstyle onto your race's head and adds it to the same option of the same mod, so it follows that mod's own switches.

- It checks the hair mods switched on in your collection once after you update, when you switch the setting on, when you press **Check now**, and whenever a hair mod is installed, switched on or changed. The hairstyle you put on is fitted too. Chat names the hairstyle numbers that were fitted, so you know what to choose; Glamourer shows the number.
- The section shows the check as it runs, with **Cancel**, and lists every hairstyle it fitted. Each has its own **Undo**, and **Undo everything this check fitted** takes them all back (click it twice). An undone hairstyle isn't fitted again by itself.
- Only for your own race, from races of the same sex. A race you're only trying on isn't fitted: Proteus waits until you've kept it for 30 seconds.
- Physics hair keeps its physics. When your race's skeleton lacks the bones a hairstyle moves with, the port brings the source race's skeleton and physics along for that hairstyle.
- A hairstyle that wouldn't sit right on your head (it sinks into it, or stands off it) is still fitted, and Proteus says which, so you can check it in game.
- A hairstyle the bend turns partly inside out is still fitted, and Proteus says which, so you can check it in game.
- To cover every race at once, open a hair model in the **Studio** and use **Port to all races**.

**Hats.** Most modded hair has no hat support, so a hat worn over it goes straight through. Proteus presses the hair a hat would cover flat against your head.

- **Make hairstyles fit under hats** is on by default. Proteus waits until you actually wear a hat, then fits the hairstyle under it and tells you in chat. If you never wear a hat, your hair mods are never touched. Untick it to stop, and your choice sticks.
- **Hide ponytails** also makes the parts no hat could cover disappear while a hat is worn: ponytails, side tails, a long fall at the back. Off by default, because hiding is all or nothing and a part judged wrongly simply vanishes.
- Or fit the hairstyle you're wearing yourself. The section says how many points would be pressed and by how far, and **Make it fit** writes the change.
- **Undo** puts back the hairstyle you're wearing. A hair mod usually ships one model per race, so **Undo all** puts back every hairstyle Proteus changed in that mod. The originals are kept in `Proteus/hatcompat-backup/` inside the mod.
- Hair whose author already added hat support is left alone, and hair that came with the game already works. The exception is hat support that hides far more hair than a hat covers, usually carried over from the hairstyle it was built from. Proteus offers to measure that again and replace it.
- **Replace hat support a hairstyle came with** makes Proteus discard an author's hand-made hat fit and use its own instead. Off by default, since a hand-made fit is usually better. The author's version is kept and comes back when you turn it off.
- Some hairstyles are welded into pieces too large for the game's shape format to address. Those points keep their shape, so a hat may still clip there.

##### Body refit

Refits chest, legs, hands and feet gear to your body by itself, the moment you put it on. Proteus works out which body mod and size the gear was made for, refits it onto your body the same way the Studio's [Body size](#body-size) tool does, saves the new size into the gear's own mod and switches it on. Chat says when it starts and when it's done.

1. Tick **Refit gear to my body as I put it on**.
2. Pick a **Collection**. Each Penumbra collection has its own choice, and it starts on the one you're using now.
3. For each part — Chest, Legs, Hands and Feet — pick a **Body** mod and a **Size**. The first body you pick is filled in for every part it has, with the size you're wearing in it (or else its first size). Hands and feet follow the chest or legs size unless you pick one. Rue bodies are only combined with Rue.
4. Gear you put on from now on is refitted. For what you're already wearing, press **Refit what I'm wearing now**.

Things worth knowing:

- **Also refit the game's own gear** does vanilla pieces too. They have no mod to save into, so each gets a new mod of its own, named after the item and the body, filed under **Proteus Upscales**.
- **Add my body's piercings to refitted gear**, off by default, brings your body mod's piercings into refitted gear. Nails always come along. Gear already refitted keeps what it was made with: undo and refit what you're wearing to change it.
- **Your choices in Penumbra win.** If you pick a mod's **Original** or another size yourself, Proteus leaves it alone. If a mod already ships your size, that option is switched on instead of making a new one.
- If Proteus can't tell which body a piece was made for, or the piece has no body skin to read, it leaves it as it is and says so in chat. You can still refit it by hand in the Studio.
- **Undo the refit of what I'm wearing** (hold Ctrl or Shift and click) removes the sizes the automatic refit made for the gear you have on, deletes the mods it made for vanilla gear, and puts back the options you had before. Those pieces won't be refitted again unless you press **Refit what I'm wearing now**. Sizes you made yourself in the Studio stay.
- **Forget this collection's body** turns it off for that collection.

### Presets

A **preset** is a named look for **one mod**: which of its options are ticked, all its colors, and its glow and layer settings. Complex packs — bodysuits, stockings — ship a dozen groups, and finding a combination worth wearing means poking at checkboxes until something clicks. A preset keeps that combination.

Presets live in a collapsible **Presets** section at the bottom of a mod's color editor, below Advanced. **+ Save…** keeps how the mod looks right now under a name; the dropdown beside it picks which one to wear. Collapsed, the section header still names what you're wearing.

- Presets marked `*` came with the mod. They're read-only — editing one gives you your own copy instead, so a mod update can never overwrite something you saved.
- A `●` beside the worn preset means you've changed something since it was saved. **Update** folds those changes in; ignore it and the preset stays as it was.
- **No preset** puts the mod's own colors back. Your option ticks are left alone — unpinning a look isn't a request to undo your own toggling.
- Trying presets is free. Only the option ticks are written to Penumbra; the colors and layer settings ride on top while a preset is worn, and the mod's own files are never touched.

Share one with **Copy code** (a string to paste into chat) or **Export…** (a `.ptp` file). The other side uses **Paste code** or **Import…**; a preset made for a different mod says so before it's added.

Applying a preset while a design binding is active previews into that binding, like any other edit — press **Update** on the Bindings tab to keep it. Applying a Glamourer design unpins any presets, since the design carries its own colors; the presets themselves stay saved.

If a mod update renames or removes an option, applying an old preset sets everything that still exists and tells you what it couldn't.

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
| Overlay gen2/vanilla | Also paints this mod onto vanilla (gen2) skin you're wearing, usually skin that comes with a piece of gear. On by default, and it costs nothing when there's no vanilla skin on you. bibo and gen3/Eve are always painted. Applies to the whole mod, and it's a global setting: design bindings don't capture it. |
| Overlay on men | Also paints this mod's women's art on men (TBSE or the men's vanilla body), moved into his own layout through the men's body maps, which download the first time they're needed. Nothing is painted on him until they're here. Art made for men is never changed. On by default; applies to the whole mod, and it's a global setting: design bindings don't capture it. |
| Reset to defaults | Restores this option's colors, glow and mode to the settings Proteus first recorded for the mod. Hold Ctrl to arm it. |

If a mod has no active option there are no colors to show, but **Advanced** still appears so **Overlay gen2/vanilla** stays reachable.

#### Effects and Geometry

Two more collapsible sections apply to the whole mod:

- **Effects** holds the animated glow effect picker and **Skindent**, the contact shadow and normal indent at strap edges. Skindent is off unless the pack asks for it, because it treats coverage as cloth pressed into skin, which is wrong for tattoos. **Pack** follows what the mod asked for; **On** and **Off** override it.
- **Geometry** reshapes the garment Proteus builds from your body, so it reads as fabric rather than paint. Proteus builds a second skin as a copy of your body, so by default it follows every hollow:
  - **Span the cleavage**: the fabric between the breasts spans straight across, the way real cloth does. Nothing is pulled inward, so it can't cut into the body.
  - **Span the buttocks**: the same across the seat.
  - **Span the belly button**: the fabric stretches flat across the navel instead of dipping into it.
  - **Smooth between the legs**: smooths the fold at the crotch. This changes the body underneath, so it applies to every garment worn at once.
  - **Smooth the nipple**: smooths the fabric over it. Not finished yet: the body underneath keeps its shape, so on a close fit it can still show through.

  Turning any of them on renders the mod as a second skin.

#### Overlay options

Below the rows, an overlay's tab may show:

- **This overlay is the whole skin** — for a converted skin mod, art that *is* the skin rather than something laid on it. Its normal map replaces the skin's instead of adding to it, and skin-tint suppression drops to 0 so your own tone comes through.
- **This art is asymmetric** — turn it on when the two sides are meant to differ: a tattoo on one arm, makeup that isn't mirrored. A vanilla body and face fold art in half, so without this one side is mirrored onto the other. Proteus can't detect it for you.
- **Reinforced toe** — on a stocking with a capped toe, knits a denser toe box: a darker panel with the toes still showing through, fading out at the edge. **Density** sets how solid it is. It's saved to the mod, so presets and designs don't capture it.

#### Rows

The editor shows up to 16 color table rows. Rows map to regions defined by the mod's index texture (if it has one). Row 16 is the fallback color used when there is no index texture. Rows the index texture never selects are dimmed.

Press **Glow** on any sub-row to light that region up on your character, so you can find which row controls what.

Each row has two sub-rows:
- **A** — applies where the index texture's green channel is 255.
- **B** — applies where the green channel is 0. Values in between blend smoothly.

For each sub-row:
- **Diffuse** (color swatch) — multiplicative tint applied to the overlay. White (`#FFFFFF`) shows the overlay's natural colors. Any other color tints it. You can recolor a plain grayscale stocking by picking a color here.
- **Emissive** (0–1 slider) — how strongly the overlay glows, with its own color. Skin can't glow, so setting this switches the overlay to a cloth layer, the same way a sphere map does.
- **Fades in light** (0–100% slider) — how much the light around you takes that glow away. 0% glows the same everywhere. 100% is a dark-only glow: full brightness in an unlit room, nothing at all under a midday sky or beside a lamp. It's set per row, so one half of a tattoo can be dark-only while the other half is always there.
- **Hide in light** (checkbox) — makes this row's opacity follow its glow, so where the light has taken the glow away there's nothing left but skin. Without it a dark-only tattoo still leaves its own colour behind — usually black — and reads as a silhouette in daylight.
- **Opacity** (-100 to 100 slider) — 0 is the mod default. -100 is transparent. 100 is fully opaque.
- **Sphere map / Metalness / Roughness / Specular** — available on Cloth. Setting any of them switches the overlay to a second skin.

Rows and sub-rows can be copied and pasted between each other.

Changes apply on screen immediately and re-composite about a second after you stop editing. They're saved to the mod's `metadata.json` — unless a design binding is active, in which case they belong to that design until you press **Update** on the Bindings tab.

### Acknowledgements
Thank you so much to Sebby for teaching me about how to use pixel-based image mapping instead of baking and for releasing the baked maps under the MIT license via the loose texture compiler.

---

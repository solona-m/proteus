using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using CheapLoc;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Proteus.Interop;
using Proteus.Localization;

namespace Proteus.Services;

/// <summary>
/// Builds the "second skin": every <see cref="OverlayLayer.Gear"/> overlay becomes a shell — a copy of
/// the character's skin mesh, pushed out along its normals and drawn as gear so it can run a full gear
/// shader (color table, sphere maps, metalness, scrolling emissive), none of which skin.shpk offers.
///
/// The shells ride on The Emperor's New accessories (set a0053), which are invisible, so they survive
/// any outfit and cost no visible equipment slot. Chest rides the right ring, legs the left.
/// </summary>
public sealed class SecondSkinService
{
    private readonly PenumbraBridge penumbra;
    private readonly TextureLoader textureLoader;
    private readonly SidecarDiscoveryService discovery;
    private readonly UVRemapService uvRemap;
    private readonly Configuration config;
    private readonly IPluginLog log;

    /// <summary>
    /// Resolve a game path to the file we should read as a BASE — never our own previous output, and
    /// never past a mod the player actually installed. See CompositorService.ResolveUpstream: it
    /// remembers the file that was behind a path BEFORE our redirect started masking it, which is the
    /// only way an append host that the player has modded keeps its own appearance across composites.
    /// Null (tests, or no upstream known) falls back to a plain resolve plus the own-output guard.
    /// </summary>
    private readonly Func<string, string?>? resolveUpstream;

    /// <summary>Textures are authored in BODY UV (the shell inherits the body's UVs).</summary>
    // internal so the compositor can prefetch this phase's art at the right size; see PrefetchAhead.
    internal const int TexSize = 2048;

    /// <summary>Coverage only decides whether a whole triangle survives, so it can be coarse.</summary>
    private const int CoverageSize = 256;

    /// <summary>
    /// The toe-cap mask is sampled per VERTEX, not per texel, so it needs more resolution than coverage
    /// (the toes are a small, position-sensitive patch of body UV) but far less than the art.
    /// </summary>
    private const int ToeCapSize = 512;

    /// <summary>
    /// How much of the capped area a shell must actually paint before it gets a toe cap. A shell that
    /// stops at the ankle has no business rebuilding the toes.
    /// <para/>
    /// Deliberately LOW. Every shell that keeps any geometry over the toes has to be capped, or it
    /// sleeves each toe while the capped shell smooths over them, and the uncapped one comes through —
    /// measured with a thigh-band overlay whose shell still had toe geometry: 226 of its toe vertices
    /// sat outside the capped shell, by up to 0.0036. Capping a shell whose toe art really is absent
    /// costs nothing, because the coverage test then trims the rebuilt cap away exactly as it trimmed
    /// what was cut out.
    /// </summary>
    private const float MinToeCoverage = 0.02f;

    /// <summary>Number of single-char base-36 shell disk ids (0-9a-z) — the ceiling on placeable layers,
    /// so an id never runs past 'z'.</summary>
    private const int DiskIdSpace = 36;

    /// <summary>Encode a layer's global index as a base-36 disk id char (0-9 then a-z). Digits-first keeps it
    /// ASCII-monotonic ('0'&lt;'9'&lt;'a'&lt;'z'), so the ghost/highlighter's char comparison still orders the stack.</summary>
    private static char DiskId(int d) => (char)(d < 10 ? '0' + d : 'a' + (d - 10));

    // A head/facewear "_met" model smaller than this is treated as an invisible/degenerate item (empty
    // frames) — the shell REPLACES it instead of appending, since a merge into a near-empty model won't
    // render. Real glasses/helmets are tens of KB; "The Emperor's New"-style invisibles are ~1.5 KB.
    private const int DegenerateModelBytes = 3000;

    /// <summary>
    /// The Emperor's New Ring — invisible, so a shell on it shows only our material. One source of truth
    /// with the resolver that finds the item to equip: this set id is what ties the published path, the
    /// EQDP entry and the Glamourer item together, and two copies of 53 could drift apart.
    /// </summary>
    private const int EmperorSetId = InvisibleRing.EmperorSetId;

    /// <summary>
    /// Every skin part is MERGED into the one ring model, each part contributing its own mesh groups.
    /// A part × layer group carries that layer's material, so different regions can run different
    /// shaders. Parts the character isn't drawing are simply skipped.
    /// </summary>
    private static readonly string[] Parts = ["top", "dwn", "glv", "sho"];

    /// <summary>Body ids tried by the whole-body fallback, in preference order. b0001 is the standard
    /// body; a few race/gender combos ship b0101 instead.</summary>
    private static readonly string[] WholeBodyIds = ["b0001", "b0101"];

    public SecondSkinService(
        PenumbraBridge penumbra, TextureLoader textureLoader, SidecarDiscoveryService discovery,
        UVRemapService uvRemap, Configuration config, IPluginLog log,
        Func<string, string?>? resolveUpstream = null)
    {
        this.penumbra = penumbra;
        this.textureLoader = textureLoader;
        this.discovery = discovery;
        this.uvRemap = uvRemap;
        this.config = config;
        this.log = log;
        this.resolveUpstream = resolveUpstream;
    }

    /// <summary>
    /// The hand-modelled toe box shipped beside the plugin, read once. Null when it is missing or will
    /// not parse, in which case the shell builds exactly as it did before — this is additive.
    /// </summary>
    private readonly List<SecondSkinWriter.AuthoredCapSet> authoredCaps = [];
    private bool authoredCapTried;

    private IReadOnlyList<SecondSkinWriter.AuthoredCapSet> AuthoredCaps()
    {
        if (authoredCapTried) return authoredCaps;
        authoredCapTried = true;
        try
        {
            var dir = discovery.AssemblyDir;
            if (dir == null) return authoredCaps;
            // ONE CAP PER BODY: toecap.mdl / toecap.<body>.mdl, each with the binding of the same name
            // that says where it sits on the body it was modelled for. The binding carries it across
            // heels and other foot-model swaps for that body; it does NOT carry it to another body, which
            // is why there is a cap per body rather than one cap and many bindings. The writer picks by
            // which binding places best, so no file has to be matched to a body by name.
            var meshDir = Path.Combine(dir, "Meshes");
            if (Directory.Exists(meshDir))
                foreach (var mp in Directory.GetFiles(meshDir, "toecap*.mdl").OrderBy(x => x))
                {
                    var bindPath = Path.ChangeExtension(mp, ".bind");
                    authoredCaps.Add(new SecondSkinWriter.AuthoredCapSet(
                        File.ReadAllBytes(mp),
                        File.Exists(bindPath) ? File.ReadAllBytes(bindPath) : null,
                        Path.GetFileNameWithoutExtension(mp)));
                    log.Information("[Proteus] second skin: toe cap {0} loaded{1}",
                        Path.GetFileName(mp),
                        File.Exists(bindPath) ? "" : " (no binding — fits one foot only)");
                }
            if (authoredCaps.Count == 0)
                log.Debug("[Proteus] second skin: no authored toe cap in {0}", meshDir);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] second skin: could not load the authored toe cap");
        }
        return authoredCaps;
    }

    /// <summary>Last cap messages told to the wearer, so a recomposite doesn't repeat them.</summary>
    private string? lastCapDeclined, lastCapUsed;

    /// <summary>
    /// Files to redirect, plus the metadata edits that make the shells load.
    ///
    /// <paramref name="ShellChanged"/> is true when the model, a material OR a texture differs from what
    /// was already on disk; a run that rewrites identical bytes reports false.
    ///
    /// <paramref name="ModelChanged"/> narrows that to the .mdl alone, which is the only change that
    /// forces a full redraw. Materials do NOT need one: verified in-game — a gear colorset edit applied
    /// correctly through Glamourer's in-place reload with no redraw. (An older comment here claimed the
    /// in-place path "cannot see a new model or material"; the material half of that was never true, and
    /// it cost a character redraw and its flicker on every colour change.)
    /// </summary>
    public sealed record Result(
        Dictionary<string, string> Redirects, List<object> Manipulations, bool ShellChanged,
        Dictionary<(string ModDir, string? Group, string? Option), List<string>> ShellMaterials,
        bool ModelChanged,
        // The game model paths hosting a shell this composite (one per host that got layers). When this set
        // SHRINKS between composites — a spill host dropped as the layer count fell — the vacated accessory
        // must reload its real model, which only a full redraw does; the compositor forces one on any change.
        List<string> HostModelPaths,
        // The subset of HostModelPaths we APPEND into — an item the player is wearing, whose own model we
        // read back as the base for the merge. These are the only host paths that have an upstream worth
        // recovering, so they are the only ones PrimeUpstreamCache should ever unpublish to go looking (see
        // there). A CARRIER host is replaced outright and its base is never read, so priming it would blank
        // the shell for the width of the prime and learn nothing.
        List<string> AppendHostModelPaths,
        // Mod directory → the content materials of that mod which back at least one DRAWN mesh this
        // composite, as paths relative to the mod root. What the colour editor should offer a grid for.
        //
        // The editor groups its tabs by the materials a piece DECLARES, which is the larger set: a material
        // bound only to meshes with no LOD0 vertices is declared and never drawn, and a tab for one would
        // save rows that reach nothing and offer a glow button with no target.
        //
        // DRAWN, deliberately, not "placed on a host". A unit that found no free material slot is still a
        // real material the user can see on their character — the fix is another ring, which the unhosted
        // notice already tells them — and taking its colour grid away would be the same silent nothing in
        // the other direction. Keyed by mod so the editor's per-frame lookup is a dictionary hit rather
        // than a filter over the whole set.
        Dictionary<string, HashSet<string>> ContentMaterials);

    /// <summary>
    /// One surface, resolved: the geometry a shell for it is cut from, and the two spaces that geometry
    /// lives in. Every source here shares one UV layout and one race code — that is what makes them a single
    /// surface, and it is why a host can serve only one of these at a time.
    /// <para/>
    /// The race code is the load-bearing field. The game deforms a model according to the race code of the
    /// PATH it loaded it from, so a body (cut from shared c0201 equipment models and DEPENDING on that
    /// deform to fit the wearer) and a face (authored at the character's own c1401 and already the right
    /// shape) demand opposite treatment from their host. Their EQDP manipulations are direct contradictions
    /// on the same set and slot: the body wants the wearer's entry EMPTIED so the game falls through to cut
    /// space and deforms, the face wants it SET so the model loads natively and does not.
    /// </summary>
    private sealed record ResolvedSurface(
        ShellSurfaceKey Key,
        IReadOnlyList<SecondSkinWriter.SourceSpec> Sources,
        IReadOnlyList<string> SourcePaths,
        string CutCode,
        string? UvSpace);

    /// <summary>
    /// One MATERIAL an imported content pack publishes, and every mesh drawn with it.
    /// <para/>
    /// A material is the allocation unit because a material is what costs a slot on the host — ten of them,
    /// shared with the shells. So the unit is not a piece: pieces that want the same .mtrl with the same
    /// colours all land here together and spend one slot between them. A pack of five piercings on a single
    /// material is the case that makes this worth doing, and it is the common shape.
    /// <para/>
    /// <paramref name="Owners"/> is every (mod, group, option) this material serves. All of them need the
    /// material registered under their own key, or the colour editor's glow button silently loses its target
    /// for every option but the first.
    /// </summary>
    private sealed record ContentUnit(
        byte[] Mtrl,
        /// <summary>The source .mtrl, relative to the mod root — what the colour editor knows this material
        /// by, and therefore what it has to be told was published.</summary>
        string MtrlRel,
        Dictionary<int, GearColorRow>? Rows,
        /// <summary>The animated glow, or null to publish the pack's material as its author wrote it. Set
        /// means the material is REBUILT onto characterscroll — see the emit loop.</summary>
        GearSettingsPreset? Glow,
        ShellSurfaceKey Surface,
        List<ContentGeometry> Geometries,
        List<(OverlayEntry Entry, ResolvedContent Content)> Owners,
        /// <summary>Texture game path → the file this pack's selection supplies for it, republished under
        /// Proteus's own mod at emit time. Empty leaves every texture to Penumbra, which is what a pack with
        /// no per-option textures wants.</summary>
        Dictionary<string, string> TexFiles)
    {
        /// <summary>The entry any per-mod lookup should use. Merged owners are all one mod — the merge key
        /// carries the mod directory — so the first is as good as any.</summary>
        public OverlayEntry Entry => Owners[0].Entry;
    }

    /// <summary>
    /// The model of <paramref name="piece"/> that belongs on a character wearing equipment code
    /// <paramref name="modelCode"/>: the exact variant if the pack ships one, else the NEAREST one its own
    /// fall-through chain reaches, else null.
    /// <para/>
    /// Nearest, not merely reachable: a pack shipping both Midlander and Highlander male models must give a
    /// Highlander his own, and "any ancestor" would be free to hand him the Midlander one. The chain is
    /// walked outward from the wearer and the first code the pack has wins.
    /// <para/>
    /// Gender is checked at every hop for the same reason <see cref="CanFallThrough"/> checks it: the chain
    /// really does contain cross-gender hops, and taking one means dressing someone in a body they do not
    /// have.
    /// </summary>
    /// <remarks>
    /// Returns the CODE as well as the path, because the caller cannot tell how to publish the model without
    /// knowing which race it was authored for — cut space is deformed onto the wearer, a race-authored model
    /// must not be. See the surface decision in the content loop.
    /// <para/>
    /// A piece with one un-keyed model reports a NULL code, and that is not the same as an empty one.
    /// <see cref="ContentPiece.ModelFor"/> falls back to <see cref="ContentPiece.Model"/> for any code at
    /// all, so reporting the code it was asked about would attribute a race to a pack that never named one —
    /// and the surface decision would then judge the model against a claim it did not make.
    /// </remarks>
    private static (string? Code, string Path)? ResolveVariant(ContentPiece piece, string? modelCode)
    {
        // Keyed pieces report the code that matched; an un-keyed one reports none. ModelCodes is empty in
        // exactly the case ModelFor ignored the code it was given.
        if (piece.ModelFor(modelCode) is { } exact)
            return (piece.ModelCodes.Any() ? modelCode : null, exact);
        if (modelCode == null || RaceIndex(modelCode) is not { } from) return null;

        // An ACCESSORY may take the chain's cross-gender hops; a garment may not. A ring, bracelet, necklace
        // or earring is a prop hung off a bone rather than a fitted shape, and the game hands them across
        // genders as a matter of course — a Midlander FEMALE character here is wearing
        // chara/accessory/a0002/model/c0101a0002_wrs.mdl, a MALE-coded model, straight from the live
        // equipment walk. Modders rely on that and ship one c0101 model for everyone; refusing it left an
        // imported lantern invisible with a message about body shape that does not apply to a lantern.
        //
        // Everything else keeps the guard, for the reason CanFallThrough keeps it: c0101 and c0201 really
        // are different bodies, and putting the male cut of a fitted top on a female is the failure the
        // refusal exists to prevent.
        bool crossGenderOk = IsAccessoryPiece(piece);

        for (int i = 0, cur = from; i < 8; i++)
        {
            cur = EqdpFallbackIndex(cur);
            if (cur == 0) break;
            if (!crossGenderOk && cur % 2 != from % 2) continue;
            foreach (var code in piece.ModelCodes)
                if (RaceIndex(code) == cur)
                    return (code, piece.ModelFor(code)!);
        }
        return null;
    }

    /// <summary>
    /// Is every model this piece ships an accessory — a ring, bracelet, necklace or earring?
    /// <para/>
    /// Read off the model FILENAME rather than its folder, and that distinction is the whole point. What the
    /// sidecar stores is the pack's ARCHIVE ENTRY, not a game path: the lantern's model is recorded as
    /// <c>base install/chara/accessory/a0189/model/c0101a0189_wrs.mdl</c>. Testing that for a
    /// <c>/accessory/</c> segment happens to work, but only because that pack mirrors the game path under
    /// its option folder — one laid out as <c>a0189/model/…</c>, which nothing forbids, would come back
    /// false and put the lantern back to invisible.
    /// <para/>
    /// The filename cannot be laid out away. <c>cNNNN</c><b>a</b><c>NNNN_slot.mdl</c> against
    /// <c>cNNNN</c><b>e</b><c>NNNN_slot.mdl</c> is the game's own spelling of accessory vs equipment, and it
    /// is the same string wherever the file sits.
    /// <para/>
    /// Not the sidecar's Slot label either: that is display text a hand-authored sidecar may not carry.
    /// Conservative on anything unreadable — unknown means "not an accessory", so the stricter rule applies.
    /// </summary>
    private static bool IsAccessoryPiece(ContentPiece piece)
    {
        var paths = piece.ModelCodes.Select(piece.ModelFor)
            .Concat([piece.Model])
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        return paths.Count > 0 && paths.All(p => ModelCategory(p) == 'a');
    }

    /// <summary>
    /// The category letter of a character model — <c>a</c> for accessory, <c>e</c> for equipment — out of
    /// the <c>cNNNNxNNNN_slot.mdl</c> name, or null when the name is not that shape.
    /// <para/>
    /// Takes the leaf of whatever it is given, so an archive entry and a game path answer alike.
    /// </summary>
    private static char? ModelCategory(string? modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return null;

        var leaf = modelPath.Replace('\\', '/');
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];

        // c + four digits + the letter. Anything shorter cannot carry a second id and a slot after it.
        if (leaf.Length < 11 || char.ToLowerInvariant(leaf[0]) != 'c') return null;
        for (int i = 1; i <= 4; i++)
            if (!char.IsAsciiDigit(leaf[i])) return null;
        return char.ToLowerInvariant(leaf[5]);
    }

    /// <summary>
    /// The attribute names a pack's own hide-toggles currently switch off for one of its models, or null
    /// when nothing is hidden. The composite drops the submeshes tagged with them.
    /// <para/>
    /// Resolved from the model's OWN attribute table, because an IMC mask addresses attributes by position
    /// and the position is not fixed — Denim Shorts lists <c>[atr_sne, atr_hiz]</c> on its Midlander model
    /// and <c>[atr_hiz, atr_sne]</c> on its Lalafell one, so bit 0 means a different piece of geometry
    /// depending on who is wearing it. Mapping through the table is what makes the toggle mean the same
    /// thing on every race.
    /// </summary>
    /// <param name="selected">The mod's live Penumbra selection: group name → chosen options.</param>
    /// <summary>
    /// Does this toggle group speak for that model? Set AND slot both.
    /// <para/>
    /// Set alone is not enough: deadrose puts its dress, bottoms and shoes on one set (43) and separates
    /// them by slot, so matching on the set let every group judge every model — and each group's unselected
    /// bits then hid the other garments' geometry. Selecting a bottoms option changed nothing, because the
    /// dress and shoes groups were still hiding it.
    /// <para/>
    /// A group naming no slot, or one naming a slot this build does not know, matches anything. That is the
    /// lenient direction on purpose: a sidecar written before the slot was recorded keeps working, and an
    /// unrecognised name turns a toggle into an obvious wrong answer rather than a silent no-op.
    /// </summary>
    /// <summary>
    /// Which IMC attribute bit an attribute NAME answers to, or null when it answers to none.
    /// <para/>
    /// The bit is in the name: a part attribute ends in a single letter, and that letter IS the part —
    /// <c>atr_tv_a</c> is part A and so bit 0, <c>atr_tv_i</c> is part I and so bit 8. Everything else —
    /// <c>atr_hij</c>, <c>atr_nek</c>, <c>atr_ude</c>, <c>atr_hiz</c>, <c>atr_sne</c> — is a body-suppression
    /// attribute the game drives from EQP, and an IMC mask never touches it.
    /// <para/>
    /// This replaced reading the model's attribute table BY POSITION, which was wrong in a way that looked
    /// almost right. On the deadrose dress, whose table begins <c>atr_hij, atr_nek, atr_tv_a, …</c>, every
    /// part was off by two: "+ arm ruffles" (bit 2) moved the skirt, "+ arm belts" (bit 1) drove
    /// <c>atr_nek</c> on body meshes that are dropped anyway so it did nothing at all, and the watch's own
    /// tags sat at positions 9 and 10 — one of them past the ten bits a mask even has, so no toggle could
    /// ever reach it.
    /// </summary>
    internal static int? PartAttributeBit(string attributeName)
    {
        // at > 0, so the letter has a real name in front of it: "_a" on its own is not an attribute.
        int at = attributeName.LastIndexOf('_');
        if (at <= 0 || at != attributeName.Length - 2) return null;
        char letter = char.ToLowerInvariant(attributeName[^1]);
        return letter is >= 'a' and <= 'j' ? letter - 'a' : null;
    }

    private static bool Governs(ContentAttributeGroup g, string modelRel)
    {
        var parsed = ContentSlot.Parse(modelRel);
        if (g.SetId >= 0 && (parsed is { } p ? ContentSlot.SetIdOf(p.SetTag) : null) is { } s
            && g.SetId != s)
            return false;

        return g.Slot is not { Length: > 0 } gs
            || ContentSlot.LabelForEquipSlot(gs) is not { } wantLabel
            || parsed is not { } mp
            || string.Equals(mp.Label, wantLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether any of this pack's toggle groups governs the model — which is what makes the visibility
    /// answer Proteus's to give, and therefore what makes it strip the model-side attribute tags. See
    /// <see cref="ContentGeometry.OwnAttributes"/>.
    /// <para/>
    /// Separate from "is anything currently hidden", and deliberately so: a group whose mask happens to show
    /// everything right now still owns the decision, and leaving the tags on in that case would hand the
    /// host item's IMC mask a veto over geometry the user asked to see.
    /// </summary>
    internal static bool GovernsModel(IReadOnlyList<ContentAttributeGroup>? groups, string modelRel)
        => groups is { Count: > 0 } && groups.Any(g => Governs(g, modelRel));

    internal static IReadOnlySet<string>? HiddenAttributes(
        IReadOnlyList<ContentAttributeGroup>? groups, string modelRel, IReadOnlyList<string> attrNames,
        IReadOnlyDictionary<string, List<string>>? selected)
    {
        if (groups is not { Count: > 0 } || attrNames.Count == 0) return null;

        HashSet<string>? hidden = null;
        foreach (var g in groups)
        {
            if (!Governs(g, modelRel)) continue;

            int mask = g.MaskFor(
                selected != null && selected.TryGetValue(g.Group, out var sel) ? sel : null);

            // Each attribute is matched to its bit BY NAME — see PartAttributeBit. A name that answers to no
            // bit is not the mask's to switch: those are the body-suppression attributes the game drives
            // from EQP, and treating them as parts is what made "+ arm belts" drive atr_nek.
            foreach (var name in attrNames)
                if (PartAttributeBit(name) is { } bit && (mask & (1 << bit)) == 0)
                    (hidden ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
        }
        return hidden;
    }

    /// <summary>Test seam for <see cref="ResolveVariant"/> — the null code it reports for an un-keyed piece
    /// is load-bearing, and a test that cannot see it cannot check it.</summary>
    internal static (string? Code, string Path)? ResolveVariantForTest(ContentPiece piece, string? modelCode)
        => ResolveVariant(piece, modelCode);

    /// <summary>
    /// How a resolved content model has to be published — or that it cannot be, for this wearer.
    /// <para/>
    /// The whole question is whether the game's race deform helps or hurts. It deforms a model by the race
    /// code of the PATH it loaded from, so:
    /// <list type="bullet">
    /// <item>a model in cut space is deformed onto the wearer, which is what every content piece has relied
    /// on since the feature existed;</item>
    /// <item>a model authored at the wearer's own race is already the right shape, so it goes on a carrier
    /// with its EQDP entry set and takes no deform at all;</item>
    /// <item>anything else — a Hrothgar reaching a Roegadyn model down the fall-through chain — would need a
    /// deform between two races that Proteus does not do, and publishing it either way is wrong. It is
    /// refused rather than rendered at the wrong size.</item>
    /// </list>
    /// Cut space is tested FIRST and that ordering is load-bearing. For a Midlander F wearing a c0201 pack
    /// all three codes are the same; taking the native arm there would move a piece that works today off an
    /// appended ring and onto a carrier, spending a host slot to change nothing.
    /// <para/>
    /// Being in the shared shape is decided on the CODE, not by comparing it to this character's cut code.
    /// Those are different questions and conflating them refused packs that work: <paramref name="cutCode"/>
    /// is voted off the paths the body was cut from, and a character whose skin comes from a whole-body
    /// model votes their own race — so an Au Ra in a c0201 pack had a resolved code matching neither arm and
    /// lost every piece to the "no race fit" branch, which exists for a different problem entirely.
    /// </summary>
    internal static ShellSurfaceKey? ContentSurface(
        ShellSurfaceKey declared, string? resolvedCode, string? wearerCode, string cutCode)
    {
        // A sidecar that names a surface by hand means it; this only decides for the default.
        if (!declared.IsBody) return declared;

        // One model for everyone. The pack named no race, so there is no race to disagree with.
        if (resolvedCode == null) return declared;

        // c0101/c0201 IS cut space, by definition — the game deforms it onto whoever wears it.
        if (ModelRace.IsSharedShape(resolvedCode)) return declared;
        if (string.Equals(resolvedCode, cutCode, StringComparison.OrdinalIgnoreCase)) return declared;
        if (wearerCode != null && string.Equals(resolvedCode, wearerCode, StringComparison.OrdinalIgnoreCase))
            return new ShellSurfaceKey(ShellSurfaceKind.Native, resolvedCode);
        return null;
    }

    /// <summary>
    /// What makes two pieces share one published material, and therefore one of the host's ten slots.
    /// <para/>
    /// Everything that decides the material's BYTES is in here and nothing else is: the mod it came from,
    /// the .mtrl file, the colour rows stamped into it, and the animated glow — which decides whether the
    /// material is the pack's own or one rebuilt onto characterscroll, and at what speed it scrolls. Two
    /// pieces agreeing on all of them would publish the same file twice and spend a slot each, so they
    /// publish it once and both draw with it. Different colours or a different glow really are a different
    /// material and legitimately cost two; merging on the effect NAME alone would silently hand one option
    /// the other's speed.
    /// <para/>
    /// The SURFACE is in the key despite having nothing to do with the bytes. A Body piece and a Face piece
    /// are allocated to different hosts — a natively-authored face must not be race-deformed, so only a
    /// carrier can hold it — and one material cannot live on two models at once however identical it is.
    /// <para/>
    /// Note what is NOT here: the model path. Sharing a material ACROSS models is the entire point. Which
    /// meshes a unit draws is <see cref="ContentGeometryKey"/>'s job, deduped inside the unit.
    /// </summary>
    internal static string ContentUnitKey(
        string modDir, ShellSurfaceKey surface, string mtrlRel, string? rowsJson, string? glowKey = null,
        string? texKey = null)
        => string.Join('\u0000', modDir, surface.ToString(), mtrlRel, rowsJson ?? "-", glowKey ?? "-", texKey ?? "-");

    /// <summary>
    /// What makes two meshes the same mesh WITHIN a unit — the resolved model, and the material its meshes
    /// are bound by.
    /// <para/>
    /// Keyed on the RESOLVED model path, never on <see cref="ContentPiece.Model"/>. That field is empty for
    /// anything the importer wrote: a model path names the race it was authored for, so the paths live in
    /// <see cref="ContentPiece.Models"/> and only a hand-authored sidecar fills Model in. Keying on it made
    /// every piece of a pack that shares one material look identical, and a mod offering a belly piercing
    /// and a hip piercing could only ever show whichever discovery reached first.
    /// </summary>
    internal static string ContentGeometryKey(string modelRel, string materialLeaf)
        => modelRel + '\u0000' + materialLeaf;

    /// <summary>
    /// The material names of a model's LOD0 meshes that actually have vertices, in declaration order.
    /// <para/>
    /// Emptied meshes are the norm in a content pack: an author starts from a stock model, deletes the
    /// vanilla geometry and adds their own, leaving zero-vertex meshes still bound to the vanilla materials.
    /// Those materials are declared but never drawn, so asking a pack to bind them would reject it over
    /// meshes that emit nothing.
    /// </summary>
    internal static List<string> UsedMaterialNames(byte[] model, List<string> declared)
    {
        var used = new List<string>();
        foreach (var name in declared)
        {
            if (used.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var leaf = name;
            if (SecondSkinWriter.TryReadLod0Geometry(model, out var pos, out _, out _,
                    SecondSkinWriter.KeepByLeaf(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { leaf.TrimStart('/') }))
                && pos.Length >= 3)
                used.Add(name);
        }
        return used;
    }

    /// <summary>
    /// Write only if the content differs; reports whether it did.
    ///
    /// Via a temp file and an atomic move, NOT WriteAllBytes. Shell output names are stable
    /// (models/secondskin_{hash}.mdl, materials/ss_{char}.mtrl — the hash is over the SOURCE, so the same
    /// source rewrites the same name), which means a rewrite truncates a file a LIVE redirect points at.
    /// That was survivable only while the composite unpublished every redirect up front; now that it
    /// doesn't, a redraw landing mid-write would read a half-written model. Same reasoning and same shape
    /// as TextureLoader.WriteWithRetry and PenumbraModMeta.AtomicWrite.
    /// </summary>
    private static bool WriteIfChanged(string path, byte[] data)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
                return false;
        }
        catch { /* unreadable — fall through and rewrite */ }

        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, data);
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, path, overwrite: true); return true; }
            catch (Exception) when (i < 5) { Thread.Sleep(50 << i); }  // the game may hold it open mid-load
            catch { try { File.Delete(tmp); } catch { } throw; }       // don't leave the temp behind
        }
    }

    /// <summary>
    /// Content hash of each shell texture we last wrote, so we can tell a real change from a rewrite of
    /// identical bytes. The shell's TEXTURES matter as much as its model: an opacity or mask edit only
    /// moves coverage, which lands in the normal map — and the game won't pick that up on an in-place
    /// reload either, because the texture belongs to an accessory rather than the body.
    /// </summary>
    private readonly Dictionary<string, ulong> _texHashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copy a pack file into the output, doing nothing when the same source is already there. Returns true
    /// when the bytes on disk changed.
    /// <para/>
    /// Not <see cref="WriteIfChanged"/>, because that reads the destination in full to compare it and the
    /// caller has already read the source in full to hand it over — so the steady state, where nothing has
    /// changed and every byte matches, is the EXPENSIVE one. Affordable for shell textures, which Proteus
    /// generates at a bounded size; not for a pack's own art. Cerise's four contested kimono textures come
    /// to 290 MB, so a composite that changed nothing was moving ~580 MB and four LOH allocations, on every
    /// gear change and several times per settle.
    /// <para/>
    /// The skip is recorded in <see cref="_texHashes"/>, the SAME memo the generated writers use, and that
    /// sharing is load-bearing rather than tidy. All of <see cref="WriteTextures"/>, the glow builder's
    /// <c>Publish</c> and its <c>Republish</c> write <c>ss_{letter}_{slot}.tex</c>, and the letter is a
    /// placement ordinal — so a path owned by a shell this composite belongs to a content unit the next.
    /// Two memos over one path never invalidate each other: the shell would regenerate identical bytes,
    /// match its own stale entry, skip, and go on sampling the texture the content unit had overwritten it
    /// with. One dictionary means whoever wrote last is what the next reader compares against.
    /// <para/>
    /// A source that cannot be stat'd falls through to the copy rather than being skipped, so the caller's
    /// own error handling still gets to see the read fail.
    /// </summary>
    private bool CopyPackFile(string srcDisk, string dstDisk)
        => CopyPackFile(_texHashes, srcDisk, dstDisk);

    /// <summary>The body of <see cref="CopyPackFile(string,string)"/> with its memo passed in, so the skip
    /// rule can be exercised without standing up the service.</summary>
    internal static bool CopyPackFile(Dictionary<string, ulong> memo, string srcDisk, string dstDisk)
    {
        ulong? stamp = null;
        try
        {
            var info = new FileInfo(srcDisk);
            if (info.Exists) stamp = StampHash(srcDisk, info.LastWriteTimeUtc.Ticks, info.Length);
        }
        catch { /* unreadable — fall through and copy, which reports properly */ }

        if (stamp is { } s && memo.TryGetValue(dstDisk, out var prev) && prev == s && File.Exists(dstDisk))
            return false;

        var wrote = WriteIfChanged(dstDisk, File.ReadAllBytes(srcDisk));
        // Recorded only once the copy is through: a throw above must not leave a memo claiming the
        // destination holds this source, or the next composite would skip the retry.
        if (stamp is { } ok) memo[dstDisk] = ok;
        return wrote;
    }

    /// <summary>
    /// Identity of a source file as a path plus its mtime and length — what a copy is memoised on.
    /// <para/>
    /// A stamp rather than a content hash, because hashing the source means READING the source, which is
    /// half the cost being avoided. The source is a file on disk that only changes when the user edits the
    /// pack, and mtime and length answer that without opening it.
    /// <para/>
    /// Salted away from <see cref="Hash"/>'s space so a stamp can never coincidentally equal a content hash
    /// for the same path — the two share one dictionary, and a false match there is a skipped write.
    /// </summary>
    private static ulong StampHash(string src, long ticks, long length)
    {
        ulong h = 14695981039346656037;   // FNV-1a, as Hash
        foreach (var c in src) { h ^= char.ToLowerInvariant(c); h *= 1099511628211; }
        h ^= (ulong)ticks;  h *= 1099511628211;
        h ^= (ulong)length; h *= 1099511628211;
        return h ^ 0x5350414D_5354414Dul;   // "STAMP" salt
    }

    // Layer count last warned about as over the host's material budget — so the chat guidance prints once
    // per changed situation, not every composite. -1 = not currently over budget.
    private int _lastOverBudgetLayers = -1;

    // Content pieces last warned about as unplaceable, so that chat notice prints once per changed
    // situation rather than every composite. -1 = everything currently fits.
    private int _lastUnhostedContent = -1;

    /// <summary>
    /// Mod directory → why none of that pack's content pieces can be worn by this character, in the words
    /// the panel shows. Empty when everything fits.
    /// <para/>
    /// Instance state rather than part of <see cref="Result"/> because <see cref="Build"/> returns null when
    /// no host took anything — which is exactly the case this has to explain. A pack built for another race,
    /// enabled on its own, produces no hosts and no result, and the reason would go nowhere.
    /// <para/>
    /// Assembled on the composite thread and swapped in as one reference, the same publish contract the
    /// compositor's own maps use.
    /// </summary>
    public volatile IReadOnlyDictionary<string, string> UnwearableContent =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Surfaces last warned about as unhostable, joined, so that guidance prints once per changed situation
    // too. Keyed on the SET rather than a count: swapping one face overlay for another keeps the count at 1
    // while being a different thing to report. Null = nothing currently unhosted.
    private string? _lastUnhostedSurfaces;

    /// <summary>Carrier slots last reported as belonging to another mod, joined, so the notice prints once
    /// per changed situation. Null = none currently claimed.</summary>
    private string? _lastClaimedCarriers;

    /// <summary>
    /// Say — once, and in chat — that a mod is sitting on an Emperor's New accessory Proteus would otherwise
    /// have used, and that Proteus left it alone. Worth a chat line for the same reason a lost redirect is:
    /// the alternative outcome is the user's piercings quietly vanishing with nothing on screen to connect
    /// it to Proteus, and the fix (free a different ring or bracelet slot) is not one anybody guesses.
    /// </summary>
    private void NotifyCarriersClaimed(IReadOnlyList<(HostAccessory Host, string By)> claimed)
    {
        // The mod FOLDER, not the file inside it — "[ninka] - basic ver. 1 (miqote)" is what the user sees
        // in Penumbra, and the path to a texture three directories down is not.
        string Owner(string disk)
        {
            var root = penumbra.GetModDirectory();
            try
            {
                if (root != null)
                {
                    var rel = Path.GetRelativePath(root, disk);
                    if (!rel.StartsWith("..", StringComparison.Ordinal))
                        return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                }
            }
            catch { /* fall through to the file name */ }
            return Path.GetFileName(disk);
        }

        var owners = claimed.Select(c => Owner(c.By)).Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var slots  = claimed.Select(c => c.Host.Slot).Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var key = string.Join("|", owners) + "#" + string.Join("|", slots);
        if (string.Equals(_lastClaimedCarriers, key, StringComparison.Ordinal)) return;
        _lastClaimedCarriers = key;

        var msg = string.Format(Loc.Localize("Chat.CarrierClaimed.Fmt",
            "[Proteus] \"{0}\" puts its own model on an Emperor's New accessory ({1}), so Proteus left that "
          + "slot alone rather than replacing it. Free a different ring or bracelet slot if you want Proteus "
          + "to have one to build on."), string.Join("\", \"", owners), string.Join(", ", slots));
        _ = Plugin.Framework.RunOnFrameworkThread(
            () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));
    }

    private static ulong Hash(byte[] data)
    {
        ulong h = 14695981039346656037;   // FNV-1a
        foreach (var b in data) { h ^= b; h *= 1099511628211; }
        return h;
    }

    /// <summary>True when the blue channel (byte 2 of each RGBA quad) is 255 across the whole buffer — i.e.
    /// the normal carries no transparency gate, so BC5 (which drops blue) is lossless for it.</summary>
    private static bool IsBlueAllWhite(byte[] rgba)
    {
        for (int i = 2; i < rgba.Length; i += 4)
            if (rgba[i] != 255) return false;
        return true;
    }

    /// <summary>
    /// Build every gear shell for the character. <paramref name="charCode"/> is the human model code
    /// ("0201" = Midlander female). <paramref name="outputRoot"/> is the managed mod directory.
    /// Returns null when there is nothing to build.
    /// </summary>
    /// <summary>
    /// The UV space an overlay's art is painted in, inferred from the body materials it targets — a mod
    /// listing only <c>*_bibo.mtrl</c> is bibo art. Returns null when the materials disagree or name no
    /// body type, in which case the art is assumed to already be in the body's space.
    /// </summary>
    /// <summary>The UV space of a body model, read from its own skin material's suffix, or null.</summary>
    private static string? SkinBodyType(byte[] model)
    {
        try
        {
            return SecondSkinWriter.MaterialNames(model)
                .Select(SecondSkinWriter.SkinMaterialBodyType)
                .FirstOrDefault(t => t != null);
        }
        catch { return null; }
    }

    private static string? InferOverlayBodyType(OverlayDescriptor d)
    {
        string? found = null;
        foreach (var p in d.MaterialGamePaths)
        {
            var t = UVRemapService.InferBodyType(p);
            if (t == null) continue;
            if (found == null) found = t;
            else if (!string.Equals(found, t, StringComparison.OrdinalIgnoreCase)) return null;   // mixed
        }
        return found;
    }

    /// <summary>
    /// Load an overlay image and, if it was painted for a different body's UV layout, remap it into the
    /// body's UV space. The shell INHERITS the body's UVs, so the destination is the character's body UV
    /// type — not the accessory material's. Mirrors CompositorService.RemapIfNeeded; keep them in step.
    /// </summary>
    private byte[]? LoadRemapped(string? rel, string sidecarRoot, string? srcType, string? dstType, int w, int h)
    {
        if (rel == null) return null;
        // Extension tolerance (metadata says diffuse.dds but the file is diffuse.png, etc.) is handled
        // centrally in TextureLoader.LoadPngAsRgba, so skin and gear resolve identically.
        var path = Path.Combine(sidecarRoot, rel);
        return RemapPath(path, srcType, dstType, w, h);
    }

    /// <summary>
    /// Remapped buffers for the composite in flight, keyed by (path, srcType, dstType, size). Cleared at
    /// the top of <see cref="Build"/>, so it never outlives one run.
    /// <para/>
    /// A cross-UV shell is the expensive case and it was doing the same work repeatedly: the SAME mask is
    /// remapped by BuildAlpha, twice more in WriteTextures (the _id merge and the relief pass) and again by
    /// BuildMaskCoverage — three or four full 4096² transfer-map remaps plus resizes, per mask, per layer.
    /// Invisible until a gen2-UV top turned up, because a bibo→bibo shell returns on the equality
    /// fast-path above and logs `remap 0ms`; the first gen2 top pushed the second-skin phase from ~1.0s to
    /// 4.6s.
    /// <para/>
    /// Safe to share the array: every mutating consumer already clones first, which is the same contract
    /// the no-op path relies on — it hands back TextureLoader's own cached buffer.
    /// <para/>
    /// CONCURRENT, and it has to be: composites genuinely overlap (see CompositorService's
    /// _compositesInFlight), <see cref="Build"/> is not locked, and this one service instance is shared —
    /// so two builds can be inside RemapPath at once while a third clears at the top of its own Build. A
    /// plain Dictionary resizing under a concurrent read does not merely lose an entry; it can spin
    /// forever in a bucket chain and hang the thread.
    /// <para/>
    /// Cross-build sharing is harmless by construction, so the worst a concurrent Clear costs is a repeat
    /// of work: the key names every input the result depends on, so an entry another build put there is
    /// exactly what this one would have computed.
    /// </summary>
    private readonly ConcurrentDictionary<(string Path, string? Src, string? Dst, int W, int H), byte[]?> remapCache = new();

    private byte[]? RemapPath(string path, string? srcType, string? dstType, int w, int h)
    {
        var png = textureLoader.LoadPngAsRgba(path, w, h);
        if (png == null || srcType == null || dstType == null) return png;
        if (string.Equals(srcType, dstType, StringComparison.OrdinalIgnoreCase)) return png;

        // Only the REMAPPING path is memoized. The two returns above are already cheap — LoadPngAsRgba has
        // its own decode cache — and caching them would duplicate that for nothing.
        var key = (path, srcType, dstType, w, h);
        if (remapCache.TryGetValue(key, out var hit)) return hit;
        var result = RemapPathCore(path, png, srcType, dstType, w, h);
        remapCache[key] = result;
        return result;
    }

    private byte[]? RemapPathCore(string path, byte[] png, string srcType, string dstType, int w, int h)
    {

        // Any source -> gen2 (vanilla): vanilla UV is the RIGHT HALF of bibo UV space, so convert to
        // bibo first (via transfer map when needed), crop, then resize.
        if (string.Equals(dstType, "gen2", StringComparison.OrdinalIgnoreCase))
        {
            var native = textureLoader.LoadPngAsRgba(path, 4096, 4096);
            if (native == null) return png;
            byte[] biboSpace;
            if (string.Equals(srcType, "bibo", StringComparison.OrdinalIgnoreCase))
            {
                biboSpace = native;
            }
            else
            {
                var converted = uvRemap.Remap(native, 4096, 4096, srcType, "bibo");
                if (ReferenceEquals(converted, native)) return png;   // no transfer map — leave it alone
                biboSpace = converted;
            }
            var rightHalf = UVRemapService.CropRightHalf(biboSpace, 4096, 4096);
            return UVRemapService.ResizeBilinear(rightHalf, 2048, 4096, w, h);
        }

        // Transfer maps operate at 4096x4096; our textures are smaller, so remap at full res then resize.
        if (w != 4096 || h != 4096)
        {
            var native4k = textureLoader.LoadPngAsRgba(path, 4096, 4096);
            if (native4k == null) return png;
            var remapped = uvRemap.Remap(native4k, 4096, 4096, srcType, dstType);
            if (ReferenceEquals(remapped, native4k)) return png;
            return UVRemapService.ResizeBilinear(remapped, 4096, 4096, w, h);
        }
        return uvRemap.Remap(png, w, h, srcType, dstType);
    }

    public Result? Build(
        string charCode,
        IReadOnlyList<(OverlayEntry Entry, ResolvedOverlay Overlay)> gearOverlays,
        string outputRoot,
        string? bodyType,
        string? effectsFolder,
        IReadOnlyDictionary<string, string>? equippedPartModels = null,
        IReadOnlyDictionary<string, string>? equippedAccessories = null,
        Func<string, bool>? gen2Allowed = null,
        int? invisibleGlassesSet = null,
        IReadOnlyList<string>? metModels = null,
        // Shape keys the game currently has enabled per body-model stem (see BodyShapeReader). Used to bake
        // body morphs (e.g. "Remove Hip Dips" = shpx_yam_softbutt) into the shell so it follows the body.
        IReadOnlyDictionary<string, HashSet<string>>? enabledBodyShapes = null,
        // Mods that carry a dedicated top mask shell (OverlayDescriptor.IsMaskShell) this build. For these,
        // the mod's OTHER shells must NOT merge the masks' _id/relief — the mask shell owns them, so merging
        // would colour the mask twice. The mask shell itself always merges (it IS the mask).
        IReadOnlySet<string>? maskShellMods = null,
        // The bare-body e0000 models the game is CURRENTLY DRAWING, per slot (see
        // CompositorService.BareBodyModelsFromModels). Ground truth for both the model code and the path a
        // bare slot is cut from: a race with no e0000 models of its own draws another race's, and only the
        // live resource says which. Null/absent slots fall back to a path built from the model code.
        IReadOnlyDictionary<string, string>? bareBodyModels = null,
        // The character's REAL race code, off a drawn chara/human/… model. charCode is the shared BODY
        // code — c0201 for every "Midlander-bodied" female — so it cannot name the race whose metadata
        // entry has to be emptied for a carrier host. Null falls back to charCode, which is right for the
        // races that ship their own body (Au Ra, Viera, Hrothgar) and merely does nothing for the rest.
        string? drawnRaceCode = null,
        // The .mtrl game paths the character is CURRENTLY drawing. Used for one thing: reading the material
        // VARIANT folder a host actually loads under. See VariantFolderFor — the model references our
        // material variant-relatively, so publishing it under the wrong variant means the game never asks
        // for it and the appended mesh renders with no material at all.
        IReadOnlySet<string>? activeMaterials = null,
        // The two CARRIERS' material variants, off their sheets. See HostAccessory.KnownVariant: a carrier
        // is equipped after the shell is built, so the live tree cannot answer for it.
        int? emperorRingVariant = null,
        int? invisibleGlassesVariant = null,
        // The character's own face/hair/tail/ear models, exactly as the live walk reported them (see
        // CompositorService.HumanPartModelsFromModels). These are the ONLY source of non-body geometry —
        // there is no rebuild-from-a-code fallback, because a human part loads from its literal path and an
        // absence here means the character genuinely is not drawing it.
        IReadOnlyList<string>? humanPartModels = null,
        // Geometry imported content packs contribute this composite — their own meshes and materials,
        // appended into the carrier verbatim rather than cut from the character. Allocated to hosts AFTER
        // the gear shells above, from whatever material capacity they leave, so a character with no content
        // packs gets a bit-identical shell allocation to before this existed.
        IReadOnlyList<(OverlayEntry Entry, ResolvedContent Content)>? contentLayers = null,
        // Every mod in the look, not just those contributing a shell. A toe cap belongs to the foot, so
        // the mod that ships the map need not be the one wearing anything over the toes.
        IReadOnlyList<OverlayEntry>? allEntries = null)
    {
        int contentIn = contentLayers?.Count ?? 0;

        // Per-build, not per-session: a remapped buffer is a 4K-derived array and holding a run's worth of
        // them across composites would dwarf the decode cache for no benefit — the inputs are re-read from
        // TextureLoader's cache anyway, and only the repetition WITHIN one build is worth avoiding.
        remapCache.Clear();

        // Cleared FIRST, so the field means "as of this build" rather than "as of some build". Nothing else
        // resets it, and several paths below return before the content loop runs — turn off the pack that
        // could not be worn and the panel would go on explaining it, having never been told otherwise.
        UnwearableContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (gearOverlays.Count == 0 && contentIn == 0) return null;

        // Which "v####" folder the game will ask for a host's materials under.
        //
        // The shell's material is stored in the model with a LEADING SLASH (see SecondSkinLayer.MaterialName
        // below), which is the variant-relative form: the game builds
        // chara/{tree}/{set}/material/v{variant}/{name} using the EQUIPPED ITEM's variant, not anything in
        // the model. This was hardcoded to v0001, so a host on any other variant had our material published
        // at a path the game never requests — and the failure was invisible from every angle we could see:
        // the redirect resolved perfectly (nothing else claims that path), the model was ours and drew, and
        // only the appended meshes silently had no material.
        //
        // Read from the live resource tree rather than guessed, so it is whatever the game actually asked
        // for. A CARRIER is the case the tree cannot answer — it is equipped after the shell is built — so
        // those pass their variant in from their item sheet; see HostAccessory.KnownVariant.
        string VariantFolderFor(HostAccessory h)
        {
            var dir = $"chara/{h.Tree}/{h.Prefix}{h.SetId:D4}/material/v";

            // The variant belongs to the EQUIPPED ITEM IN THIS SLOT, not to the set, so only a material the
            // game loaded for this slot answers the question outright. An accessory set is a jewellery set —
            // one id covers _nek, _ear, _wrs and _rir — and each worn piece carries its own variant, so
            // matching the set alone would take whichever piece the (unordered) set enumerated first and
            // publish the shell under a neighbour's variant, differently from run to run. The slot is in the
            // material's own name, so the exact answer is cheap to insist on.
            //
            // Set-wide is kept only as a recovery for the host's materials being briefly absent — a stale
            // snapshot — and ONLY when every piece of the set agrees on one variant, which is the single
            // shape in which a neighbour cannot be lying about this slot. Disagreement means the set spans
            // variants and no neighbour can speak for us, so fall through rather than flip a coin.
            var slotTag = $"_{h.Slot}_";
            string? setVariant = null;
            bool setDisagrees = false;
            if (activeMaterials != null)
                foreach (var m in activeMaterials)
                {
                    if (!m.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                    int end = m.IndexOf('/', dir.Length);
                    if (end <= dir.Length) continue;
                    var folder = m[(dir.Length - 1)..end];
                    if (m.Contains(slotTag, StringComparison.OrdinalIgnoreCase)) return folder;
                    if (setVariant == null) setVariant = folder;
                    else if (!string.Equals(setVariant, folder, StringComparison.OrdinalIgnoreCase))
                        setDisagrees = true;
                }

            if (setVariant != null && !setDisagrees) return setVariant;
            return h.KnownVariant is { } v ? $"v{v:D4}" : "v0001";
        }

        // A mask OCCLUDES everything beneath it (matches CompositorService.MaskAdds): in a mask's territory
        // every gear overlay — top group included — is erased (its coverage drops to cov·W, and W=0 where the
        // mask is opaque), so the fabric shells go transparent to skin under the mask and only the mask shell
        // renders there. A mask no longer hands its coverage to a lower shell, which would otherwise draw its
        // art/relief/colour straight over the mask.
        static bool MaskAdds(OverlayEntry e, ResolvedOverlay o) => false;

        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manipulations = new List<object>();
        // Maps each gear overlay's identity to its shell material disk file names (ss_{letter}.mtrl) — what
        // the live resource handle reports — so the colorset editor's "glow" button can target them. A key
        // can hold SEVERAL: a mod/option may carry more than one gear overlay, all baking the same shared
        // colour table, so a row's glow must reach every one of their shell materials.
        var shellMaterials = new Dictionary<(string, string?, string?), List<string>>();
        // Per mod, the content materials backing a drawn mesh — see Result.ContentMaterials for why the
        // declared set is not good enough, and why this is not the hosted set either.
        var contentMaterials = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var modelsDir = Path.Combine(outputRoot, "models");
        var materialsDir = Path.Combine(outputRoot, "materials");
        var texturesDir = Path.Combine(outputRoot, "textures");
        Directory.CreateDirectory(modelsDir);
        Directory.CreateDirectory(materialsDir);
        Directory.CreateDirectory(texturesDir);

        // ── every skin part the character is drawing, MERGED into the one ring model ──
        // The shell is a COPY of the body geometry, so it must be cut from the models the character is
        // actually drawing. A shell built from any other body/size is a different shape and the body
        // pokes through it at any push distance. Resolve them live, every time.
        // gen2 (vanilla) is opt-in per the gear mode, exactly like the skin layer's gen2 sibling — but the
        // gate is per-PART, not per-character: a bibo torso plus a vanilla skirt's exposed legs is ONE
        // shell, and only the vanilla legs must be withheld unless a gear overlay opted into "All bodies".
        // Content packs are in the "allowed" set unconditionally: they paint nothing onto the body, and the
        // body is resolved here only to derive the cut code and the hosts. Gating them out would leave a
        // vanilla-bodied wearer with no resolved parts at all and drop a pack that never touched her skin.
        bool anyGen2Allowed = gen2Allowed == null || contentIn > 0
                           || gearOverlays.Any(g => gen2Allowed(g.Entry.ModDirectory));

        // FFXIV keys EQUIPMENT to a model race, not the character's race. Viera and Hrothgar wear Midlander
        // models, race-deformed onto their own skeleton, so a c1801 character's gear, accessories AND e0000
        // parts all live at c0201 paths — the c1801 equivalents were never shipped. Skin is the opposite:
        // keyed to the real race (mt_c1801b0001_bibo.mtrl). The shell is cut from equipment models and
        // hosted on accessories, so everything in that space must use the MODEL code; charCode stays for
        // the body itself. Read it off whatever the game already resolved rather than hardcoding a race
        // table — and it is simply charCode for races that ship their own models.
        var equippedPaths = (equippedPartModels?.Values ?? Enumerable.Empty<string>())
            .Concat(equippedAccessories?.Values ?? Enumerable.Empty<string>())
            .Concat(metModels ?? Enumerable.Empty<string>())
            // NEVER count the Emperor's ring. It is OUR host: last composite redirected it, so Penumbra
            // resolves it straight back to our own output and it reports whatever code WE published it at.
            // Reading the model race off it is a feedback loop — observed live as c0101 -> build shell ->
            // publish at c1801 -> next composite reads 1801 -> every c1801e0000 part missing -> shell torn
            // down -> redraw restores c0101 -> rebuild, forever. (ChooseHosts guards the same hazard when
            // it picks a base model; this is the same trap one layer up.) Vanilla only ships a0053 at
            // c0101 anyway, so it is never evidence of anything.
            .Where(pth => !pth.Contains($"a{EmperorSetId:D4}", StringComparison.OrdinalIgnoreCase));

        // Group rather than take-the-first: a real disagreement is decided by weight of evidence, not by
        // dictionary enumeration order. On the character that exposed this, the honest gear (ril a0031 and
        // met e5501) both say 0201 while only the discounted Emperor said 0101 — and picking wrong costs
        // the ENTIRE shell, not the one stray redraw a wrong guess costs elsewhere.
        static List<IGrouping<string, string>> CodeVotes(IEnumerable<string> paths)
            => paths.Select(PathCharCode).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!)
                .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Resolve the winner, breaking an exact tie with evidence the main vote didn't get to see.
        //
        // The old tie-break was <see cref="CodeVotes"/>'s lexicographic ThenBy, which is arbitrary: on a
        // Miqo'te wearing one c0201 necklace and one c0101 ring it picked c0101 purely because "0101"
        // sorts first, then reported her own c0201 equipment as race-deformed. That is not an exotic
        // input — many vanilla accessories ship at c0101 ONLY, whatever the wearer's race, so a 1-1 split
        // across two worn accessories is the ordinary shape of this vote on a character wearing no gear.
        string PickCode(List<IGrouping<string, string>> votes, bool votedOnBare)
        {
            int best = votes[0].Count();
            var tied = votes.Where(g => g.Count() == best).Select(g => g.Key).ToList();
            if (tied.Count == 1) return tied[0];

            // Runoff 1 — the bare-body parts the game is DRAWING. Same kind of evidence as the main vote
            // (the race the game resolved this character's equipment to), held out of it because
            // uncovered slots outnumber worn items and would drown real gear. When the worn items split
            // exactly, that objection is gone and this is the only tiebreaker that is about equipment at
            // all. Skipped when the main vote already WAS the bare parts — it would just restage the tie.
            if (!votedOnBare)
            {
                var bare = CodeVotes(bareBodyModels?.Values ?? Enumerable.Empty<string>())
                    .Where(g => tied.Contains(g.Key, StringComparer.OrdinalIgnoreCase)).ToList();
                if (bare.Count == 1 || (bare.Count > 1 && bare[0].Count() > bare[1].Count()))
                    return bare[0].Key;
            }

            // Runoff 2 — the character's own body code, when the evidence is still balanced. Home beats
            // foreign: a shell hosted in the space the character already draws in needs no deformation,
            // so on a genuine coin-flip it is the choice with the smaller failure.
            if (tied.Contains(charCode, StringComparer.OrdinalIgnoreCase)) return charCode;

            // Runoff 3 — on a male/female coin flip, take the female. The alternative is CodeVotes'
            // lexicographic order, which always hands it to the male code because "0101" sorts before
            // "0201", and that is the losing side of the bet twice over: c0201 is the space nearly every
            // body mod is authored in, and being wrong toward c0101 is the failure that SHRINKS a shell
            // (male->female is a downward deform, applied to geometry already cut female — the reported
            // bug). Being wrong toward c0201 on an actually-male character is the same magnitude in the
            // other direction, but it is by far the rarer input.
            var female = tied.FirstOrDefault(c => RaceIndex(c) is { } n && n % 2 == 0);
            if (female != null) return female;

            return tied[0];   // deterministic last resort — CodeVotes already ordered these by key
        }

        var codeVotes = CodeVotes(equippedPaths);
        var voteSource = "equipped";
        var votedOnBare = false;

        // Wearing nothing: the e0000 parts the game is DRAWING are the only evidence left, and they are
        // evidence of the same kind — the race the game resolved this character's equipment to. Without
        // them a naked Au Ra fell through to the probe below, which picked her own c1401 (no e0000 models
        // exist there), so every part missed and the shell came out empty.
        //
        // Counted ONLY when nothing is equipped, never alongside gear: uncovered slots OUTNUMBER worn
        // items, and EQDP is per-item, so a character whose gear ships at her own race while her bare
        // slots fall back to Midlander would see her gear outvoted 3-to-1. modelCode gates hosting —
        // LoadCandidate skips any accessory whose path code differs — so a flipped vote would reject every
        // ring she actually wears and dump the whole look onto the undeformed Emperor fallback.
        if (codeVotes.Count == 0)
        {
            codeVotes = CodeVotes(bareBodyModels?.Values ?? Enumerable.Empty<string>());
            voteSource = "drawn bare-body";
            votedOnBare = true;
        }

        string? modelCode = null;
        if (codeVotes.Count > 0)
        {
            modelCode = PickCode(codeVotes, votedOnBare);
            if (codeVotes.Count > 1)
                log.Warning("[Proteus] second skin: {0} models disagree on a model code [{1}] — using c{2}",
                    voteSource, string.Join(", ", codeVotes.Select(g => $"{g.Key}x{g.Count()}")), modelCode);
        }
        if (modelCode == null)
        {
            // Nothing equipped, so there is no resolved path to read it off — and defaulting to charCode
            // strands exactly the races this exists for: a bare Viera would ask for c1801e0000_top.mdl,
            // which was never shipped, and end up with no shell at all. Probe instead — the character's own
            // code first, then the two bases everything else deforms from — and take the first that
            // actually has an e0000 torso.
            foreach (var cand in new[] { charCode, "0201", "0101" })
            {
                // Existence only — no need to read the model, which would pull megabytes just to discard
                // them. A mod redirect counts, else ask the game index directly.
                //
                // ResolvePlayer is NOT an existence test: Penumbra ECHOES the game path back when no mod
                // redirects it, so "!= null" was true for every candidate and the loop always stopped on the
                // first one, charCode. That silently un-did the whole point of probing — an Au Ra female
                // (c1401 ships no e0000 models; she draws Midlander c0201) asked for c1401e0000_*.mdl, all
                // four parts missed, and she got no shell at all. A redirect only counts when it resolves to
                // something OTHER than the path we asked for, and that something is a real file on disk.
                var probe = $"chara/equipment/e0000/model/c{cand}e0000_top.mdl";
                var probeDisk = penumbra.ResolvePlayer(probe);
                bool modded = probeDisk != null
                           && !string.Equals(probeDisk, probe, StringComparison.OrdinalIgnoreCase)
                           && File.Exists(probeDisk);
                if (!modded && !Plugin.DataManager.FileExists(probe)) continue;
                modelCode = cand;
                break;
            }
            modelCode ??= charCode;
        }

        // Non-null from here, and pinned into its own local rather than left to flow analysis: the block
        // above always ends in a value, but Build is long enough that the compiler stops carrying that
        // guarantee to the far end of it — and scattering `!` at each use would be asserting the same fact
        // five times with nothing to point at.
        string equipCode = modelCode;

        if (!string.Equals(modelCode, charCode, StringComparison.OrdinalIgnoreCase))
            log.Information("[Proteus] second skin: c{0} wears c{1} equipment models (race-deformed) — any "
                          + "bare slot the live walk missed is rebuilt in c{1}", charCode, modelCode);

        // Each kept part carries its bytes, the shape keys enabled on THAT body model (by stem) so the
        // writer bakes only the morphs the game is actually applying to that part, and the game path it
        // was cut from — the path's race code is what decides how the game deforms this geometry, and the
        // shell has to be hosted in that same space (see cutCode below).
        var bodies = new List<(byte[] Bytes, HashSet<string>? Shapes, string Path, string? Uv)>();
        string? modelType = null;   // UV space of the first kept part, from its own skin material
        // Bare-body slots attempted vs. missing — the whole-body fallback below fires only when EVERY one
        // of them came back missing (see there for why "any one missing" is the wrong trigger).
        int barePartsTried = 0, barePartsMissing = 0;
        foreach (var part in Parts)
        {
            // When gear is equipped in a slot, the bare-body part for that slot ISN'T drawn — the gear
            // model is, and it carries the skin it exposes posed to fit (a high heel tiptoes the foot,
            // a bikini bottom reshapes the hip, etc.), as an mt_c….b….skin mesh beside its cloth meshes.
            // Cut the shell from that equipped model so it deforms WITH the gear AND covers only the skin
            // the gear actually exposes (the hidden skin under cloth isn't in the model, so nothing pokes
            // through it); the flat bare-body e0000 would shell the whole body and float off the posed
            // skin. The skin-material filter in SecondSkinWriter keeps only the skin mesh. Slots with no
            // gear (or gear that exposes no skin) fall back to the bare body e0000.
            // Prefer the e0000 model the game is ACTUALLY drawing in this slot over one rebuilt from the
            // model code: EQDP can send a slot to a different race than the vote settled on, and the live
            // resource is the only thing that knows. Rebuild only for a slot that isn't in the live set
            // (nothing drawn there, or the walk came back empty) — same path as before.
            var bareBody = bareBodyModels != null && bareBodyModels.TryGetValue(part, out var drawnBare)
                ? drawnBare
                : $"chara/equipment/e0000/model/c{equipCode}e0000_{part}.mdl";
            var bodyGamePath = equippedPartModels != null && equippedPartModels.TryGetValue(part, out var eq)
                ? eq
                : bareBody;
            bool isBarePart = string.Equals(bodyGamePath, bareBody, StringComparison.Ordinal);
            if (isBarePart) barePartsTried++;

            // ResolvePlayer only yields a real file for MODDED models; a vanilla piece resolves to the
            // game path unchanged, so read from the game data in that case. The transcoder reads each
            // model's own vertex declaration, so vanilla and modded models both skin correctly.
            var bodyDisk = penumbra.ResolvePlayer(bodyGamePath);
            var bytes = textureLoader.LoadRawFile(bodyDisk, bodyGamePath);

            if (bytes == null)
            {
                // Only BARE-BODY misses count toward the fallback. A missing EQUIPPED model doesn't: the
                // gear is still drawn in that slot, and shelling the bare skin under it is precisely the
                // poke-through the comment above says to avoid — just skip the slot.
                if (isBarePart) barePartsMissing++;
                // Information, not Debug: when a shell fails to build this is usually the reason, and at
                // Debug it is invisible in the log level people actually run at — which has already cost
                // one round of "why did this fail?" that the log couldn't answer. Says only what it knows:
                // a corrupt mod file and a path the race doesn't ship both land here.
                log.Information("[Proteus] second skin: {0} not loadable, skipping part {1}", bodyGamePath, part);
                continue;
            }

            // The part's UV space names itself in its skin material's suffix. A vanilla (gen2) part gets
            // no shell unless a gear overlay is set to All bodies — otherwise the overlay would wear on
            // vanilla whether or not the author opted in. Ambiguity (a vanilla _a material alongside a
            // gen3 body) is avoided by reading THIS part's own model rather than the loaded-material soup.
            var partType = SkinBodyType(bytes);
            if (string.Equals(partType, "gen2", StringComparison.OrdinalIgnoreCase) && !anyGen2Allowed)
            {
                log.Information("[Proteus] second skin: {0} is vanilla (gen2) — no gear overlay opted into All bodies, skipping part", bodyGamePath);
                continue;
            }
            // Each part's own UV space, resolved path and size — the shell takes ONE uv space (the first
            // kept part's, below) and maps every part's art with it, so a part whose space differs here
            // has its VERTICES converted into that space instead (see uvConverters below). Logged per part
            // because the fallback paths resolve through Penumbra to whatever body mod owns them, which
            // can differ slot to slot: a Bibo+ heel's foot beside a gen3 torso is an ordinary wardrobe.
            // A shape FINGERPRINT of the skin geometry we are about to cut from. The shell only conforms if
            // this is the same mesh the game draws, and the two ways that fails look identical in game —
            // the body pokes through the fabric either way:
            //   - wrong variant: a body mod ships several chest sizes and ResolvePlayer handed us a
            //     different one than the character renders. A different size is a different SHAPE, so the
            //     vertex count and/or bounds differ from a known-good run of the same option.
            //   - race deformation: the cut mesh is right, but the game deforms host and body differently.
            //     Then these numbers MATCH a known-good run and the fault is in hosting, not geometry.
            // Cheap enough to always emit: the writer parses this same geometry every composite anyway.
            var shape = "(no skin geometry)";
            if (SecondSkinWriter.TryReadLod0Geometry(bytes, out var dbgPos, out _, out var dbgTri)
                && dbgPos.Length >= 3)
            {
                float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue;
                float x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
                for (int v = 0; v + 2 < dbgPos.Length; v += 3)
                {
                    if (dbgPos[v]     < x0) x0 = dbgPos[v];
                    if (dbgPos[v]     > x1) x1 = dbgPos[v];
                    if (dbgPos[v + 1] < y0) y0 = dbgPos[v + 1];
                    if (dbgPos[v + 1] > y1) y1 = dbgPos[v + 1];
                    if (dbgPos[v + 2] < z0) z0 = dbgPos[v + 2];
                    if (dbgPos[v + 2] > z1) z1 = dbgPos[v + 2];
                }
                shape = $"{dbgPos.Length / 3}v/{dbgTri.Length / 3}t bounds=[{x0:F3}..{x1:F3}, "
                      + $"{y0:F3}..{y1:F3}, {z0:F3}..{z1:F3}]";
            }

            log.Information("[Proteus] second skin part {0}: uv={1} {2} ({3} KB) skin={4} <- {5}",
                part, partType ?? "(unknown)", bodyGamePath, bytes.Length / 1024, shape,
                bodyDisk ?? "(game data)");

            // Shape keys enabled on this exact body model (matched by file stem, e.g. c0201e0000_dwn).
            HashSet<string>? partShapes = null;
            enabledBodyShapes?.TryGetValue(Interop.BodyShapeReader.Stem(bodyGamePath), out partShapes);

            bodies.Add((bytes, partShapes, bodyGamePath, partType));
            modelType ??= partType;
        }

        // ── whole-body fallback ──────────────────────────────────────────────
        // Not every race ships e0000 parts. Viera, Hrothgar and Au Ra F have none, so the game resolves
        // those paths through EQDP to another race's model and the direct path never loads. Left alone that
        // silently drops the torso and hands from the shell, leaving a fabric that renders only where some
        // equipped gear model happened to carry a skin mesh — 2 meshes where a Midlander gets 6.
        //
        // LAST resort, and a poor one. The primary answer is bareBodyModels: the e0000 model the game is
        // actually drawing in each slot, whatever race it resolved to, which IS the modded body. This fires
        // only when that live set had nothing for a slot (an empty/stale draw-object walk) AND the path
        // rebuilt from the model code missed — i.e. we know nothing about what the character draws.
        //
        // Its weakness is UV space, not shape: a body mod replaces the e0000 EQUIPMENT models (Bibo+ ships
        // c0201e0000_top/dwn/glv/sho and nothing under obj/body/…/model/), so this reads VANILLA bytes in
        // vanilla UV even for a modded character. SkinBodyType then reports gen2 and the gate below drops it
        // unless a gear overlay opted into All bodies — which is the honest outcome: a vanilla-UV shell over
        // a Bibo+ body is art in the wrong place, not a rescue. Don't "fix" that by loosening the gate.
        //
        // It is the WHOLE body and cannot be split per slot, so it REPLACES everything cut above rather
        // than stacking a second shell over skin it already covers (coincident geometry that z-fights and
        // spends the host's mesh budget twice). The cost is the gear-posed parts — a heel's tiptoed foot —
        // and one consistent shell is the better trade. Decided here rather than mid-loop so the result
        // can't depend on which slot happened to fail first.
        //
        // Trigger: EVERY bare-body slot attempted was missing, which is what "this race ships no e0000
        // models" actually looks like. Firing on any ONE missing slot would mean a single corrupt file on
        // a race that does ship them wipes the gear-posed parts that loaded perfectly well and shells bare
        // skin underneath gear the game is still drawing.
        if (barePartsTried > 0 && barePartsMissing == barePartsTried)
        {
            // b0001 is the standard body, but a few race/gender combos ship b0101, and cutting the shell
            // from the wrong one yields a plausible-looking shell of the wrong shape — worse than failing.
            // Prefer whichever body the player's MOD owns, since that is the one they are actually wearing;
            // else take the first that exists.
            // The file name carries the customization-type suffix — c1401b0001_TOP.mdl, the same "top" the
            // e0000 torso uses (Penumbra.GameData GamePaths.Mdl.Customization). Without it this asked for
            // c1401b0001.mdl, which exists for no race, so the fallback loaded nothing for ANYONE and the
            // race it exists to rescue got an empty shell with only "no whole-body model loaded" in the log.
            (byte[] Bytes, string Path, string? Disk)? pick = null;
            foreach (var bodyId in WholeBodyIds)
            {
                var wholePath = $"chara/human/c{charCode}/obj/body/{bodyId}/model/c{charCode}{bodyId}_top.mdl";
                // ResolvePlayer ECHOES the game path when nothing redirects it, so a non-null result is not
                // evidence of a mod — only a resolved path that DIFFERS and is a real file on disk is.
                var resolved = penumbra.ResolvePlayer(wholePath);
                var wholeDisk = resolved != null
                             && !string.Equals(resolved, wholePath, StringComparison.OrdinalIgnoreCase)
                             && File.Exists(resolved) ? resolved : null;
                var wholeBytes = textureLoader.LoadRawFile(wholeDisk, wholePath);
                if (wholeBytes == null) continue;
                if (wholeDisk != null) { pick = (wholeBytes, wholePath, wholeDisk); break; }
                pick ??= (wholeBytes, wholePath, wholeDisk);
            }

            if (pick is { } whole)
            {
                var wholeType = SkinBodyType(whole.Bytes);
                if (string.Equals(wholeType, "gen2", StringComparison.OrdinalIgnoreCase) && !anyGen2Allowed)
                {
                    // Warning, not Information: this is the normal outcome for a modded body (no body mod
                    // replaces the human body model, so it always reads vanilla), and it means the shell
                    // ships SHORT — with 0 parts cut above, not at all. Whoever reads the log after "my
                    // glow didn't appear" needs to see it at the level they actually run at.
                    log.Warning("[Proteus] second skin: whole-body fallback {0} is vanilla (gen2) — no gear "
                              + "overlay opted into All bodies, leaving the {1} part(s) cut above as-is. The "
                              + "live bare-body models were unavailable this composite; a redraw usually fixes it",
                              whole.Path, bodies.Count);
                }
                else
                {
                    // enabledBodyShapes is keyed by the stem of the model the GAME is drawing (e.g.
                    // c0201e0000_dwn). A race with no e0000 models of its own draws ANOTHER race's, so the
                    // whole body's stem (c1801b0001) never appears there and an exact lookup quietly bakes
                    // no morphs at all — the shell would sit off a body with "Remove Hip Dips" enabled,
                    // which is the very thing the shape-key baking exists to prevent. Fall back to the
                    // union of every enabled set: baking a shape key a model doesn't declare is a no-op,
                    // so folding in the face and other stems costs nothing.
                    HashSet<string>? wholeShapes = null;
                    if (enabledBodyShapes != null
                        && !enabledBodyShapes.TryGetValue(Interop.BodyShapeReader.Stem(whole.Path), out wholeShapes))
                    {
                        wholeShapes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var set in enabledBodyShapes.Values) wholeShapes.UnionWith(set);
                        if (wholeShapes.Count == 0) wholeShapes = null;
                    }
                    log.Information("[Proteus] second skin: a bare-body e0000 part was not loadable (usual cause: "
                                  + "c{0} ships no e0000 models and the game resolves them through EQDP) — cutting "
                                  + "the whole shell from {1} instead, replacing {2} part(s) cut above",
                                  charCode, whole.Path, bodies.Count);
                    bodies.Clear();
                    bodies.Add((whole.Bytes, wholeShapes, whole.Path, wholeType));
                    modelType = wholeType;
                }
            }
            else
            {
                // Silence here once cost a debugging round: the trigger fired, nothing loaded, and the log
                // said nothing at all — leaving a shell short of geometry with no line explaining why.
                log.Information("[Proteus] second skin: every bare-body e0000 part was missing and no whole-body "
                              + "model loaded for c{0} either (tried {1}) — the shell keeps only the {2} part(s) "
                              + "cut from equipped gear", charCode, string.Join(", ", WholeBodyIds), bodies.Count);
            }
        }

        if (bodies.Count == 0)
        {
            log.Warning("[Proteus] second skin: no skin models resolved for c{0} (or all parts gated out)", charCode);
            return null;
        }

        if (modelType != null && !string.Equals(modelType, bodyType, StringComparison.OrdinalIgnoreCase))
        {
            log.Information("[Proteus] second skin: body UV is {0} per the model's material (was {1})",
                modelType, bodyType ?? "unknown");
            bodyType = modelType;
        }

        // ── one shell, one UV space ───────────────────────────────────────────────────────────
        // The shell is ONE mesh set painted by ONE art set, and that art is remapped into `bodyType`
        // once — so every part's UVs have to be in that space or the art lands somewhere else on it.
        // Parts routinely disagree: the slots are cut from whatever mod owns each one, so a Bibo+ heel
        // (bibo UV feet) sits beside a gen3 torso perfectly normally. Left alone, the odd part samples
        // the art at coordinates meant for another layout — which reads as the garment simply MISSING
        // there, because the art it lands in is the empty 80% of the sheet, not as visible smearing.
        //
        // Fix it on the geometry rather than the art: rewrite that part's vertices into the shell's
        // space. Converting the art per part instead would need a second material per divergent space,
        // and materials are the scarce resource here (10 per host, shared with the layer stack).
        //
        // Also repairs the coverage trim for free — it tests each triangle's UV footprint against the
        // art, so with the UVs corrected the divergent part stops being trimmed against the wrong region.
        var uvConverters = new List<Func<float, float, (float U, float V)?>?>(bodies.Count);
        foreach (var b in bodies)
        {
            var conv = uvRemap.UvConverter(b.Uv, bodyType);
            uvConverters.Add(conv);
            if (b.Uv == null || string.Equals(b.Uv, bodyType, StringComparison.OrdinalIgnoreCase)) continue;
            string partUv = b.Uv, shellUv = bodyType ?? "unknown";
            if (conv != null)
                log.Information("[Proteus] second skin: {0} is {1}-UV in a {2}-UV shell — converting its "
                              + "vertices to {2}", b.Path, partUv, shellUv);
            else
                // Not fatal, and not silent: this part renders as if the overlay had no art there.
                log.Warning("[Proteus] second skin: {0} is {1}-UV in a {2}-UV shell and no transfer map "
                          + "covers that pair — the overlay will not land on this part", b.Path, partUv, shellUv);
        }

        // ── the space the geometry is IN ──────────────────────────────────────────────────────
        // The game race-deforms a model according to the race code of the PATH it loaded it from. The body
        // this shell copies is drawn from these exact paths, so a shell hosted under the same code deforms
        // with it, and a shell hosted under any other code does not.
        //
        // This is NOT the equipment code voted above. Those are different EQDP chains and they disagree in
        // ordinary cases: a Miqo'te female draws c0201 e0000 body parts (Midlander, deformed 0201->0801)
        // while her facewear ships native at c0801. That mismatch is why her shell renders Midlander-sized.
        //
        // Knowing it does NOT let us fix it by moving the path, which build #294 tried and had to undo:
        // every host a character can offer — worn accessory, facewear, or the Emperor ring the EQDP entry
        // conjures — loads in that character's own space, so requiring cut space leaves no host at all. It
        // is kept because it is the honest diagnosis (logged per composite, warned on per host) and because
        // the eventual fix reads it: deform the geometry from cut space into the host's space ourselves,
        // the way TexTools race-converts, before writing the shell.
        //
        // Majority, because one host serves the whole shell: a race-native gear top cut beside bare c0201
        // legs is genuinely two spaces at once. Ties and unreadable paths fall back to the equipment code.
        var cutVotes = CodeVotes(bodies.Select(b => b.Path));
        var cutCode = equipCode;
        if (cutVotes.Count == 1
            // A tie means the shell is half in each space and neither is more right than the other, so
            // keep the equipment code rather than letting grouping order decide it.
            || (cutVotes.Count > 1 && cutVotes[0].Count() > cutVotes[1].Count()))
            cutCode = cutVotes[0].Key;
        if (cutVotes.Count > 1)
            log.Warning("[Proteus] second skin: the cut parts are in more than one model space [{0}] — hosting "
                      + "in c{1}; the other part(s) will be deformed differently from the body they copy",
                string.Join(", ", cutVotes.Select(g => $"{g.Key}x{g.Count()}")), cutCode);
        // The tally, ALWAYS — not just on the multi-space warning above. A unanimous vote can still be
        // unanimously wrong (every bare part rebuilt from a bad modelCode votes the same bad code, which
        // is how a c0101 cutCode reached a Midlander female), and without the breakdown the log said only
        // WHICH code won, never on what evidence. That is the difference between a report that identifies
        // the bug and one that just confirms the symptom.
        log.Information("[Proteus] second skin: cut in c{0} space ({1} part(s), votes [{2}]) — a host that "
                      + "loads under a different code will render it a race-size wrong",
            cutCode, bodies.Count,
            cutVotes.Count > 0
                ? string.Join(", ", cutVotes.Select(g => $"c{g.Key}x{g.Count()}"))
                : $"no readable path codes, fell back to the equipment code c{equipCode}");

        // ── the surfaces this build cuts from ─────────────────────────────────────────────────
        // The body first — it is the only surface assembled from everything resolved above, and the only one
        // that can span several hosts. Human-part surfaces are appended below as the layers that need them
        // are grouped. The list is why the code beneath stops reading `bodies`/`cutCode`/`bodyType` as
        // ambient facts about "the" shell and asks a surface instead.
        //
        // Every source is a BODY part, so all of them take the default body-skin mesh filter and the
        // configured connector heuristic. Those were three arrays index-aligned with `bodies` by convention;
        // see SecondSkinWriter.SourceSpec for why they are one thing now.
        bool skipConnectors = config.HideConnectorMeshes == ConnectorMeshMode.Neolithe;
        var bodySurface = new ResolvedSurface(
            new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty),
            bodies.Select((b, i) => new SecondSkinWriter.SourceSpec(
                b.Bytes,
                KeepMaterial: null,
                EnabledShapes: b.Shapes,
                UvConv: i < uvConverters.Count ? uvConverters[i] : null,
                DropConnectors: skipConnectors)).ToList(),
            bodies.Select(b => b.Path).ToList(),
            cutCode,
            bodyType);
        var surfaces = new List<ResolvedSurface> { bodySurface };

        // ── which surface each layer paints ───────────────────────────────────────────────────
        // From the overlay's own declared material, which is the only statement a mod makes about where it
        // lives. Two fall back to the body: a synthesized MASK shell (its coverage art is body-UV by
        // construction and it names no material), and an overlay naming no material at all — the latter
        // could not be placed either way, so it keeps the behaviour it had.
        ShellSurfaceKey SurfaceKeyOf(ResolvedOverlay ov)
        {
            if (ov.Descriptor.IsMaskShell) return bodySurface.Key;
            var keys = ShellSurface.KeysFor(ov.Descriptor.MaterialGamePaths);
            if (keys.Count == 0) return bodySurface.Key;
            if (keys.Count > 1)
                // One overlay painting two surfaces needs one layer per surface — each has its own geometry
                // and its own coverage — and that split is not built yet. Take the first and SAY so, rather
                // than silently painting one surface's art onto another's mesh.
                log.Warning("[Proteus] second skin: overlay \"{0}/{1}\" names {2} surfaces [{3}] — only {4} is "
                          + "cut; split it into one overlay per surface to get the rest",
                    ov.OptionGroup ?? "", ov.Option ?? "", keys.Count, string.Join(", ", keys), keys[0]);
            return keys[0];
        }

        // The .mdl folder a human part's models live under, matching ShellSurfaceKind — or null for a kind
        // that names no such folder.
        //
        // Body and Native are the two, for opposite reasons: a Body surface is cut from equipment, and a
        // Native one is a pack's OWN geometry published at a race rather than a part cut from the character.
        // Neither has a chara/human folder to read from, and the old catch-all quietly called both "zear"
        // and went looking for Viera ears.
        //
        // Null rather than a throw. The kind is deserialised from a sidecar someone can hand-edit, and JSON
        // will map a number naming no member straight onto the enum; throwing turned one unresolvable
        // overlay into a failed composite that loses every shell, where returning null drops that overlay
        // alone with a line saying so — which is what the resolver already does for every other way a
        // surface can fail to resolve.
        static string? PartFolder(ShellSurfaceKind kind) => kind switch
        {
            ShellSurfaceKind.Face => "face",
            // The eyes live in the face's folder and are cut from a face model. Only the SURFACE is
            // separate — see ShellSurfaceKind.Iris — so the search is the same one.
            ShellSurfaceKind.Iris => "face",
            ShellSurfaceKind.Hair => "hair",
            ShellSurfaceKind.Tail => "tail",
            ShellSurfaceKind.Ear  => "zear",
            _                     => null,
        };

        // Resolve one human-part surface: the model the character is DRAWING for it, cut down to the meshes
        // bound to the material the overlay named.
        //
        // No fallbacks, unlike the body. Every fallback in the body resolver exists because equipment is
        // EQDP-indirected and the direct path can legitimately miss; a human part is loaded from its literal
        // path, so if the live walk did not report it the character is not wearing it and there is nothing
        // to cut. Guessing here would cut a shell for a face she isn't wearing.
        ResolvedSurface? ResolveHumanSurface(ShellSurfaceKey key, IReadOnlySet<string> targetLeaves)
        {
            if (PartFolder(key.Kind) is not { } part)
            {
                log.Warning("[Proteus] second skin: {0} overlay(s) skipped — {1} names no human part to cut "
                          + "from. Check the Surface in this mod's sidecar", key, key.Kind);
                return null;
            }

            var folder = $"/obj/{part}/{key.Id}/";
            var candidates = (humanPartModels ?? [])
                .Where(p => p.Contains(folder, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0)
            {
                log.Warning("[Proteus] second skin: {0} overlay(s) skipped — the character is not drawing a "
                          + "model for {1} (live walk saw [{2}])",
                    key, key, string.Join(", ", humanPartModels ?? []));
                return null;
            }

            // A part can draw several models (a face ships eyes and brows beside the face itself). Take the
            // one that actually DECLARES the targeted material rather than the first — the material is what
            // the overlay named, so it is the only unambiguous way to say which model it meant.
            string? pick = null;
            byte[]? pickBytes = null;
            foreach (var cand in candidates)
            {
                var bytes = textureLoader.LoadRawFile(penumbra.ResolvePlayer(cand), cand);
                if (bytes == null) continue;
                pickBytes ??= bytes; pick ??= cand;      // first loadable, as the fallback
                List<string> mats;
                try { mats = SecondSkinWriter.MaterialNames(bytes); }
                catch { continue; }
                if (!mats.Any(m => targetLeaves.Contains(m.TrimStart('/')))) continue;
                pick = cand; pickBytes = bytes;
                break;
            }
            if (pick == null || pickBytes == null)
            {
                log.Warning("[Proteus] second skin: {0} — none of the {1} drawn model(s) could be read, skipping",
                    key, candidates.Count);
                return null;
            }

            var keep = SecondSkinWriter.KeepByLeaf(targetLeaves);
            var shape = "(no matching geometry)";
            if (SecondSkinWriter.TryReadLod0Geometry(pickBytes, out var hPos, out _, out var hTri, keep)
                && hPos.Length >= 3)
                shape = $"{hPos.Length / 3}v/{hTri.Length / 3}t";

            HashSet<string>? partShapes = null;
            enabledBodyShapes?.TryGetValue(Interop.BodyShapeReader.Stem(pick), out partShapes);

            // Its own path's race code, with no vote: there is one source and it is authored at the
            // character's own race, which is exactly why it must be hosted with no deform.
            var hCut = PathCharCode(pick) ?? charCode;
            log.Information("[Proteus] second skin part {0}: {1} ({2} KB) geometry={3} materials=[{4}] cut in c{5}",
                key, pick, pickBytes.Length / 1024, shape, string.Join(", ", targetLeaves), hCut);

            if (shape == "(no matching geometry)")
            {
                log.Warning("[Proteus] second skin: {0} — no mesh in {1} uses [{2}], so there is nothing to "
                          + "cut. The overlay names a material this model does not carry",
                    key, pick, string.Join(", ", targetLeaves));
                return null;
            }

            return new ResolvedSurface(
                key,
                [new SecondSkinWriter.SourceSpec(
                    pickBytes,
                    KeepMaterial: keep,
                    EnabledShapes: partShapes,
                    UvConv: null,             // native: a face's art is authored in the face's own layout
                    DropConnectors: false)],  // the connector heuristic is body-tuned; it eats real geometry here
                [pick],
                hCut,
                null);                        // no remappable UV space — there are no transfer maps for a face
        }

        // Group the layers by surface, resolving each non-body surface once. A layer whose surface cannot be
        // resolved is dropped here, before it can consume a host slot or a disk letter.
        //
        // layerSurfaceName remembers each layer's surface as TEXT, so nothing downstream has to re-derive it:
        // SurfaceKeyOf logs when an overlay spans two surfaces, and calling it a second time to build a
        // message logged that warning twice.
        var layerSurface = new int[gearOverlays.Count];
        var layerSurfaceName = new string[gearOverlays.Count];
        var resolvedByKey = new Dictionary<ShellSurfaceKey, int> { [bodySurface.Key] = 0 };
        var droppedLayers = new HashSet<int>();
        for (int i = 0; i < gearOverlays.Count; i++)
        {
            var key = SurfaceKeyOf(gearOverlays[i].Overlay);
            layerSurfaceName[i] = key.ToString();
            if (resolvedByKey.TryGetValue(key, out var known)) { layerSurface[i] = known; continue; }

            var leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, ov) in gearOverlays)
                if (SurfaceKeyOf(ov).Equals(key))
                    foreach (var mp in ov.Descriptor.MaterialGamePaths)
                        if (!string.IsNullOrEmpty(mp)) leaves.Add(Path.GetFileName(mp));

            var resolved = ResolveHumanSurface(key, leaves);
            if (resolved == null) { resolvedByKey[key] = -1; layerSurface[i] = -1; continue; }
            surfaces.Add(resolved);
            resolvedByKey[key] = surfaces.Count - 1;
            layerSurface[i] = surfaces.Count - 1;
        }
        for (int i = 0; i < gearOverlays.Count; i++)
            if (layerSurface[i] < 0) droppedLayers.Add(i);

        // ── imported content, resolved into units before anything is allocated ────────────────
        // A UNIT is one published MATERIAL and every mesh drawn with it, because a material is what costs a
        // slot on the host. Pieces that want the same .mtrl with the same colours therefore land in one unit
        // and spend one slot between them — a pack of five piercings on a single material is the ordinary
        // shape, and charging it five of ten would be most of the budget for one mod.
        //
        // Resolved here, before anything is allocated: a piece that cannot be built must never consume
        // capacity a shell could have used, and its reason is reported once rather than once per host.
        var contentUnits = new List<ContentUnit>();
        var unitByKey = new Dictionary<string, ContentUnit>(StringComparer.OrdinalIgnoreCase);
        var unitGeometry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Model path → its bytes and the materials its LOD0 meshes actually draw with. A null Model is a
        // file that could not be read, cached so the warning is printed once rather than per option.
        // Attrs rides along with Used because both are properties of the MODEL, not of the option that
        // named it — and a pack whose options all point at one file was re-parsing it once per option just
        // to read the same attribute table back.
        var modelCache =
            new Dictionary<string, (byte[]? Model, List<string> Used, List<string> Attrs)>(
                StringComparer.OrdinalIgnoreCase);

        // Mod directory → why none of its pieces can be worn, for the panel to say out loud. Recorded per
        // MOD rather than per piece: a pack authored for one race fails identically for every piece it has,
        // and fifteen copies of the same sentence is not more informative than one. First reason wins.
        // (EST slot, set id) → the skeleton already claimed for it this build. EST holds one entry per body
        // part, so this is what stops two packs writing contradictory manipulations for one item and lets
        // the second one be reported instead of silently losing.
        //
        // The slot is lower-cased into the key. Everything that CONSUMES it is case-insensitive — EstPartKey
        // lower-cases before matching — so keying case-sensitively would let "Body" and "body" occupy two
        // entries, emit two contradictory manipulations for one body part, and skip the very warning that
        // exists to catch that.
        var estClaimed = new Dictionary<(string Slot, int SetId), int>();

        // Game path → the pack file Penumbra currently resolves it to, for this build only. See
        // ContentMaterialFile: the lookup runs per drawn material of every layer, and the answer is a
        // property of the path rather than of the layer asking.
        var mtrlFileCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Extra-skeleton claims noted while resolving content, written only for the options that actually
        // reached a host. Deferred because the entry lands on an item that is NOT the pack's: claiming a
        // chest piece's skeleton for geometry that never published would break that item's own ex bones in
        // exchange for nothing, and a pack whose materials all fail to bind — or that loses its host to the
        // material budget — is exactly that case.
        var estPending = new List<((string Mod, string? Group, string? Option) Owner, string Slot, int Entry)>();

        var unwearable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Held rather than logged on the spot, and emitted below once it is known whether the pack got
        // ANYTHING through. A refused piece is only news when it is the whole story: a pack that ships a
        // garment in default data and overrides one race of it per size legitimately refuses the
        // unconditional copy on exactly the character wearing that garment through a size option, and
        // logging inline meant a healthy mod filed a race warning on every composite.
        var refusals = new List<(string ModDir, string Group, string Option, string Reason)>();
        void Unwearable(string modDir, string reason, string? group, string? option)
        {
            if (!unwearable.ContainsKey(modDir)) unwearable[modDir] = reason;
            refusals.Add((modDir, group ?? "", option ?? "", reason));
        }

        // A mod's live Penumbra selection, fetched once per mod per composite. Only the IMC hide-toggles
        // need it — every other gate is resolved upstream into the content layers — and a pack's options do
        // not change mid-build, so asking again for each of its nine materials would be nine round trips
        // for one answer. Null (Penumbra unavailable, or the mod unknown to it) leaves every toggle at the
        // pack's own default, which is the state it ships in.
        //
        // Marshalled ONTO THE FRAMEWORK THREAD, unlike the ResolvePlayer calls elsewhere in this build.
        // This reads Penumbra's collection state, which a user editing a collection is concurrently
        // writing, and every other caller of GetModSettings in this plugin is already on that thread — the
        // draw loop and the ModSettingChanged handler. Blocking for a frame is affordable because the cache
        // makes it once per mod; the same trade CompositorService makes for GetActivePlayerMaterialPaths.
        var selectionCache = new Dictionary<string, IReadOnlyDictionary<string, List<string>>?>(
            StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, List<string>>? ModSelection(string modDir)
        {
            if (selectionCache.TryGetValue(modDir, out var known)) return known;
            IReadOnlyDictionary<string, List<string>>? found = null;
            try
            {
                found = Plugin.Framework.RunOnFrameworkThread(() =>
                        penumbra.GetPlayerCollectionId() is { } id
                            ? penumbra.GetModSettings(id, modDir)?.Options
                            : null)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                log.Warning("[Proteus] content: could not read {0}'s Penumbra settings ({1}) — its hide "
                          + "toggles fall back to the pack's defaults", modDir, ex.Message);
            }
            return selectionCache[modDir] = found;
        }

        for (int i = 0; i < contentIn; i++)
        {
            var (cEntry, rc) = contentLayers![i];
            var piece = rc.Piece;
            var modRoot = cEntry.ModRoot;
            // TWO codes, and they are not the same question.
            //
            // equipCode is what this character's GEAR loads at — usually the shared c0201/c0101, because most
            // sets ship no per-race model. charCode is the character's own race. A pack in the shared shape
            // is found by the first; a pack built for one race is found only by the second, and looking for
            // it under equipCode is why a Miqo'te could not wear a Miqo'te-authored pack: their gear still
            // loads at c0201, so the c0801 model was never even a candidate.
            //
            // Shared shape first, so a pack offering both keeps the cheaper, deform-able path.
            var ownCode = drawnRaceCode ?? charCode;
            var variant = ResolveVariant(piece, equipCode)
                       ?? ResolveVariant(piece, ownCode);
            if (modRoot == null || variant is not { } v)
            {
                // Named by RACE, not by code. "ships [c0801]" tells a modder something and tells everyone
                // else nothing, and this is the message that has to explain an enabled pack showing nothing.
                var reason = string.Format(Strings.Content.NotForYourRaceFmt,
                    ModelRace.DescribeAll(piece.ModelCodes), ModelRace.Describe(ownCode));
                Unwearable(cEntry.ModDirectory, reason, rc.OptionGroup, rc.Option);
                continue;
            }
            var modelRel = v.Path;

            // Cut space or native — see ContentSurface. Null means the model is authored for neither, and
            // publishing it either way would put it on the character at the wrong size.
            var surfaceKey = ContentSurface(piece.SurfaceKey, v.Code, ownCode, bodySurface.CutCode);
            if (surfaceKey is not { } pieceSurface)
            {
                var reason = string.Format(Strings.Content.NoRaceFitFmt,
                    ModelRace.Describe(v.Code), ModelRace.Describe(ownCode));
                Unwearable(cEntry.ModDirectory, reason, rc.OptionGroup, rc.Option);
                continue;
            }

            // Read and inspected ONCE per file, however many options name it. Two options binding different
            // meshes of one .mdl is ordinary, and the scan below is not cheap — UsedMaterialNames walks the
            // LOD0 geometry once per declared material. Handing back the same byte[] also lets the writer's
            // reference-keyed parse cache recognise it as one model rather than parsing it twice.
            var modelPath = Path.Combine(modRoot, modelRel);
            if (!modelCache.TryGetValue(modelPath, out var parsedModel))
            {
                try
                {
                    var bytes = File.ReadAllBytes(modelPath);
                    // The attribute table fails on its own terms: it is what the pack's hide toggles resolve
                    // through, and losing it costs those toggles, not the piece. Everything else about a
                    // model whose submesh ranges will not walk still reads.
                    List<string> attrs;
                    try { attrs = [.. SecondSkinWriter.AttributeNames(bytes)]; }
                    catch (Exception ex)
                    {
                        attrs = [];
                        log.Warning("[Proteus] content: {0} — could not read {1}'s attribute table, so its "
                                  + "hide toggles do nothing ({2})", cEntry.ModDirectory, modelRel, ex.Message);
                    }
                    parsedModel = (bytes, UsedMaterialNames(bytes, SecondSkinWriter.MaterialNames(bytes)), attrs);
                }
                catch (Exception ex)
                {
                    log.Warning("[Proteus] content: {0} \"{1}/{2}\" — {3} could not be read as a model ({4})",
                        cEntry.ModDirectory, rc.OptionGroup ?? "", rc.Option ?? "", modelRel, ex.Message);
                    parsedModel = (null, [], []);
                }
                modelCache[modelPath] = parsedModel;
            }
            if (parsedModel.Model == null) continue;   // unreadable, and already reported
            var model = parsedModel.Model;

            // Which of the model's materials actually carry geometry. A pack commonly ships a stock model
            // with the vanilla meshes emptied out (0 vertices) and its own mesh added, so the materials on
            // those empty meshes are declared but never drawn — demanding a binding for them would reject
            // a pack over meshes that emit nothing.
            var used = parsedModel.Used;
            if (used.Count == 0)
            {
                log.Warning("[Proteus] content: {0} \"{1}/{2}\" — {3} has no LOD0 geometry at all, skipping",
                    cEntry.ModDirectory, rc.OptionGroup ?? "", rc.Option ?? "", modelRel);
                continue;
            }

            // The pack's own hide-toggles, applied by DROPPING geometry rather than by letting the game do
            // it. The game reads an IMC attribute mask off the item being worn, and these meshes are about
            // to move onto a host accessory — so the pack's own mask governs a set nobody has equipped.
            // See ContentAttributeGroup.
            IReadOnlySet<string>? hidden = null;
            // Whether this pack's IMC toggles govern this model at all. When they do, Proteus owns the
            // visibility answer end to end — it drops what the mask hides AND strips the tags from what
            // survives, so the host accessory's own IMC mask cannot overrule the half we kept. See
            // ContentGeometry.OwnAttributes.
            bool ownAttributes = false;
            if (cEntry.Metadata.ContentAttributes is { Count: > 0 } attrGroups)
            {
                hidden = HiddenAttributes(attrGroups, modelRel, parsedModel.Attrs,
                    ModSelection(cEntry.ModDirectory));
                ownAttributes = GovernsModel(attrGroups, modelRel);
                if (hidden is { Count: > 0 })
                    log.Information("[Proteus] content: {0} — {1} hides [{2}]",
                        cEntry.ModDirectory, modelRel, string.Join(", ", hidden));
            }

            // The extra skeleton this piece's "ex" bones live in — NOTED here, written far below once the
            // piece is known to have reached a host. See ContentSkeleton and EstManipulation: the pack
            // declares the entry against the set it replaces, and this geometry is about to leave that set
            // for a host accessory, which has no EST of its own, so the bones would never load.
            //
            // Tagged with this LAYER's option so the claim can be matched against what actually published.
            // A record from the pack's default data carries no option of its own and is noted against every
            // layer, which is what makes it fire if any one of them lands.
            foreach (var skel in cEntry.Metadata.ContentSkeletons ?? [])
                if (skel.Group == null
                    || (string.Equals(skel.Group, rc.OptionGroup, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(skel.Option, rc.Option, StringComparison.OrdinalIgnoreCase)))
                    estPending.Add(((cEntry.ModDirectory, rc.OptionGroup, rc.Option), skel.Slot, skel.Entry));

            foreach (var leaf in used)
            {
                // Binding is by NAME and never guessed — see ContentPiece.Materials. A mesh whose material
                // the pack does not ship is dropped, loudly, with the fix in the message: the alternative is
                // binding it to whatever else is lying around, which renders a metal piercing as skin.
                var rel = piece.MaterialFor(leaf);
                if (rel == null)
                {
                    // A mesh bound to the BODY's own material is a different thing entirely, and dropping it
                    // is the wanted outcome rather than a shortfall. Outfit packs ship the body they were
                    // fitted to so the garment sits right in Penumbra; the character here already has their
                    // own, and publishing a second one would put a whole duplicate body on them. Said
                    // quietly and without advice, because "rebind and re-export" is the wrong instruction —
                    // there is nothing to fix.
                    if (SecondSkinWriter.IsBodySkinMaterial(leaf))
                        log.Information("[Proteus] content: {0} \"{1}/{2}\" — {3} is the body's own material, "
                                      + "so those meshes are left to the character's own skin",
                            cEntry.ModDirectory, rc.OptionGroup ?? "", rc.Option ?? "", leaf);
                    else
                        log.Warning("[Proteus] content: {0} \"{1}/{2}\" — mesh material {3} is not bound to any "
                                  + "material this pack ships, so those meshes are dropped. Rebind the mesh to "
                                  + "one of [{4}] and re-export",
                            cEntry.ModDirectory, rc.OptionGroup ?? "", rc.Option ?? "", leaf,
                            string.Join(", ", piece.Materials.Values));
                    continue;
                }

                // The file to publish. Three questions in order, and the order is the point.
                //
                // First: which of THIS PACK'S options supplies it, which is what a print or dye group is
                // asking. Then Penumbra, for a pack whose layout the option map cannot describe. Then the
                // file the importer froze.
                //
                // The pack's own options come first because Penumbra answers a different question — who wins
                // this game path across every installed mod. A second mod claiming it wins, the resolve
                // lands outside this mod, ContentMaterialFile rightly refuses it, and the frozen choice gets
                // published however the pack's own options are set. That is Cerise: "Royally Bundled Bun"
                // claims its kimono material, so every print rendered as whichever one the import baked.
                var mtrlDisk = SelectedMaterialFile(modRoot, piece.SourcesFor(leaf),
                                   ModSelection(cEntry.ModDirectory))
                            ?? ContentMaterialFile(modRoot, piece.GamePathsFor(leaf), mtrlFileCache)
                            ?? Path.Combine(modRoot, rel);

                byte[] mtrl;
                try { mtrl = File.ReadAllBytes(mtrlDisk); }
                catch (Exception ex)
                {
                    log.Warning("[Proteus] content: {0} \"{1}/{2}\" — material {3} could not be read ({4})",
                        cEntry.ModDirectory, rc.OptionGroup ?? "", rc.Option ?? "", mtrlDisk, ex.Message);
                    continue;
                }

                // Per-MATERIAL settings win over the option's. That is where the colour panel writes, because
                // a tab governs a material: a pack holding nine accessories in one always-on piece has one
                // option and nine materials, and per-option storage gave all nine tabs the same settings.
                // The option's values remain the fallback, so packs edited before that keep their colours.
                var matSettings = cEntry.Metadata.PeekMaterialSettings(rel);
                var rowPresets = matSettings?.ColorTableRows ?? rc.ColorTableRows;
                var rows = BuildSparseRows(rowPresets);

                // Only a glow that actually names an effect counts. A preset left behind with its numbers
                // but no scroll map is not a glow, and treating it as one would split a material slot for
                // nothing.
                var glowSource = matSettings?.Glow ?? rc.Glow;
                var glow = glowSource?.GlowKey() != null ? glowSource : null;

                // The textures the selection puts behind this material — the half of a print that is not in
                // the .mtrl at all. In the unit key for the same reason the rows are: two options that share
                // a material but not its textures are two materials to publish.
                var texFiles = SelectedTextureFiles(modRoot, piece, mtrl, ModSelection(cEntry.ModDirectory));

                var key = ContentUnitKey(cEntry.ModDirectory, pieceSurface, rel,
                    rows == null ? null : JsonSerializer.Serialize(rowPresets), glow?.GlowKey(),
                    TextureKey(texFiles));

                if (!unitByKey.TryGetValue(key, out var unit))
                {
                    unitByKey[key] = unit =
                        new ContentUnit(mtrl, rel, rows, glow, pieceSurface, [], [], texFiles);
                    contentUnits.Add(unit);
                }

                // Recorded HERE, where the material is known to back a drawn mesh — not down at the emit
                // loop, which only sees the units a host had room for. A piece the user can see but that
                // spilled past the material budget still needs its colour grid; see Result.ContentMaterials.
                if (!contentMaterials.TryGetValue(cEntry.ModDirectory, out var modMats))
                    contentMaterials[cEntry.ModDirectory] =
                        modMats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                modMats.Add(rel);

                // Same mesh of the same model twice — an option listing a piece it already lists, or two
                // options sharing one file — is still drawn once.
                if (unitGeometry.Add(key + '\u0000' + ContentGeometryKey(modelRel, leaf)))
                    unit.Geometries.Add(new ContentGeometry(model,
                        SecondSkinWriter.KeepByLeaf(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            { leaf.TrimStart('/') }),
                        // characterscroll samples its scroll map with uv1, so a glowing mesh is the only
                        // content mesh that is not copied byte-for-byte. Per GEOMETRY rather than per layer:
                        // the glow belongs to the unit, and the unit is what owns these.
                        MirrorUv1: glow != null,
                        // The pack's own hide toggles, baked in — see ContentGeometry.HiddenAttributes.
                        HiddenAttributes: hidden,
                        OwnAttributes: ownAttributes));

                // Every option this material serves, so the colour editor can find it under any of them.
                // Compared case-insensitively, as option names are everywhere else in this codebase.
                if (!unit.Owners.Any(o =>
                        string.Equals(o.Content.OptionGroup, rc.OptionGroup, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(o.Content.Option, rc.Option, StringComparison.OrdinalIgnoreCase)))
                    unit.Owners.Add((cEntry, rc));
            }
        }

        // A mod that got SOMETHING through is not unwearable, whatever else it dropped. The field means "why
        // NONE of that pack's pieces can be worn" and the panel treats it that way — it paints the mod amber
        // and returns before the colour grid — so one refused piece must not speak for the pack.
        //
        // It bites on an ordinary pack now: one that ships a garment in default data and overrides one race
        // of it per size leaves the unconditional copy with no model for exactly the race its size options
        // cover (the importer drops the shadowed path), so that piece is legitimately refused on the very
        // character wearing the garment through the size option.
        var wore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in contentUnits)
        {
            if (unit.Geometries.Count == 0) continue;
            foreach (var (owner, _) in unit.Owners) wore.Add(owner.ModDirectory);
        }
        foreach (var mod in wore) unwearable.Remove(mod);

        // The held refusals, now that "did the pack get anything through" has an answer. Same predicate as
        // the panel's, so the log and the panel cannot disagree about whether a mod is in trouble.
        foreach (var (modDir, group, option, reason) in refusals)
        {
            if (wore.Contains(modDir))
                log.Debug("[Proteus] content: {0} \"{1}/{2}\" — {3} (other pieces of it are worn)",
                    modDir, group, option, reason);
            else
                log.Warning("[Proteus] content: {0} \"{1}/{2}\" — {3}", modDir, group, option, reason);
        }

        // Published the moment the loop that fills it ends, and NOT later. Sitting it beside the host
        // allocation looked equivalent and was not: a pack refused for its race is the run where nothing
        // gets hosted, so the build returns at the `placed == 0` guard well before that point and the reason
        // never reached the panel — which then said "no active options", the exact unhelpful line this
        // exists to replace. It appeared only when some unrelated overlay happened to publish something.
        UnwearableContent = unwearable;

        // The surfaces those units live on, resolved AFTER the gear layers so a key both use is the one cut
        // from real geometry. A surface introduced HERE carries no sources at all: the piece brought its own
        // meshes, so the surface exists only to name the race space it was authored in and — through
        // RequiresNativeHost — which hosts are allowed to carry it.
        var unitSurface = new int[contentUnits.Count];
        var contentByKey = new Dictionary<ShellSurfaceKey, int>();
        for (int i = 0; i < contentUnits.Count; i++)
        {
            var key = contentUnits[i].Surface;
            if (resolvedByKey.TryGetValue(key, out var known) && known >= 0) { unitSurface[i] = known; continue; }
            if (contentByKey.TryGetValue(key, out var made)) { unitSurface[i] = made; continue; }

            // A natively-authored part is already the character's own shape, so its space is the character's
            // own race — not the shared equipment cut space the body lives in.
            var cut = key.IsBody ? bodySurface.CutCode : (drawnRaceCode ?? charCode);
            surfaces.Add(new ResolvedSurface(key, [], [], cut, null));
            contentByKey[key] = surfaces.Count - 1;
            unitSurface[i] = surfaces.Count - 1;
        }

        // Accessories the shell can spill across, in fill priority (glasses -> rings -> bracelet -> necklace
        // -> Emperor fallback). Each holds MaxMaterials - BaseMatCount layers; layers are distributed across
        // them so a big look can span several items. An already-equipped host APPENDS; the Emperor REPLACES.
        //
        // Chosen against the BODY's cut space. With one surface that is simply the shell's space; with more
        // than one, the body is the surface that has to be able to spill across several hosts, and the
        // others are carrier-only anyway (ShellSurfaceKey.RequiresNativeHost).
        // The packs whose geometry this build is placing. A carrier slot is left alone when another mod's
        // model is on it — but these are not "another mod": their meshes are about to become the shell, and
        // an import can legitimately leave a .mdl redirect behind (StripModelRedirects spares the options it
        // REFUSED, so their pieces keep working under Penumbra). Without this, such a pack vetoes the very
        // carrier its own content needs, the layers that need a native host are dropped as unhosted, and
        // nothing on screen says why.
        var hostedPackRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cEntry, _) in contentLayers ?? [])
            if (cEntry.ModRoot is { Length: > 0 } r) hostedPackRoots.Add(r);

        var hosts = ChooseHosts(bodySurface.CutCode, equipCode, drawnRaceCode ?? charCode,
            equippedAccessories, metModels, invisibleGlassesSet, outputRoot, hostedPackRoots,
            emperorRingVariant, invisibleGlassesVariant);

        // Which surface each host carries. A host is one model at one path with one EQDP entry, so it can
        // only ever serve layers whose surfaces agree on a race code — hence an index per host rather than a
        // free-for-all. All body today.
        var hostSurface = new int[hosts.Count];

        // ── per-host publish decision, resolved once for BOTH loops below ──────────────────────────────
        // The material loop names the materials baked into each shell, and the host loop publishes the
        // model — and the two MUST agree on the race code, because a material is looked up under the code
        // its model loads at. Deciding this inside the host loop (where it started) left the material loop
        // still naming everything at cutCode while the model went out at hostRace, which is a shell with
        // materials the game will never find. One computation, both consumers.
        //
        // hostRace: the race whose EQDP entry the game consults — the CHARACTER's real one, never the code
        // of the path the model happens to load from right now. Reading it off host.ModelPath was a feedback
        // loop against our own fix, and it alternated in the wild on a Miqo'te wearing the injected glasses:
        //
        //   composite A  the walk sees c0801e5501_met (her native facewear). 0801 != cutCode 0201, so the
        //                pair fires: c0801 is emptied, the shell is published at both codes, and the game
        //                duly falls through to the c0201 twin. Suit on.
        //   composite B  the walk now sees c0201e5501_met — because of A. 0201 == cutCode, so the test goes
        //                FALSE, no manipulation is emitted, and only the c0201 path is published. With
        //                c0801's entry no longer emptied the game asks for c0801, where we publish nothing.
        //                Suit off.
        //   composite C  the walk sees c0801 again… and round it goes.
        //
        // That is "the suit disappears when I drag a slider, and comes back when I refresh". drawnRaceCode
        // is read off the drawn chara/human model, which our redirects cannot move, so it is stable. The
        // path code stays as a FALLBACK for the first composite of a login, before any walk has returned a
        // human model; charCode is the last resort.
        //
        // Native: cut space is NOT on the wearer's fall-through chain, so the usual trick — empty the
        // wearer's entry and let the game walk to cut space, inheriting the deform — would land it on the
        // wrong body. Put a c0101 shell in front of a Midlander female and vanilla fall-through (c0201 ->
        // c0101 is the game's own hop) applies the male->female deform to geometry already cut female: it
        // renders, shrunk and low, which is exactly what two Midlanders reported. cutCode is only a VOTE
        // over the paths the parts came from — a label — while the geometry was copied from what this
        // character actually draws, so when the label is incoherent with the wearer, trust the wearer:
        // publish at hostRace, declare THAT race has the model, and the game loads it with no deform.
        //
        // Carriers only. An APPEND host redirects host.ModelPath — the path the game already resolved for
        // the player's own item — so a mismatch there renders a race-size wrong rather than wrong-bodied,
        // which build #294 established beats no shell. Those keep WarnForeignAppendHost and are untouched.
        // Cap total placeable layers at the single-char base-36 disk-id space (0-9a-z = 36). Any excess
        // folds into the over-budget drop path below, so a disk id can never run past 'z' into filesystem-
        // reserved chars. 36 is far beyond the practical geometric limit (~15 stacked shells).
        int totalCapacity = Math.Min(hosts.Sum(h => SecondSkinWriter.MaxMaterials - h.BaseMatCount), DiskIdSpace);

        // Only a shell whose bytes actually differ from what's on disk needs a full redraw.
        bool shellChanged = false;

        // Layers assigned to each host, filled in order. Two letters per layer: the in-model MATERIAL INDEX
        // (host base + position within that host) so appended names don't collide with the host's own, and a
        // globally-unique DISK letter so two hosts never overwrite the same ss_<letter> file on disk (the
        // ghost/highlighter also parse that single letter — see ShellNormalGhost).
        var perHostLayers = new List<SecondSkinLayer>[hosts.Count];
        for (int h = 0; h < hosts.Count; h++) perHostLayers[h] = new List<SecondSkinLayer>();

        int diskLetter = 0;
        int maskLayers = 0, clothLayers = 0;    // successfully placed
        int overBudget = 0, overBudgetMask = 0; // real layers that ran out of accessory capacity
        // Layers with no host that could carry their SURFACE — a different failure from running out of
        // capacity, and one the over-budget advice cannot fix. Recorded as the actual layer indices so the
        // count and the surface names below come from one source: deriving the names from "everything not in
        // work" instead swept up capacity-dropped layers, so a look that overflowed by two body layers
        // reported "Body" as unhostable and told the user to free a ring slot, which would not help.
        var unhostedLayers = new List<int>();

        // ── Layer → host distribution ──────────────────────────────────────────
        // Layers arrive bottom-first with the mask LAST (it must render on top). Accessory hosts draw in the
        // order ChooseHosts returns them, the FIRST drawing IN FRONT. So the TOP layers (including the mask)
        // fill the first host and lower layers spill to the hosts behind it — otherwise the mask, being last,
        // would spill onto the rearmost host (e.g. the Emperor fallback ring) and render BEHIND the fabric.
        // Within a host the layers stay in stack order so the topmost gets the highest material index (= drawn
        // last = on top). If the look exceeds total capacity the BOTTOM layers drop, never the mask. A look
        // that fits on ONE host is unchanged (same order as before).
        //
        // Now done per SURFACE, body first. Two rules make that necessary rather than tidy:
        //   - a host is one model at one path with ONE EQDP entry, so it can only carry layers whose surfaces
        //     agree on a race code (see ResolvedSurface);
        //   - a natively-authored surface needs its host published with no deform, which only a CARRIER can
        //     promise — an append host's metadata belongs to the player's own item and is not ours to move.
        // Body runs first and takes exactly what it always took, so a character with no human-part overlays
        // gets a bit-identical allocation.
        var remaining = new int[hosts.Count];
        for (int i = 0; i < hosts.Count; i++)
            remaining[i] = SecondSkinWriter.MaxMaterials - hosts[i].BaseMatCount;
        var hostClaim = new int?[hosts.Count];     // surface index that has taken this host
        int diskBudget = DiskIdSpace;              // the base-36 cap, now enforced across all surfaces

        var work = new List<(int LayerIdx, int HostIdx)>();
        // Surface order: body, then the rest in the order they were resolved. Body's priority is absolute —
        // it never yields a host to a human part.
        foreach (var surfIdx in Enumerable.Range(0, surfaces.Count))
        {
            var surf = surfaces[surfIdx];
            var layerIdxs = new List<int>();
            for (int i = 0; i < gearOverlays.Count; i++)
                if (layerSurface[i] == surfIdx) layerIdxs.Add(i);
            if (layerIdxs.Count == 0) continue;

            bool carrierOnly = surf.Key.RequiresNativeHost;
            var eligible = new List<int>();
            for (int i = 0; i < hosts.Count; i++)
            {
                if (remaining[i] <= 0) continue;
                // A carrier is the only host whose EQDP we may rewrite, so it is the only one that can
                // publish a native surface undeformed.
                if (carrierOnly && hosts[i].BaseModel != null) continue;
                // Already taken by ANOTHER SURFACE. Identity, not cut-code equality — a host is built from
                // exactly one surface's sources (hostSurface below), so two surfaces sharing it means the
                // second one's geometry silently replaces the first's for every layer on that host.
                //
                // Matching cut codes are not sufficient and testing them here was a real bug: they only make
                // the EQDP publish compatible, which says nothing about the GEOMETRY. On a Midlander female
                // the body cuts at c0201 and her face is c0201f0002 — same code — so a face layer would join
                // a carrier the body had partly filled, the host would be rebuilt from the single face model,
                // and her body layers would render cut from face geometry. It does not reproduce on a race
                // whose face code differs from its equipment code (an Au Ra cuts body c0201, face c1401),
                // which is exactly why in-game testing did not surface it.
                if (hostClaim[i] is { } claimed && claimed != surfIdx) continue;
                eligible.Add(i);
            }

            int capacity = Math.Min(eligible.Sum(i => remaining[i]), diskBudget);
            if (capacity == 0 && carrierOnly)
            {
                // Skipped, not squeezed. A native surface on a deforming host renders visibly wrong — a face
                // shell scaled by a race delta sits off the face — and unlike the body there is no version of
                // that worth shipping. Reported separately from a capacity overflow, because the remedy is
                // different: free a ring or facewear SLOT, not "equip another accessory".
                unhostedLayers.AddRange(layerIdxs);
                log.Warning("[Proteus] second skin: {0} — {1} layer(s) skipped, no host can carry it. It must "
                          + "not be race-deformed, so it needs a slot Proteus can replace outright (a free "
                          + "ring, or the facewear slot); the {2} host(s) available are all append hosts or "
                          + "already full",
                    surf.Key, layerIdxs.Count, hosts.Count);
                continue;
            }

            int placeable = Math.Min(layerIdxs.Count, capacity);
            int dropCount = layerIdxs.Count - placeable;
            int cursor = layerIdxs.Count - 1;              // the TOP layer of THIS surface (its mask)
            foreach (var h in eligible)
            {
                if (cursor < dropCount) break;
                int take = Math.Min(remaining[h], cursor - dropCount + 1);
                take = Math.Min(take, diskBudget);
                if (take <= 0) break;
                for (int k = cursor - take + 1; k <= cursor; k++)   // ascending → topmost lands last (highest idx)
                    work.Add((layerIdxs[k], h));
                remaining[h] -= take;
                diskBudget -= take;
                hostClaim[h] = surfIdx;
                hostSurface[h] = surfIdx;
                cursor -= take;
            }
            for (int k = 0; k < dropCount; k++)            // the dropped bottom layers = over budget
            {
                overBudget++;
                if (gearOverlays[layerIdxs[k]].Overlay.Descriptor.IsMaskShell) overBudgetMask++;
            }
        }
        // Layers whose surface could not be resolved at all (the character isn't drawing that part, or its
        // model names no such material). Already logged in detail by the resolver.
        unhostedLayers.AddRange(droppedLayers);

        // ── content units take what the shells left ───────────────────────────
        // After the shells, and out of the same remaining[]/hostClaim[]/diskBudget state, so a character
        // with no content packs gets the allocation it always got. One slot per unit, first host that has
        // room and is allowed to carry that surface; a unit that finds none is reported, never squeezed in
        // over a shell.
        var contentWork = new List<(int Unit, int HostIdx)>();
        var contentUnhosted = new List<int>();
        for (int u = 0; u < contentUnits.Count; u++)
        {
            int surfIdx = unitSurface[u];
            bool carrierOnly = surfaces[surfIdx].Key.RequiresNativeHost;
            int chosen = -1;
            for (int h = 0; h < hosts.Count && diskBudget > 0; h++)
            {
                if (remaining[h] <= 0) continue;
                // A carrier is the only host whose EQDP we may rewrite, so it is the only one that can
                // publish a natively-authored piece without the game deforming it.
                if (carrierOnly && hosts[h].BaseModel != null) continue;
                if (hostClaim[h] is { } claimed && claimed != surfIdx) continue;
                chosen = h;
                break;
            }
            if (chosen < 0) { contentUnhosted.Add(u); continue; }

            remaining[chosen]--;
            diskBudget--;
            hostClaim[chosen] = surfIdx;
            hostSurface[chosen] = surfIdx;
            contentWork.Add((u, chosen));
        }

        // Per host, and reading THAT host's surface's cut code — not an ambient one. Computed AFTER the
        // allocation, because which surface a host carries is what the allocation decides. This is what lets
        // a host carrying a natively-authored surface reach the no-deform branch while a body host beside it
        // still arranges its fall-through.
        var plan = new (string HostRace, bool Native, string PublishCode)[hosts.Count];
        for (int i = 0; i < hosts.Count; i++)
        {
            var h0 = hosts[i];
            var hSurf = surfaces[hostSurface[i]];
            var hCut = hSurf.CutCode;
            var race = drawnRaceCode
                    ?? (h0.ModelPath != null ? PathCharCode(h0.ModelPath) : null)
                    ?? charCode;
            // A natively-authored surface is ALREADY the right shape for this character, so any deform is
            // damage — never fall through, whatever the codes happen to say.
            bool native = h0.BaseModel == null
                       && (hSurf.Key.RequiresNativeHost
                        || (!string.Equals(race, hCut, StringComparison.OrdinalIgnoreCase)
                            && !CanFallThrough(race, hCut)));
            if (native && !hSurf.Key.RequiresNativeHost)
                log.Warning("[Proteus] second skin: host {0}{1:D4}/{2} — the shell claims to be cut in c{3}, "
                          + "which is not on c{4}'s fall-through chain. Publishing NATIVELY at c{4} instead "
                          + "(no deform); one of the two codes is wrong and c{3} is the suspect",
                    h0.Prefix, h0.SetId, h0.Slot, hCut, race);
            plan[i] = (race, native, native ? race : hCut);
        }

        // ── Sibling-relief pre-pass ──────────────────────────────────────────────
        // Each cloth overlay keeps its own shell, but two opaque shells at the same body position OCCLUDE
        // rather than blend — so a ribbing/relief hidden behind a sibling fabric never shows. Fix: additively
        // compound every overlay's normal into its SAME-MOD sibling shells, gated by that overlay's own
        // coverage (baked into the normal's alpha lane so CompoundNormal's src-alpha gate masks it). Whichever
        // shell wins the depth test then carries the combined relief. Only R/G is written, so blue (each
        // shell's own coverage gate) is untouched — the diffuse and index are never affected.
        //
        // Coverage (BuildAlpha) is computed here ONCE per non-mask overlay and reused as the shell's own alpha
        // below, so it isn't computed — or logged — twice.
        // The UV space a layer's ART must end up in: its own surface's. For the body that is the shell's body
        // type and the art is remapped into it. For every human part it is NATIVE — a face overlay is painted
        // in that face's own layout, there is no transfer map to or from it, and there never will be. Both
        // ends are forced to null there, because a stray SourceBodyType left in a mod's metadata would
        // otherwise run a bibo->gen3 BODY remap across face art.
        (string? Src, string? Dst) UvFor(int layerIdx, OverlayDescriptor d)
        {
            var s = surfaces[layerSurface[layerIdx] >= 0 ? layerSurface[layerIdx] : 0];
            return s.Key.IsBody
                ? (d.SourceBodyType ?? InferOverlayBodyType(d), s.UvSpace)
                : (null, null);
        }

        /// <summary>How far this layer's surface wants its shell pushed off the skin — see
        /// <see cref="ShellSurfaceKey.PushScale"/>. Resolved the same way as the UV pair above.</summary>
        float PushFor(int layerIdx)
            => surfaces[layerSurface[layerIdx] >= 0 ? layerSurface[layerIdx] : 0].Key.PushScale;

        byte[]?[] alphaByLayer = new byte[gearOverlays.Count][];
        var reliefContribs = new List<(string ModDir, int LayerIdx, byte[] Normal)>();
        for (int i = 0; i < gearOverlays.Count; i++)
        {
            var (rEntry, rOv) = gearOverlays[i];
            var rd = rOv.Descriptor;
            if (rd.IsMaskShell) continue;   // mask coverage/relief is handled by BuildMaskCoverage
            if (layerSurface[i] < 0) continue;   // surface unresolved — the layer is not being built
            var (rSrc, rDst) = UvFor(i, rd);
            var rAlpha = BuildAlpha(rd, rEntry, rSrc, rDst, TexSize, TexSize, MaskAdds(rEntry, rOv));
            alphaByLayer[i] = rAlpha;
            if (rd.Normal == null || rAlpha == null) continue;
            var rNormal = LoadRemapped(rd.Normal, rEntry.SidecarRoot, rSrc, rDst, TexSize, TexSize);
            if (rNormal == null) continue;
            rNormal = (byte[])rNormal.Clone();   // LoadRemapped may hand back a shared cached buffer
            int nn = Math.Min(rAlpha.Length, rNormal.Length / 4);
            for (int p = 0; p < nn; p++) rNormal[p * 4 + 3] = rAlpha[p];   // coverage → alpha lane (the gate)
            reliefContribs.Add((rEntry.ModDirectory, i, rNormal));
        }

        // A toe cap belongs to the FOOT, not to the mod that happens to ship the map. One mod paints it
        // and every shell over those toes is rebuilt with it — otherwise a wardrobe of stockings needs the
        // same map copied into each one, and any shell missing it sleeves the toes while its neighbour
        // caps them. The map is remapped into the body's UV using its own mod's source type, so it is
        // shared as body-UV pixels that any shell can use.
        byte[]? sharedToeCap = null;
        var capCandidates = (allEntries ?? gearOverlays.Select(g => g.Entry).ToList())
            .GroupBy(e => e.ModDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
        foreach (var tEntry in capCandidates)
        {
            var tPath = discovery.ResolveActiveToeCap(tEntry);
            if (tPath == null) continue;

            // Remapped with the OWNING mod's UV space; after that it is body-UV pixels anyone can use.
            var tDesc = gearOverlays.FirstOrDefault(g =>
                string.Equals(g.Entry.ModDirectory, tEntry.ModDirectory, StringComparison.OrdinalIgnoreCase)).Overlay?.Descriptor;
            var tSrc = tDesc != null ? tDesc.SourceBodyType ?? InferOverlayBodyType(tDesc) : bodyType;
            sharedToeCap = ReadToeCap(tPath, tSrc, bodyType);
            if (sharedToeCap != null)
            {
                log.Information("[Proteus] second skin: toe cap {0} from \"{1}\" applies to every shell over the toes",
                    Path.GetFileName(tPath), tEntry.ModDirectory);
                break;
            }
        }

        var inHost = new int[hosts.Count];
        foreach (var (i, hIdx) in work)
        {
            var (entry, ov) = gearOverlays[i];
            bool isMaskShell = ov.Descriptor.IsMaskShell;
            var host = hosts[hIdx];

            string shader = ov.Descriptor.ShaderPackage;
            char matLetter = (char)('a' + host.BaseMatCount + inHost[hIdx]);   // in-model material index (per-host, <= 'j')
            char diskChar  = DiskId(diskLetter);                               // globally-unique disk id (base-36, 0-9a-z)
            // Materials live INSIDE the host's own model, so name them with the code that model is loaded
            // under — the equipped host's real resolved path, or the rebuild's publish code for a carrier
            // (see mdlGamePath below). On an append host this also keeps our added letters matching the
            // base's own material names instead of mixing two codes inside one model.
            //
            // plan[hIdx].PublishCode, NOT cutCode: the two are the same except on the native-publish path,
            // and hardcoding cutCode there names materials the game would look for under a different code
            // and never find. That is why the plan is computed before this loop rather than inside the
            // host loop below.
            var hostCode = host.ModelPath != null
                ? PathCharCode(host.ModelPath) ?? plan[hIdx].PublishCode
                : plan[hIdx].PublishCode;
            string matName = $"mt_c{hostCode}{host.Prefix}{host.SetId:D4}_{host.Slot}_{matLetter}.mtrl";
            string matVariant = VariantFolderFor(host);
            string matGamePath = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/material/{matVariant}/{matName}";
            string texPrefix   = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/texture/ss_{diskChar}_";

            // Which UV space is this art painted in, and which must it end up in? A mod listing only
            // *_bibo.mtrl is bibo art; the gear layer has no material-match gate like the skin layer, so the
            // remap into the body's UV is explicit. A human-part layer is native at both ends — see UvFor.
            var layerSurf = surfaces[layerSurface[i] >= 0 ? layerSurface[i] : 0];
            var (srcType, dstType) = UvFor(i, ov.Descriptor);
            log.Information("[Proteus] gear layer mat={0}/{10}/disk={1} -> host {2}{3:D4}/{4}: shader={5} UV {6}->{7}{8}{9} [{11}]",
                matLetter, diskChar, host.Prefix, host.SetId, host.Slot, shader, srcType ?? "(unknown)", dstType ?? "(native)",
                srcType != null && dstType != null && !string.Equals(srcType, dstType, StringComparison.OrdinalIgnoreCase) ? " [REMAP]" : "",
                isMaskShell ? " [MASK SHELL]" : "", matVariant, layerSurf.Key);

            // The mask shell's coverage IS the mask; other shells' coverage is the overlay's art shaped by masks.
            bool mergeMasks = isMaskShell || !(maskShellMods?.Contains(entry.ModDirectory) ?? false);
            var alpha = isMaskShell
                ? BuildMaskCoverage(entry, srcType, dstType, TexSize, TexSize)
                : alphaByLayer[i];   // computed once in the sibling-relief pre-pass above

            // Error-drops (below) don't consume a host slot — inHost/diskLetter only advance on a full success.
            // A null coverage means the art failed to load or the overlay is empty (BuildAlpha logged why).
            // Drop the shell rather than render it fully opaque — a fabric with no coverage gate covers the
            // WHOLE body and the masks never carve it (this masked a diffuse.dds/.png extension mismatch).
            if (alpha == null) continue;
            var coverage = Downsample(alpha, TexSize, TexSize, CoverageSize);

            // Same-mod siblings' relief compounds into this fabric shell (never into a mask shell — its normal
            // IS the mask relief). Self is excluded so a shell doesn't double-stamp its own normal.
            // Same SURFACE as well as same mod. A sibling's normal is stamped at the sibling's own UV
            // coordinates, so compounding a face overlay's relief into a body shell would carve face detail
            // across the torso at face UVs — the surface check is what keeps relief inside one atlas.
            var siblingReliefs = isMaskShell
                ? null
                : reliefContribs.Where(c => c.LayerIdx != i
                        && layerSurface[c.LayerIdx] == layerSurface[i]
                        && string.Equals(c.ModDir, entry.ModDirectory, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Normal).ToList();

            var texPaths = WriteTextures(entry, ov.Descriptor, shader, texPrefix, texturesDir, redirects, diskChar,
                alpha, srcType, dstType, ov.ColorTableRows, effectsFolder, ref shellChanged, mergeMasks, siblingReliefs);
            if (texPaths == null) continue;

            var template = textureLoader.LoadRawMtrl(null, GearMaterialWriter.TemplateFor(shader));
            if (template == null) { log.Error("[Proteus] second skin: missing template material for {0}", shader); continue; }

            var scroll = new ScrollSettings(
                ov.Descriptor.ScrollSpeedX ?? ScrollSettings.Default.SpeedX,
                ov.Descriptor.ScrollSpeedY ?? ScrollSettings.Default.SpeedY,
                ov.Descriptor.ScrollTilingX ?? ScrollSettings.Default.TilingX,
                ov.Descriptor.ScrollTilingY ?? ScrollSettings.Default.TilingY);

            byte[] mtrl;
            // A mask shell's colour lives in the colorset over a WHITE base (no diffuse of its own), so the
            // colorset diffuse must be linearised to render at the authored (sRGB) value — matching the skin
            // bake. Fabric shells carry colour in their base texture with a white colorset, so they don't.
            try { mtrl = GearMaterialWriter.Build(template, texPaths, BuildRows(ov.ColorTableRows, neutralWhenEmpty: isMaskShell), scroll, config.GearCutoutAlpha, linearizeDiffuse: isMaskShell); }
            catch (Exception ex) { log.Error(ex, "[Proteus] second skin: material build failed for {0}", shader); continue; }

            var matDisk = Path.Combine(materialsDir, $"ss_{diskChar}.mtrl");
            shellChanged |= WriteIfChanged(matDisk, mtrl);
            redirects[matGamePath] = Rel(outputRoot, matDisk);
            var shellKey = (entry.ModDirectory, ov.OptionGroup, ov.Option);
            if (!shellMaterials.TryGetValue(shellKey, out var shellList))
                shellMaterials[shellKey] = shellList = new List<string>();
            shellList.Add($"ss_{diskChar}.mtrl");

            // A shell follows every body contour, so hosiery sleeves each toe unless the toe area is
            // marked — then the writer cuts that region out and rebuilds it as one rounded cap.
            // BODY SURFACES ONLY. The map is body UV and the cap is a foot; handing one to a face or a
            // tail layer would cut its geometry against a mask painted for another atlas entirely, and
            // the coverage gate below would be comparing body-UV texels to face-UV alpha.
            var toeCap = layerSurf.Key.IsBody
                ? ToeCapFor(ov.Descriptor, entry, srcType, dstType, sharedToeCap, alpha)
                : null;
            perHostLayers[hIdx].Add(new SecondSkinLayer
            {
                MaterialName = "/" + matName,   // the model stores material names with a leading slash
                Coverage = coverage,
                CoverageWidth = coverage == null ? 0 : CoverageSize,
                CoverageHeight = coverage == null ? 0 : CoverageSize,
                PushScale = PushFor(i),
                ToeCap = toeCap,
                ToeCapWidth = toeCap == null ? 0 : ToeCapSize,
                ToeCapHeight = toeCap == null ? 0 : ToeCapSize,
                ToeCapStrength = Math.Clamp(ov.Descriptor.ToeCapStrength ?? 1f, 0f, 1f),
            });
            inHost[hIdx]++; diskLetter++;       // slot consumed
            if (isMaskShell) maskLayers++; else clothLayers++;
        }

        // ── imported content: the pack's own meshes and its own material ──────────────────────
        // Everything about the host — its name, its variant folder, its material letter — comes from the
        // same convention the shells above use, because from the host's side there is no difference: a
        // content unit is one more material on the accessory. What differs is what fills it. The .mtrl is
        // the PACK'S, published byte-for-byte (colour rows aside): it already names its own textures and its
        // own shader, and those textures are still served by the pack's own Penumbra redirects, so there is
        // nothing here for Proteus to bake.
        int contentPlaced = 0;
        foreach (var (u, hIdx) in contentWork)
        {
            var unit = contentUnits[u];
            var host = hosts[hIdx];
            char matLetter = (char)('a' + host.BaseMatCount + inHost[hIdx]);
            char diskChar  = DiskId(diskLetter);

            var hostCode = host.ModelPath != null
                ? PathCharCode(host.ModelPath) ?? plan[hIdx].PublishCode
                : plan[hIdx].PublishCode;
            string matName     = $"mt_c{hostCode}{host.Prefix}{host.SetId:D4}_{host.Slot}_{matLetter}.mtrl";
            string matVariant  = VariantFolderFor(host);
            string matGamePath = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/material/{matVariant}/{matName}";
            string texPrefix   = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/texture/ss_{diskChar}_";

            byte[]? mtrl;
            bool glowBuilt = false;
            if (unit.Glow != null)
            {
                mtrl = BuildContentGlowMaterial(unit, texPrefix, texturesDir, diskChar, effectsFolder,
                    redirects, ref shellChanged);
                glowBuilt = mtrl != null;
                // The pack's own material rather than nothing: an effect file that has gone missing, or a
                // template the game could not hand us, must not take the piece off the character.
                mtrl ??= GearMaterialWriter.PatchColorTable(unit.Mtrl, unit.Rows);
            }
            else
            {
                // Only the colour rows the user edited are stamped in; every other row stays as the author
                // left it. PatchColorTable no-ops on a material with no colour set, so a pack that ships one
                // is not a requirement — it just cannot be recoloured.
                mtrl = GearMaterialWriter.PatchColorTable(unit.Mtrl, unit.Rows);
            }

            var matDisk = Path.Combine(materialsDir, $"ss_{diskChar}.mtrl");
            shellChanged |= WriteIfChanged(matDisk, mtrl);
            redirects[matGamePath] = Rel(outputRoot, matDisk);

            // ── the pack's own textures, republished ──────────────────────────────
            //
            // The material keeps naming its textures at the pack's paths — nothing is rewritten inside the
            // .mtrl — but Proteus now serves those paths itself, from the files the selection chose. That is
            // what makes a print group work: all four Cerise prints name the same four paths, so leaving
            // them to Penumbra put the print in the hands of whichever installed mod won the path.
            //
            // Copied rather than pointed at. A Penumbra mod's file map is relative to the mod folder, so a
            // redirect cannot reach into the pack's own directory, and the copy is what makes Proteus's
            // output stand on its own — the source mod can be disabled and the piece still draws.
            //
            // Only textures a SELECTED option supplies are here (see SelectedTextureFiles). A pack with no
            // per-option textures produces an empty map and nothing below runs, which is every pack that
            // worked before this.
            //
            // Skipped entirely for a material the glow builder rebuilt: that one names texPrefix paths it
            // published itself — from the same unit.TexFiles — so redirecting the PACK'S paths as well would
            // copy files nothing reads, and would hand a spurious "two materials want different files"
            // warning to whichever non-glow unit legitimately claims the same path. A glow that FAILED to
            // build falls back to the pack's own material, which does name these paths, so it belongs here.
            int texIdx = 0;
            var republish = glowBuilt
                ? Enumerable.Empty<KeyValuePair<string, string>>()
                : unit.TexFiles.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var (texGamePath, srcDisk) in republish)
            {
                var dstDisk = Path.Combine(texturesDir, $"ct_{diskChar}_{texIdx++}.tex");
                try { shellChanged |= CopyPackFile(srcDisk, dstDisk); }
                catch (Exception ex)
                {
                    // Left to Penumbra, which is where it was before — a texture that will not copy is not
                    // worth dropping the piece over.
                    log.Warning("[Proteus] content: {0} — could not republish {1} from {2} ({3}); that "
                              + "texture falls back to whichever mod Penumbra resolves it to",
                        unit.Entry.ModDirectory, texGamePath, srcDisk, ex.Message);
                    continue;
                }

                // Two units claiming one texture path with DIFFERENT files cannot both win — the map has one
                // slot per path. Said out loud rather than silently letting the last one through, because the
                // symptom (one piece wearing another's print) reads as this fix having failed.
                var relTex = Rel(outputRoot, dstDisk);
                if (redirects.TryGetValue(texGamePath, out var already)
                 && !string.Equals(already, relTex, StringComparison.OrdinalIgnoreCase))
                    log.Warning("[Proteus] content: {0} — two materials want different files at {1}; the "
                              + "later one wins and the earlier piece may show the wrong texture",
                        unit.Entry.ModDirectory, texGamePath);

                redirects[texGamePath] = relTex;
            }

            // Same "ss_" naming as a shell, deliberately: ShellColorsetApplier and ColorTableHighlighter
            // both key on that prefix and on the single disk char, so a content material gets the live
            // colour re-assert and the editor's glow highlight for free.
            //
            // Registered under EVERY option this material serves. One shared material is reached from any of
            // the options that share it, and keying it to only the first would leave the colour editor's
            // glow button pointing at nothing for all the others.
            foreach (var (oEntry, oContent) in unit.Owners)
            {
                var cKey = (oEntry.ModDirectory, oContent.OptionGroup, oContent.Option);
                if (!shellMaterials.TryGetValue(cKey, out var cList))
                    shellMaterials[cKey] = cList = new List<string>();
                cList.Add($"ss_{diskChar}.mtrl");
            }

            perHostLayers[hIdx].Add(new SecondSkinLayer
            {
                MaterialName = "/" + matName,   // the model stores material names with a leading slash
                Geometry = unit.Geometries,
            });
            inHost[hIdx]++; diskLetter++;
            contentPlaced++;

            // The glow state is in here because its absence is otherwise invisible: a unit that resolved no
            // effect, and one whose effect failed to build and fell back to the pack's own material, publish
            // the same-looking line and the same-looking piece. Says which of the two, every composite.
            log.Information("[Proteus] content mat={0}/{7}/disk={1} -> host {2}{3:D4}/{4}: {5} — {8} mesh(es) "
                          + "for [{6}] glow={9}",
                matLetter, diskChar, host.Prefix, host.SetId, host.Slot,
                unit.Entry.ModDirectory,
                string.Join(", ", unit.Owners.Select(o => o.Content.Option ?? "(default)")),
                matVariant, unit.Geometries.Count,
                unit.Glow?.Scroll is { Length: > 0 } s
                    ? (glowBuilt ? s : s + " (FAILED — published the pack's own material)")
                    : "(none)");
        }

        // ── extra skeletons, now that we know what published ──────────────────
        //
        // shellMaterials holds a key per option whose material actually reached a host, so it is the exact
        // record of "this pack put something on the character". A claim whose option is not in it is
        // dropped: the entry would rewrite a body part the user is wearing, and doing that for geometry
        // that never appeared is all cost and no benefit.
        // A pack that offers one skeleton to several body parts is offering ALTERNATIVES, not requirements:
        // the Cerise jacket declares 6085 for both Body and Head so it works whether it is worn as a coat or
        // a hood. Whichever part the character is actually drawing takes it, and the others have nothing to
        // say. So an unresolvable slot is only worth a warning once the whole list is walked and no part
        // took that skeleton at all — reported after the loop rather than inside it.
        var estMissed = new List<(string Mod, string Slot, int Entry)>();
        var estLanded = new HashSet<(string Mod, int Entry)>();

        foreach (var (owner, slot, entry) in estPending)
        {
            if (!shellMaterials.ContainsKey(owner)) continue;

            if (EstSetId(slot, equippedPartModels, bareBodyModels, humanPartModels) is not { } estSet)
            {
                estMissed.Add((owner.Mod, slot, entry));
                continue;
            }

            // Deduplicated on the TARGET, not the source: several options of one pack asking the same body
            // part for the same skeleton is one entry, and two asking for DIFFERENT skeletons is a conflict
            // the table cannot express — first wins, and the second says so.
            var key = (slot.ToLowerInvariant(), estSet);
            if (!estClaimed.TryAdd(key, entry))
            {
                if (estClaimed[key] != entry)
                    log.Warning("[Proteus] content: {0} wants extra skeleton {1} on the {2} (set {3}), which "
                              + "is already claimed for {4} — EST holds one entry per body part",
                        owner.Mod, entry, slot, estSet, estClaimed[key]);
                continue;
            }

            manipulations.Add(EstManipulation(drawnRaceCode ?? charCode, slot, estSet, entry));
            estLanded.Add((owner.Mod, entry));
            log.Information("[Proteus] content: {0} — extra skeleton {1} claimed on the {2}, set {3}. "
                          + "That replaces whatever entry that item had",
                owner.Mod, entry, slot, estSet);
        }

        // Deduplicated, because a pack with several options offering the same alternative would otherwise
        // say it once per option.
        foreach (var (mod, slot, entry) in estMissed.Distinct())
        {
            if (estLanded.Contains((mod, entry))) continue;
            log.Warning("[Proteus] content: {0} needs extra skeleton {1} on the {2}, but this character "
                      + "is drawing nothing there — its ex bones will not load", mod, entry, slot);
        }

        if (contentUnhosted.Count > 0)
        {
            // In chat as well as the log, and deduped by count the same way the over-budget notice is. A
            // piece that silently does not appear is the failure mode this whole file is most careful about:
            // nothing on screen says the accessory ran out of material slots, and the mod, its option and
            // its enable state all still look completely correct.
            if (_lastUnhostedContent != contentUnhosted.Count)
            {
                _lastUnhostedContent = contentUnhosted.Count;
                var msg = string.Format(Loc.Localize("Chat.ContentUnplaced.Fmt",
                    "[Proteus] {0} mesh piece(s) from your mods could not be placed — your accessories are "
                  + "out of material slots. Equip another ring / bracelet / necklace, or turn off a layer."),
                    contentUnhosted.Count);
                _ = Plugin.Framework.RunOnFrameworkThread(
                    () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));
            }
            log.Warning("[Proteus] content: {0} piece material(s) could not be placed — every host is full or "
                      + "cannot carry their surface. Free an accessory slot, or turn off a layer",
                contentUnhosted.Count);
        }
        else _lastUnhostedContent = -1;

        int placed = maskLayers + clothLayers + contentPlaced;
        if (placed == 0) return null;

        // Layers whose SURFACE could not be hosted, reported apart from a capacity overflow because the
        // remedy is different. Overflow says "equip another accessory"; this needs a slot Proteus can replace
        // OUTRIGHT — a free ring, or the facewear slot — since only there may we rewrite the metadata that
        // stops the game deforming a face-shaped shell into the wrong shape. Telling someone to equip another
        // ring when both their ring slots are full would be advice that cannot work.
        //
        // Deduped on the SET of unhosted surfaces rather than a count: the count is stable while the user
        // shuffles which face overlay is on, and would suppress the notice for a genuinely different one.
        if (unhostedLayers.Count > 0)
        {
            // Names taken from the SAME layers the count came from — see unhostedLayers.
            var keys = string.Join(", ", unhostedLayers
                .Select(i => layerSurfaceName[i])
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
            if (!string.Equals(_lastUnhostedSurfaces, keys, StringComparison.Ordinal))
            {
                _lastUnhostedSurfaces = keys;
                var msg = string.Format(Loc.Localize("Chat.UnhostedLayers.Fmt",
                    "[Proteus] Some layers on your {1} could not be placed (layers: {0}). Those must not be "
                  + "race-deformed, so they need a slot Proteus can replace outright: free a ring slot "
                  + "(either hand) or your facewear slot and they will appear."), unhostedLayers.Count, keys);
                _ = Plugin.Framework.RunOnFrameworkThread(
                    () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));
            }
            log.Warning("[Proteus] second skin: {0} layer(s) unhosted on surface(s) [{1}]",
                unhostedLayers.Count, keys);
        }
        else _lastUnhostedSurfaces = null;

        // Guidance when even all equipped accessories can't hold the look (deduped by total layer count).
        if (overBudget > 0)
        {
            int totalLayers = maskLayers + clothLayers + overBudget;
            int totalMask = maskLayers + overBudgetMask;
            if (_lastOverBudgetLayers != totalLayers)
            {
                _lastOverBudgetLayers = totalLayers;
                var msg = string.Format(Loc.Localize("Chat.OverBudget.Fmt",
                    "[Proteus] This look has {0} layers ({1} Mask, {2} Cloth), but only {3} fit across your "
                  + "accessories (Proteus' invisible fallback ring already included). Turn off some layers, "
                  + "or equip another pair of glasses / ring / bracelet / necklace so the rest fit."),
                    totalLayers, totalMask, totalLayers - totalMask, totalCapacity);
                // Marshalled: the shell build runs off the framework thread, and ChatGui's queue is not
                // safe to enqueue into concurrently with the tick that drains it.
                _ = Plugin.Framework.RunOnFrameworkThread(
                    () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));   // 25 = yellow
            }
            log.Warning("[Proteus] second skin: {0} layers exceed total accessory capacity {1} — {2} dropped",
                placed + overBudget, totalCapacity, overBudget);
        }
        else _lastOverBudgetLayers = -1;

        // Build one shell model per host that got layers; fold each into the single Result.
        bool modelChangedAny = false;
        var hostModelPaths = new List<string>();
        var appendHostModelPaths = new List<string>();
        for (int h = 0; h < hosts.Count; h++)
        {
            if (perHostLayers[h].Count == 0) continue;
            var host = hosts[h];
            // The surface THIS host carries: its geometry, and the race space every decision below is made
            // against. Previously every host was built from every source and measured against one ambient
            // cut code, which is exactly the assumption a second surface breaks.
            var surface = surfaces[hostSurface[h]];

            // All three resolved above the material loop, so the materials baked into this shell and the
            // path it is published at agree on a race code. See the plan block after ChooseHosts.
            bool carrier = host.BaseModel == null;
            var (hostRace, nativeAtHostRace, publishCode) = plan[h];
            bool differs = !string.Equals(hostRace, surface.CutCode, StringComparison.OrdinalIgnoreCase);

            byte[] shell;
            SecondSkinWriter.Stats stats;
            try
            {
                // A host filled entirely with imported content needs NO sources: every layer brought its own
                // geometry, so parsing the body here would cost the whole header walk to contribute nothing —
                // and worse, the merged model's flags and LOD block are taken from source 0, which would then
                // describe a body rather than the piece actually being emitted.
                var srcs = perHostLayers[h].All(l => l.Geometry.Count > 0)
                    ? []
                    : surface.Sources;
                DumpShellInputs(h, srcs, perHostLayers[h], host.BaseModel);
                shell = SecondSkinWriter.Build(srcs, perHostLayers[h], host.BaseModel,
                    out stats, msg => log.Debug("[Proteus] second skin: {0}", msg), AuthoredCaps());
            }
            catch (EmptyShellException ex) when (ex.ByToggle)
            {
                // Not a failure: the user switched off the only thing this host was carrying. Reported at
                // Information for the same reason it gets its own arm — an error here sent someone who had
                // ticked two "hide" checkboxes hunting for a UV-coverage bug.
                log.Information("[Proteus] second skin: host {0}{1:D4}/{2} has nothing to draw — {3}",
                    host.Prefix, host.SetId, host.Slot, ex.Message);
                continue;
            }
            catch (Exception ex)
            {
                log.Error(ex, "[Proteus] second skin: model build failed for host {0}{1:D4}/{2}", host.Prefix, host.SetId, host.Slot);
                continue;   // this host fails; the others still build
            }

            // The toe cap was wanted but no binding described this body, so none was emitted. Say so —
            // the toes just quietly lose their cap otherwise, and there is a concrete thing the wearer
            // can do about it (bake a binding against this body). Deduped: the shell rebuilds often.
            if (stats.CapDeclined is { } declined)
            {
                if (lastCapDeclined != declined)
                {
                    lastCapDeclined = declined;
                    var capMsg = $"[Proteus] Toe cap skipped: {declined}. The cap is fitted by a binding "
                               + "measured against each supported body; this one has none, so the toes are "
                               + "left uncapped rather than torn.";
                    // Marshalled for the same reason as the messages above: this runs off the framework
                    // thread and ChatGui's queue is not safe to enqueue into concurrently with the tick
                    // that drains it.
                    _ = Plugin.Framework.RunOnFrameworkThread(
                        () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(capMsg, 25).Build()));
                }
                log.Warning("[Proteus] second skin: toe cap declined — {0}", declined);
            }

            // Which cap this shell actually got. Said out loud because the alternative is reading the
            // Dalamud log, which is size-capped and quietly stops writing — "is the cap I just authored
            // being used?" should not need forensics. Only on a change, so it is not chat spam.
            if (stats.CapUsed is { } capUsed && lastCapUsed != capUsed)
            {
                lastCapUsed = capUsed;
                _ = Plugin.Framework.RunOnFrameworkThread(
                    () => Plugin.ChatGui.Print(new SeStringBuilder()
                        .AddUiForeground($"[Proteus] Toe cap: {capUsed}", 25).Build()));
                log.Information("[Proteus] second skin: toe cap {0}", capUsed);
            }

            // Redirect the path the game ACTUALLY loads (host.ModelPath) for an equipped host. The Emperor
            // fallback has no resolved path to copy (ModelPath null), so its path is rebuilt here — in
            // cutCode space, matching the EQDP entry written below, which declares THAT race/gender to have
            // its own model for the slot.
            //
            // CUT space, paired with the EQDP entries written below — the same route a Midlander-only gear
            // mod takes onto every other race: its entry for the wearer's race is empty, so the game walks
            // to the parent race's model and race-deforms it on the way. Our shell is a copy of c0201 body
            // parts that get exactly that deform, so inheriting it is what makes the shell fit.
            //
            // Verified in game on a Miqo'te female wearing the Emperor's New Ring: published at c0201, EQDP
            // for Midlander Female, and the shell rendered at the body's size. (Build #294 published here
            // too and rendered nothing — but her ring slot was EMPTY that whole time, so the game never
            // asked for any ring model. That was a dead test, not evidence against this.)
            //
            // One published path per host, except the invisible-carrier case below. An earlier version
            // hedged by publishing every shell at a second code; that alias came back as an equipped
            // accessory next composite and poisoned the model-race vote above. Don't re-add it generally.
            //
            // The carrier exception below DOES publish two codes, and that includes the Emperor's ring —
            // which looks like exactly the re-add this warns against. It is safe for one specific reason:
            // a{EmperorSetId} is filtered out of the model-race vote at its source (see the .Where on
            // equippedPaths), so no alias of it can reach the vote whatever code it loads under. Nothing
            // else enjoys that exemption, so the warning still stands for every other path.
            var mdlGamePath = host.ModelPath
                ?? $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/model/c{publishCode}{host.Prefix}{host.SetId:D4}_{host.Slot}.mdl";
            var mdlDisk = Path.Combine(modelsDir, $"secondskin_{h}.mdl");
            var modelChanged = WriteIfChanged(mdlDisk, shell);

            // What the model ON DISK asks the game for — read back from the FILE, not from the bytes we just
            // built, because those are different questions and only the file is what the game loads.
            //
            // A carrier was observed drawing with no material at all, and Penumbra named the request:
            // chara/equipment/e5501/material/v0001/mt_c0201a0053_rir_a.mtrl — the GLASSES directory with the
            // EMPEROR RING's material name. Shell files are keyed by host INDEX (secondskin_{h}.mdl) and the
            // host list changes between composites, so index 0 is the ring one composite and the glasses the
            // next. Reading the built bytes would have agreed with itself and proved nothing; reading the
            // file catches a write that did not land.
            try
            {
                // Re-read ONLY after a write. When WriteIfChanged reports unchanged it has already read the
                // file and proven it equals `shell`, so `shell` IS the disk contents and a second read would
                // buy nothing at a few megabytes a composite. When it reports changed we have just written,
                // and re-reading is the one thing that can catch a write reporting success without landing —
                // which is the anomaly this exists for.
                var onDisk = shell;
                if (modelChanged)
                {
                    onDisk = File.ReadAllBytes(mdlDisk);
                    if (!onDisk.AsSpan().SequenceEqual(shell))
                        log.Warning("[Proteus] shell file {0} does NOT match the bytes just built for host "
                                  + "{1}{2:D4}/{3} — the write did not land, so the game is loading a stale shell",
                            Path.GetFileName(mdlDisk), host.Prefix, host.SetId, host.Slot);
                }

                var declared = SecondSkinWriter.MaterialNames(onDisk);
                // Slot as well as set: one accessory set covers _nek, _ear, _wrs and _rir, so matching the set
                // alone would list a sibling piece's material under this host — the same conflation the
                // variant lookup had to be narrowed for.
                var published = redirects.Keys.Where(k =>
                    k.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)
                 && k.Contains($"{host.Prefix}{host.SetId:D4}/", StringComparison.OrdinalIgnoreCase)
                 && k.Contains($"_{host.Slot}_", StringComparison.OrdinalIgnoreCase));

                log.Information("[Proteus] second skin host {0}{1:D4}/{2}: model declares {3} material(s) [{4}] "
                              + "— published [{5}]",
                    host.Prefix, host.SetId, host.Slot, declared.Count, string.Join(", ", declared),
                    string.Join(", ", published));
            }
            catch (System.Exception ex)
            {
                log.Warning("[Proteus] could not read back the shell model's material names: {0}", ex.Message);
            }
            shellChanged   |= modelChanged;
            modelChangedAny |= modelChanged;
            redirects[mdlGamePath] = Rel(outputRoot, mdlDisk);
            hostModelPaths.Add(mdlGamePath);
            // Append hosts only: BaseModel non-null means we merged into the player's own item, so this
            // path has a real upstream (their necklace/ring mod) that later composites must read back.
            if (host.BaseModel != null) appendHostModelPaths.Add(mdlGamePath);

            // ── carrier hosts: make the game load our copy from CUT space ─────────────────────────
            // A host whose model we REPLACE has no appearance of its own to protect — the Emperor's ring is
            // invisible, and our injected glasses are only ever seen AS the shell. So its per-race metadata
            // is ours to rewrite, and rewriting it is what makes non-Midlander races fit: empty the entry
            // for the race the game would load it natively from, and the lookup falls through to the parent
            // — the c0201 space the shell was cut in — picking up the same deform the body already gets.
            //
            // NOT done for an APPEND host (real glasses, a worn ring): there we merge into the player's own
            // item, and emptying its entry would swap their frames for a deformed Midlander pair. Those
            // keep the size warning from LoadCandidate instead.
            //
            // carrier/hostRace/differs/nativeAtHostRace are computed at the top of the loop — a fall-through
            // pair is only ever emitted for codes the guard there has already proven reach each other.
            if (carrier && nativeAtHostRace)
            {
                // Cut space was rejected, so there is no fall-through to arrange: the shell is published at
                // the wearer's own code and this entry is what makes the game load it from there. Nothing
                // is emptied — an empty entry is a request to be deformed, and the whole point here is to
                // take the geometry at face value and skip the deform.
                manipulations.Add(EqdpManipulation(hostRace, host.EqdpSlot, host.SetId));

                // Publish at c{hostRace} EXPLICITLY rather than trusting mdlGamePath. For the Emperor ring
                // (ModelPath null) they are already the same path and this is a no-op. For a facewear
                // carrier they are NOT: mdlGamePath is the resolved — or, for a pending injection, the
                // predicted — path, which is built from equipCode and can name a different race than the
                // entry we just wrote. Declaring hostRace has a model while publishing only at equipCode
                // is a redirect the game never asks for, so derive the path from the same code as the
                // manipulation. Whatever mdlGamePath already registered stays, on the same reasoning the
                // pair branch keeps its native copy: if this slot turns out not to be EQDP-driven, the
                // resolved path is still there and we are no worse off.
                var nativePath = $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/model/"
                               + $"c{hostRace}{host.Prefix}{host.SetId:D4}_{host.Slot}.mdl";
                if (!redirects.ContainsKey(nativePath))
                {
                    redirects[nativePath] = Rel(outputRoot, mdlDisk);
                    hostModelPaths.Add(nativePath);
                }
                log.Information("[Proteus] second skin: EQDP for {0} {1}{2:D4} — c{3} has the model, loaded "
                              + "natively with no fall-through -> {4}",
                    host.EqdpSlot, host.Prefix, host.SetId, hostRace, nativePath);
            }
            else if (carrier && differs)
            {
                manipulations.Add(EqdpManipulation(surface.CutCode, host.EqdpSlot, host.SetId));
                manipulations.Add(EqdpManipulation(hostRace, host.EqdpSlot, host.SetId, hasModel: false));

                // Publish at BOTH codes, derived from hostRace/cutCode rather than from whatever the walk
                // resolved. If the emptied entry takes, the game loads the cut-space copy and deforms it
                // (right size); if this slot turns out not to be EQDP-driven — facewear is a bonus item and
                // may not be — it loads the native copy and we are no worse off than before. Deriving the
                // pair instead of reusing host.ModelPath is what stops the published set from shrinking on
                // the composites where the walk has already been steered onto the twin.
                string PathFor(string code)
                    => $"chara/{host.Tree}/{host.Prefix}{host.SetId:D4}/model/"
                     + $"c{code}{host.Prefix}{host.SetId:D4}_{host.Slot}.mdl";

                foreach (var code in new[] { surface.CutCode, hostRace })
                {
                    var p = PathFor(code);
                    if (redirects.ContainsKey(p)) continue;
                    redirects[p] = Rel(outputRoot, mdlDisk);
                    hostModelPaths.Add(p);
                }
                log.Information("[Proteus] second skin: EQDP for {0} {1}{2:D4} — c{3} has the model, c{4} "
                              + "emptied so the game falls through to it -> {5} (native copy kept at {6})",
                    host.EqdpSlot, host.Prefix, host.SetId, surface.CutCode, hostRace,
                    PathFor(surface.CutCode), PathFor(hostRace));
            }
            else if (carrier && host.Prefix == 'a')
            {
                // Emperor's ring in a race that is already cut-space (Midlander/Miqo'te/Elezen/Roegadyn
                // females all read c0201 here): it loads no model at all without an entry saying it has one.
                manipulations.Add(EqdpManipulation(surface.CutCode, host.EqdpSlot, host.SetId));
                log.Information("[Proteus] second skin: EQDP for {0} {1}{2:D4} — c{3} has the model -> {4}",
                    host.EqdpSlot, host.Prefix, host.SetId, surface.CutCode, mdlGamePath);
            }
            log.Information("[Proteus] second skin: host {0}{1:D4}/{2} <- {3} layer(s) -> {4} meshes, {5} KB (append={6})",
                host.Prefix, host.SetId, host.Slot, perHostLayers[h].Count, stats.Meshes, shell.Length / 1024, host.BaseModel != null);
        }
        if (hostModelPaths.Count == 0) return null;

        return new Result(redirects, manipulations, shellChanged, shellMaterials, modelChangedAny,
                          hostModelPaths, appendHostModelPaths, contentMaterials);
    }

    private static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace('/', '\\');

    /// <summary>
    /// The overlay's coverage, exactly as the skin layer computes it: the art's own alpha, then shaped
    /// by the mod's selected "Masks" options. Returns one byte per texel, or null when nothing bounds it
    /// (in which case the shell covers the whole body).
    ///
    /// Each mask contributes W *= (1-a) and T += gray*a, and the result is baseAlpha*W/255 + T — so a
    /// mask can both carve coverage away and force it on. Mirrors CompositorService.CombinedMaskAt /
    /// ApplyCoverageMask; keep the two in step.
    /// </summary>
    private byte[]? BuildAlpha(
        OverlayDescriptor d, OverlayEntry entry, string? srcType, string? dstType, int w, int h,
        bool maskAdds = true)
    {
        var artPath = d.Diffuse ?? d.Normal ?? d.Mask;
        var masks = discovery.ResolveActiveMasks(entry);
        if (artPath == null && masks.Count == 0)
            return null;   // empty overlay (no art, no masks) — caller drops the shell

        int n = w * h;
        var alpha = new byte[n];

        if (artPath != null)
        {
            var art = LoadRemapped(artPath, entry.SidecarRoot, srcType, dstType, w, h);
            if (art == null)
            {
                log.Warning("[Proteus] gear art failed to load: {0} (mod {1}) — dropping this shell",
                    artPath, entry.ModDirectory);
                return null;
            }
            // Every pass below is per-texel with no carried state, so partitioning them cannot change a
            // byte — and at TexSize each is 4.19M iterations, run once per mask per layer.
            var al = alpha; var ar = art;
            CompositorService.ParallelPixels(0, n, 1, (from, to) =>
            { for (int i = from; i < to; i++) al[i] = ar[i * 4 + 3]; });
        }
        else
        {
            Array.Fill(alpha, (byte)255);
        }

        // combine the selected masks into weight/target, then apply
        byte[]? wArr = null, tArr = null;
        for (int p = masks.Count - 1; p >= 0; p--)
        {
            var m = RemapPath(masks[p], srcType, dstType, w, h);   // masks share the overlay's UV space
            if (m == null) continue;
            if (wArr == null)
            {
                var w0 = wArr = new byte[n];
                var t0 = tArr = new byte[n];
                CompositorService.ParallelPixels(0, n, 1, (from, to) =>
                {
                    for (int i = from; i < to; i++)
                    {
                        int o = i * 4, a = m[o + 3];
                        int g = (m[o] * 77 + m[o + 1] * 150 + m[o + 2] * 29) >> 8;   // luminance
                        w0[i] = (byte)(255 - a);
                        t0[i] = (byte)(g * a / 255);
                    }
                });
            }
            else
            {
                var w0 = wArr; var t0 = tArr!;
                CompositorService.ParallelPixels(0, n, 1, (from, to) =>
                {
                    for (int i = from; i < to; i++)
                    {
                        int o = i * 4, a = m[o + 3];
                        int g = (m[o] * 77 + m[o + 1] * 150 + m[o + 2] * 29) >> 8;
                        int inv = 255 - a;
                        t0[i] = (byte)(t0[i] * inv / 255 + g * a / 255);
                        w0[i] = (byte)(w0[i] * inv / 255);
                    }
                });
            }
        }

        if (wArr != null)
        {
            var al = alpha; var w0 = wArr; var t0 = tArr;
            CompositorService.ParallelPixels(0, n, 1, (from, to) =>
            {
                for (int i = from; i < to; i++)
                {
                    if (al[i] == 0) continue;                      // no base coverage -> mask has no say
                    int v = al[i] * w0[i] / 255 + (maskAdds ? t0![i] : 0);
                    al[i] = (byte)(v > 255 ? 255 : v);
                }
            });
        }

        long opaque = 0, clear = 0;
        foreach (var a in alpha) { if (a == 0) clear++; else if (a == 255) opaque++; }
        log.Information(
            "[Proteus] gear coverage: art={0} masks={1} [{2}] -> {3:F1}% clear, {4:F1}% opaque, {5:F1}% partial",
            artPath ?? "none", masks.Count,
            string.Join(", ", masks.Select(Path.GetFileNameWithoutExtension)),
            clear * 100.0 / n, opaque * 100.0 / n, (n - clear - opaque) * 100.0 / n);

        return alpha;
    }

    /// <summary>
    /// Coverage for a dedicated mask shell: the union (max alpha) of the mod's active masks, remapped into
    /// the body's UV space. Unlike <see cref="BuildAlpha"/> — which SHAPES an overlay's coverage by the masks
    /// (absent mask ⇒ overlay stays) — here the mask IS the shape (absent mask ⇒ nothing renders). Returns
    /// null when no mask resolves, in which case the shell would cover the whole body (so callers gate on
    /// there being mask assets first).
    /// </summary>
    private byte[]? BuildMaskCoverage(OverlayEntry entry, string? srcType, string? dstType, int w, int h)
    {
        int n = w * h;
        byte[]? cov = null;
        // Combine TOP-TERRITORY-WINS, matching CombinedMaskAt (which carves the other layers the same way):
        // at each pixel the topmost mask with territory (alpha) there decides the coverage — its grayscale.
        // Process bottom masks first so the top one (assets are highest-priority-first) lands last and
        // overrides. A mask that is BLACK in its territory (a=255, g=0) drives coverage to 0 — a hole — even
        // where a LOWER mask is white. Alpha alone (a union) would instead display the black regions opaque.
        var assets = discovery.ResolveActiveMaskAssets(entry);
        for (int mi = assets.Count - 1; mi >= 0; mi--)
        {
            var m = RemapPath(assets[mi].MaskPath, srcType, dstType, w, h);   // masks share the overlay's UV space
            if (m == null) continue;
            cov ??= new byte[n];
            for (int i = 0; i < n; i++)
            {
                int o = i * 4, a = m[o + 3];
                if (a == 0) continue;                                          // outside this mask's territory
                int g = (m[o] * 77 + m[o + 1] * 150 + m[o + 2] * 29) >> 8;     // luminance
                cov[i] = (byte)(cov[i] * (255 - a) / 255 + g * a / 255);       // territory alpha-over
            }
        }
        return cov;
    }

    /// <summary>
    /// Write out exactly what the writer is about to be handed, so the offline harness can rebuild the
    /// same shell instead of approximating its inputs. Enabled by CREATING the folder — it does nothing
    /// until %TEMP%\proteus-shell-dump exists, and there is nothing to turn off afterwards but deleting
    /// it again.
    /// <para/>
    /// This exists because approximating those inputs cost several rounds of chasing defects that the
    /// harness could not reproduce: it was pointed at a different foot model than the one equipped, and
    /// then at one body where the game passes several, no shape keys, and no connector-mesh mode.
    /// </summary>
    private void DumpShellInputs(int host, IReadOnlyList<SecondSkinWriter.SourceSpec> sources,
                                 IReadOnlyList<SecondSkinLayer> layers, byte[]? baseModel)
    {
        var dir = Path.Combine(Path.GetTempPath(), "proteus-shell-dump");
        if (!Directory.Exists(dir)) return;
        try
        {
            var pre = Path.Combine(dir, $"host{host}_");
            for (int i = 0; i < sources.Count; i++) File.WriteAllBytes($"{pre}body{i}.mdl", sources[i].Model);
            if (baseModel != null) File.WriteAllBytes($"{pre}base.mdl", baseModel);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"bodies={sources.Count}");
            for (int i = 0; i < sources.Count; i++)
            {
                var sp = sources[i];
                sb.AppendLine($"source[{i}] dropConnectors={sp.DropConnectors} uvConv={(sp.UvConv == null ? "none" : "yes")} "
                            + $"shapes={(sp.EnabledShapes is { } sk ? string.Join(',', sk) : "")}");
            }
            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                sb.AppendLine($"layer[{i}] material={l.MaterialName} "
                            + $"coverage={(l.Coverage == null ? "none" : $"{l.CoverageWidth}x{l.CoverageHeight}")} "
                            + $"toeCap={(l.ToeCap == null ? "none" : $"{l.ToeCapWidth}x{l.ToeCapHeight}")} strength={l.ToeCapStrength}");
                if (l.ToeCap != null) File.WriteAllBytes($"{pre}layer{i}_toecap.raw", l.ToeCap);
                if (l.Coverage != null) File.WriteAllBytes($"{pre}layer{i}_coverage.raw", l.Coverage);
            }
            File.WriteAllText($"{pre}inputs.txt", sb.ToString());
            log.Information("[Proteus] second skin: dumped build inputs for host {0} to {1}", host, dir);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] second skin: could not dump build inputs");
        }
    }

    /// <summary>
    /// Load this shell's toe-cap map as a single-channel mask in the BODY's UV space (the shell inherits
    /// the body's UVs, so the same remap the art takes applies here). Greyscale, so the red channel is the
    /// value. Null — and no cap — when the file won't load or the map is all black.
    /// </summary>
    private byte[]? ReadToeCap(string path, string? srcType, string? dstType)
    {
        var rgba = RemapPath(path, srcType, dstType, ToeCapSize, ToeCapSize);
        if (rgba == null)
        {
            log.Warning("[Proteus] second skin: toe cap map {0} failed to load — shells built without a cap", path);
            return null;
        }

        var mask = new byte[ToeCapSize * ToeCapSize];
        bool any = false;
        for (int p = 0; p < mask.Length; p++)
        {
            mask[p] = rgba[p * 4];
            if (mask[p] != 0) any = true;
        }
        return any ? mask : null;   // all black = untouched everywhere; keep the build byte-identical
    }

    /// <summary>
    /// The toe cap for one shell: its option's own map if it names one, otherwise the shared map any mod
    /// in the look supplied. Returned only when this shell's art actually reaches the toes — a cap cuts
    /// the toe box out and rebuilds it, so handing one to a shell that stops at the ankle would carve a
    /// hole in the body and fill it with fabric nobody asked for.
    /// </summary>
    private byte[]? ToeCapFor(OverlayDescriptor d, OverlayEntry entry, string? srcType, string? dstType,
                              byte[]? shared, byte[]? alpha)
    {
        if ((d.ToeCapStrength ?? 1f) <= 0f) return null;

        var mask = d.ToeCap != null
            ? ReadToeCap(Path.Combine(entry.SidecarRoot, d.ToeCap), srcType, dstType)
            : shared;
        if (mask == null || alpha == null) return null;

        // How much of the capped area this shell actually paints, sampling the coverage under the map.
        int over = 0, painted = 0;
        int step = TexSize / ToeCapSize;
        for (int y = 0; y < ToeCapSize; y++)
            for (int x = 0; x < ToeCapSize; x++)
            {
                if (mask[y * ToeCapSize + x] < 128) continue;
                over++;
                if (alpha[(y * step) * TexSize + x * step] >= 32) painted++;
            }
        float share = over == 0 ? 0f : (float)painted / over;
        if (share < MinToeCoverage)
        {
            log.Debug("[Proteus] second skin: shell covers {0:P0} of the toe cap area — below {1:P0}, left uncapped",
                share, MinToeCoverage);
            return null;
        }

        log.Information("[Proteus] second skin: toe cap at strength {0:0.##}, shell covers {1:P0} of it",
            d.ToeCapStrength ?? 1f, share);
        return mask;
    }

    /// <summary>Box-downsample the coverage for triangle trimming; it only decides keep/drop.</summary>
    private static byte[]? Downsample(byte[]? src, int w, int h, int size)
    {
        if (src == null) return null;
        var dst = new byte[size * size];
        int sx = w / size, sy = h / size;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int max = 0;   // keep a texel if ANY source texel under it is visible
                for (int j = 0; j < sy; j++)
                    for (int i = 0; i < sx; i++)
                        max = Math.Max(max, src[(y * sy + j) * w + x * sx + i]);
                dst[y * size + x] = (byte)max;
            }
        return dst;
    }

    /// <summary>
    /// Author the shell's textures. Returns the game paths in the shader's slot order, or null on
    /// failure. The overlay's alpha is written into the NORMAL's BLUE channel — that is what gates
    /// transparency for gear, and therefore what lets stacked shells composite instead of occlude.
    /// </summary>
    /// <summary>A flat RGBA texture at the shell texture size. The fallback for a slot the source doesn't
    /// fill — see the callers for what each colour means, since a wrong one is never blank, it renders.</summary>
    private static byte[] Solid(byte r, byte g, byte b, byte a)
    {
        var t = new byte[TexSize * TexSize * 4];
        for (int i = 0; i < t.Length; i += 4) { t[i] = r; t[i + 1] = g; t[i + 2] = b; t[i + 3] = a; }
        return t;
    }

    private List<string>? WriteTextures(
        OverlayEntry entry, OverlayDescriptor d, string shader, string texPrefix,
        string texturesDir, Dictionary<string, string> redirects, char letter, byte[]? alpha,
        string? srcType, string? dstType, List<ColorTableRowPreset>? rows, string? effectsFolder,
        ref bool texturesChanged, bool mergeMasks = true,
        IReadOnlyList<byte[]>? siblingReliefs = null)   // each: a normal RGBA with coverage in its alpha lane
    {
        var sidecarRoot = entry.SidecarRoot;
        var outputRoot = Directory.GetParent(texturesDir)!.FullName;

        byte[]? Png(string? rel) => LoadRemapped(rel, sidecarRoot, srcType, dstType, TexSize, TexSize);

        var diffuse = Png(d.Diffuse);
        var normal = Png(d.Normal);
        var mask = Png(d.Mask);
        var index = Png(d.Index);

        // The scroll map is NOT body-UV art — it's a tiling pattern the shader samples with uv1, so it
        // must NOT be UV-remapped (that would tear the pattern apart). It also lives in an effects
        // folder, not the sidecar tree, so resolve it separately.
        byte[]? scroll = null;
        if (d.Scroll != null)
        {
            var effectPath = SidecarDiscoveryService.ResolveEffectPath(entry, effectsFolder, d.Scroll);
            if (effectPath != null)
                scroll = textureLoader.LoadPngAsRgba(effectPath, TexSize, TexSize);
            else
                log.Warning("[Proteus] second skin: effect \"{0}\" not found", d.Scroll);
        }

        // ── Proteus "Masks" options ──────────────────────────────────────────
        // A mask isn't only coverage: its export can also ship its OWN row assignment (Masks/<x>_id.png)
        // and relief normal (Masks/<x>_n.dds). The skin layer merges both — LoadIndexMerged and the
        // masks-driven relief pass in CompositorService — so the gear layer must too, or a mask silently
        // loses its rows and its bump. (Coverage itself is already folded in by BuildAlpha.)
        //
        // Skipped when this mod carries a dedicated top mask shell for its OTHER overlays (mergeMasks=false):
        // the mask shell owns the _id/relief, so merging here too would colour the mask twice. The mask
        // shell itself passes mergeMasks=true, so its own _id/relief still land.
        //
        // _id is merged bottom-first (assets are highest-priority-first, so reverse) — each mask overwrites
        // the _id where it is present, so the TOP mask wins on overlap. The RELIEF is folded in afterwards by
        // the shared CombineMaskReliefs (top-first claim), the SAME combine the skin body normal uses.
        var mergeTopFirst = mergeMasks
            ? discovery.ResolveActiveMaskAssets(entry)
            : new List<(string MaskPath, string? NormalPath, string? IndexPath)>();
        foreach (var (maskPath, maskNormalPath, maskIndexPath) in Enumerable.Reverse(mergeTopFirst))
        {
            if (maskIndexPath == null) continue;
            var maskPng = RemapPath(maskPath, srcType, dstType, TexSize, TexSize);
            var maskIdx = RemapPath(maskIndexPath, srcType, dstType, TexSize, TexSize);
            if (maskPng == null || maskIdx == null) continue;
            // LoadPngAsRgba hands back a shared cached array — clone before writing into it.
            index = index != null ? (byte[])index.Clone() : Solid(0, 0, 0, 255);
            var idxBuf = index; var mp = maskPng; var mi = maskIdx;
            // Per texel, reading and writing only its own index, so partitioning is byte-identical.
            CompositorService.ParallelPixels(0, idxBuf.Length, 4, (from, to) =>
            {
                for (int i = from; i < to; i += 4)
                {
                    if (mp[i + 3] < 128) continue;    // only where the mask is actually present
                    idxBuf[i]     = mi[i];            // red   → row pair
                    idxBuf[i + 1] = mi[i + 1];        // green → sub-row
                }
            });
        }

        // Mask relief: same top-first claim-combine as the skin body normal (CombineMaskReliefs), so the two
        // paths can't drift. A higher mask's trim wins over a lower one's; plain fill leaves the base normal.
        var reliefMasks = new List<(byte[] Relief, byte[] Coverage)>();
        foreach (var (maskPath, maskNormalPath, _) in mergeTopFirst)
        {
            if (maskNormalPath == null) continue;
            var maskPng    = RemapPath(maskPath, srcType, dstType, TexSize, TexSize);
            var maskNormal = RemapPath(maskNormalPath, srcType, dstType, TexSize, TexSize);
            if (maskPng != null && maskNormal != null)
                reliefMasks.Add((maskNormal, maskPng));
        }
        if (reliefMasks.Count > 0)
        {
            normal = normal != null ? (byte[])normal.Clone() : Solid(128, 128, 255, 255);
            CompositorService.CombineMaskReliefs(normal, TexSize, TexSize, reliefMasks);
        }

        // Sibling relief: additively fold each same-mod sibling overlay's normal into this shell's normal so a
        // relief hidden behind this fabric (occluded shell) still shows here. ADDITIVE (CompoundNormal), not
        // claim-replace — ribbing bumps stack ON the fabric weave rather than flattening it. Each sibling
        // carries its own coverage in its alpha lane, so CompoundNormal's src-alpha gate lands it only where
        // that sibling is visible. R/G only — blue stays this shell's coverage gate, so it rides this fabric.
        if (siblingReliefs is { Count: > 0 })
        {
            normal = normal != null ? (byte[])normal.Clone() : Solid(128, 128, 255, 255);
            foreach (var sib in siblingReliefs)
                CompositorService.CompoundNormal(normal, sib, TexSize, TexSize);
        }

        // ── row-selector repair ──────────────────────────────────────────────
        // Runs after every mask _id merge, so it sees the final row assignment.
        //
        // An exported _id is antialiased art, but red/17+1 is a discrete row lookup: each edge texel ramps
        // down through rows nobody configured, and the shell's shader resolves those against the TEMPLATE
        // colorset, painting a one-texel template-coloured fringe just inside every edge. The skin layer
        // never showed this because it skips rows with no preset; a shell can't skip — it hands the texture
        // to the GPU. See CompositorService.SnapIndexRowsToDefined.
        //
        // The repair goes to a SEPARATE buffer that only the shader's "id" slot uses. The opacity pass below
        // deliberately keeps reading the unrepaired index: it skips texels whose row has no preset, so
        // repairing them first would newly apply the row's Opacity across the whole antialiased band and
        // push every edge toward opaque (or transparent), visibly fattening or thinning the garment. The
        // fringe is a shader-side problem; opacity behaviour has no reason to move with it.
        var shaderIndex = index;
        if (index != null && rows is { Count: > 0 })
        {
            // LoadPngAsRgba hands back a shared cached array; the mask merge above clones only when it
            // actually merged, so clone here too rather than writing through to the cache.
            shaderIndex = (byte[])index.Clone();
            CompositorService.SnapIndexRowsToDefined(shaderIndex, TexSize, TexSize, rows.Select(p => p.Row).ToList());
        }

        // ── per-row opacity ──────────────────────────────────────────────────
        // Each color table row carries an Opacity (-100..100), and the index texture says which row a
        // pixel uses — so opacity is per-region, not global. Same blend the skin layer applies
        // (CompositorService.ApplyIndexedOpacity): negative fades toward transparent, positive pushes
        // toward opaque, interpolated between sub-rows A and B by the index's green channel.
        if (alpha != null && index != null && rows is { Count: > 0 })
        {
            // The row's two opacities, indexed by the 1-based row pair the index texture names, resolved
            // ONCE up front. This loop runs per texel — 4.19M times at TexSize — and it used to do
            // `rows.FirstOrDefault(p => p.Row == pair)` inside, which allocates a closure capturing `pair`
            // and linearly scans the row list on every one of them. The red channel is a /17 bucket, so
            // there are only ever 16 distinct answers.
            //
            // A separate `present` flag rather than a NaN sentinel in the value array. NaN would be
            // indistinguishable from a row whose Opacity genuinely IS NaN — corrupt sidecar JSON, or a
            // deserializer turning a malformed number into one — and that row would then be silently
            // skipped instead of applied, which is the kind of difference nobody traces back to here.
            const int PairCount = 17;                       // pairs are 1..16; index 0 is unused
            var hasPreset = new bool[PairCount];
            var opAByPair = new float[PairCount];
            var opBByPair = new float[PairCount];
            foreach (var preset in rows)
            {
                if (preset.Row < 1 || preset.Row >= PairCount) continue;
                // FIRST match wins, exactly as FirstOrDefault did: two presets can carry the same Row, and
                // letting the later one overwrite would silently change which opacity a texel gets — and
                // with it the output's content hash, which renames and re-uploads the texture.
                if (hasPreset[preset.Row]) continue;
                hasPreset[preset.Row] = true;
                opAByPair[preset.Row] = preset.SubRowA?.Opacity ?? 0;
                opBByPair[preset.Row] = preset.SubRowB?.Opacity ?? 0;
            }

            var src = alpha;
            var dst = (byte[])alpha.Clone();
            var idx = index;
            // Per texel, no carried state, so partitioning cannot change a byte.
            CompositorService.ParallelPixels(0, src.Length, 1, (from, to) =>
            {
                for (int i = from; i < to; i++)
                {
                    float a = src[i] / 255f;
                    if (a <= 0f) continue;

                    int pair = idx[i * 4] / 17 + 1;                     // red → 1-based row pair
                    if (pair < 1 || pair >= PairCount) continue;
                    if (!hasPreset[pair]) continue;                     // no preset for this row pair

                    float blendA = idx[i * 4 + 1] / 255f;               // green → sub-row A weight
                    float opA = opAByPair[pair];
                    float op = opBByPair[pair] + (opA - opBByPair[pair]) * blendA;
                    if (op == 0f) continue;

                    float newA = op < 0f ? a * (100f + op) / 100f : a + (1f - a) * op / 100f;
                    dst[i] = (byte)(Math.Clamp(newA, 0f, 1f) * 255f + 0.5f);
                }
            });
            alpha = dst;
        }

        // norm: RG = the normal itself, B = TRANSPARENCY (the gear alpha gate), A = unused.
        // This is the whole trick: a Proteus overlay is gated by opacity, and on the gear layer that
        // opacity has to be translated into the normal map's BLUE channel or the shell renders solid.
        // (It also needs the material's transparency flag on — see GearMaterialWriter.)
        var norm = normal != null ? (byte[])normal.Clone() : Solid(128, 128, 255, 255);
        {
            var nrm = norm; var al = alpha;
            CompositorService.ParallelPixels(0, TexSize * TexSize, 1, (from, to) =>
            {
                for (int i = from; i < to; i++)
                    nrm[i * 4 + 2] = al?[i] ?? 255;   // blue is the gate; alpha is not used
            });
        }

        var slots = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["norm"] = norm,
            // A fabricated mask must be WHITE, not mid-grey. The gear shaders read occlusion/gloss out
            // of it, so a 50% grey mask halves the lighting everywhere and a white surface renders grey.
            ["mask"] = mask ?? Solid(255, 255, 255, 255),
            // No index texture → select Row 16 sub-row A everywhere, matching the SKIN layer's fallback
            // (it applies row16A as a flat tint when desc.Index == null). red 255 → row pair 16, green 255
            // → sub-row A. Defaulting to black (row 1) instead picked up the template's default row — which
            // renders the shell a flat red — and ignored the Row 16 tint the overlay actually carries.
            ["id"]   = shaderIndex ?? Solid(255, 255, 0, 255),

            ["base"] = diffuse ?? Solid(255, 255, 255, 255),  // tint also comes from the color table
            ["catc"] = scroll ?? Solid(0, 0, 0, 255),         // black = no glow
        };

        var order = GearMaterialWriter.TextureOrder(shader);
        var paths = new List<string>(order.Count);
        bool compress = config.EnableCompression;
        foreach (var slot in order)
        {
            var gamePath = texPrefix + slot + ".tex";
            var disk = Path.Combine(texturesDir, $"ss_{letter}_{slot}.tex");

            // Compression (opt-in). The "id" (index) slot is NEVER compressed: its red/green encode discrete
            // colour-table row selectors (red / 17 + 1), and any lossy error crosses a bucket boundary and
            // picks the wrong row (wrong colour/glow, seams). The normal's BLUE channel is the gear
            // transparency gate (see WriteTextures above), which BC5 (2-channel) drops — so it only uses BC5
            // when its blue is uniformly opaque (255 ⇒ nothing to lose), else BC7 preserves the gate.
            // Everything else (base/mask/catc) is continuous → BC7.
            var encoding = TexEncoding.Uncompressed;
            if (compress && !string.Equals(slot, "id", StringComparison.OrdinalIgnoreCase))
                encoding = string.Equals(slot, "norm", StringComparison.OrdinalIgnoreCase)
                    ? (IsBlueAllWhite(slots[slot]) ? TexEncoding.Bc5 : TexEncoding.Bc7)
                    : TexEncoding.Bc7;

            // Skip the write when the content AND its encoding match what we last wrote — otherwise every
            // recomposite would look like a change and force a redraw. The encoding is folded into the hash
            // so toggling compression forces a rewrite instead of a stale skip.
            var hash = Hash(slots[slot]) ^ ((ulong)((int)encoding + 1) * 0x9E3779B97F4A7C15ul);
            bool same = _texHashes.TryGetValue(disk, out var prev) && prev == hash && File.Exists(disk);
            if (!same)
            {
                if (!textureLoader.WriteTex(slots[slot], TexSize, TexSize, disk, encoding))
                {
                    log.Error("[Proteus] second skin: failed to write {0}", disk);
                    return null;
                }
                _texHashes[disk] = hash;
                texturesChanged = true;
            }

            redirects[gamePath] = Rel(outputRoot, disk);
            paths.Add(gamePath);
        }
        return paths;
    }

    /// <summary>
    /// Rebuild an imported pack's material onto <c>characterscroll.shpk</c>, so its meshes can carry the
    /// same animated glow a second-skin shell can. Null when it cannot be done, and the caller falls back to
    /// publishing the pack's own material — a missing effect file must never take the piece off the wearer.
    /// <para/>
    /// Three things make this cheap. The pack's norm/mask/index textures are still served by the pack's OWN
    /// Penumbra redirects, so the rebuilt material just names the same game paths and nothing is copied.
    /// The shader's four slots are <c>norm, mask, id, catc</c> with no base, which is exactly what a pack
    /// like the piercings ships once the scroll map is added. And nothing here touches the pack on disk:
    /// this runs from <c>unit.Mtrl</c>, re-read every composite, so clearing the effect republishes the
    /// author's bytes with no undo state to keep.
    /// <para/>
    /// What IS lost is a base texture, if the pack has one — characterscroll has no slot for it, and that is
    /// load-bearing rather than an oversight (see <see cref="GearMaterialWriter.TextureOrder"/>: a base
    /// present drives the diffuse and the colour table's is ignored, so a glow on white art can never read).
    /// The panel says so before the user turns it on.
    /// </summary>
    private byte[]? BuildContentGlowMaterial(
        ContentUnit unit, string texPrefix, string texturesDir, char letter, string? effectsFolder,
        Dictionary<string, string> redirects, ref bool texturesChanged)
    {
        var glow = unit.Glow;
        // ToScrollSettings returns null exactly when Scroll is empty, so a non-null result also pins the
        // effect name for the lookups below.
        if (glow?.ToScrollSettings() is not { } scrollSettings || glow.Scroll is not { } effectName) return null;

        // The scroll map, from the mod's own Effects/ folder then the user's library — the same lookup a
        // shell's glow uses, so one library serves both.
        var effectPath = SidecarDiscoveryService.ResolveEffectPath(unit.Entry, effectsFolder, effectName);
        if (effectPath == null)
        {
            // The library folder is named as well as the effect. It comes from Penumbra's mod directory, so
            // it is null whenever that IPC is momentarily unavailable — and a null library silently reduces
            // the lookup to the pack's own Effects folder, which most packs do not have. That reads exactly
            // like a missing file while the file is sitting in the library.
            log.Warning("[Proteus] content: {0} wants effect \"{1}\", which is in neither its own Effects "
                      + "folder nor the library ({2}) — publishing the pack's own material instead",
                unit.Entry.ModDirectory, effectName, effectsFolder ?? "(library unavailable)");
            return null;
        }
        var scroll = textureLoader.LoadPngAsRgba(effectPath, TexSize, TexSize);
        if (scroll == null)
        {
            log.Warning("[Proteus] content: effect \"{0}\" could not be decoded ({1})", effectName, effectPath);
            return null;
        }

        var template = textureLoader.LoadRawMtrl(null, GearMaterialWriter.TemplateFor(RenderModeInference.GlowShader));
        if (template == null)
        {
            log.Error("[Proteus] content: missing the {0} template material", RenderModeInference.GlowShader);
            return null;
        }

        // The pack's own texture paths, kept verbatim. A slot the pack doesn't name gets the same fallback
        // WriteTextures picks for a shell, written beside the scroll map.
        var packTex = TextureLoader.ParseMtrlBytes(unit.Mtrl);
        var outputRoot = Directory.GetParent(texturesDir)!.FullName;
        // A ref parameter can't be captured by a local function; folded back into the caller's flag below.
        bool wroteAnything = false;

        string? Publish(string slot, byte[] rgba)
        {
            var gamePath = texPrefix + slot + ".tex";
            var disk = Path.Combine(texturesDir, $"ss_{letter}_{slot}.tex");
            // Same rules as the shell path: never compress "id" (its red/green are discrete row selectors
            // and a lossy bucket crossing picks the wrong row), BC7 for the continuous ones.
            var encoding = config.EnableCompression && !string.Equals(slot, "id", StringComparison.OrdinalIgnoreCase)
                ? TexEncoding.Bc7
                : TexEncoding.Uncompressed;

            var hash = Hash(rgba) ^ ((ulong)((int)encoding + 1) * 0x9E3779B97F4A7C15ul);
            if (!(_texHashes.TryGetValue(disk, out var prev) && prev == hash && File.Exists(disk)))
            {
                if (!textureLoader.WriteTex(rgba, TexSize, TexSize, disk, encoding))
                {
                    log.Error("[Proteus] content: failed to write {0}", disk);
                    return null;
                }
                _texHashes[disk] = hash;
                wroteAnything = true;
            }
            redirects[gamePath] = Rel(outputRoot, disk);
            return gamePath;
        }

        // Republish the pack's OWN textures under paths Proteus owns, rather than naming the pack's paths
        // and hoping they resolve to the pack.
        //
        // They do not always. A content pack names its textures in whatever namespace its author invented,
        // and nothing makes that unique: this pack asks for chara/neolithe/neolithe_piercings_index.tex, a
        // path Neolithe [ALL IN ONE] also claims — and wins. The game therefore sampled ALL IN ONE's index
        // (red 255 → row 16) while the pack's own selects row 1, so every colour and glow written to row 1
        // rendered as nothing and the piece drew from row 16's silver instead. The piece looked untouched
        // and nothing anywhere said another mod had taken the texture.
        //
        // Copied by BYTES, not decoded and re-encoded: whatever the author shipped is what gets published,
        // and WriteIfChanged means a recomposite is not a rewrite.
        var packRoot = unit.Entry.ModRoot;
        string? Republish(string slot, string? packPath, byte[] fallback)
        {
            // Nothing named at all: the shell's own fallback for an empty slot.
            if (packPath == null) return Publish(slot, fallback);

            // The pack's OWN selection first, exactly as the non-glow path resolves it — see
            // SelectedTextureFiles. This used to go straight to ContentTextureFile, which asks Penumbra who
            // wins the path globally and then, when that answer is refused, name-searches the mod folder and
            // takes whatever the walk reaches first. Both are wrong for a print group: Cerise ships
            // v01_c0201e6085_met_d.tex under four print folders, so putting a glow on the kimono handed the
            // print to directory order. The unit already carries the answer; ask it before guessing.
            var file = unit.TexFiles.TryGetValue(packPath, out var chosen) ? chosen
                     : packRoot == null ? null
                     : ContentTextureFile(packRoot, packPath);

            // Named, but the pack does not ship it — a material may legitimately point at a VANILLA game
            // texture. Keep the author's path so the game supplies it; substituting a flat colour here
            // would blank a texture that was resolving perfectly well.
            if (file == null) return packPath;

            try
            {
                var disk = Path.Combine(texturesDir, $"ss_{letter}_{slot}.tex");
                // Through the memo for the same reason the non-glow path is: this is the pack's own art,
                // whatever size the author shipped, and re-reading it to discover it has not changed is the
                // cost that dominates a composite. See _packCopies.
                if (CopyPackFile(file, disk)) wroteAnything = true;
                var gamePath = texPrefix + slot + ".tex";
                redirects[gamePath] = Rel(outputRoot, disk);
                return gamePath;
            }
            catch (Exception ex)
            {
                log.Warning("[Proteus] content: could not republish {0} ({1}) — naming the pack's own path "
                          + "instead: {2}", slot, file, ex.Message);
                return packPath;
            }
        }

        // Fallbacks match the shell path exactly, so the two can't disagree about what "no texture" means:
        // a white mask (a grey one halves the lighting everywhere) and a row-16-A index.
        var norm = Republish("norm", packTex.Normal, Solid(128, 128, 255, 255));
        var mask = Republish("mask", packTex.Mask,   Solid(255, 255, 255, 255));
        var id   = Republish("id",   packTex.Index,  Solid(255, 255, 0, 255));
        var catc = Publish("catc", scroll);
        texturesChanged |= wroteAnything;
        if (norm == null || mask == null || id == null || catc == null) return null;

        // Built with NO rows, then the pack's own colour table grafted on, then the user's rows written over
        // that. Order matters: Build clones vanilla e6257, so grafting is what keeps the author's silver,
        // metalness and roughness instead of inheriting the template's — and doing it after Build restores
        // the author's tile alpha, which Build zeroes because a second skin is skin and a piercing is not.
        var built = GearMaterialWriter.Build(template, [norm, mask, id, catc], rows: null, scroll: scrollSettings);
        var grafted = GearMaterialWriter.CopyColorTable(built, unit.Mtrl);
        return GearMaterialWriter.PatchColorTable(grafted, unit.Rows, isScroll: true);
    }

    /// <summary>
    /// The file this pack's own selection supplies for a material, or null when its options say nothing.
    /// <para/>
    /// The LAST selected group wins, not the first — that is Penumbra's rule, and getting it backwards is
    /// what made an imported piercing pack invisible. Its "base install" group ships one neutral normal at
    /// every piece's texture path, and the per-piece toggle groups after it (eyebrow / dermal / nose ring /
    /// lip ring) swap in the real one. Base install is a single-select group, so it is ALWAYS ticked and was
    /// always found first: every piece got the neutral normal, whose coverage gate is empty, and four
    /// piercings loaded on the character and drew nothing. Penumbra, publishing the same pack itself, hands
    /// the later group's file over — which is why raising that mod above Proteus "fixed" it, and why the
    /// same report arrived twice from different users.
    /// <para/>
    /// A source from the pack's DEFAULT data (no group) always counts as ticked, but ranks below every
    /// group: default data is what a mod publishes before any option is considered, so an option that names
    /// the same path is an override of it, whichever order they happen to be recorded in.
    /// <para/>
    /// Null rather than a guess when a group is entirely unselected — the caller has two more answers to
    /// try, and inventing one here would take precedence over both.
    /// </summary>
    internal static string? SelectedMaterialFile(
        string modRoot, IReadOnlyList<ContentMaterialSource> sources,
        IReadOnlyDictionary<string, List<string>>? selected)
    {
        // BACKWARDS, so the last selected group is found first and the scan can stop there. Walking forwards
        // and keeping the last match would stat every source in the list on every call — and this runs per
        // material, per layer, per composite, against packs that put nine files behind one leaf.
        string? fromDefault = null;  // the pack's default data, used only when no group supplies the path

        for (int i = sources.Count - 1; i >= 0; i--)
        {
            var s = sources[i];
            if (s.File.Length == 0) continue;
            bool grouped = s.Group != null;
            if (grouped
             && (selected == null
              || !selected.TryGetValue(s.Group!, out var on)
              || !on.Any(x => string.Equals(x, s.Option, StringComparison.OrdinalIgnoreCase))))
                continue;

            // Constrained to the mod's own folder, like ContentMaterialFile — and here the check is doing
            // more than restating "this pack's file". A source File is a manifest VALUE, and those are only
            // ever slash-normalised (PenumbraPackage.ReadFiles); the traversal rejection guards zip ENTRY
            // names, which is a different list. So a pack claiming "C:/Users/…/id_rsa" hands Path.Combine a
            // rooted second argument, gets it back verbatim, and — through SelectedTextureFiles — has that
            // file copied into the mod Proteus publishes.
            var disk = Path.Combine(modRoot, s.File.Replace('/', Path.DirectorySeparatorChar));
            if (!IsUnder(modRoot, disk) || !File.Exists(disk)) continue;

            if (grouped) return disk;   // the last selected group, reached first — nothing earlier can beat it
            fromDefault ??= disk;       // any default will do; two of them cannot disagree about one path
        }

        return fromDefault;
    }

    /// <summary>
    /// The texture files this pack's selection supplies for a material, keyed by the game path the material
    /// names them at. Empty when the pack ships none of them.
    /// <para/>
    /// This is what actually decides a print. All four Cerise prints name the same four texture paths and
    /// two of them share one .mtrl byte-for-byte, so <see cref="SelectedMaterialFile"/> alone cannot tell
    /// Blue Rose from Pink Floral — only the file behind <c>..._met_d.tex</c> does. Publishing the material
    /// while leaving its textures to Penumbra's global resolve left the print to whichever mod won the path.
    /// <para/>
    /// Each path is answered by the same rule as the material: the pack's own selection, default data
    /// counting as always on. A path no selected option supplies is absent from the result and left exactly
    /// as the material names it, so a vanilla texture stays vanilla.
    /// </summary>
    internal static Dictionary<string, string> SelectedTextureFiles(
        string modRoot, ContentPiece piece, byte[] mtrl,
        IReadOnlyDictionary<string, List<string>>? selected)
    {
        var picked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (piece.TextureOptions is not { Count: > 0 }) return picked;

        MtrlTexturePaths slots;
        try { slots = TextureLoader.ParseMtrlBytes(mtrl); }
        catch { return picked; }
        if (!slots.Parsed) return picked;

        foreach (var tex in new[] { slots.Diffuse, slots.Normal, slots.Mask, slots.Index })
        {
            if (tex is not { Length: > 0 }) continue;
            var sources = piece.TextureSourcesFor(tex);
            if (sources.Count == 0) continue;
            if (SelectedMaterialFile(modRoot, sources, selected) is { } disk) picked[tex] = disk;
        }
        return picked;
    }

    /// <summary>A stable digest of a texture selection, for the unit key. Two options that share a material
    /// but point it at different textures are two different materials to publish — without this in the key
    /// Blue Rose and Pink Floral merge into one unit and one of them silently wins.</summary>
    private static string TextureKey(Dictionary<string, string> picked)
        => picked.Count == 0
            ? ""
            : string.Join(" ", picked.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                                          .Select(p => p.Key + "=" + p.Value));

    /// <summary>
    /// The material file this piece should publish RIGHT NOW, out of the game paths its pack redirects the
    /// leaf under — or null to fall back on the one the importer recorded.
    /// <para/>
    /// The SECOND question the publish asks, not the first: <see cref="SelectedMaterialFile"/> reads the
    /// pack's own option state and gets there without Penumbra. This one remains for a pack whose layout the
    /// option map cannot describe — nothing recorded for the leaf, or a group structure the importer could
    /// not resolve into sources.
    /// <para/>
    /// Asked live because the recorded answer cannot be right for a pack that ships one material many times.
    /// The dye and metal option groups every dress mod carries redirect the same leaf once per colour, and
    /// the importer sees them all at once and has to pick blind. deadrose has nine files behind one leaf, so
    /// eight of its nine dye options published the wrong one.
    /// <para/>
    /// Constrained to the mod's own folder for the same reason the texture lookup is: a content material
    /// belongs to its pack, and an answer from anywhere else is answering a different question. That refusal
    /// is what sent Cerise back to its frozen print — "Royally Bundled Bun" claims the same kimono path and
    /// wins it — and why the option map above had to exist.
    /// </summary>
    /// <param name="cache">
    /// Per-build memo, keyed by mod root and game path. Not an optimisation so much as a bound: this runs
    /// once per drawn material of every content layer, and a pack with fifteen layers of four materials
    /// apiece was making over a hundred IPC round trips per composite for a handful of distinct answers —
    /// several times per settle, since a redraw produces more than one composite. The answer cannot change
    /// within a build, so one call per distinct path is all there is to make.
    /// </param>
    private string? ContentMaterialFile(
        string modRoot, IReadOnlyList<string> gamePaths, Dictionary<string, string?> cache)
    {
        foreach (var gamePath in gamePaths)
        {
            var key = modRoot + '\u0000' + gamePath;
            if (!cache.TryGetValue(key, out var hit))
            {
                var viaPenumbra = penumbra.ResolvePlayer(gamePath);
                // ResolvePlayer echoes the request back when nothing redirects it, which is not a file.
                cache[key] = hit =
                    viaPenumbra != null
                 && !string.Equals(viaPenumbra, gamePath, StringComparison.OrdinalIgnoreCase)
                 && File.Exists(viaPenumbra)
                 && IsUnder(modRoot, viaPenumbra)
                        ? viaPenumbra
                        : null;
            }
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>
    /// The file inside a pack that backs one of the textures its material names.
    /// <para/>
    /// The FALLBACK now, not the first answer: the glow builder asks <c>unit.TexFiles</c> before this, which
    /// carries the pack's own option state. Both routes here are guesses by comparison — Penumbra reports
    /// who wins the path across every installed mod, and the name search below takes whatever the directory
    /// walk reaches first. On a print group both are wrong, and the walk is wrong at random: Cerise ships
    /// one texture name under four print folders.
    /// <para/>
    /// Penumbra's answer is only accepted from inside THIS pack. "What does the game load at this path" and
    /// "which file did this pack ship" are different questions, and they diverge exactly when another mod
    /// has claimed the same path — which is the case this republish exists to defeat. Accepting a foreign
    /// answer would copy the collision into our own output and change nothing.
    /// <para/>
    /// Null when the pack ships nothing by that name. That is normal and not an error: a material may name
    /// a vanilla game texture it does not provide, and that one should keep resolving through the game.
    /// </summary>
    private string? ContentTextureFile(string modRoot, string gamePath)
    {
        var viaPenumbra = penumbra.ResolvePlayer(gamePath);
        // ResolvePlayer echoes the request back when nothing redirects it, which is not a file.
        if (viaPenumbra != null
            && !string.Equals(viaPenumbra, gamePath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(viaPenumbra)
            && IsUnder(modRoot, viaPenumbra))
            return viaPenumbra;

        try
        {
            var leaf = Path.GetFileName(gamePath.Replace('\\', '/'));
            return leaf.Length == 0
                ? null
                : Directory.EnumerateFiles(modRoot, leaf, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Whether <paramref name="path"/> sits inside <paramref name="root"/>. Through
    /// <see cref="Path.GetRelativePath"/> so <c>..</c> and mixed separators cannot smuggle a path out.</summary>
    private static bool IsUnder(string root, string path)
    {
        try
        {
            var rel = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
            return !Path.IsPathRooted(rel)
                && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && rel != "..";
        }
        catch { return false; }
    }

    /// <summary>
    /// Every colour-table row neutral white, so no row keeps the gear template's default (often dark)
    /// colour. The index texture can select ANY pair — one the colorset never defined, or the undefined
    /// sub-row of a pair that set only the other — and that row must not paint something the author never
    /// chose, exactly as the skin layer defaults its rows to white. (16 pairs = 32 sub-rows.)
    /// </summary>
    private static Dictionary<int, GearColorRow> NeutralRows()
    {
        var rows = new Dictionary<int, GearColorRow>();
        for (int r = 0; r < 32; r++)
            rows[r] = new GearColorRow { Diffuse = (1f, 1f, 1f), Emissive = (0f, 0f, 0f) };
        return rows;
    }

    /// <summary>Map the metadata's 1-based row/sub-row presets onto 0-based color table rows.</summary>
    /// <param name="neutralWhenEmpty">
    /// With no presets, return the all-white baseline instead of null. Null means "keep the gear template's
    /// own colour table", which is right for an ordinary shell — its template belongs to the look being
    /// worn — and wrong for a MASK shell, which has no look of its own. A mask shell is a WHITE base plus
    /// whatever the colorset says (see the Build call), so white rows are its blank canvas: it starts
    /// uncoloured and the Masks colorset dyes it from there. Inheriting the template's table instead would
    /// start it at some arbitrary dark colour the author never picked. Only the mask-shell caller passes true.
    /// </param>
    private static Dictionary<int, GearColorRow>? BuildRows(List<ColorTableRowPreset>? presets,
                                                            bool neutralWhenEmpty = false)
    {
        if (presets == null || presets.Count == 0) return neutralWhenEmpty ? NeutralRows() : null;
        var rows = NeutralRows();

        foreach (var p in presets)
        {
            if (p.Row is < 1 or > 16) continue;
            Add((p.Row - 1) * 2, p.SubRowA);
            Add((p.Row - 1) * 2 + 1, p.SubRowB);
        }
        return rows;

        void Add(int rowIndex, ColorTableSubRowPreset? sub)
        {
            if (sub == null) return;   // leaves the neutral-white row from the init above
            rows[rowIndex] = RowFrom(sub);
        }
    }

    /// <summary>
    /// The rows an imported content pack's OWN material should have overwritten — only the ones the user
    /// actually edited.
    /// <para/>
    /// Deliberately NOT <see cref="BuildRows"/>: that one starts from <see cref="NeutralRows"/> and returns
    /// all 32 rows, because a shell's material is cloned from a vanilla template whose colours have to be
    /// neutralised wholesale. A content material is the author's own, and everything they set and the user
    /// did not touch has to survive — so this returns a SPARSE dictionary, and
    /// <see cref="GearMaterialWriter.PatchColorTable"/> leaves every row not in it alone.
    /// </summary>
    internal static Dictionary<int, GearColorRow>? BuildSparseRows(List<ColorTableRowPreset>? presets)
    {
        if (presets == null || presets.Count == 0) return null;
        var rows = new Dictionary<int, GearColorRow>();
        foreach (var p in presets)
        {
            if (p.Row is < 1 or > 16) continue;
            if (p.SubRowA is { } a) rows[(p.Row - 1) * 2] = RowFrom(a);
            if (p.SubRowB is { } b) rows[(p.Row - 1) * 2 + 1] = RowFrom(b);
        }
        return rows.Count > 0 ? rows : null;
    }

    /// <summary>One sub-row preset as the material writer's row. Shared by both row builders so a shell and
    /// a content material can never disagree about what a preset means.</summary>
    private static GearColorRow RowFrom(ColorTableSubRowPreset sub)
    {
        var rgb = ParseHex(sub.Diffuse);
        // Glow colour is INDEPENDENT of the diffuse (a scrolling material wants a near-black diffuse
        // with a white emissive), falling back to the diffuse when not given.
        //
        // A row with an intensity but NO colour anywhere stays dark, and that is deliberate. The editor's
        // swatch shows an unset colour as white, so this used to be a genuine mismatch — but the fix for it
        // belongs where the value is WRITTEN, not here: the Glow slider now stores white the moment someone
        // raises it (see ColorTableEditor). Resolving it white here instead reinterpreted every row already
        // authored, and mods carrying an inert Glow value suddenly emitted at full strength — one shipped
        // bodysuit had five of them and its patterns blew out.
        var emis = ParseHex(sub.EmissiveColor) ?? rgb;
        return new GearColorRow
        {
            Diffuse = rgb,
            Specular = ParseHex(sub.Specular),
            // Always write emissive — a template's own emissive must be CLEARED, not inherited.
            // Vanilla characterscroll rows carry a warm non-zero emissive that renders as a flat
            // white glow and drowns out the scroll map entirely.
            Emissive = sub.Emissive > 0f && emis is { } c
                ? (c.R * sub.Emissive, c.G * sub.Emissive, c.B * sub.Emissive)
                : (0f, 0f, 0f),
            // The dial itself, for characterscroll — see GearColorRow.EmissiveStrength.
            EmissiveStrength = sub.Emissive,
            SphereMapIndex = sub.SphereMap,
            SphereMapMask = sub.SphereIntensity,
            Roughness = sub.Roughness,
            Metalness = sub.Metalness,
        };
    }

    private static (float R, float G, float B)? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return (((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }

    // ── EQDP ─────────────────────────────────────────────────────────────────

    private static readonly string[] RaceNames = ModelRace.Names;

    /// <summary>
    /// The accessory a second skin rides on. For an already-equipped ring/bracelet <see cref="BaseModel"/>
    /// holds its model bytes (the shell is appended into it) and <see cref="BaseMatCount"/> its material
    /// count; for the Emperor's New Ring fallback both are 0/null and an EQDP edit forces the model.
    /// </summary>
    // ModelPath is the ACTUAL loaded game path for an equipped host (rings/bracelet/worn or injected
    // glasses) — used as the redirect key so we match the char code the game really requested. Invisible
    // items (Emperor accessories, invisible glasses) have no model for many races and load a fallback race
    // (e.g. c0101 male on a female), so a path rebuilt from the player's char code would miss. Null for the
    // Emperor fallback, whose EQDP edit forces the player-race model, making the rebuilt path correct.
    private readonly record struct HostAccessory(
        int SetId, string Slot, string EqdpSlot, byte[]? BaseModel, int BaseMatCount, string Tree, char Prefix,
        string? ModelPath = null,
        // The material variant to publish under, when the live resource tree cannot answer. Only a CARRIER
        // needs this: it is equipped after the shell is built, so its materials are not in the tree yet and
        // VariantFolderFor would fall back to v0001 — wrong for any carrier item that is not variant 1, and
        // wrong in a way that renders nothing at all. Null for worn hosts, which the tree does answer for.
        int? KnownVariant = null);

    /// <summary>
    /// Every model the shell can be hosted on, in FILL priority — the policy is written out at the top of
    /// the method body. Each carries its base material count; a host holds up to
    /// <c>MaxMaterials - BaseMatCount</c> layers, and layers are distributed across this list in order (see
    /// Build), so a look too big for one host spills into the next. A candidate with no room to append even
    /// one layer is skipped.
    /// <para/>
    /// <paramref name="cutCode"/> is the race code the shell GEOMETRY is in (see Build). A CARRIER host —
    /// one whose model we replace — may load under a different code, because Build then rewrites its EQDP
    /// entry to pull our copy into cut space; a worn item may not, because its metadata is the player's.
    /// <paramref name="equipCode"/> is the code the character's EQUIPMENT loads at, used only to predict
    /// where our not-yet-loaded injected glasses will appear.
    /// </summary>
    private List<HostAccessory> ChooseHosts(string cutCode, string equipCode, string wearerCode,
        IReadOnlyDictionary<string, string>? equipped,
        IReadOnlyList<string>? metModels, int? invisibleGlassesSet, string outputRoot,
        IReadOnlySet<string> hostedPackRoots,
        int? emperorRingVariant, int? invisibleGlassesVariant)
    {
        log.Information("[Proteus] host: choosing from equipped accessories [{0}], head/glasses [{1}]",
            equipped == null ? "(null)" : string.Join(", ", equipped.Select(kv => $"{kv.Key}={kv.Value}")),
            metModels == null || metModels.Count == 0 ? "(none)" : string.Join(", ", metModels));

        // Load a candidate host model: resolve through Penumbra, read its bytes, and count its materials.
        // Returns null (having warned) when the path is unparseable, unloadable, or its material table
        // won't parse — a host we can't understand must be SKIPPED, never guessed at, because an
        // understated material count makes the appended material letters collide with the base's own.
        (int SetId, byte[] Bytes, int Mats)? LoadCandidate(string slot, string gamePath, char prefix)
        {
            if (ParseSetId(gamePath, prefix) is not int setId)
            {
                log.Warning("[Proteus] host: {0} — cannot parse a '{1}' set id from {2}, skipping", slot, prefix, gamePath);
                return null;
            }

            // The shell is cut from equipment models, so it may only be redirected onto a host the game
            // loads under the SAME model code. An invisible item with no model at that code loads a
            // different one instead (e.g. c0101 male under a c0201 female); the game then applies
            // race-conversion deformation to whatever sits at that path, which shrinks and warps our shell
            // — it ends up inside the skin with only its edges poking through, correctly shaped and
            // animated but visibly the wrong size. Skip it and let the next candidate host instead.
            //
            // A host that loads under a different code than the shell was cut in renders it at the wrong
            // SIZE: the code in the path is what decides whether the game race-deforms a model, so a
            // c0201-cut shell hosted at c0801 gets no deform while the body it copies does.
            //
            // Never rejected for loading under a foreign code. Build #294 rejected, and on a Miqo'te
            // wearing only our carrier glasses that removed the last host: no shell at all, and her glasses
            // frames came back, since hiding them is a side effect of hosting on them. The size warning
            // belongs to the APPEND path only (see WarnForeignAppendHost) — a carrier host is redirected
            // into cut space by Build instead, so warning here would cry wolf every composite.

            // A host's model path is one WE redirect to the shell, so Penumbra can resolve it straight back
            // to our own previous output. Taking that as the "base" is a feedback loop: on the append path
            // it would merge the shell into the shell again every composite, doubling the model each run.
            // The composite clears redirects and reloads before getting here, but that's async and races —
            // observed in the wild resolving to an 875 KB "glasses" model (our 854 KB shell).
            //
            // Go through the upstream resolver, NOT a plain resolve. An append host is an item the player
            // chose, and they may well have modded it: this path has to come back as THEIR necklace/ring
            // file even on the composites where our own redirect masks it. Reading the game's original
            // instead — what this did before — silently reverted a modded host to vanilla from the second
            // composite onward, and took the appended shell with it.
            var disk = resolveUpstream?.Invoke(gamePath) ?? penumbra.ResolvePlayer(gamePath);
            // Defence in depth: ResolveUpstream already biases toward "ours" and returns null rather than
            // hand back our output, so this should not fire — but a null resolver (tests) still needs it,
            // and appending the shell to itself is the one failure that compounds silently every run.
            if (disk != null && IsInsideOutputRoot(disk, outputRoot))
            {
                log.Debug("[Proteus] host: {0} resolved to our own output ({1}) and no upstream is known — "
                        + "reading the game's original instead", slot, disk);
                disk = null;
            }

            var bytes = textureLoader.LoadRawFile(disk, gamePath);
            if (bytes == null)
            {
                log.Warning("[Proteus] host: {0} ({1}{2:D4}) model {3} not loadable (disk={4}) — skipping", slot, prefix, setId, gamePath, disk ?? "(null)");
                return null;
            }

            try { return (setId, bytes, SecondSkinWriter.MaterialNames(bytes).Count); }
            catch (Exception ex)
            {
                log.Warning(ex, "[Proteus] host: {0} ({1}{2:D4}) material parse failed — skipping", slot, prefix, setId);
                return null;
            }
        }

        // True when the model has no real geometry to append onto — an invisible item ("The Emperor's
        // New …"-style empty frames). A shell merged into one of those never renders, so it must be
        // REPLACED with a standalone shell instead.
        bool IsDegenerate(byte[] bytes) => bytes.Length < DegenerateModelBytes;

        // An APPEND host merges into an item the player chose, so its metadata is not ours to rewrite —
        // which means a host loading under a code other than the shell's stays at the wrong size and the
        // player needs to be told. Carrier hosts (replaced, invisible) are fixed silently by Build.
        void WarnForeignAppendHost(string slot, char prefix, int setId, string gamePath)
        {
            if (PathCharCode(gamePath) is not { } pathCc
                || string.Equals(pathCc, cutCode, StringComparison.OrdinalIgnoreCase))
                return;
            log.Warning(
                "[Proteus] host: {0} ({1}{2:D4}) loads as c{3} but the shell was cut in c{4} — the game "
              + "deforms the body and not this worn item, so the shell will render a race-size wrong. Free "
              + "a ring slot (either hand) or your facewear slot to let Proteus host it in c{4} instead",
                slot, prefix, setId, pathCc, cutCode);
        }

        // Load an equipped model (accessory or head-equipment glasses) as a host candidate, or null if absent,
        // unloadable, or already FULL (its own materials leave no room to append even one layer). Tree is
        // "accessory" (prefix a — rings/bracelet/necklace) or "equipment" (prefix e — glasses/head); the
        // shell's redirect + material game-paths are built from these so both trees resolve correctly.
        HostAccessory? ConsiderPath(string slot, string? gamePath, string tree, char prefix, string eqdpSlot)
        {
            if (gamePath == null)
            {
                log.Information("[Proteus] host: {0} — none equipped", slot);
                return null;
            }

            // The Emperor's New Ring is invisible and only loads a model via our own EQDP edit — it is the
            // FALLBACK, never an append host. Appending to it skips that EQDP, so its model never loads and
            // nothing renders. Skip it here so it drops through to the replace+EQDP path below. (Only the
            // accessory tree has an Emperor set; the equipment/glasses tree never does.)
            if (prefix == 'a' && ParseSetId(gamePath, prefix) == EmperorSetId)
            {
                log.Information("[Proteus] host: {0} is the Emperor's ring (a{1:D4}) — reserved for fallback, skipping", slot, EmperorSetId);
                return null;
            }

            if (LoadCandidate(slot, gamePath, prefix) is not { } c) return null;

            if (c.Mats >= SecondSkinWriter.MaxMaterials)
            {
                log.Debug("[Proteus] host: {0} ({1}{2:D4}) already carries {3}/{4} materials — no room to append, skipping",
                    slot, prefix, c.SetId, c.Mats, SecondSkinWriter.MaxMaterials);
                return null;
            }
            log.Information("[Proteus] host: {0} ({1}{2:D4}) candidate — {3} base material(s), capacity {4}",
                slot, prefix, c.SetId, c.Mats, SecondSkinWriter.MaxMaterials - c.Mats);
            return new HostAccessory(c.SetId, slot, eqdpSlot, c.Bytes, c.Mats, tree, prefix, gamePath);
        }

        // Accessory host (ring/bracelet): look the slot up in the equipped-accessory map.
        HostAccessory? Consider(string slot, string eqdpSlot)
            => ConsiderPath(slot,
                equipped != null && equipped.TryGetValue(slot, out var gp) ? gp : null,
                "accessory", 'a', eqdpSlot);

        var hosts = new List<HostAccessory>();
        // Worn accessories that would host, but load under a code the shell was not cut in: kept aside
        // for the last resort below rather than used, since we cannot move a worn item into cut space.
        var foreignAccessories = new List<HostAccessory>();

        // ── the host policy, in order, and the same on every race ──────────────────────────────────
        // Prefer a host we can move into cut space; never rewrite an item the player chose; never end up
        // with no host at all.
        //
        // 1. Facewear CARRIER — our injected pair or a degenerate (invisible, empty-frames) item. Its model
        //    is REPLACED, so nothing of it is ever seen and Build may rewrite its EQDP to pull the shell
        //    into cut space. The player's own visible pair is deliberately NOT a candidate: appending would
        //    merge into their item, whose metadata is not ours to move, so it could only ever host at the
        //    wrong size. Leave it alone and let the accessories below take it.
        foreach (var metPath in OrderMetCandidates(metModels, invisibleGlassesSet))
        {
            if (LoadCandidate("met", metPath, 'e') is not { } c) continue;

            bool ours = invisibleGlassesSet is int inv && inv == c.SetId;
            if (ours || IsDegenerate(c.Bytes))
            {
                log.Information("[Proteus] host: glasses/head e{0:D4} (met, REPLACE — {1}, base {2} B)",
                    c.SetId, ours ? "our injected pair" : "degenerate base", c.Bytes.Length);
                hosts.Add(new HostAccessory(c.SetId, "met", "Head", null, 0, "equipment", 'e', metPath));
                break;
            }
            log.Information("[Proteus] host: glasses/head e{0:D4} is the player's own pair ({1} material(s), "
                          + "{2} B) — not ours to redirect into c{3}, leaving it alone",
                c.SetId, c.Mats, c.Bytes.Length, cutCode);
        }

        // Nothing occupies the head "_met" slot yet, but the invisible-glasses feature is on — so the
        // compositor is about to equip our pair. Host on it NOW: the injected model only loads after the
        // equip's redraw, and OUR pair always takes the REPLACE path (no base bytes needed), its path fully
        // determined by our set id plus this character.
        //
        // KNOWN empty, not merely "not known to be occupied". A null metModels means no draw-object walk
        // has ever succeeded, and treating that as "no hat worn" is how this REPLACE host silently took a
        // player's hat off: the slot was occupied all along, we just had not looked yet. The caller retries
        // the walk before asking, so null here means it genuinely could not find out — in which case the
        // shell falls through to a worn accessory or the Emperor's-ring carrier, which replace nothing.
        if (hosts.Count == 0 && metModels is { Count: 0 } && invisibleGlassesSet is int pending)
        {
            // Predicted with the EQUIPMENT code, not the cut code: this is a guess at the path the game
            // will load our pair from once it is equipped, and equipment loads in the character's own
            // space (a Miqo'te's facewear ships native at c0801 even though her body parts are c0201).
            var pendingPath = $"chara/equipment/e{pending:D4}/model/c{equipCode}e{pending:D4}_met.mdl";
            log.Information("[Proteus] host: invisible glasses e{0:D4} (met, REPLACE — pending injection)", pending);
            hosts.Add(new HostAccessory(pending, "met", "Head", null, 0, "equipment", 'e', pendingPath,
                KnownVariant: invisibleGlassesVariant));
        }
        // (No invisible-glasses-from-nothing route for a slot we don't fill ourselves: an empty head/facewear
        // slot loads NO model, so there's nothing to redirect.)

        // 2. Worn accessories — rings (right then left), bracelet, necklace — appended so they stay
        //    visible, but ONLY when they already load in the shell's own space. One that doesn't would
        //    render the shell a race-size wrong, and unlike a carrier we cannot fix it, so it is held back
        //    for step 4 and the Emperor's ring gets first refusal.
        //
        //    Build #320 briefly moved the Emperor's ring AHEAD of these, on the theory that appending into
        //    a worn item was what stopped the shell rendering. That was wrong: a REPLACE carrier host
        //    (e5501/met, append=False) reproduces the same symptom, so the append/carrier split explains
        //    nothing and the reorder was reverted rather than left in on a dead rationale.
        foreach (var (slot, eqdp) in new[] { ("rir", "RFinger"), ("ril", "LFinger"), ("wrs", "Wrists"), ("nek", "Neck") })
        {
            if (Consider(slot, eqdp) is not { } acc) continue;
            if (acc.ModelPath != null && PathCharCode(acc.ModelPath) is { } accCc
                && !string.Equals(accCc, cutCode, StringComparison.OrdinalIgnoreCase))
            {
                log.Information("[Proteus] host: {0} ({1}{2:D4}) loads as c{3}, not the shell's c{4} — held back, "
                              + "the Emperor's ring hosts in c{4} instead", slot, acc.Prefix, acc.SetId, accCc, cutCode);
                foreignAccessories.Add(acc);
                continue;
            }
            hosts.Add(acc);
        }

        // 3. Invisible "Emperor's New" CARRIERS (replace + EQDP), in every accessory slot that is FREE —
        //    right ring, left ring, bracelet, necklace, in that order. A slot holding the player's own piece
        //    is not ours to take. Offered even when step 2 found hosts, since these are also the spill
        //    capacity for a look too big for them.
        //
        //    ALL free slots are offered, not just the first. A carrier is the only host whose EQDP we may
        //    rewrite, so it is the only kind that can publish a natively-authored surface (a face, hair, a
        //    tail) with no deform — see ShellSurfaceKey.RequiresNativeHost. With one carrier the body's
        //    layers took it and every human-part layer was skipped for want of a host, which on a character
        //    wearing two rings and a real pair of glasses meant a face overlay could never render at all.
        //
        //    Resolved per slot rather than assumed: an accessory SET covers every accessory slot, so the
        //    Emperor's New pieces are normally all a0053 — but a slot whose invisible piece isn't in the
        //    sheet is simply not offered, because equipping a VISIBLE item as a carrier would put jewellery
        //    on the player that they never chose and then hide it behind our shell.
        // The invisible piece for a slot. Rings keep the variant the caller read off the sheet (it is the
        // same item for both hands); the others resolve their own, since a set's pieces need not share one.
        InvisibleRing.Identity? carrierFor(string slot)
        {
            var id = InvisibleRing.ResolveFor(Plugin.DataManager, log, slot);
            if (id == null) return null;
            return slot is "rir" or "ril" && emperorRingVariant is { } v
                ? id.Value with { Variant = v }
                : id;
        }

        // Whichever mod already provides an invisible carrier's model, if one does. The Emperor's New pieces
        // have NO model of their own — that is the whole point of them — so anything that answers here is a
        // mod that has put geometry on this slot on purpose, and taking the slot would replace it.
        //
        // Every code the game could ask under: the shell's cut space, the character's equipment space, and
        // the wearer's own race, which is the one such packs are usually authored in (this pack is c0801 on
        // a Miqo'te whose gear is all c0201, so testing the first two alone would have missed it).
        //
        // A PLAIN resolve, deliberately not the upstream resolver the host loader uses. The question here is
        // only "does someone else provide this path", and ResolveUpstream answers a richer one at a cost:
        // it memoises what it finds into the compositor's upstream map — for carrier paths that are not
        // append hosts and have no business being in it — and warns, at Warning level, every time a path we
        // publish resolves to our own file, which for a carrier is the normal case. Two of those per
        // composite buried the copy of that warning that means something.
        // Deduped once, not per slot: on most characters cutCode and equipCode are the same string, so this
        // is two codes rather than three, and the whole check costs one resolve per code per FREE carrier
        // slot — slots holding the player's own jewellery never reach it.
        var carrierCodes = new[] { cutCode, equipCode, wearerCode }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        string? CarrierClaimedBy(string slot, int setId)
        {
            foreach (var code in carrierCodes)
            {
                var gamePath = $"chara/accessory/a{setId:D4}/model/c{code}a{setId:D4}_{slot}.mdl";
                var disk = penumbra.ResolvePlayer(gamePath);
                if (disk == null
                 || string.Equals(disk, gamePath, StringComparison.OrdinalIgnoreCase)   // nothing provides it
                 || IsInsideOutputRoot(disk, outputRoot)                                // our own last publish
                 || hostedPackRoots.Any(r => IsUnder(r, disk)))   // a pack we are placing — see the caller
                    continue;
                return disk;
            }
            return null;
        }

        int freeCarriers = 0;
        // Carrier slots someone else's mod has claimed. Held back rather than dropped: if nothing else can
        // host, a shell that renders still beats protecting a mod, and step 5 takes them.
        var claimedCarriers = new List<(HostAccessory Host, string By)>();
        foreach (var (slot, eqdpSlot, _) in InvisibleRing.CarrierSlots)
        {
            var worn = equipped != null && equipped.TryGetValue(slot, out var wp) ? wp : null;
            // Ours counts as free: it is the piece we equipped for exactly this on an earlier composite.
            if (worn != null && ParseSetId(worn, 'a') != EmperorSetId) continue;
            if (carrierFor(slot) is not { } id) continue;

            var host = new HostAccessory(id.ModelSet, slot, eqdpSlot, null, 0, "accessory", 'a',
                KnownVariant: id.Variant);

            // Someone's mod lives here. Wearing an Emperor's New piece to carry a mod — piercings, jewellery,
            // nails — is a whole modding idiom, and this carrier REPLACES the model at that path and then
            // outranks them on priority, so taking it silently deletes what they equipped it for.
            if (CarrierClaimedBy(slot, id.ModelSet) is { } owner)
            {
                claimedCarriers.Add((host, owner));
                log.Information("[Proteus] host: a{0:D4}/{1} carries another mod's model ({2}) — leaving it "
                              + "alone so that mod keeps showing", id.ModelSet, slot, owner);
                continue;
            }

            hosts.Add(host);
            freeCarriers++;
        }
        if (freeCarriers == 0)
        {
            // THREE causes now, and each sends the reader somewhere different. "Every slot is occupied"
            // sends them to their equipment; "no invisible piece exists" to the resolver's own line; and a
            // slot left alone because another mod's model lives on it is neither — saying "you are wearing
            // your own jewellery" there would send them to look at an empty finger.
            bool anyPieceExists = InvisibleRing.CarrierSlots.Any(c => carrierFor(c.Slot) != null);
            log.Information(claimedCarriers.Count > 0
                ? $"[Proteus] host: no free carrier slot — {claimedCarriers.Count} was/were left to another "
                + "mod (see the lines above) and the rest hold the player's own pieces"
                : anyPieceExists
                ? "[Proteus] host: every accessory slot holds the player's own piece — no free slot for an "
                + "invisible carrier"
                : "[Proteus] host: no invisible carrier item could be resolved for any accessory slot — see "
                + "the \"invisible carrier\" lines above; nothing can host a natively-authored surface");
        }

        // 4. Last resort: every ring slot full AND nothing above usable. Host on a held-back accessory
        //    anyway — a shell a race-size wrong beats no shell at all, which is what rejecting outright
        //    produced in build #294 (and it took the carrier glasses' invisibility with it).
        if (hosts.Count == 0 && foreignAccessories.Count > 0)
        {
            var fallback = foreignAccessories[0];
            WarnForeignAppendHost(fallback.Slot, fallback.Prefix, fallback.SetId, fallback.ModelPath!);
            hosts.Add(fallback);
        }

        // 5. Nothing else at all: take the claimed carriers after all. Protecting another mod's ring is the
        //    right default, but not at the price of the user's whole second skin — and the notice above has
        //    already told them which mod is involved and how to give Proteus a slot of its own.
        if (hosts.Count == 0 && claimedCarriers.Count > 0)
        {
            foreach (var (host, by) in claimedCarriers)
            {
                log.Warning("[Proteus] host: taking a{0:D4}/{1} even though \"{2}\" provides its model — "
                          + "nothing else can host the shell, so that mod's piece will not render",
                    host.SetId, host.Slot, by);
                hosts.Add(host);
            }
            claimedCarriers.Clear();   // taken after all, so there is nothing to tell the user we spared
        }

        // AFTER the last resort, so the notice is only sent when the slot really was left alone.
        if (claimedCarriers.Count > 0) NotifyCarriersClaimed(claimedCarriers);
        else _lastClaimedCarriers = null;   // re-arm: the same mod appearing later is news again

        if (hosts.Count == 0)
            log.Warning("[Proteus] host: nothing can host the shell — no free facewear or ring slot, and no "
                      + "worn accessory to append to");

        log.Information("[Proteus] host: {0} host(s) in fill order: {1}", hosts.Count,
            string.Join(" -> ", hosts.Select(h => $"{h.Prefix}{h.SetId:D4}/{h.Slot}(cap {SecondSkinWriter.MaxMaterials - h.BaseMatCount})")));
        return hosts;
    }

    /// <summary>The set id from a model path for the given tree prefix, e.g. ("…/a0114/model/…", 'a') → 114
    /// or ("…/equipment/e5524/model/…", 'e') → 5524. Null when the path carries no such id — callers must
    /// SKIP the candidate rather than substitute a default: guessing a set builds redirects for an item the
    /// player isn't wearing, which silently never renders.</summary>
    private static int? ParseSetId(string gamePath, char prefix)
    {
        var m = System.Text.RegularExpressions.Regex.Match(gamePath, $@"/{prefix}(\d+)/");
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>
    /// The race/gender code a model path is loaded under, e.g. "…/model/c0101e0279_met.mdl" → "0101".
    /// Null when the path carries none. This is NOT always the wearer's own code: an item with no model
    /// for their race falls back to another (commonly c0101), and the game race-deforms whatever it finds
    /// there — so a shell built for the wearer must never be redirected onto a foreign-race path.
    /// </summary>
    private static string? PathCharCode(string gamePath)
    {
        // 'b' as well as 'a'/'e': the whole-body fallback cuts from chara/human/…/c1401b0001_top.mdl, and
        // that path's code is just as much "the space this geometry is in" as an equipment path's is —
        // there it happens to be the character's own race, which is why that geometry hosts natively.
        //
        // 'f'/'h'/'t'/'z' for the same reason, one surface further out: a human part is cut from
        // chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl and friends. Without them the match
        // fails and the caller reads "no readable path code" — which does not fail loudly, it falls back
        // to the EQUIPMENT code, and a face would then be hosted in c0201 and race-deformed on a c1401
        // character. Silent, and wrong in exactly the way that is hardest to see from a log.
        var m = System.Text.RegularExpressions.Regex.Match(gamePath, @"/c(\d+)[abefhtz]\d+_");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Head equipment and the facewear/glasses bonus slot both render through "_met", so more than one
    /// candidate can be loaded at once. Order them deterministically — our own injected pair first (its
    /// frames must never show), then by set id — so the chosen host can't flip between composites and
    /// churn a shell rebuild + full redraw.
    /// </summary>
    /// <summary>Is this resolved disk path one of OUR managed-mod files (i.e. our own composite output)?</summary>
    private static bool IsInsideOutputRoot(string diskPath, string outputRoot)
    {
        try
        {
            var full = Path.GetFullPath(diskPath);
            var root = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // unparseable path — treat as external, the old behaviour
    }

    private static IEnumerable<string> OrderMetCandidates(IReadOnlyList<string>? metModels, int? invisibleGlassesSet)
        => metModels == null
            ? []
            : metModels
                .OrderByDescending(p => invisibleGlassesSet is int inv && ParseSetId(p, 'e') == inv)
                .ThenBy(p => ParseSetId(p, 'e') ?? int.MaxValue)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Slot's position in an EQDP entry. Each slot owns TWO bits (model, material), so its mask is
    /// <c>3 &lt;&lt; 2*index</c>. Equipment and accessories are separate tables with their own numbering —
    /// Penumbra picks the table from the slot name, so the two runs of indices don't collide.
    /// </summary>
    private static int EqdpSlotIndex(string slot) => slot switch
    {
        "Head" => 0, "Body" => 1, "Hands" => 2, "Legs" => 3, "Feet" => 4,        // equipment
        "Ears" => 0, "Neck" => 1, "Wrists" => 2, "RFinger" => 3, "LFinger" => 4, // accessory
        _ => 0,
    };

    /// <summary>The leading race/gender index of a char code ("0801" → 8), or null when it carries none.
    /// Two digits, matching how the game numbers them: odd = male, even = female, and (n-1)/2 indexes
    /// <see cref="RaceNames"/>.
    /// <para/>
    /// Bounded to the playable range 1..18. Out of it there is no race, so callers get null rather than a
    /// number: unbounded, <see cref="EqdpFallbackIndex"/>'s catch-all arm made every unknown index look
    /// like a child of Midlander, so <see cref="CanFallThrough"/> waved through pairs like c9101 -> c0101.
    /// A guard that decides how a shell is published should not accept a race that cannot exist.</summary>
    private static int? RaceIndex(string? code) => ModelRace.Index(code);

    /// <summary>
    /// The race the game falls through to when a set declares no model for <paramref name="n"/>, or 0 at
    /// the root. Mirrors the game's own table (the same one Penumbra.GameData's <c>GenderRace.Fallback</c>
    /// encodes): most races fall to their own gender's Midlander, with three exceptions — Hrothgar males
    /// go to Roegadyn males, Lalafell females to Lalafell males, and Midlander females to Midlander males.
    /// </summary>
    private static int EqdpFallbackIndex(int n) => ModelRace.Fallback(n);

    /// <summary>
    /// Would emptying <paramref name="from"/>'s entry actually land the game on <paramref name="to"/>?
    /// <para/>
    /// The carrier branch in Build empties the WEARER's own EQDP entry to pull the shell into cut space.
    /// That is only sound when cut space is somewhere on the wearer's fall-through chain — otherwise the
    /// empty sends the game somewhere we publish nothing, or (worse) somewhere we publish a shell cut for
    /// a different body, which arrives race-deformed. Two Midlanders reported exactly that: a shell sitting
    /// too low, as if scaled, cleared by a plugin reload. Nothing validated the pair before this.
    /// <para/>
    /// Same gender is required on top of reachability. The chain does contain two cross-gender hops
    /// (Midlander female -> Midlander male, Lalafell female -> Lalafell male), but arriving at one means
    /// the shell was cut from the other gender's body parts — a cutCode vote that is wrong at its source,
    /// not a deform worth inheriting. Emptying c0201 on a Midlander female to land her on c0101 is the
    /// single worst case the guard rejects, and the one the reports match.
    /// </summary>
    internal static bool CanFallThrough(string? from, string? to)
    {
        if (RaceIndex(from) is not { } f || RaceIndex(to) is not { } t) return false;
        if (f % 2 != t % 2) return false;
        // The chain is at most a few hops and always shrinks toward the root; the bound is a guard against
        // a malformed index steering it into a cycle, not a real depth.
        for (int i = 0, cur = f; i < 8; i++)
        {
            cur = EqdpFallbackIndex(cur);
            if (cur == 0) return false;
            if (cur == t) return true;
        }
        return false;
    }

    /// <summary>
    /// Declare whether a set has a model for a given race/gender. Char codes run c0101 = Midlander male,
    /// c0201 = Midlander female, c0301 = Highlander male, and so on.
    /// <para/>
    /// <paramref name="hasModel"/> false is the interesting one: it is how a Midlander-only gear mod reaches
    /// every other race. With a race's entry empty the game walks to that race's PARENT, loads the parent's
    /// model, and applies the racial deform on the way — so declaring "no model for c1401" while publishing
    /// ours at c0201 hands our shell the same deform the character's c0201 body parts already get. Setting
    /// it instead makes the game load the model natively at that race, with no deform at all.
    /// <para/>
    /// The entry used to be a hardcoded 192, which is <c>3 &lt;&lt; 6</c> — right for RFinger and wrong for
    /// every other slot, harmless only while the Emperor's ring was the sole target.
    /// </summary>
    private static object EqdpManipulation(string charCode, string slot, int setId, bool hasModel = true)
    {
        int n = RaceIndex(charCode) ?? 2;
        string race = RaceNames[Math.Clamp((n - 1) / 2, 0, RaceNames.Length - 1)];
        string gender = n % 2 == 1 ? "Male" : "Female";

        return new
        {
            Type = "Eqdp",
            Manipulation = new
            {
                Entry = hasModel ? 3 << (2 * EqdpSlotIndex(slot)) : 0,
                Gender = gender,
                Race = race,
                SetId = setId,
                Slot = slot,
                ShiftedEntry = hasModel ? 3 : 0,
            },
        };
    }

    /// <summary>
    /// Point one body part's EST entry at an extra skeleton: "wearing <paramref name="setId"/> on
    /// <paramref name="estSlot"/> loads skeleton <paramref name="entry"/>".
    /// <para/>
    /// This is what makes an imported pack's <c>j_ex_*</c> bones exist. They are not in the model — the game
    /// loads them from an extra skeleton, and only when this table says so. The pack declares the entry
    /// against the set IT replaces; Proteus has moved that geometry onto a host accessory, which has no EST
    /// of its own, so the entry is re-pointed at the body part the bones actually belong to.
    /// <para/>
    /// One entry per (race, slot, set) is all the table holds, so writing this REPLACES whatever the worn
    /// item had. A modded chest piece with ex bones of its own loses them. That is the mechanism, not this
    /// code: two garments cannot both own one EST slot, and knowing there was a clash would mean reading
    /// the live table. The caller logs what it claimed so the trade is at least visible.
    /// </summary>
    /// <param name="estSlot">"Body", "Head", "Hair" or "Face" — Penumbra's own EST slot names.</param>
    internal static object EstManipulation(string charCode, string estSlot, int setId, int entry)
    {
        int n = RaceIndex(charCode) ?? 2;
        return new
        {
            Type = "Est",
            Manipulation = new
            {
                Gender = n % 2 == 1 ? "Male" : "Female",
                Race = RaceNames[Math.Clamp((n - 1) / 2, 0, RaceNames.Length - 1)],
                SetId = setId,
                Slot = estSlot,
                Entry = entry,
            },
        };
    }

    /// <summary>
    /// The set id whose EST entry governs <paramref name="estSlot"/> for this character — the number that
    /// says WHICH body part is being asked to load the skeleton. Null when it cannot be determined.
    /// <para/>
    /// Equipment slots come off the live equipment walk, then the bare-body walk. That order matters and the
    /// fallback is not cosmetic: <c>EquippedPartModelsFromModels</c> filters e0000 out, so a character with a
    /// bare chest has no "top" entry at all, and the answer for them is set 0 — the bare body's own — rather
    /// than nothing.
    /// <para/>
    /// Hair and face are read from the drawn human-part paths, where the id is the folder
    /// (<c>chara/human/c0201/obj/hair/h0101/…</c> → 101), because those are not equipment and appear in
    /// neither equipment map.
    /// </summary>
    internal static int? EstSetId(
        string estSlot,
        IReadOnlyDictionary<string, string>? equipped,
        IReadOnlyDictionary<string, string>? bare,
        IReadOnlyList<string>? humanParts)
    {
        if (EstPartKey(estSlot) is not { } key) return null;

        if (key is "hair" or "face")
        {
            var folder = $"/obj/{key}/";
            foreach (var p in humanParts ?? [])
            {
                int at = p.IndexOf(folder, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                var rest = p[(at + folder.Length)..];
                int end = rest.IndexOf('/');
                if (end <= 1) continue;
                if (int.TryParse(rest[1..end], out var id)) return id;   // skip the h/f kind letter
            }
            return null;
        }

        foreach (var map in new[] { equipped, bare })
            if (map != null && map.TryGetValue(key, out var path)
             && ContentSlot.Parse(path) is { } parsed)
                return ContentSlot.SetIdOf(parsed.SetTag);
        return null;
    }

    /// <summary>
    /// Which of the character's own models an EST slot is about — Penumbra's slot names against the model
    /// keys the compositor's equipment walk uses.
    /// <para/>
    /// Null for a name this does not know rather than a guess: the entry is written onto someone else's
    /// item, and picking the wrong one would move a skeleton the user never asked about.
    /// </summary>
    internal static string? EstPartKey(string estSlot) => estSlot.ToLowerInvariant() switch
    {
        "body" => "top",
        "head" => "met",
        "hair" => "hair",
        "face" => "face",
        _      => null,
    };
}

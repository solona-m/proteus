using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Proteus.Services;
using static Proteus.Services.BodyBridge;

public static partial class SecondSkinWriter
{
    private sealed partial class ShellBuild
    {
        private readonly IReadOnlyList<SourceSpec> sources;
        private readonly IReadOnlyList<SecondSkinLayer> layers;
        private readonly byte[]? baseModel;
        private readonly Action<string>? diag;
        private readonly IReadOnlyList<AuthoredCapSet>? authoredCaps;
        private readonly PushSweep? pushSweep;
        private readonly BuildTimings? timings;

        /// <summary>
        /// How much of the layer's push a vertex at <paramref name="y"/> gets: the sweep's ladder while it is on,
        /// else the shipped foot band. The sweep REPLACES the band rather than compounding with it — it is a
        /// measuring instrument, and the millimetres it announces have to be the millimetres it applied.
        /// </summary>
        /// <remarks>For passes that need the figure OUTSIDE the per-vertex loop, which takes the sweep's tally
        /// through <see cref="PushSweep.Take"/> instead. This one does not count toward that tally.</remarks>
        internal float PushBandAt(float y) => pushSweep?.MultiplierAt(y) ?? FootPushAt(y);
        private long tPrepare;
        private List<Source> parsed = null!;
        private List<byte[]> sourceModels = null!;
        private int redundantSubs;
        private int redundantTris;
        private ConnectorProfile?[]? measuredParts;
        private Source? baseSrc;
        private Source? capSrc;
        private byte[]? capBytes;
        private Dictionary<int, CapPlacement>? capPlaced;
        private string? capDeclined;
        private string? capUsed;
        private float capStandoff;
        private bool anyLayerWantsCap;
        private const float smoothStrength = 0f;
        private float bridgeStrength;
        private float cleftStrength;
        private SecondSkinLayer? bridgeDef;
        private SecondSkinLayer? cleftDef;
        private float foldStrength;
        private SecondSkinLayer? foldDef;
        private Dictionary<(Source, int, bool, bool, bool), BustBridgePlan?> bridgePlans = null!;
        private Dictionary<(Source, int), float[]?> bridgeWeights = null!;
        private Dictionary<(Source, int), float[]?> cleftWeightCache = null!;
        private List<Source> geomSrcs = null!;
        private Dictionary<byte[], Source> geomByModel = null!;
        private int baseMatCount;
        private List<string> boneNames = null!;
        private Dictionary<string, ushort> boneIndex = null!;
        private List<byte[]> boneBBox = null!;
        private List<Source> boneSources = null!;
        private List<string> attrNames = null!;
        private Dictionary<string, int> attrIndex = null!;
        private int attrOverflow;
        private MemoryStream vBuf = null!;
        private MemoryStream iBuf = null!;
        private List<byte[]> meshOut = null!;
        private List<byte[]> declOut = null!;
        private List<byte[]> subOut = null!;
        private List<ushort[]> boneTables = null!;
        private List<ushort> submeshBoneMap = null!;
        private uint idxCursor;
        private ushort subCursor;
        private int triIn;
        private int triOut;
        private int vertOut;
        private int shapedTotal;
        private int uvMoved;
        private int uvUnmapped;
        private int uvRetangented;
        private int hiddenSubs;
        private int trimmedOut;
        private RimSeg[]? weldRim;
        private HashSet<int> weldRimVerts = null!;
        private Dictionary<(int Mesh, int Src), Vec3> weldRimPos = null!;
        private List<Vec3> capRimLandings = null!;
        private int welded;
        private int weldWorst;
        private float weldWorstD;
        private List<RimSeg> shellRim = null!;
        private int capWelded;
        private List<SkinTri>? bodySkin;
        private List<SkinTri>? bodySolid;
        private Dictionary<int, CapUvPlan?> capUvCache = null!;
        private Dictionary<Source, Dictionary<int, NailBedPlan>>? nailBedCache;
        private List<Vec3> capAllVerts = null!;
        private float capPushNow;
        private SecondSkinLayer? capDefNow;
        private bool capGrafted;
        private long tLayers;
        private Dictionary<string, (byte[] Mask, int Size)> toeReinforceMaps = null!;
        private long tSerialize;
        private int meshCount;
        private int boneCount;
        private List<uint> boneStrOff = null!;
        private List<uint> attrStrOff = null!;
        private List<uint> matStrOff = null!;
        private byte[] strings = null!;
        private byte[] o = null!;

        /// <summary>Host meshes left out whole, by their index in the host file — see <c>Build</c>'s
        /// <c>dropHostMesh</c>.</summary>
        private readonly Func<int, bool>? dropHostMesh;

        public ShellBuild(IReadOnlyList<SourceSpec> sources, IReadOnlyList<SecondSkinLayer> layers, byte[]? baseModel, Action<string>? diag, IReadOnlyList<AuthoredCapSet>? authoredCaps, PushSweep? pushSweep, BuildTimings? timings, Func<int, bool>? dropHostMesh = null)
        {
            this.dropHostMesh = dropHostMesh;
            this.sources = sources;
            this.layers = layers;
            this.baseModel = baseModel;
            this.diag = diag;
            this.authoredCaps = authoredCaps;
            this.pushSweep = pushSweep;
            this.timings = timings;
        }

        public byte[] Run(out Stats stats)
        {
            stats = default;
            PrepareSources();
            PlanRedundancy();
            MarkCleftRings();
            ChooseToeCap();
            PlanBridges();
            PlaceCap();
            ParseContent();
            UnionBones();
            UnionAttributes();
            AllocateOutput();
            EmitHost();
            EmitLayers();
            WriteStringBlock();
            WriteHeaders();
            return Validate(ref stats);
        }

        private void PrepareSources()
        {
            tPrepare = PhaseCounter.Begin();
            if (layers.Count == 0) throw new ArgumentException("need at least one layer", nameof(layers));
            // Only a shell layer needs a source; a build made entirely of content layers legitimately has none.
            if (sources.Count == 0 && layers.Any(l => l.Geometry.Count == 0))
                throw new ArgumentException("need at least one source model", nameof(sources));

            parsed = sources.Select(s => Parse(s.Model)).ToList();

            // The raw model bytes, for the cap passes that read geometry straight out of a .mdl.
            sourceModels = sources.Select(s => s.Model).ToList();

            // Attach each source's enabled shape keys and check the .mdl actually contains them.
            for (int i = 0; i < parsed.Count; i++)
            {
                var en = sources[i].EnabledShapes;
                parsed[i].EnabledShapes = en;
                parsed[i].UvConv = sources[i].UvConv;
                parsed[i].UnmirrorSides = sources[i].UnmirrorSides;
                parsed[i].Keep = sources[i].KeepMaterial ?? IsBodySkinMaterial;
                parsed[i].CoverNails = sources[i].CoverNails;
                parsed[i].HiddenAttrs = sources[i].HiddenAttributes is { Count: > 0 } ha ? ha : null;
                if (parsed[i].HiddenAttrs != null)
                    diag?.Invoke($"source {i}: the game is not drawing [{string.Join(", ", parsed[i].HiddenAttrs!)}] "
                               + "— those submeshes are left out");
                // Warn only when an enabled shape is missing from the .mdl.
                if (en == null || en.Count == 0 || diag == null) continue;
                foreach (var name in en)
                    if (!parsed[i].Shapes.ContainsKey(name))
                        diag($"shape '{name}' enabled but not present in source {i} — not baked");
            }
        }

        private void PlanRedundancy()
        {
            // ── which geometry is already drawn by something else ──────────────────────────────────────
            // Planned once per source, not per layer. The bands come from the sources themselves, so an EMPTY band
            // list means this part is alone and nothing covers anything. A supplied profile is checked against the
            // filter this build emits with: one measured with a wider filter overstates this part's box.
            ConnectorProfile? UsableProfile(ConnectorProfile? supplied, Source src, int index)
            {
                if (supplied == null) return null;
                var emitted = new HashSet<int>();
                int end = src.Lod0MeshIndex + src.Lod0MeshCount;
                for (int m = src.Lod0MeshIndex; m < end && m < src.MeshCount; m++)
                {
                    int mo = src.MeshStart + m * 36;
                    if (mo + 36 > src.S.Length) break;
                    if (BitConverter.ToUInt16(src.S, mo) == 0) continue;
                    ushort mat = BitConverter.ToUInt16(src.S, mo + 8);
                    if (mat < src.MatNames.Count && src.Keep(src.MatNames[mat])) emitted.Add(m);
                }
                // A subset is fine: only a mesh the profile measured that this build will not emit means the filters disagree.
                foreach (var mp in supplied.Meshes)
                    if (!emitted.Contains(mp.Index))
                    {
                        diag?.Invoke($"redundant pass: source {index} was handed a measurement covering mesh "
                                   + $"{mp.Index}, which this build's material filter excludes — re-measuring "
                                   + "rather than trusting it");
                        return null;
                    }
                return supplied;
            }

            redundantSubs = 0;
            redundantTris = 0;
            measuredParts = null;
            if (sources.Any(s => s.DropConnectors))
            {
                // Every source is measured, not only the candidates: a part that is not a candidate is still cover.
                var all = new ConnectorProfile?[sources.Count];
                for (int i = 0; i < sources.Count; i++)
                {
                    all[i] = UsableProfile(sources[i].Profile, parsed[i], i)
                          ?? ReadConnectorProfile(sources[i].Model, parsed[i].Keep);
                    // A submesh the game is not drawing is neither candidate nor cover. Filtered out of the measurement
                    // here because the measurement is cached per model and which variant is drawn is not a model property.
                    if (all[i] is { } measured && parsed[i].HiddenAttrs is { } hidden)
                    {
                        var src = parsed[i];
                        all[i] = measured.Without(sub => IsHidden(src, sub.AttrMask, hidden));
                    }
                }

                for (int i = 0; i < sources.Count; i++)
                {
                    if (!sources[i].DropConnectors || all[i] is not { } profile) continue;
                    var others = new List<ConnectorProfile.Box>(sources.Count);
                    for (int k = 0; k < all.Length; k++)
                        if (k != i && all[k]?.PartBox is { } b) others.Add(b);
                    // No hidden-attribute filter here: hidden submeshes were already taken out of the measurement above.
                    parsed[i].DropSubmeshes = PlanConnectorDrops(
                        profile, others, isHidden: null, diag, $"source {i}", out int subs, out int tris,
                        variantBits: VariantBits(parsed[i]), keepAddOns: parsed[i].CoverNails);
                    redundantSubs += subs;
                    redundantTris += tris;
                }

                // ── the overlap between parts ──────────────────────────────────────────────────────────
                // Adjacent parts overlap by a margin of each other's surface; drawn as a semi-transparent shell that
                // margin is the alpha twice. The unit has to be the triangle, and a submesh the pass above dropped must
                // not count as cover, a ring, or a piece for the cut to judge.
                var joinInput = new ConnectorProfile?[sources.Count];
                for (int i = 0; i < sources.Count; i++)
                    joinInput[i] = all[i] is { } measured && parsed[i].DropSubmeshes is { Count: > 0 } dropped
                        ? measured.Without(sub => dropped.Contains((sub.Mesh, sub.Index)))
                        : all[i];

                // Cut at the JOIN: the parts are stitched on a ring of shared vertices, and that ring is the boundary.
                // See PlanJoinCut.
                var flaps = PlanJoinCut(joinInput, CoincidenceEps, diag, out _);
                for (int i = 0; i < sources.Count; i++) parsed[i].JoinFlaps = flaps[i];
                SpareNailBeds();
                measuredParts = joinInput;
            }
        }

        private void MarkCleftRings()
        {
            // The rings between parts, for the cleft span: it moves the legs part's waist while the torso part,
            // solved on its own, stays put.
            if (sources.Count > 1 && layers.Any(l => l.CleftBridgeStrength > 0f))
            {
                measuredParts ??= sources.Select((s, i) => (ConnectorProfile?)ReadConnectorProfile(s.Model, parsed[i].Keep))
                                         .ToArray();
                MarkJoinRings(measuredParts, parsed);
            }

            baseSrc = baseModel != null ? Parse(baseModel) : null;
        }

        private void ChooseToeCap()
        {
            // The hand-modelled toe box, bundled with the plugin, merged like any other source: its bone table joins
            // the union by name and its vertices keep their blend indices. ONE CAP PER BODY; the binding covers the
            // footwear axis (heels are another foot model for the same body). Nothing can ask which body is equipped,
            // so the caps identify themselves: whichever binding places the most vertices belongs to this foot.
            capSrc = null;
            capBytes = null;
            capPlaced = null;
            capDeclined = null;
            capUsed = null;
            // How far the chosen cap already stands off this body; the push below is driven from it.
            capStandoff = 0f;
            // Same predicate as the per-layer `wantCap` test below, hoisted: selection and placement are only worth
            // paying for when a layer will graft the cap. Keep the two in step.
            anyLayerWantsCap = layers.Any(l => l.ToeCap != null && l.ToeCapStrength > 0f);
        }

        private void PlanBridges()
        {
            // ── the bust bridge is solved ONCE for the host, not once per layer ──────────────────────────
            // Stack order is the reason: a per-layer solve gates each layer's region on its own coverage, so two
            // spanning layers reach different heights and the lower one comes through the upper. One displacement
            // for every spanning layer, gated on the UNION of their coverages, keeps them LayerSeparation apart.
            // Only the span is a shell pass; the nipple relax belongs to the body (SmoothBodyNipples), and
            // NippleSmoothStrength survives on the layer as the declaration the body pass reads.
            // ONE DEFINITION PER FEATURE: each pass's coverage gate is the union of the layers that asked for THAT pass.
            
            var bustLayers  = layers.Where(l => l.BustBridgeStrength > 0f).ToList();
            var cleftLayers = layers.Where(l => l.CleftBridgeStrength > 0f).ToList();
            bridgeStrength = bustLayers.Count  == 0 ? 0f : bustLayers.Max(l => l.BustBridgeStrength);
            cleftStrength = cleftLayers.Count == 0 ? 0f : cleftLayers.Max(l => l.CleftBridgeStrength);

            SecondSkinLayer? MakeDef(List<SecondSkinLayer> ls, float bust, float cleft, string what)
            {
                if (ls.Count == 0) return null;
                // A spanning layer with no coverage map paints everything, so the union is everything and the
                // gate falls away — which is what a null Coverage already means downstream.
                var sized = ls.Where(l => l.Coverage != null && l.CoverageWidth > 0 && l.CoverageHeight > 0)
                              .ToList();
                byte[]? union = null;
                int uw = 0, uh = 0;
                if (sized.Count == ls.Count && sized.Count > 0)
                {
                    uw = sized[0].CoverageWidth; uh = sized[0].CoverageHeight;
                    if (sized.All(l => l.CoverageWidth == uw && l.CoverageHeight == uh
                                    && l.Coverage!.Length >= uw * uh))
                    {
                        union = (byte[])sized[0].Coverage!.Clone();
                        for (int k = 1; k < sized.Count; k++)
                        {
                            var c = sized[k].Coverage!;
                            for (int p = 0; p < union.Length; p++) if (c[p] > union[p]) union[p] = c[p];
                        }
                    }
                }
                diag?.Invoke($"bust bridge: one {what} solve for {ls.Count} layer(s) — span {bust:0.##}, "
                           + $"cleft {cleft:0.##}, smooth {smoothStrength:0.##}, coverage "
                           + (union == null ? "union unavailable — spanning every seeded vertex"
                                            : $"{uw}x{uh} union"));
                return new SecondSkinLayer
                {
                    MaterialName = "/bridge.mtrl",       // never emitted; this carries coverage and strength only
                    Coverage = union,
                    CoverageWidth = union == null ? 0 : uw,
                    CoverageHeight = union == null ? 0 : uh,
                    BustBridgeStrength = bust,
                    NippleSmoothStrength = smoothStrength,
                    CleftBridgeStrength = cleft,
                };
            }

            bridgeDef = MakeDef(bustLayers, bridgeStrength, 0f, "chest");
            cleftDef = MakeDef(cleftLayers, 0f, cleftStrength, "cleft");
            // The fold's own span on the SHELL, flat across the crotch — see the call below.
            var foldLayers = layers.Where(l => l.FoldSmoothStrength > 0f).ToList();
            foldStrength = foldLayers.Count == 0 ? 0f : foldLayers.Max(l => l.FoldSmoothStrength);
            foldDef = MakeDef(foldLayers, 0f, 0f, "fold span");
            // Per source mesh AND per feature set: the settings are per mod, so two layers on one host may differ
            // and must not inherit each other's plan — a cached null would otherwise read as a hit for every later layer.
            bridgePlans = new Dictionary<(Source, int, bool, bool, bool), BustBridgePlan?>();
            bridgeWeights = new Dictionary<(Source, int), float[]?>();
            cleftWeightCache = new Dictionary<(Source, int), float[]?>();
        }

        private void PlaceCap()
        {
            var tCapSelect = PhaseCounter.Begin();
            if (authoredCaps is { Count: > 0 } && anyLayerWantsCap)
            {
                // Which bones the body has, before asking where anything lands: position alone cannot tell these bodies
                // apart, but a cap bound to bones this body lacks is the wrong body.
                var bodyBones = new HashSet<string>(StringComparer.Ordinal);
                foreach (var psrc in parsed)
                    foreach (var bn in psrc.BoneNames)
                        if (!string.IsNullOrEmpty(bn)) bodyBones.Add(bn);

                AuthoredCapSet? best = null;
                float bestRate = float.MaxValue, bestCover = -1f, bestSpread = float.MaxValue;
                float bestTrip = float.MaxValue;
                List<SkinTri>? fitSurface = null;
                foreach (var cand in authoredCaps)
                {
                    if (cand.Bind == null)
                    {
                        // No binding: usable only as a last resort, exactly as authored.
                        if (best == null) { best = cand; bestRate = float.MaxValue; bestCover = -1f; }
                        continue;
                    }

                    var want = ReadBindBones(cand.Bind);
                    float cover = want.Count == 0 ? 0f
                                : (float)want.Count(bodyBones.Contains) / want.Count;
                    var probe = TryPlaceCapFromBind(cand.Bind, sourceModels, null, cand.Cap, CapBindProbeStride);
                    if (probe is not { Count: > 0 }) continue;
                    int tot = probe.Sum(p => p.Considered), miss = probe.Sum(p => p.Missed);
                    float rate = tot > 0 ? (float)miss / tot : 1f;
                    float spread = CapStandoffSpread(probe, fitSurface ??= BindSurface(sourceModels),
                                                     out float standoff);
                    float trip = CapRoundTrip(probe, cand.Cap);
                    if (authoredCaps.Count > 1)
                        diag?.Invoke($"authored cap: '{cand.Name}' places all but {rate * 100:F0}% on this body, "
                                   + $"this body has {cover * 100:F0}% of the {want.Count} bone(s) it was bound "
                                   + $"to, sits a median {standoff:F5} off the skin within {spread:F5}, and "
                                   + $"reproduces itself here to {trip:F5}");

                    // Bone coverage decides; the placement rate only separates caps the body can equally carry; the round
                    // trip (a binding reconstructs its own cap exactly only on the body it was measured on) is an identity
                    // test that separates bodies sharing a skeleton; the standoff spread breaks the last tie.
                    bool better;
                    if (cover > bestCover + CapBoneCoverTie) better = true;
                    else if (cover < bestCover - CapBoneCoverTie) better = false;
                    else if (rate < bestRate - CapPlaceRateTie) better = true;
                    else if (rate > bestRate + CapPlaceRateTie) better = false;
                    else if (trip < bestTrip * CapRoundTripTie) better = true;
                    else if (trip > bestTrip / CapRoundTripTie) better = false;
                    else better = spread < bestSpread;
                    if (better)
                    {
                        bestCover = cover; bestRate = rate; bestSpread = spread; bestTrip = trip;
                        capStandoff = standoff; best = cand;
                    }
                }

                if (best is { } chosen && bestRate <= CapBindMaxUnplaced)
                {
                    capBytes = chosen.Cap;
                    try { capSrc = ParseCached(chosen.Cap); }
                    catch (Exception ex) { diag?.Invoke($"authored cap failed to parse, ignoring: {ex.Message}"); }
                    if (capSrc != null && chosen.Bind != null)
                    {
                        capUsed = $"{CapBodyName(chosen.Name)} ({(1f - bestRate) * 100:F0}% placed)";
                        diag?.Invoke($"authored cap: using '{chosen.Name}'");
                        var placed = TryPlaceCapFromBind(chosen.Bind, sourceModels, diag, chosen.Cap);
                        // Not lifted clear of the toenails: a signed distance against a thin two-sided nail shell reads "buried"
                        // for vertices merely beside it.
                        if (placed is { Count: > 0 }) capPlaced = placed.ToDictionary(p => p.Mesh);
                    }
                }
                else if (best != null)
                {
                    // A declined cap must also call off the CUT, or the toe box is a hole. capSrc stays non-null so
                    // BuildVerbatim does not fall back to generating a cap, which throws.
                    capBytes = best.Value.Cap;
                    try { capSrc = ParseCached(best.Value.Cap); } catch { /* declined anyway */ }
                    capDeclined = bestRate is > 0f and < float.MaxValue
                        ? $"{bestRate * 100:F0}% of the toe cap could not be placed on this body"
                        : "no toe cap has been measured against this body";
                    diag?.Invoke($"authored cap: DECLINED — {capDeclined}; the toes keep the plain shell");
                }
            }
            timings?.CapSelect.Stop(tCapSelect);
        }

        private void ParseContent()
        {
            // Imported content models are parsed once each, keyed by reference: the same byte[] is handed to every
            // layer cut from it.
            geomSrcs = new List<Source>();
            geomByModel = new Dictionary<byte[], Source>(ReferenceEqualityComparer.Instance);
            foreach (var g in layers.SelectMany(l => l.Geometry))
            {
                if (geomByModel.ContainsKey(g.Model)) continue;
                var gs = Parse(g.Model);
                // Deliberately NOT `gs.Keep = g.KeepMaterial`: two geometries may share one model, and the emit loop
                // filters with the geometry's own predicate.
                geomByModel[g.Model] = gs;
                geomSrcs.Add(gs);
            }

            baseMatCount = baseSrc?.MatNames.Count ?? 0;
            if (baseMatCount + layers.Count > MaxMaterials)
                throw new InvalidOperationException(
                    $"host has {baseMatCount} materials + {layers.Count} layers > {MaxMaterials} max");
        }

        private void UnionBones()
        {
            // Union bone list. u16 indices, so hundreds of bones are fine. The host (if any) goes FIRST so its
            // own meshes can remap their bone tables by name.
            boneNames = new List<string>();
            boneIndex = new Dictionary<string, ushort>(StringComparer.Ordinal);
            boneBBox = new List<byte[]>();
            // Content models and the authored cap contribute bones too. Materialised: walked three times below.
            boneSources = [.. baseSrc != null ? new[] { baseSrc }.Concat(parsed) : parsed, .. geomSrcs,
                 .. capSrc != null ? new[] { capSrc } : []];
            foreach (var src in boneSources)
                for (int i = 0; i < src.BoneNames.Length; i++)
                {
                    if (boneIndex.ContainsKey(src.BoneNames[i])) continue;
                    boneIndex[src.BoneNames[i]] = (ushort)boneNames.Count;
                    boneNames.Add(src.BoneNames[i]);
                    var bb = new byte[BBoxSize];
                    if ((i + 1) * BBoxSize <= src.BoneBBoxes.Length)
                        Array.Copy(src.BoneBBoxes, i * BBoxSize, bb, 0, BBoxSize);
                    boneBBox.Add(bb);
                }
        }

        private void UnionAttributes()
        {
            // Union ATTRIBUTE list, renumbered like the bones: a submesh's mask indexes its own model's table. These
            // are what a mod's checkboxes toggle by name. 32 is the ceiling (the mask is a u32); extras past it are
            // left unnamed rather than aliased. A source emitted untagged (ContentGeometry.OwnAttributes) contributes
            // no names, so its unused attributes cannot cost another pack a slot.
            var ownedOnly = new HashSet<Source>();
            foreach (var (model, src) in geomByModel)
                if (layers.SelectMany(l => l.Geometry).Where(g => ReferenceEquals(g.Model, model))
                          .All(g => g.OwnAttributes))
                    ownedOnly.Add(src);

            // A source whose every geometry drops its variant tags contributes only the rest.
            var variantDropped = new HashSet<Source>();
            foreach (var (model, src) in geomByModel)
                if (layers.SelectMany(l => l.Geometry).Where(g => ReferenceEquals(g.Model, model))
                          .All(g => g.DropVariantAttributes))
                    variantDropped.Add(src);

            attrNames = new List<string>();
            attrIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var src in boneSources)
                foreach (var name in ownedOnly.Contains(src) ? []
                                   : variantDropped.Contains(src) ? src.AttrNames.Where(n => !IsVariantAttribute(n))
                                   : src.AttrNames)
                {
                    if (attrIndex.ContainsKey(name) || attrNames.Count >= 32) continue;
                    attrIndex[name] = attrNames.Count;
                    attrNames.Add(name);
                }
            // Counted over the same sources the union was built from, or untagged sources report names as dropped.
            attrOverflow = boneSources.Where(s => !ownedOnly.Contains(s))
                .SelectMany(s => s.AttrNames).Distinct(StringComparer.Ordinal)
                .Count() - attrNames.Count;
        }

        private void AllocateOutput()
        {
            vBuf = new MemoryStream();
            iBuf = new MemoryStream();
            meshOut = new List<byte[]>();
            declOut = new List<byte[]>();        // per-mesh vertex declaration (source format, preserved)
            subOut = new List<byte[]>();
            boneTables = new List<ushort[]>();   // one per emitted mesh
            submeshBoneMap = new List<ushort>();
            idxCursor = 0;
            subCursor = 0;
            triIn = 0;
            triOut = 0;
            vertOut = 0;
            shapedTotal = 0;   // index entries rewired to a morphed vertex by an enabled body shape key
            uvMoved = 0;
            uvUnmapped = 0;   // vertices put through a UV-space conversion, and those it couldn't place
            uvRetangented = 0;             // meshes whose tangent frame was re-fitted to the converted UVs
            hiddenSubs = 0;                // submeshes dropped by a pack's own hide toggles
            trimmedOut = 0;                // triangles dropped as a second layer over an overlapping part

            // The rim of the cap the current layer grafts, pushed to that layer's offset; null on a layer with no cap.
            weldRim = null;
            // Which pre-split cap vertices that rim runs through, so the graft knows which to snap back.
            weldRimVerts = new HashSet<int>();
            // ONE SOURCE OF TRUTH for where the cap's rim ends up, keyed by cap mesh and PRE-SPLIT source index: the
            // weld and both splits match positions exactly, so the graft reads this back rather than recomputing it.
            weldRimPos = new Dictionary<(int Mesh, int Src), Vec3>();
            // Every point on the cap's rim that a shell vertex was welded onto, this layer. The cap is split
            // at these when it is emitted, so both boundaries end up with the same vertex positions.
            capRimLandings = new List<Vec3>();
            welded = 0;
            weldWorst = 0;
            weldWorstD = 0f;

            // The shell's own rim once welded, carrying its normals from BEFORE averaging, for the cap to snap onto.
            shellRim = new List<RimSeg>();
            capWelded = 0;

            // The body's skin, for re-deriving the skinning of a vertex the weld moved. Built on first use.
            bodySkin = null;

            // EVERYTHING SOLID, for the clearance pass alone: toenails may live on their own mesh under their own
            // material and must still be cleared. Only distances are read from this, never a coordinate.
            bodySolid = null;

            // The cap's projection depends only on the cap and the bodies, never on the layer, and is the most
            // expensive step.
            capUvCache = new Dictionary<int, CapUvPlan?>();

            // ── THE CAP IS GRAFTED INTO THE SHELL'S OWN MESH ──────────────────────────────────────────────
            // A cap emitted as its own mesh can only hold a COPY of each rim vertex; grafted into the shell's mesh,
            // its triangles reference the shell's own rim vertices and there is no join left to leak.
            // These carry the current layer's cap settings into EmitMesh, which is declared above the layer loop.
            // capAllVerts is every cap vertex as placed this layer: the toenail drop needs it for EVERY capped layer.
            capAllVerts = new List<Vec3>();
            capPushNow = 0f;
            capDefNow = null;
            capGrafted = false;

            timings?.Prepare.Stop(tPrepare);
        }

        private void EmitHost()
        {
            tLayers = PhaseCounter.Begin();

            // Host pre-pass: the ring/bracelet's own LOD0 meshes, verbatim and unfiltered, at their authored
            // material indices (0..baseMatCount-1) — so the accessory still renders under the appended shell.
            if (baseSrc != null)
            {
                int mapBase = submeshBoneMap.Count;
                bool mapAppended = false;
                int bEnd = baseSrc.Lod0MeshIndex + baseSrc.Lod0MeshCount;
                for (int m = baseSrc.Lod0MeshIndex; m < bEnd && m < baseSrc.MeshCount; m++)
                {
                    int bmo = baseSrc.MeshStart + m * 36;
                    ushort srcMat = BitConverter.ToUInt16(baseSrc.S, bmo + 8);
                    // A whole mesh left out when asked: a garment whose skin is being replaced loses that skin.
                    if (dropHostMesh != null && dropHostMesh(m)) continue;
                    EmitMesh(baseSrc, m, srcMat, 0f, preserve: true, cov: null, mapBase, ref mapAppended);
                }
            }
        }

        private void EmitLayers()
        {
            // Each layer's reinforced-toe region, for the caller (Stats.ToeReinforceMaps). The body's toe-line
            // triangles are read once, on demand.
            toeReinforceMaps = new Dictionary<string, (byte[] Mask, int Size)>(StringComparer.Ordinal);
            List<ToeLineTri>? toeLineBody = null;

            for (ushort layer = 0; layer < layers.Count; layer++)
            {
                EmitLayer(ref toeLineBody, layer);
            }

            timings?.Layers.Stop(tLayers);
        }

        /// <summary>Emits every source mesh into one layer, grafting and welding that layer's toe cap.</summary>
        private void EmitLayer(ref List<ToeLineTri>? toeLineBody, ushort layer)
        {
            new LayerEmitter(this, layer).Run(ref toeLineBody);
        }

        private void WriteStringBlock()
        {
            tSerialize = PhaseCounter.Begin();

            // WHICH filter emptied it decides how the caller reports this: coverage trimming is a fault, a pack's
            // own hide toggles are the user's choice.
            if (meshOut.Count == 0)
                throw new EmptyShellException(hiddenSubs > 0
                    ? $"every mesh was hidden by the pack's own toggles ({hiddenSubs} submesh(es))"
                    : "no geometry survived coverage trimming",
                    byToggle: hiddenSubs > 0);

            meshCount = meshOut.Count;
            boneCount = boneNames.Count;

            // ── string block: bone names (union), attribute names (union), material names ──
            var strMs = new MemoryStream();
            boneStrOff = new List<uint>();
            foreach (var b in boneNames)
            {
                boneStrOff.Add((uint)strMs.Position);
                strMs.Write(Encoding.ASCII.GetBytes(b));
                strMs.WriteByte(0);
            }
            attrStrOff = new List<uint>();
            foreach (var a in attrNames)
            {
                attrStrOff.Add((uint)strMs.Position);
                strMs.Write(Encoding.ASCII.GetBytes(a));
                strMs.WriteByte(0);
            }

            matStrOff = new List<uint>();
            // Host materials FIRST (indices 0..baseMatCount-1, referenced verbatim by the host's own meshes),
            // then the appended shell layer materials.
            if (baseSrc != null)
                foreach (var name in baseSrc.MatNames)
                {
                    matStrOff.Add((uint)strMs.Position);
                    strMs.Write(Encoding.ASCII.GetBytes(name));
                    strMs.WriteByte(0);
                }
            foreach (var l in layers)
            {
                matStrOff.Add((uint)strMs.Position);
                strMs.Write(Encoding.ASCII.GetBytes(l.MaterialName));
                strMs.WriteByte(0);
            }
            while (strMs.Position % 4 != 0) strMs.WriteByte(0);
            strings = strMs.ToArray();
        }

        private void WriteHeaders()
        {
            // Flags, the 0x44 header and the LOD block come from the source the emitted geometry was cut from
            // (source 0, or the first content model).
            var head = parsed.Count > 0 ? parsed[0] : geomSrcs[0];

            // The CULLING quantities are about extent, so the merged model takes the max across every source:
            // too small and the game culls the shell while the body is still on screen.
            float radius = head.Radius, modelClip = head.ModelClip, shadowClip = head.ShadowClip;
            foreach (var src in (baseSrc != null ? new[] { baseSrc }.Concat(parsed) : parsed).Concat(geomSrcs))
            {
                if (src.Radius     > radius)     radius     = src.Radius;
                if (src.ModelClip  > modelClip)  modelClip  = src.ModelClip;
                if (src.ShadowClip > shadowClip) shadowClip = src.ShadowClip;
            }

            uint stackSize = (uint)(meshCount * DeclSize);

            var ms = new MemoryStream();
            // ModelFileHeader copied from the head source, EXCEPT the version: forced to v6, the only bone-table
            // format this writer emits (see WriteBoneTablesV6). A v5 header over v6 tables skins every vertex to garbage.
            var fileHeader = new byte[0x44];
            Array.Copy(head.S, fileHeader, 0x44);
            BitConverter.TryWriteBytes(fileHeader.AsSpan(0), MdlVersionV6);
            ms.Write(fileHeader);
            for (int i = 0; i < meshCount; i++) ms.Write(declOut[i]);   // each mesh's own (source) declaration
            ms.Write(new byte[4]);                                      // string count (unused)
            Span<byte> tmp4 = stackalloc byte[4];
            BitConverter.TryWriteBytes(tmp4, (uint)strings.Length);
            ms.Write(tmp4);
            ms.Write(strings);

            long mhPos = ms.Position;
            var mh = new byte[56];
            BitConverter.GetBytes(radius).CopyTo(mh, 0);
            W16(mh, 4, (ushort)meshCount);
            W16(mh, 6, (ushort)attrNames.Count);                        // attribute names, carried
            W16(mh, 8, (ushort)subOut.Count);
            W16(mh, 10, (ushort)(baseMatCount + layers.Count));
            W16(mh, 12, (ushort)boneCount);
            W16(mh, 14, (ushort)boneTables.Count);
            W16(mh, 16, 0); W16(mh, 18, 0); W16(mh, 20, 0);             // shapes dropped
            mh[22] = 1;                                                 // lodCount
            mh[23] = head.Flags1;
            W16(mh, 24, 0);                                             // elementIdCount
            mh[26] = 0;                                                 // terrain shadow meshes
            mh[27] = (byte)(head.Flags2 & ~0x10);                       // no extra LODs
            BitConverter.GetBytes(modelClip).CopyTo(mh, 28);
            BitConverter.GetBytes(shadowClip).CopyTo(mh, 32);
            int boneTableShorts = boneTables.Sum(t => (t.Length + 1) & ~1);
            W16(mh, 44, (ushort)boneTableShorts);                       // BoneTableArrayCountTotal
            ms.Write(mh);

            long lodPos = ms.Position;
            ms.Write(head.Lods, 0, 3 * 60);                             // patched below

            foreach (var nm in meshOut) ms.Write(nm);
            // BETWEEN the meshes and the submeshes: the format puts the attribute name table there.
            foreach (var off in attrStrOff) { BitConverter.TryWriteBytes(tmp4, off); ms.Write(tmp4); }
            foreach (var ns in subOut) ms.Write(ns);
            foreach (var off in matStrOff) { BitConverter.TryWriteBytes(tmp4, off); ms.Write(tmp4); }
            foreach (var off in boneStrOff) { BitConverter.TryWriteBytes(tmp4, off); ms.Write(tmp4); }

            WriteBoneTablesV6(ms, boneTables);

            // submesh bone map
            BitConverter.TryWriteBytes(tmp4, (uint)(submeshBoneMap.Count * 2));
            ms.Write(tmp4);
            var mapBytes = new byte[submeshBoneMap.Count * 2];
            for (int i = 0; i < submeshBoneMap.Count; i++)
                BitConverter.TryWriteBytes(mapBytes.AsSpan(i * 2), submeshBoneMap[i]);
            ms.Write(mapBytes);

            ms.WriteByte(0);                                            // padding amount

            // Bounding boxes: 4 model-level boxes then one per union bone. The model box must cover EVERY
            // part, or the merged model gets culled whenever only one part is on screen.
            ms.Write(UnionModelBBoxes(baseSrc != null ? [baseSrc, .. parsed, .. geomSrcs] : [.. parsed, .. geomSrcs]));
            foreach (var bb in boneBBox) ms.Write(bb);

            long vtxOffOut = ms.Position;
            vBuf.Position = 0; vBuf.CopyTo(ms);
            long idxOffOut = ms.Position;
            iBuf.Position = 0; iBuf.CopyTo(ms);
            o = ms.ToArray();

            uint vtxSize = (uint)vBuf.Length, idxSize = (uint)iBuf.Length;
            W32(o, 4, stackSize);
            W32(o, 8, (uint)(vtxOffOut - 0x44 - stackSize));            // RuntimeSize
            W16(o, 12, (ushort)meshCount);                              // vertDeclCount == meshCount
            W16(o, 14, (ushort)(baseMatCount + layers.Count));
            W32(o, 16, (uint)vtxOffOut); W32(o, 20, 0); W32(o, 24, 0);
            W32(o, 28, (uint)idxOffOut); W32(o, 32, 0); W32(o, 36, 0);
            W32(o, 40, vtxSize); W32(o, 44, 0); W32(o, 48, 0);
            W32(o, 52, idxSize); W32(o, 56, 0); W32(o, 60, 0);
            o[64] = 1;                                                  // lodCount

            int ol = (int)lodPos;
            W16(o, ol + 0, 0);                                          // mesh index
            W16(o, ol + 2, (ushort)meshCount);
            W32(o, ol + 44, vtxSize);
            W32(o, ol + 48, idxSize);
            W32(o, ol + 52, (uint)vtxOffOut);
            W32(o, ol + 56, (uint)idxOffOut);
            for (int l = 1; l < 3; l++)                                 // LOD 1/2 carry no meshes
            {
                int p = ol + l * 60;
                W16(o, p + 0, (ushort)meshCount);
                W16(o, p + 2, 0);
            }
            _ = mhPos;
        }

        private byte[] Validate(ref Stats stats)
        {
            // Every submesh bone map entry must name a bone in the union list. The [boneStart, boneStart+boneCount)
            // window is deliberately NOT checked: real body models fail it as authored and render fine, so the game
            // does not read it the way the struct suggests.
            {
                int badEntry = submeshBoneMap.Count(v => v >= boneNames.Count);
                if (badEntry > 0)
                    diag?.Invoke($"BONE MAP: {badEntry} entry(ies) name a bone past the {boneNames.Count}-bone "
                               + "union list — the by-name remap failed to place them");
            }

            if (attrNames.Count > 0)
                diag?.Invoke($"attributes: {attrNames.Count} carried [{string.Join(", ", attrNames)}]");
            // The mask is a u32, so an attribute past the 32nd has no bit and whatever it switched is stuck on.
            if (attrOverflow > 0)
                diag?.Invoke($"ATTRIBUTES: {attrOverflow} past the 32 a submesh mask can address were dropped — "
                           + "whatever those switched can no longer be turned off");

            if (shapedTotal > 0) diag?.Invoke($"shape bake: {shapedTotal} index entries rewired to morphed vertices");
            // Per LAYER: every layer rebuilds the same sources, so divided back out.
            if (uvMoved > 0)
                diag?.Invoke($"uv conversion: {uvMoved / layers.Count} vertices moved into the shell's UV space"
                           + (uvUnmapped > 0 ? $", {uvUnmapped / layers.Count} left as authored (no correspondence)" : "")
                           + $", {uvRetangented / layers.Count} mesh(es) re-tangented");

            // The trim is counted once per SHELL layer, so divided back out like the figures above.
            int shellLayers = layers.Count(l => l.Geometry.Count == 0);
            stats = new Stats(meshCount, subOut.Count, boneCount, triIn, triOut, vertOut, capDeclined, capUsed,
                              redundantSubs, redundantTris, shellLayers > 0 ? trimmedOut / shellLayers : 0,
                              toeReinforceMaps.Count > 0 ? toeReinforceMaps : null);
            timings?.Serialize.Stop(tSerialize);
            return o;
        }

        // One submesh's attribute mask, renumbered from its own model's table onto the union.
        private uint RemapAttrs(Source src, uint mask)
        {
            if (mask == 0 || src.AttrNames.Length == 0) return 0;
            uint outMask = 0;
            for (int bit = 0; bit < 32 && bit < src.AttrNames.Length; bit++)
                if ((mask & (1u << bit)) != 0 && attrIndex.TryGetValue(src.AttrNames[bit], out var to))
                    outMask |= 1u << to;
            return outMask;
        }

        // Emit one source mesh into the merged model. preserve=true: exact byte copy, every triangle, authored
        // material index (host and cap). preserve=false: BuildVerbatim's push/colour/uv1 rewrites, coverage-trimmed.
        // `cov` null keeps all triangles; `mapBase`/`mapAppended` share the source's submesh bone map across its meshes.
        private void EmitMesh(Source src, int m, ushort materialIndex, float push, bool preserve,
                      SecondSkinLayer? cov, int mapBase, ref bool mapAppended,
                      bool mirrorUv1 = false, IReadOnlySet<string>? hiddenAttrs = null,
                      bool clearAttrs = false, CapUvPlan? capUv = null, bool dropVariantAttrs = false)
        {
            new MeshEmitter(this, src, m, materialIndex, push, preserve, cov, mapBase, mirrorUv1, hiddenAttrs, clearAttrs, capUv,
                            dropVariantAttrs).Run(ref mapAppended);
        }

        /// <summary>
        /// Take the hands' nail beds back out of the join cut: each is a small island bounded by the finger it lies
        /// on, which the cut reads as a flap past an inner join that the finger already draws.
        /// </summary>
        private void SpareNailBeds()
        {
            foreach (var src in parsed)
            {
                if (!src.CoverNails || src.JoinFlaps is not { Count: > 0 } flaps) continue;
                int spared = 0;
                foreach (var (m, flap) in flaps)
                {
                    if (NailBedsOf(src, m) is not { } beds) continue;
                    foreach (var verts in beds.VertsOf)
                        foreach (int v in verts)
                            if (flap.Remove((ushort)v)) spared++;
                }
                if (spared > 0)
                    diag?.Invoke($"nail beds: {spared} vertex/vertices spared from the join cut");
            }
        }

        /// <summary>A hand mesh's nail beds and the fingertip UV under each, found once per build — see NailBeds.
        /// Null for every other mesh.</summary>
        private NailBedPlan? NailBedsOf(Source src, int m)
        {
            if (!src.CoverNails) return null;
            nailBedCache ??= new Dictionary<Source, Dictionary<int, NailBedPlan>>();
            if (!nailBedCache.TryGetValue(src, out var plans))
                nailBedCache[src] = plans = NailBeds(src, diag);
            return plans.GetValueOrDefault(m);
        }
    }
}

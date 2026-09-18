using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

public static partial class HatCompatSolve
{
    private sealed class HatFit
    {
        private readonly byte[] mdl;
        private readonly byte[]? head;
        private readonly string? raceCode;
        private List<MeshVerts> meshes = null!;
        private Dictionary<int, IReadOnlyDictionary<int, Vector3>> moved = null!;
        private Vector3 centre;
        private float radius;
        private float[] bandFloor = null!;
        private float hatLine;
        private SecondSkinWriter.Source parsed = null!;
        private Dictionary<int, int[]> valence = null!;
        private List<ModelPart> drop = null!;
        private HashSet<long> gone = null!;
        private List<Candidate> candidates = null!;
        private int budget;
        private int spent;
        private List<float> presses = null!;
        private int dropped;
        private int droppedForValues;
        private int droppedForSpares;
        private int wanted;
        private Result result = null!;

        public HatFit(byte[] mdl, byte[]? head, string? raceCode)
        {
            this.mdl = mdl;
            this.head = head;
            this.raceCode = raceCode;
        }

        public Result Run()
        {
            if (!MeasureHead()) return result;
            CutAboveHatLine();
            CollectCandidates();
            FitBudget();
            PressCandidates();
            return CutUnaffordable();
        }

        private bool MeasureHead()
        {
            meshes = ReadLod0Meshes(mdl);
            moved = new Dictionary<int, IReadOnlyDictionary<int, Vector3>>();
            if (meshes.Count == 0)
                { result = new Result(moved, Vector3.Zero, 0, 0, 0, 0); return false; }

            var frame = head != null ? HeadFrameFrom(head) : null;
            (centre, radius) = frame ?? HeadFrame(meshes);
            // Read once; HeadFloor uses it twice below.
            var headMeshes = head != null ? ReadLod0Meshes(head) : [];
            var floor = frame != null
                ? HeadFloor(headMeshes, centre, radius, centre.Y + HatLine)
                : ScalpFloor(meshes, centre, radius);

            // A SECOND floor, measured down to the bottom of the hat's band: the cranium-only floor has no samples
            // in directions pointing into the band, and the median it falls back on understates the occiput.
            bandFloor = frame != null
                ? HeadFloor(headMeshes, centre, radius, centre.Y + HatLine - HatBandDrop)
                : floor;

            hatLine = centre.Y + HatLine;

            parsed = SecondSkinWriter.Parse(mdl);
            valence = Valence(mdl, parsed, meshes);
            return true;
        }

        private void CutAboveHatLine()
        {
            // Everything a hat certainly hides is cut away rather than pressed; nothing else is removed.
            (drop, gone) = CutAtHatLine(mdl, parsed, meshes, hatLine, centre, radius, raceCode);
        }

        private void CollectCandidates()
        {
            // THE RING: hair in a thin band above the rim is kept and pressed flat onto the scalp, so a hat whose
            // rim rides higher than the reference shows flattened hair rather than bare scalp.
            candidates = new List<Candidate>();
            foreach (var mv in meshes)
            {
                valence.TryGetValue(mv.Mesh, out var cost);
                for (int v = 0; v < mv.Positions.Length; v++)
                {
                    var p = mv.Positions[v];
                    if (gone.Contains(VertexKey(mv.Mesh, v))) continue;      // cut away; a shape value would move nothing

                    // The ring, plus the fade below the rim that stops it kinking against untouched hair.
                    float above = HatProfile.AboveRim(raceCode, p, centre);
                    if (above > RingHeight || above < -RingFade) continue;

                    var d = p - centre;
                    float len = d.Length();
                    if (len < 1e-5f) continue;

                    // Hair already inside the scalp needs nothing, and must not spend shape values. bandFloor,
                    // not floor: floor has no samples in directions pointing at the rim.
                    float scalp = bandFloor[BinOf(d / len)];
                    if (len <= scalp) continue;

                    // ONTO the scalp, not into it: the ring is sometimes in plain view.
                    float target = scalp;
                    if (len <= target) continue;

                    int slots = cost != null && v < cost.Length ? cost[v] : 0;
                    if (slots == 0) continue;                           // drawn by nothing; a spare would do nothing

                    // Full strength at the rim and above, tapering to nothing at the bottom of the fade.
                    float w = above >= 0f ? 1f : (above + RingFade) / RingFade;
                    if (w <= 0f) continue;

                    candidates.Add(new Candidate(
                        mv.Mesh, v, p, Vector3.Lerp(p, centre + d * (target / len), w),
                        len - target, (len - target) * w, slots));
                }
            }
        }

        private void FitBudget()
        {
            // NO EDGE RAMP on the ring: it is only one or two edge rings tall, so a ramp would stop it lying flat.

            // The header's shape-value count is for the whole file, so the model's own shapes come off the top.
            int already = BitConverter.ToUInt16(mdl, parsed.Mh + 20);
            budget = Math.Max(0, MaxShapeValues - already);
            spent = 0;
            if (candidates.Sum(c => c.Cost) > budget)
                // Over budget: ABOVE THE HAT LINE FIRST (hair that would pierce the hat), then furthest out
                // first; below the line the press is cosmetic.
                candidates.Sort((a, b) =>
                {
                    bool aboveA = a.From.Y >= hatLine, aboveB = b.From.Y >= hatLine;
                    if (aboveA != aboveB) return aboveA ? -1 : 1;
                    return b.Need.CompareTo(a.Need);
                });

            presses = new List<float>();
            dropped = 0;
            droppedForValues = 0;
            droppedForSpares = 0;
            wanted = candidates.Sum(c => c.Cost);
        }

        private void PressCandidates()
        {
            // How many vertices each mesh already has, and how many spares the shape has promised it so far.
            var meshVertexCount = meshes.ToDictionary(m => m.Mesh, m => m.Positions.Length);
            var spares = new Dictionary<int, int>();
            foreach (var c in candidates)
            {
                if (spent + c.Cost > budget) { dropped++; droppedForValues++; continue; }

                // AND the mesh's own vertex ceiling: each moved vertex is a SPARE appended to its mesh, and
                // VertexCount is a u16. AddShape refuses outright past it, so stop there for a partial fit.
                meshVertexCount.TryGetValue(c.Mesh, out int have);
                spares.TryGetValue(c.Mesh, out int used);
                if (have + used >= ushort.MaxValue) { dropped++; droppedForSpares++; continue; }
                spares[c.Mesh] = used + 1;

                spent += c.Cost;
                if (!moved.TryGetValue(c.Mesh, out var here))
                    moved[c.Mesh] = here = new Dictionary<int, Vector3>();
                ((Dictionary<int, Vector3>)here)[c.Vertex] = c.To;
                presses.Add(c.Press);
            }
        }

        private Result CutUnaffordable()
        {
            // WHAT THE PRESS COULD NOT AFFORD, ABOVE THE LINE, IS CUT INSTEAD. Only above the line (below it is
            // visible silhouette), and only triangles whose every corner was dropped or already cut.
            if (dropped > 0)
            {
                var unaffordable = new HashSet<long>();
                foreach (var c in candidates)
                {
                    if (c.From.Y < hatLine) continue;                       // the fade: the author's silhouette
                    bool pressed = moved.TryGetValue(c.Mesh, out var here) && here.ContainsKey(c.Vertex);
                    if (!pressed) unaffordable.Add(VertexKey(c.Mesh, c.Vertex));
                }
                if (unaffordable.Count > 0)
                    HatCompatSolve.CutUnaffordable(mdl, parsed, meshes, gone, unaffordable, drop);
            }

            presses.Sort();
            return new Result(
                moved,
                centre, radius,
                presses.Count > 0 ? presses[presses.Count / 2] : 0f,
                presses.Count > 0 ? presses[^1] : 0f,
                presses.Count,
                dropped)
            {
                Cut = drop,
                DroppedForValues = droppedForValues,
                DroppedForSpares = droppedForSpares,
                WantedValues = wanted,
                Budget = budget,
            };
        }
    }
}

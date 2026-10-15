using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Den.Tools;
using Den.Tools.Matrices;
using Den.Tools.Splines;
using MapMagic.Core;
using MapMagic.Nodes;
using MapMagic.Nodes.MatrixGenerators;
using MapMagic.Nodes.ObjectsGenerators;
using MapMagic.Nodes.SplinesGenerators;
using MapMagic.Products;
using UnityEngine;

namespace SigmaMod
{
    // POIs with a footprint (an airfield's runway, a long compound): placed only where the ground already suits
    // them, and graded to fit. A place one of the game's objects outputs would fill, picked for such a POI by its
    // hash (Poi), is tried against the terrain as it stands before any POI grading: the footprint (a length along
    // the POI's own forward, +Z, by a width) at its number of headings (default 24, 7.5 degrees apart), offset per place, sampled every Step metres on its
    // centre line, both edges and lines between them no more than 25 m apart. A straight grade is fitted along its length; the heading is kept if the grade is
    // within max_grade and no point is further than max_cut above or below it, the least cut and fill winning, and
    // only if the whole footprint and its shoulders lie inside the tile (no grading across a tile seam). No heading
    // that fits: the place keeps what the game puts there. A real airfield is sited the same way: on ground that is
    // flat already, then graded.
    //
    // The graph, built once when the world loads:
    //   the output's own places  -> PoiRest  -> the output (what stays the game's)
    //                            -> PoiTake  -> a new objects output per POI (the POI, turned to its heading)
    //                            -> PoiGrade -> per POI, on the height before the terrain's height output: the
    //                                           footprint graded to its fitted grade, eased into the ground over its
    //                                           shoulder
    // All three decide alike (one decision per tile and place list, PoiPlan.Decide). The places come from before the
    // game splits them between its outputs, which is also what its flattened pads and roads are made from, so roads
    // still lead to a POI's origin.
    internal sealed class PoiTemplate
    {
        public string Id;
        public GameObject Prefab;
        public float Length, Width, Shoulder, MaxGrade, MaxCut;
        public int Headings = 24;   // headings tried, over half a turn (a footprint fits both ways round)
        public bool AlongX;         // its length along the model's X, not Z (the model turns 90 degrees less)
        public float CenterX, CenterZ;   // the footprint's centre in the model's own space (its origin, where roads
                                         // arrive, need not be in the middle)

        // the model's turn for a footprint heading, degrees
        public float Yaw(float heading) => heading - (AlongX ? 90f : 0f);

        // where the footprint's centre is from the model's origin, in the world, at a heading
        public Vector3 Offset(float heading)
        {
            float y = Yaw(heading) * Mathf.Deg2Rad, c = Mathf.Cos(y), s = Mathf.Sin(y);
            return new Vector3(CenterX * c + CenterZ * s, 0f, -CenterX * s + CenterZ * c);
        }
    }

    internal sealed class PoiPlan
    {
        public const float Step = 10f;     // m between samples along a footprint
        public const float Across = 25f;   // m at most between sample lines across it

        public ulong Output;
        public double Share;
        public PoiTemplate[] Templates;

        // per place: which POI (-1: the game's), its heading (degrees) and the graded height at its origin and slope
        public sealed class Decision
        {
            public int[] Which;
            public float[] Heading, Height, Slope;
        }

        private readonly ConditionalWeakTable<TransitionsList, Decision> _decided = new ConditionalWeakTable<TransitionsList, Decision>();

        // what the full-resolution tiles decided for each place (by its hash): the low-resolution draft tiles shown at
        // a distance take the same, once known, so a site doesn't turn or vanish as its tile comes close
        private readonly Dictionary<int, (int which, float heading, float height, float slope)> _main = new Dictionary<int, (int, float, float, float)>();

        public Decision Decide(TileData data, TransitionsList places, MatrixWorld heights)
        {
            lock (_decided)
            {
                if (_decided.TryGetValue(places, out var known)) return known;
                var d = new Decision { Which = new int[places.count], Heading = new float[places.count], Height = new float[places.count], Slope = new float[places.count] };
                for (int i = 0; i < places.count; i++)
                {
                    d.Which[i] = -1;
                    var trs = places.arr[i];
                    if (data.isDraft && _main.TryGetValue(trs.hash, out var known2))
                    {
                        (d.Which[i], d.Heading[i], d.Height[i], d.Slope[i]) = known2;
                        continue;
                    }
                    if (heights == null || Poi.Hash01(trs.hash, Poi.SaltFor(Output)) >= Share) continue;
                    int k = Math.Min(Templates.Length - 1, (int)(Poi.Hash01(trs.hash, Poi.PickSaltFor(Output)) * Templates.Length));
                    if (Fit(data, trs, heights, Templates[k], out float heading, out float a, out float b))
                    {
                        d.Which[i] = k;
                        d.Heading[i] = heading;
                        d.Height[i] = a;
                        d.Slope[i] = b;
                    }
                }
                _decided.Add(places, d);
                if (!data.isDraft)
                    for (int i = 0; i < places.count; i++)
                        _main[places.arr[i].hash] = (d.Which[i], d.Heading[i], d.Height[i], d.Slope[i]);
                if (Plugin.VerboseLogEntry.Value)
                {
                    int tried = 0, taken = 0;
                    var where = new System.Text.StringBuilder();
                    for (int i = 0; i < places.count; i++)
                    {
                        if (heights != null && Poi.Hash01(places.arr[i].hash, Poi.SaltFor(Output)) < Share) tried++;
                        if (d.Which[i] < 0) continue;
                        taken++;
                        where.Append($"; '{Templates[d.Which[i]].Id}' at ({places.arr[i].pos.x:0}, {places.arr[i].pos.z:0}) heading {d.Heading[i]:0}, grade {d.Slope[i] * 100f:+0.00;-0.00}%");
                    }
                    if (tried > 0) Plugin.Verbose($"POIs [{Output}]: tile at ({data.area.active.worldPos.x:0}, {data.area.active.worldPos.z:0}): {tried} spot(s) tried, {taken} fit" + where);
                }
                return d;
            }
        }

        // the best heading for a footprint at a place: a straight grade along it (height a at the origin, rising b per
        // metre along forward) within the limits, with the least cut and fill; false if none
        private static bool Fit(TileData data, Transition trs, MatrixWorld heights, PoiTemplate t, out float heading, out float a, out float b)
        {
            heading = 0f; a = 0f; b = 0f;
            float scale = data.globals.height;
            var area = data.area.active;
            float margin = t.Shoulder + Step;   // beyond the footprint's edges (the samples reach them): its blend
            float minX = (float)area.worldPos.x + margin, maxX = (float)(area.worldPos.x + area.worldSize.x) - margin;
            float minZ = (float)area.worldPos.z + margin, maxZ = (float)(area.worldPos.z + area.worldSize.z) - margin;
            int headings = Math.Max(1, t.Headings);
            float offset = (float)(Poi.Hash01(trs.hash, 0x0DEC0DEu) * 180.0 / headings);
            float best = float.MaxValue;
            int along = Math.Max(2, (int)(t.Length / Step) + 1);
            int across = Math.Max(3, (int)Math.Ceiling(t.Width / Across) + 1);   // no hill between the lines unseen
            var s = new float[along * across];
            var h = new float[along * across];
            for (int k = 0; k < headings; k++)
            {
                float deg = offset + k * 180f / headings;
                float rad = deg * Mathf.Deg2Rad;
                float fx = Mathf.Sin(rad), fz = Mathf.Cos(rad);   // forward (+Z turned by deg about Y)
                float rx = fz, rz = -fx;                          // right
                Vector3 centre = trs.pos + t.Offset(deg);
                bool inside = true;
                int n = 0;
                for (int i = 0; i < along && inside; i++)
                {
                    float si = -t.Length * 0.5f + t.Length * i / (along - 1);
                    for (int j = 0; j < across; j++)
                    {
                        float aj = -t.Width * 0.5f + t.Width * j / (across - 1);
                        float x = centre.x + fx * si + rx * aj;
                        float z = centre.z + fz * si + rz * aj;
                        if (x < minX || x > maxX || z < minZ || z > maxZ) { inside = false; break; }
                        s[n] = si;
                        h[n] = heights.GetWorldInterpolatedValue(x, z) * scale;
                        n++;
                    }
                }
                if (!inside) continue;
                // least squares h = a + b s
                double ss = 0, sh = 0, sss = 0, ssh = 0;
                for (int i = 0; i < n; i++) { ss += s[i]; sh += h[i]; sss += s[i] * s[i]; ssh += s[i] * h[i]; }
                double den = n * sss - ss * ss;
                double bb = Math.Abs(den) < 1e-9 ? 0 : (n * ssh - ss * sh) / den;
                double aa = (sh - bb * ss) / n;
                if (Math.Abs(bb) > t.MaxGrade) continue;
                float worst = 0f;
                for (int i = 0; i < n; i++) worst = Mathf.Max(worst, Mathf.Abs(h[i] - (float)(aa + bb * s[i])));
                if (worst > t.MaxCut || worst >= best) continue;
                best = worst;
                heading = deg; a = (float)aa; b = (float)bb;
            }
            return best < float.MaxValue;
        }
    }

    // the places an output keeps (those no POI takes)
    [Serializable]
    public class PoiRest : Generator, IMultiInlet, IOutlet<TransitionsList>
    {
        public readonly Inlet<TransitionsList> placesIn = new Inlet<TransitionsList>();
        public readonly Inlet<MatrixWorld> heightIn = new Inlet<MatrixWorld>();
        [NonSerialized] internal PoiPlan Plan;

        public IEnumerable<IInlet<object>> Inlets() { yield return placesIn; yield return heightIn; }

        public override void Generate(TileData data, StopToken stop)
        {
            var places = data.ReadInletProduct(placesIn);
            if (places == null) return;
            var d = Plan.Decide(data, places, data.ReadInletProduct(heightIn));
            var kept = new TransitionsList();
            for (int i = 0; i < places.count; i++) if (d.Which[i] < 0) kept.Add(places.arr[i]);
            data.StoreProduct(this, kept);
        }
    }

    // one POI's places, turned to their headings
    [Serializable]
    public class PoiTake : Generator, IMultiInlet, IOutlet<TransitionsList>
    {
        public readonly Inlet<TransitionsList> placesIn = new Inlet<TransitionsList>();
        public readonly Inlet<MatrixWorld> heightIn = new Inlet<MatrixWorld>();
        [NonSerialized] internal PoiPlan Plan;
        [NonSerialized] internal int Template;

        public IEnumerable<IInlet<object>> Inlets() { yield return placesIn; yield return heightIn; }

        public override void Generate(TileData data, StopToken stop)
        {
            var places = data.ReadInletProduct(placesIn);
            if (places == null) return;
            var d = Plan.Decide(data, places, data.ReadInletProduct(heightIn));
            var taken = new TransitionsList();
            for (int i = 0; i < places.count; i++)
            {
                if (d.Which[i] != Template) continue;
                var trs = places.arr[i];
                trs.rotation = Quaternion.Euler(0f, Plan.Templates[Template].Yaw(d.Heading[i]), 0f);
                taken.Add(trs);
            }
            data.StoreProduct(this, taken);
        }
    }

    // One POI's footprints graded into the height: inside the footprint, the fitted straight grade along its length,
    // flat across; beyond its edges, eased back into the ground over its shoulder (a smooth falloff with distance
    // from the rectangle, so the corners round off only in the shoulder). siteIn is the ground before any POI
    // grading, which the sites are decided on; heightIn is the height so far, graded on.
    [Serializable]
    public class PoiGrade : Generator, IMultiInlet, IOutlet<MatrixWorld>
    {
        public readonly Inlet<TransitionsList> placesIn = new Inlet<TransitionsList>();
        public readonly Inlet<MatrixWorld> siteIn = new Inlet<MatrixWorld>();
        public readonly Inlet<MatrixWorld> heightIn = new Inlet<MatrixWorld>();
        [NonSerialized] internal PoiPlan Plan;
        [NonSerialized] internal int Template;

        public IEnumerable<IInlet<object>> Inlets() { yield return placesIn; yield return siteIn; yield return heightIn; }

        public override void Generate(TileData data, StopToken stop)
        {
            var src = data.ReadInletProduct(heightIn);
            if (src == null) return;
            var places = data.ReadInletProduct(placesIn);
            if (places == null) { data.StoreProduct(this, src); return; }
            var d = Plan.Decide(data, places, data.ReadInletProduct(siteIn));
            var t = Plan.Templates[Template];
            MatrixWorld graded = null;
            int sites = 0;
            for (int i = 0; i < places.count; i++)
            {
                if (d.Which[i] != Template) continue;
                if (graded == null) graded = new MatrixWorld(src);
                var centre = places.arr[i].pos + t.Offset(d.Heading[i]);
                int changed = Grade(graded, centre, d.Heading[i], d.Height[i], d.Slope[i], t);
                sites++;
                if (Plugin.VerboseLogEntry.Value)
                {
                    float sc = src.worldSize.y;
                    Plugin.Verbose($"POIs: '{t.Id}' graded at ({centre.x:0}, {centre.z:0}) on a {(data.isDraft ? "draft" : "full")} tile: centre {src.GetWorldInterpolatedValue(centre.x, centre.z) * sc:0.0} m -> " +
                                   $"{graded.GetWorldInterpolatedValue(centre.x, centre.z) * sc:0.0} m (aimed {d.Height[i]:0.0} m), {changed} pixels; matrix at ({src.worldPos.x:0}, {src.worldPos.z:0}) " +
                                   $"size {src.worldSize.x:0} x {src.worldSize.z:0} x {src.worldSize.y:0}, {src.rect.size.x} px");
                    lock (Graded) Graded.Add((centre, d.Heading[i], d.Height[i], d.Slope[i], t.Id));
                }
            }
            data.StoreProduct(this, graded ?? src);
            if (sites > 0) Plugin.Verbose($"POIs: '{t.Id}': {sites} site(s) graded on a {(data.isDraft ? "draft" : "full")} tile");
        }

        // sites graded, for the check on the terrain's final heights (verbose log only)
        internal static readonly List<(Vector3 centre, float heading, float a, float b, string id)> Graded = new List<(Vector3, float, float, float, string)>();

        private static int Grade(MatrixWorld m, Vector3 centre, float heading, float a, float b, PoiTemplate t)
        {
            int changed = 0;
            float rad = heading * Mathf.Deg2Rad, fx = Mathf.Sin(rad), fz = Mathf.Cos(rad), rx = fz, rz = -fx;
            float halfL = t.Length * 0.5f, halfW = t.Width * 0.5f, shoulder = Mathf.Max(1f, t.Shoulder);
            float reach = Mathf.Sqrt(halfL * halfL + halfW * halfW) + shoulder;
            float px = m.worldSize.x / (m.rect.size.x - 1), pz = m.worldSize.z / (m.rect.size.z - 1);
            int x0 = Mathf.Max(0, (int)((centre.x - reach - m.worldPos.x) / px)), x1 = Mathf.Min(m.rect.size.x - 1, (int)((centre.x + reach - m.worldPos.x) / px) + 1);
            int z0 = Mathf.Max(0, (int)((centre.z - reach - m.worldPos.z) / pz)), z1 = Mathf.Min(m.rect.size.z - 1, (int)((centre.z + reach - m.worldPos.z) / pz) + 1);
            float scale = m.worldSize.y;
            for (int iz = z0; iz <= z1; iz++)
                for (int ix = x0; ix <= x1; ix++)
                {
                    float dx = m.worldPos.x + ix * px - centre.x, dz = m.worldPos.z + iz * pz - centre.z;
                    float along = dx * fx + dz * fz, across = dx * rx + dz * rz;
                    float ex = Mathf.Max(0f, Mathf.Abs(along) - halfL), ez = Mathf.Max(0f, Mathf.Abs(across) - halfW);
                    float out_ = Mathf.Sqrt(ex * ex + ez * ez);
                    if (out_ >= shoulder) continue;
                    float k = 1f - out_ / shoulder;
                    float w = k * k * (3f - 2f * k);   // smooth: full on the footprint, easing out to nothing
                    float target = (a + b * Mathf.Clamp(along, -halfL, halfL)) / scale;
                    int pos = iz * m.rect.size.x + ix;
                    m.arr[pos] += (target - m.arr[pos]) * w;
                    changed++;
                }
            return changed;
        }
    }

    // verbose log only: the terrain's final heights at graded sites, after the height output has put them together
    [HarmonyLib.HarmonyPatch(typeof(HeightOutput200), nameof(HeightOutput200.Finalize))]
    internal static class PoiHeightCheck
    {
        private static void Postfix(TileData data)
        {
            if (!Plugin.VerboseLogEntry.Value || data?.heights == null) return;
            List<(Vector3 centre, float heading, float a, float b, string id)> sites;
            lock (PoiGrade.Graded) sites = PoiGrade.Graded.ToList();
            var h = data.heights;
            foreach (var s in sites)
            {
                if (s.centre.x < h.worldPos.x || s.centre.x > h.worldPos.x + h.worldSize.x || s.centre.z < h.worldPos.z || s.centre.z > h.worldPos.z + h.worldSize.z) continue;
                float rad = s.heading * Mathf.Deg2Rad;
                var along = s.centre + new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * 500f;
                Plugin.Verbose($"POIs: '{s.id}' terrain after the height output ({(data.isDraft ? "draft" : "full")}): centre {h.GetWorldInterpolatedValue(s.centre.x, s.centre.z) * h.worldSize.y:0.0} m (graded {s.a:0.0}), " +
                               $"500 m along {h.GetWorldInterpolatedValue(along.x, along.z) * h.worldSize.y:0.0} m (graded {s.a + s.b * 500f:0.0})");
            }
        }
    }

    internal static class PoiGraph
    {
        // what was changed in a graph, to put back before building again (the graph asset outlives a scene)
        private sealed class Undo
        {
            public readonly List<Generator> Added = new List<Generator>();
            public readonly List<KeyValuePair<IInlet<object>, IOutlet<object>>> Links = new List<KeyValuePair<IInlet<object>, IOutlet<object>>>();
        }

        private static readonly Dictionary<Graph, Undo> Done = new Dictionary<Graph, Undo>();

        // the footprint POIs into the world's graph; true if it changed
        public static bool Build(MapMagicObject mm, List<PoiPlan> plans)
        {
            var g = mm.graph;
            Restore(g);
            if (plans.Count == 0) return false;
            var heightOut = g.GeneratorsOfType<HeightOutput200>().FirstOrDefault();
            var heightSrc = heightOut != null ? g.GetLink(heightOut) : null;
            if (heightOut == null || heightSrc == null) { Plugin.Log.LogWarning("POIs: the world's graph has no linked height output; footprint POIs not placed"); return false; }
            var undo = new Undo();
            Done[g] = undo;
            undo.Links.Add(new KeyValuePair<IInlet<object>, IOutlet<object>>(heightOut, heightSrc));
            IOutlet<object> height = heightSrc;   // the height as it goes on, stamp after stamp

            foreach (var plan in plans)
            {
                var output = g.GetGeneratorById(plan.Output) as ObjectsOutput;
                var places = output != null ? g.GetLink(output) : null;
                if (places == null) { Plugin.Log.LogWarning($"POIs: [{plan.Output}] isn't a linked objects output; skipped"); continue; }
                undo.Links.Add(new KeyValuePair<IInlet<object>, IOutlet<object>>(output, places));

                var rest = Add<PoiRest>(g, undo);
                rest.Plan = plan;
                g.Link(places, rest.placesIn);
                g.Link(heightSrc, rest.heightIn);
                g.Link(rest, output);

                for (int k = 0; k < plan.Templates.Length; k++)
                {
                    var t = plan.Templates[k];
                    var take = Add<PoiTake>(g, undo);
                    take.Plan = plan; take.Template = k;
                    g.Link(places, take.placesIn);
                    g.Link(heightSrc, take.heightIn);
                    var mine = Add<ObjectsOutput>(g, undo);
                    mine.prefabs = new[] { t.Prefab };
                    mine.allowReposition = output.allowReposition;
                    mine.instantiateClones = output.instantiateClones;
                    mine.outputLevel = output.outputLevel;
                    mine.objHeight = true; mine.relativeHeight = true;
                    mine.useRotation = true; mine.rotateYonly = true; mine.takeTerrainNormal = false; mine.regardPrefabRotation = false;
                    mine.useScale = false;
                    g.Link(take, mine);

                    var grade = Add<PoiGrade>(g, undo);
                    grade.Plan = plan; grade.Template = k;
                    g.Link(places, grade.placesIn);
                    g.Link(heightSrc, grade.siteIn);
                    g.Link(height, grade.heightIn);
                    height = grade;
                    Plugin.Log.LogInfo($"POIs: '{t.Id}' ({t.Length:0} x {t.Width:0} m, grade {t.MaxGrade:P1}, cut {t.MaxCut:0.#} m) takes {plan.Share:P0} of [{plan.Output}]'s places that fit, graded in");
                }
            }
            g.Link(height, heightOut);
            return true;
        }

        private static T Add<T>(Graph g, Undo undo) where T : Generator
        {
            var gen = (T)Generator.Create(typeof(T));
            g.Add(gen);
            undo.Added.Add(gen);
            return gen;
        }

        private static void Restore(Graph g)
        {
            if (!Done.TryGetValue(g, out var undo)) return;
            foreach (var gen in undo.Added) if (g.ContainsGenerator(gen)) { g.UnlinkGenerator(gen); g.Remove(gen); }
            foreach (var kv in undo.Links) g.Link(kv.Value, kv.Key);
            Done.Remove(g);
        }
    }
}

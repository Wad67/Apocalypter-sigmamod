using System;
using System.Collections.Generic;
using System.Linq;
using Den.Tools;
using MapMagic.Core;
using MapMagic.Nodes;
using MapMagic.Nodes.ObjectsGenerators;
using MapMagic.Products;
using UnityEngine;

namespace SigmaMod
{
    // Points of interest: templates with sigma_type "poi", placed by the terrain generator where the game already
    // places camps, wrecks, caves or buildings, instead of some of them. Not more of them: a POI takes a share of the
    // places one of the game's objects outputs (a node of the MapMagic graph, named by its id in the template's
    // .json) would fill, so the world is no more cluttered. Those places already have what the game gives them:
    // flattened ground under them, and roads to them where the game runs roads to that kind of place (both come
    // from the places before they are shared out between the outputs).
    //
    //   "my_outpost": { "poi": { "in": "15169460551123206150", "share": 0.15 } }
    //
    //   in        the objects output (its graph node id, as a string; a list for several), from
    //             BepInEx/SigmaMod-mapmagic-graph.txt
    //   share     of that output's places, 0..1; default: as one more of its kinds (1 / (prefabs + 1))
    //   footprint for a POI that needs ground to suit it (PoiGraph): length, width (m, along its +Z and across),
    //             shoulder (m of blend into the ground beyond its width, default 30), max_grade (rise over run along
    //             it, default 0.02), max_cut (m of cut or fill it may need, default 3), headings (tried over half a
    //             turn, default 24), along ("z", default, or "x": which of the model's axes the length runs on), center ([x, z]
    //             in the model's own space: where the footprint's middle is, default the origin), bounds (an
    //             object of the model whose mesh marks the site out: length, width, along and centre from it, where
    //             not given; the object isn't drawn), ground (the level the ground is cut to, from the model's origin:
    //             metres, or an object whose top it is, such as a slab the model stands on; default the origin), raise (m the
    //             model stands above that ground).
    //             Only places where it fits
    //             take it, turned to the heading that fits best, and the ground is graded to it.
    //
    // Which places go to POIs is decided by each place's own hash, so it is the same on every load and in every
    // save; the places that don't keep exactly what the game would have put there. Several POIs in one output share
    // what it gives them, again by each place's hash.
    //
    // It works on what MapMagic is about to place on a tile (Graph.OnOutputFinalized, every objects output's
    // places for the tile together): places of that output's prefabs move to the POIs. That runs on MapMagic's
    // threads, so everything it needs is captured on the main thread first (Rebuild, after the templates load,
    // before tiles generate). POIs with no places on a tile are still listed, empty, so the tile's pool clears them.
    internal static class Poi
    {
        private const uint Salt = 0x51A7A5u;    // which places: a hash of their own, apart from the game's choice

        internal static uint SaltFor(ulong output) => Salt ^ (uint)output;
        internal static uint PickSaltFor(ulong output) => Salt * 31u ^ (uint)(output >> 32);

        private sealed class Group
        {
            public ulong Output;
            public HashSet<GameObject> Prefabs;        // what that output places
            public double Share;
            public ObjectsPool.Prototype[] Pois;       // the POIs taking its places
            public string Name;
        }

        private static Group[] _groups = new Group[0];
        private static bool _subscribed, _built;
        private static string _plan;
        private static int _tiles;

        // on the main thread, after the templates are loaded: which outputs give places to which POIs
        public static void Rebuild()
        {
            var pois = CustomPrefabs.All.Where(t => string.Equals(t.Type, "poi", StringComparison.OrdinalIgnoreCase) && t.Prefab != null).ToList();
            var mm = Resources.FindObjectsOfTypeAll<MapMagicObject>().FirstOrDefault(m => m.gameObject.scene.IsValid());
            // nothing about the POIs changed (another asset was rebuilt): the world stays as it is. Planning again
            // regrades the graph and regenerates every tile, which takes the places away for a moment and drops
            // everything standing in them.
            string signature = (mm != null ? mm.GetInstanceID().ToString() : "-") + "|" + string.Join("|", pois.Select(t =>
                t.Id + "#" + t.Prefab.GetInstanceID() + "#" + (t.Spec != null && t.Spec.TryGetValue("poi", out var ps) ? Json.Write(ps) : "")).ToArray());
            if (signature == _plan) return;
            _plan = signature;
            if (pois.Count == 0)
            {
                _groups = new Group[0];
                if (mm != null && mm.graph != null) { PoiGraph.Build(mm, new List<PoiPlan>()); if (_built) Regenerate(mm); }
                _built = false;
                return;
            }
            if (mm == null || mm.graph == null) { Plugin.Log.LogWarning("POIs: no MapMagic world in this scene; none placed"); _groups = new Group[0]; return; }
            var outputs = new Dictionary<ulong, ObjectsOutput>();
            foreach (var gen in AllGenerators(mm.graph, new HashSet<Graph>()))
                if (gen is ObjectsOutput o) outputs[o.id] = o;

            var groups = new Dictionary<ulong, Group>();
            var shares = new Dictionary<ulong, double?>();
            var plans = new Dictionary<ulong, PoiPlan>();
            foreach (var t in pois)
            {
                if (t.Spec == null || !t.Spec.TryGetValue("poi", out var pv) || !(pv is Dictionary<string, object> spec))
                {
                    Plugin.Log.LogWarning($"POI '{t.Id}': no \"poi\" block in its .json; not placed");
                    continue;
                }
                double? share = spec.TryGetValue("share", out var sv) && sv is double d ? Math.Max(0, Math.Min(1, d)) : (double?)null;
                var footprint = spec.TryGetValue("footprint", out var fv) ? fv as Dictionary<string, object> : null;
                foreach (var idText in Ids(spec.TryGetValue("in", out var iv) ? iv : null))
                {
                    if (footprint != null)
                    {
                        if (!ulong.TryParse(idText, out ulong fid) || !outputs.TryGetValue(fid, out var fout))
                        {
                            Plugin.Log.LogWarning($"POI '{t.Id}': \"in\": {idText} is not an objects output of the world's graph (see SigmaMod-mapmagic-graph.txt)");
                            continue;
                        }
                        var tpl = new PoiTemplate
                        {
                            Id = t.Id, Prefab = t.Prefab,
                            Length = Num(footprint, "length", 100), Width = Num(footprint, "width", 30), Shoulder = Num(footprint, "shoulder", 30),
                            MaxGrade = Num(footprint, "max_grade", 0.02f), MaxCut = Num(footprint, "max_cut", 3),
                            Headings = (int)Num(footprint, "headings", 24),
                            AlongX = footprint.TryGetValue("along", out var al) && string.Equals(al as string, "x", StringComparison.OrdinalIgnoreCase),
                            CenterX = Pair(footprint, "center", 0), CenterZ = Pair(footprint, "center", 1),
                        };
                        if (footprint.TryGetValue("bounds", out var bv) && bv is string bname) FromBounds(t, bname, footprint, tpl);
                        if (footprint.TryGetValue("ground", out var gv)) GroundOf(t, gv, tpl);
                        tpl.Ground -= Num(footprint, "raise", 0f);   // the model that much above its ground
                        if (!plans.TryGetValue(fid, out var plan)) plans[fid] = plan = new PoiPlan { Output = fid, Templates = new PoiTemplate[0], Share = -1 };
                        plan.Templates = plan.Templates.Concat(new[] { tpl }).ToArray();
                        if (share.HasValue) plan.Share = Math.Max(plan.Share, share.Value);   // else worked out below
                        continue;
                    }
                    if (!ulong.TryParse(idText, out ulong id) || !outputs.TryGetValue(id, out var output))
                    {
                        Plugin.Log.LogWarning($"POI '{t.Id}': \"in\": {idText} is not an objects output of the world's graph (see SigmaMod-mapmagic-graph.txt)");
                        continue;
                    }
                    if (!groups.TryGetValue(id, out var g))
                    {
                        var prefabs = new HashSet<GameObject>(Enumerable.Where(output.prefabs, p => p != null));
                        groups[id] = g = new Group { Output = id, Prefabs = prefabs, Pois = new ObjectsPool.Prototype[0], Name = string.Join(", ", prefabs.Select(p => p.name).ToArray()) };
                    }
                    // a POI placed as the output places its own: the same reposition, clone and rotation settings
                    var like = output.GetPrototypes().FirstOrDefault();
                    var proto = new ObjectsPool.Prototype
                    {
                        prefab = t.Prefab,
                        allowReposition = like == null || like.allowReposition,
                        instantiateClones = like != null && like.instantiateClones,
                        regardPrefabRotation = like != null && like.regardPrefabRotation,
                        regardPrefabScale = like != null && like.regardPrefabScale,
                    };
                    g.Pois = g.Pois.Concat(new[] { proto }).ToArray();
                    // the output's share: the given one (the largest, if several POIs give one), else one more kind each
                    double given = share ?? 1.0 / (g.Prefabs.Count + g.Pois.Length);
                    shares.TryGetValue(id, out var had);
                    shares[id] = share.HasValue ? Math.Max(had ?? 0, given) : had ?? (double?)null;
                    if (!share.HasValue && !(had.HasValue)) shares[id] = null;
                }
            }
            foreach (var g in groups.Values)
            {
                shares.TryGetValue(g.Output, out var given);
                g.Share = given ?? (double)g.Pois.Length / (g.Prefabs.Count + g.Pois.Length);
                Plugin.Log.LogInfo($"POIs: {string.Join(", ", g.Pois.Select(p => p.prefab.name).ToArray())} take {g.Share:P0} of the places of [{g.Output}] ({g.Name})");
            }
            _groups = groups.Values.ToArray();

            // footprint POIs: into the graph, deciding before the ground is final (PoiGraph)
            foreach (var plan in plans.Values)
                if (plan.Share < 0)
                {
                    int kinds = Enumerable.Count(outputs[plan.Output].prefabs, p => p != null);
                    plan.Share = (double)plan.Templates.Length / (kinds + plan.Templates.Length);
                }
            bool changed = PoiGraph.Build(mm, plans.Values.ToList());
            // tiles made before the POIs were known (or the graph changed) are made again
            if (changed || _built || _groups.Length > 0) Regenerate(mm);
            _built = changed || _groups.Length > 0;
            if (!_subscribed)
            {
                Graph.OnOutputFinalized += Share;
                _subscribed = true;
            }
        }

        // A footprint from a node of the model (a plane or box marking the site out): its mesh's extent in the model's
        // own space gives the length (the longer side, which also sets along), width and centre, where the .json
        // doesn't give them; the node isn't drawn (it only marks the site out)
        // the ground the model stands on, from its origin: a height (m), or an object whose top it is (a slab under it)
        private static void GroundOf(CustomPrefabs.Info t, object v, PoiTemplate tpl)
        {
            if (v is double d) { tpl.Ground = (float)d; return; }
            var root = t.Prefab.transform;
            var node = v is string name ? root.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == name) : null;
            var mf = node != null ? node.GetComponent<MeshFilter>() : null;
            if (mf == null || mf.sharedMesh == null) { Plugin.Log.LogWarning($"POI '{t.Id}': footprint ground '{v}': no object of that name with a mesh"); return; }
            var b = mf.sharedMesh.bounds;
            float top = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                top = Mathf.Max(top, root.InverseTransformPoint(node.TransformPoint(c)).y);
            }
            tpl.Ground = top;
            Plugin.Log.LogInfo($"POI '{t.Id}': its ground is the top of '{node.name}', {top:0.00} m from its origin");
        }

        private static void FromBounds(CustomPrefabs.Info t, string name, Dictionary<string, object> footprint, PoiTemplate tpl)
        {
            var root = t.Prefab.transform;
            var node = string.Equals(name, "root", StringComparison.OrdinalIgnoreCase) || name == root.name || name == t.RootName
                ? root : root.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == name);
            var mf = node != null ? node.GetComponent<MeshFilter>() : null;
            if (mf == null || mf.sharedMesh == null) { Plugin.Log.LogWarning($"POI '{t.Id}': footprint bounds '{name}': no object of that name with a mesh"); return; }
            var b = mf.sharedMesh.bounds;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                var p = root.InverseTransformPoint(node.TransformPoint(c));
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
            }
            float sx = maxX - minX, sz = maxZ - minZ;
            bool alongX = footprint.ContainsKey("along") ? tpl.AlongX : sx > sz;
            tpl.AlongX = alongX;
            if (!footprint.ContainsKey("length")) tpl.Length = alongX ? sx : sz;
            if (!footprint.ContainsKey("width")) tpl.Width = alongX ? sz : sx;
            if (!footprint.ContainsKey("center")) { tpl.CenterX = (minX + maxX) * 0.5f; tpl.CenterZ = (minZ + maxZ) * 0.5f; }
            foreach (var r in node.GetComponents<Renderer>()) r.enabled = false;
            Plugin.Log.LogInfo($"POI '{t.Id}': footprint from '{node.name}': {tpl.Length:0} x {tpl.Width:0} m along {(alongX ? "X" : "Z")}, centre ({tpl.CenterX:0.#}, {tpl.CenterZ:0.#}); '{node.name}' not drawn");
        }

        private static float Pair(Dictionary<string, object> d, string key, int i) =>
            d.TryGetValue(key, out var v) && v is List<object> l && l.Count > i && l[i] is double x ? (float)x : 0f;

        private static float Num(Dictionary<string, object> d, string key, float fallback) => d.TryGetValue(key, out var v) && v is double x ? (float)x : fallback;

        private static IEnumerable<string> Ids(object v)
        {
            if (v is string s) yield return s.Trim();
            else if (v is double d) yield return ((ulong)d).ToString();   // a number loses precision past 2^53: give ids as strings
            else if (v is List<object> list) foreach (var x in list) foreach (var y in Ids(x)) yield return y;
        }

        internal static IEnumerable<Generator> AllGenerators(Graph g, HashSet<Graph> seen)
        {
            if (g == null || !seen.Add(g)) yield break;
            foreach (var gen in g.generators)
            {
                if (gen == null) continue;
                yield return gen;
                foreach (var f in gen.GetType().GetFields())
                    if (f.FieldType == typeof(Graph) && f.GetValue(gen) is Graph sub)
                        foreach (var x in AllGenerators(sub, seen)) yield return x;
            }
        }

        // on MapMagic's threads: a tile's places, some moved from the outputs' own prefabs to their POIs
        private static void Share(Type type, TileData data, IApplyData apply, StopToken stop)
        {
            var groups = _groups;
            if (groups.Length == 0 || type != typeof(ObjectsOutput) || !(apply is ObjectsOutput.ApplyObjectsData a) || a.prototypes == null) return;
            try
            {
                var prototypes = a.prototypes.ToList();
                var transitions = a.transitions.ToList();
                int moved = 0;
                foreach (var g in groups)
                {
                    var taken = new List<Transition>[g.Pois.Length];
                    for (int k = 0; k < taken.Length; k++) taken[k] = new List<Transition>();
                    for (int i = 0; i < prototypes.Count; i++)
                    {
                        if (prototypes[i] == null || !g.Prefabs.Contains(prototypes[i].prefab)) continue;
                        var kept = new List<Transition>(transitions[i].Count);
                        foreach (var trs in transitions[i])
                        {
                            if (Hash01(trs.hash, SaltFor(g.Output)) < g.Share)
                            {
                                int which = Math.Min(g.Pois.Length - 1, (int)(Hash01(trs.hash, PickSaltFor(g.Output)) * g.Pois.Length));
                                taken[which].Add(trs);
                                moved++;
                            }
                            else kept.Add(trs);
                        }
                        transitions[i] = kept;
                    }
                    for (int k = 0; k < g.Pois.Length; k++)
                    {
                        int at = prototypes.IndexOf(g.Pois[k]);
                        if (at >= 0) transitions[at].AddRange(taken[k]);
                        else { prototypes.Add(g.Pois[k]); transitions.Add(taken[k]); }
                    }
                }
                a.prototypes = prototypes.ToArray();
                a.transitions = transitions.ToArray();
                if (moved > 0 && (++_tiles <= 5 || _tiles % 25 == 0))
                    Plugin.Verbose($"POIs: {moved} place(s) on a tile went to POIs ({_tiles} tiles so far)");
            }
            catch (Exception e) { Plugin.Log.LogError("POIs: " + e.Message); }
        }

        // ---------------------------------------------------------------- regenerating the world, frozen
        // Regenerating takes every tile's ground away for a moment; anything standing on it in a running game falls
        // through. In play the game is frozen (time stopped: physics, vehicles, FSMs' waits) until the terrain is back:
        // no tile generating for half a second, at least a second after it began (at most 90 s). While the game loads,
        // nothing is frozen: its loading runs on game time, and nothing stands on the ground yet.
        private static float _frozenScale = -1f, _freezeSince, _quietSince = -1f;
        private static MapMagicObject _regenerating;

        private static void Regenerate(MapMagicObject mm)
        {
            if (PlaceSpots.Playing() && _frozenScale < 0f)
            {
                _frozenScale = Time.timeScale;
                Time.timeScale = 0f;
                Plugin.Log.LogInfo("POIs: the world is regenerating; the game is frozen until the ground is back");
            }
            if (_frozenScale >= 0f) { _freezeSince = Time.unscaledTime; _quietSince = -1f; _regenerating = mm; }
            mm.Refresh(true);
        }

        // every frame (the runner)
        internal static void Tick()
        {
            if (_frozenScale < 0f) return;
            float now = Time.unscaledTime;
            bool busy = false;
            try { if (_regenerating != null && _regenerating.tiles != null) foreach (var t in _regenerating.tiles.All()) if (t != null && t.IsGenerating) { busy = true; break; } }
            catch { }
            if (busy) _quietSince = -1f;
            else if (_quietSince < 0f) _quietSince = now;
            bool done = _quietSince >= 0f && now - _quietSince > 0.5f && now - _freezeSince > 1f;
            if (!done && now - _freezeSince < 90f) return;
            if (Time.timeScale == 0f) Time.timeScale = _frozenScale;   // not if something else has set it since
            Plugin.Log.LogInfo($"POIs: the world is back after {now - _freezeSince:0.0} s; the game runs again");
            _frozenScale = -1f; _regenerating = null;
        }

        // a place's own hash, mixed, as 0..1
        internal static double Hash01(int hash, uint salt)
        {
            uint h = unchecked((uint)hash * 2654435761u ^ salt);
            h ^= h >> 15; h = unchecked(h * 2246822519u); h ^= h >> 13; h = unchecked(h * 3266489917u); h ^= h >> 16;
            return h / 4294967296.0;
        }
    }
}

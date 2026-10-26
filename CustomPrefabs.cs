using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ES3Internal;
using HarmonyLib;
using NWH.WheelController3D;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SigmaMod
{
    // Turns every .glb template in assets/ into a game prefab: vehicles, parts and items alike.
    //
    // A template's root carries es3prefab, the Easy Save prefab id of the vanilla prefab it was
    // exported from (the donor). The donor is cloned and the template applied onto the clone:
    //  - a node whose es3local matches an object of the donor is that object: transform, mesh and
    //    materials come from the template, parenting follows the template;
    //  - collider nodes matching a donor collider update it; donor colliders the template dropped are
    //    disabled;
    //  - nodes made in Blender (no components list, no ids) are added as new objects, colliders
    //    included;
    //  - nodes exported from the game that the donor prefab does not have (fitted parts, cargo,
    //    objects created at runtime) are skipped: they are separate prefabs or runtime state;
    //  - donor objects the template dropped keep their logic but stop rendering.
    // Active state and renderer state of donor objects stay as the donor has them, because game FSMs
    // drive those at runtime.
    //
    // Identity: root custom property sigma_id (else the file name) is the template id; the ES3 prefab
    // id is a hash of it, and everything new gets stable ids, so saves map the same way every session.
    // sigma_name is the display name (ItemName FSM, so Apocaspawner and hover text show it).
    // The prefab is an inactive root object kept across scenes, which is where Apocaspawner's catalog
    // looks; it is registered with each scene's reference manager on load, before the save is read.
    //
    // Injection: every instance, however it was made (Spawn, the game's spawners, Apocaspawner, a save loading), fires
    // Instantiated from ES3Prefab.Awake. A node's custom property sigma_behaviour (a name, or several
    // separated by commas) calls the handlers registered under that name with the instance object and
    // the node's custom properties.
    public static class CustomPrefabs
    {
        public sealed class Info
        {
            public string Id;            // sigma_id or file name
            public string Name;          // display name
            public long PrefabId;        // Easy Save prefab id
            public string Path;
            public string Mod;           // mod that ships the file (sigma.json "name" or its folder name)
            public string DonorName;
            public bool IsVehicle;
            public string Type;          // root sigma_type
            public string RootName;      // the root's name in the model (Blender's); the root object itself is named after the id
            public Dictionary<string, object> Spec;   // its block in the .json
            public GameObject Prefab;    // template: active itself, kept asleep under an inactive holder, like a vanilla prefab asset
            public GameObject Donor;     // the game prefab its objects come from
            public bool Spawns;          // joins the game's spawn lists that hold Donor (root sigma_spawn, default true)
            public double SpawnWeight = 1; // root sigma_spawn as a number: how likely next to each other entry of a list
            // root sigma_spawn_like: game prefabs (Easy Save ids) whose spawn lists it joins instead of Donor's, so a
            // part built from scratch turns up where those do, and a variant somewhere other than its donor
            public readonly List<GameObject> SpawnLike = new List<GameObject>();
            // custom properties of each template node, by the ES3 local id of the object it became
            public readonly Dictionary<long, Dictionary<string, object>> NodeExtras = new Dictionary<long, Dictionary<string, object>>();
            // sigma_id of non-root nodes (unique within the template) -> ES3 local id
            public readonly Dictionary<string, long> NodeIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            internal ES3Prefab Es3;
            internal DateTime Stamp;
            internal readonly List<KeyValuePair<long, UnityEngine.Object>> Refs = new List<KeyValuePair<long, UnityEngine.Object>>();
        }

        public const string IdKey = "sigma_id", NameKey = "sigma_name", BehaviourKey = "sigma_behaviour", TypeKey = "sigma_type", SpawnKey = "sigma_spawn",
                            SpawnLikeKey = "sigma_spawn_like";

        private sealed class FileEntry { public DateTime Stamp; public readonly List<Info> Templates = new List<Info>(); }
        private static readonly Dictionary<string, FileEntry> ByPath = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<long, Info> ByPrefabId = new Dictionary<long, Info>();
        private static readonly Dictionary<string, List<Action<GameObject, Dictionary<string, object>, Info>>> Behaviours =
            new Dictionary<string, List<Action<GameObject, Dictionary<string, object>, Info>>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> UnknownBehaviours = new HashSet<string>();
        private static Transform _workbench;

        // ---------------------------------------------------------------- public API

        // fired for every new instance of a custom prefab (root object, its template)
        public static event Action<GameObject, Info> Instantiated;

        public static IEnumerable<Info> All => ByPath.Values.SelectMany(f => f.Templates);

        public static Info Find(string id) => All.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

        // the template behind any object of a custom prefab instance
        public static bool TryGet(GameObject go, out Info info)
        {
            info = null;
            var es3 = go != null ? go.GetComponentInParent<ES3Prefab>() : null;
            return es3 != null && ByPrefabId.TryGetValue(es3.prefabId, out info);
        }

        // the custom properties of the template node an instance object came from
        public static bool TryGetExtras(GameObject go, out Dictionary<string, object> extras)
        {
            extras = null;
            if (!TryGet(go, out var info)) return false;
            var es3 = go.GetComponentInParent<ES3Prefab>();
            return es3.localRefs.TryGetValue(go, out long id) && info.NodeExtras.TryGetValue(id, out extras);
        }

        // the object of an instance whose template node has sigma_id = id; any object of the instance will do
        public static GameObject FindNode(GameObject instance, string id)
        {
            if (!TryGet(instance, out var info) || !info.NodeIds.TryGetValue(id, out long localId)) return null;
            var es3 = instance.GetComponentInParent<ES3Prefab>();
            foreach (var kv in es3.localRefs)
                if (kv.Value == localId && kv.Key is GameObject go) return go;
            return null;
        }

        // the sigma_id of the template node an instance object came from
        public static string IdOf(GameObject go) =>
            TryGetExtras(go, out var ex) && ex.TryGetValue(IdKey, out var v) ? v as string : null;

        public static void RegisterBehaviour(string name, Action<GameObject, Dictionary<string, object>, Info> attach)
        {
            if (!Behaviours.TryGetValue(name, out var list)) Behaviours[name] = list = new List<Action<GameObject, Dictionary<string, object>, Info>>();
            list.Add(attach);
        }

        // ---------------------------------------------------------------- registration

        internal static void Init()
        {
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                var mgr = ES3ReferenceMgrBase.GetManagerFromScene(scene, false);
                if (mgr == null) return;
                try { RegisterAssets(mgr); }
                catch (Exception e) { Plugin.Log.LogError("custom prefabs: " + e); }
            };
        }

        // asset folders found at the last scene load (hot reload watches these)
        internal static List<AssetSources.Source> Sources;

        internal static List<Info> TemplatesOf(string glb) => ByPath.TryGetValue(glb, out var e) ? new List<Info>(e.Templates) : new List<Info>();

        private static void RegisterAssets(ES3ReferenceMgrBase mgr)
        {
            int n = 0;
            Sources = AssetSources.Find();
            foreach (var src in Sources)
                foreach (var path in Directory.GetFiles(src.Dir, "*.glb", SearchOption.AllDirectories))
                    n += LoadFile(src, path, mgr).Count;
            if (n > 0) Registered();
        }

        // (re)builds a file's templates if it changed and registers them with the scene's save manager
        internal static List<Info> LoadFile(AssetSources.Source src, string path, ES3ReferenceMgrBase mgr)
        {
            _donorIndex = null;
            List<Info> list;
            try { list = Get(path, mgr); }
            catch (Exception e) { Plugin.Log.LogError("custom prefab " + path + ": " + e); return new List<Info>(); }
            if (list.Count == 0)
                Plugin.Log.LogWarning($"custom prefab: {src.Mod}/{Path.GetFileName(path)}: nothing loaded (no template in its script for a root object, and no root object with {IdKey})");
            foreach (var t in list) { t.Mod = src.Mod; Register(t, mgr); }
            return list;
        }

        internal static void Registered()
        {
            FrameTrace.Mark("registered: POIs");
            Poi.Rebuild();
            FrameTrace.Mark("registered: places");
            try { Places.Rebuild(); } catch (Exception e) { Plugin.Log.LogError("places: " + e); }
            FrameTrace.Mark("registered: spawn lists");
            SpawnLists.Join(All.Where(i => i.Spawns));
            FrameTrace.Mark("registered: Apocaspawner");
            InvalidateApocaspawner();
        }

        // newest of the .glb, its .json and its .lua (with the Lua libraries); a file that is not there counts as unchanged
        internal static DateTime Stamp(string glb)
        {
            var stamp = File.GetLastWriteTimeUtc(glb);
            if (File.Exists(JsonPath(glb)) && File.GetLastWriteTimeUtc(JsonPath(glb)) > stamp) stamp = File.GetLastWriteTimeUtc(JsonPath(glb));
            var lua = Lua.Stamp(glb);
            if (lua > stamp) stamp = lua;
            return stamp;
        }

        internal static bool Changed(string glb) => !ByPath.TryGetValue(glb, out var have) || have.Stamp != Stamp(glb);

        private static void Register(Info t, ES3ReferenceMgrBase mgr)
        {
            mgr.AddPrefab(t.Es3);
            foreach (var kv in t.Refs) mgr.Add(kv.Value, kv.Key);
        }

        // Apocaspawner caches its catalog; ask it to rebuild so new prefabs show up (soft dependency)
        private static void InvalidateApocaspawner()
        {
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Apocaspawner.Catalog", false)).FirstOrDefault(x => x != null);
                type?.GetMethod("Invalidate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, null);
            }
            catch (Exception e) { Plugin.Verbose("Apocaspawner catalog not invalidated: " + e.Message); }
        }

        // Builds (or rebuilds when the file changed) the prefabs of a file: one per root object that has a
        // sigma_id. Root objects without one are not loaded (reference meshes, scraps, stray colliders).
        // ids as a number, a string of comma-separated numbers, or a list of either
        private static IEnumerable<long> Ids(object v)
        {
            if (v == null) yield break;
            var items = v is List<object> l ? l : new List<object> { v };
            foreach (var x in items)
                foreach (var part in Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture).Split(','))
                    if (long.TryParse(part.Trim(), out long id)) yield return id;
        }

        // sigma_spawn: absent or true 1, false 0, a number (or a numeric string) that number
        private static double SpawnWeightOf(Dictionary<string, object> ex)
        {
            if (!ex.TryGetValue(SpawnKey, out var v)) return 1;
            switch (v)
            {
                case bool b: return b ? 1 : 0;
                case double d: return Math.Max(0, d);
                case string s:
                    s = s.Trim().ToLowerInvariant();
                    if (s == "false") return 0;
                    if (s == "true") return 1;
                    return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? Math.Max(0, n) : 1;
            }
            return 1;
        }

        private static string JsonPath(string glb) => Path.ChangeExtension(glb, ".json");

        // A script's template carries what the model's custom properties would, so nothing needs tagging in Blender.
        // Its root object: "model" (a root object's name), else the root with that sigma_id, else the root named as the
        // template, else the file's only root. On it, from the template: name, type ("poi" when it has "poi"), spawn,
        // spawn_like, behaviour. Below it, from a "nodes" entry: behaviour, and spawns (the template an empty is a spot
        // of). What the script says wins over the model's own properties.
        private static readonly string[,] ScriptKeys = { { "name", NameKey }, { "type", TypeKey }, { "spawn", SpawnKey }, { "spawn_like", SpawnLikeKey }, { "behaviour", BehaviourKey } };
        internal static readonly string[] ScriptOnly = { "model", "name", "type", "spawn", "spawn_like", "behaviour", "spawns", "one_of", StarterKits.Key };

        private static void ScriptProperties(GltfModel m, Dictionary<string, object> doc, string path)
        {
            string file = Path.GetFileName(Lua.ScriptPath(path));
            var claimed = new HashSet<int>();
            foreach (var kv in doc)
            {
                if (!(kv.Value is Dictionary<string, object> spec) || kv.Key == "fsms" || kv.Key == "states" || kv.Key == "actions") continue;
                int root = ScriptRoot(m, kv.Key, spec, claimed, out string why);
                if (root < 0) { Plugin.Log.LogWarning($"{file}: template '{kv.Key}': {why}"); continue; }
                claimed.Add(root);
                var ex = ExtrasOf(m.Nodes[root]);
                ex[IdKey] = kv.Key;
                for (int k = 0; k < ScriptKeys.GetLength(0); k++)
                    if (spec.TryGetValue(ScriptKeys[k, 0], out var v) && v != null) ex[ScriptKeys[k, 1]] = Text(v, ScriptKeys[k, 1]);
                if (!spec.ContainsKey("type") && spec.ContainsKey("poi")) ex[TypeKey] = "poi";
                if (!(spec.TryGetValue("nodes", out var nv) && nv is Dictionary<string, object> nodes)) continue;
                foreach (var nk in nodes)
                {
                    if (!(nk.Value is Dictionary<string, object> ns)) continue;
                    bool spot = ns.TryGetValue("spawns", out var sp), beh = ns.TryGetValue("behaviour", out var bv);
                    if (!spot && !beh) continue;
                    int i = Below(m, root, nk.Key);
                    if (i < 0 || i == root) { Plugin.Log.LogWarning($"{file}: {kv.Key}: nodes: no object named '{nk.Key}' below the root"); continue; }
                    var nex = ExtrasOf(m.Nodes[i]);
                    if (spot) nex[Places.PlaceKey] = Convert.ToString(sp, System.Globalization.CultureInfo.InvariantCulture);
                    if (beh) nex[BehaviourKey] = Text(bv, BehaviourKey);
                }
            }
        }

        // a list as the comma-separated text the property takes; spawn and spawn_like read lists themselves
        private static object Text(object v, string key) =>
            v is List<object> l && key != SpawnLikeKey
                ? string.Join(", ", l.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray()) : v;

        private static int ScriptRoot(GltfModel m, string id, Dictionary<string, object> spec, HashSet<int> claimed, out string why)
        {
            why = null;
            var names = string.Join(", ", m.Roots.Select(r => "'" + m.Nodes[r].Name + "'").ToArray());
            if (spec.TryGetValue("model", out var mv) && mv is string model)
            {
                foreach (int r in m.Roots) if (m.Nodes[r].Name == model) return r;
                why = $"no root object named '{model}' (roots: {names})";
                return -1;
            }
            foreach (int r in m.Roots)
                if (m.Nodes[r].Extras != null && m.Nodes[r].Extras.TryGetValue(IdKey, out var iv) && iv is string s && s.Trim() == id) return r;
            foreach (int r in m.Roots) if (m.Nodes[r].Name == id) return r;
            var free = m.Roots.Where(r => !claimed.Contains(r)).ToList();
            if (m.Roots.Count == 1 && free.Count == 1) return free[0];
            why = $"which root object is it? Say model = \"<name>\" (roots: {names})";
            return -1;
        }

        private static int Below(GltfModel m, int i, string name)
        {
            if (m.Nodes[i].Name == name) return i;
            foreach (int c in m.Nodes[i].Children) { int f = Below(m, c, name); if (f >= 0) return f; }
            return -1;
        }

        private static Dictionary<string, object> ExtrasOf(GNode n) => n.Extras ?? (n.Extras = new Dictionary<string, object>());

        // The .json's shorthand expanded to the long form (Shorthand.cs). With VerboseLog the long form is written to
        // BepInEx/SigmaMod-expanded/<mod>/<file>.json, and checked to expand to itself, so it loads the same.
        private static Dictionary<string, object> ExpandShorthand(Dictionary<string, object> doc, string path)
        {
            string file = Path.GetFileName(JsonPath(path));
            Shorthand.FindType = Types.Any;
            var res = Shorthand.Expand(doc, w => Plugin.Log.LogWarning($"{file}: {w}"));
            if (!Plugin.VerboseLogEntry.Value) return res;
            try
            {
                string mod = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)));
                string dir = Path.Combine(Path.Combine(BepInEx.Paths.BepInExRootPath, Plugin.NAME + "-expanded"), mod);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, file), Json.Pretty(res));
                if (Json.Write(Shorthand.Expand(res, null)) != Json.Write(res))
                    Plugin.Log.LogWarning($"{file}: its long form doesn't expand to itself; please report this");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{file}: writing the long form: {e.Message}"); }
            return res;
        }

        private static List<Info> Get(string path, ES3ReferenceMgrBase mgr)
        {
            var stamp = Stamp(path);
            if (ByPath.TryGetValue(path, out var have) && have.Stamp == stamp) return have.Templates;

            // read both files before touching the old version: a half-saved .glb or a .json typo keeps it
            GltfModel model;
            Dictionary<string, object> overrides = null;
            try
            {
                model = GltfReader.Load(path);
                if (File.Exists(JsonPath(path)))
                {
                    try { overrides = Json.Parse(File.ReadAllText(JsonPath(path))) as Dictionary<string, object>; }
                    catch (Exception e) { throw new Exception($"{Path.GetFileName(JsonPath(path))}: {e.Message}"); }
                    if (overrides != null) overrides = ExpandShorthand(overrides, path);
                }
                // the asset's script: its entries over the .json's; it says which root each template is, and what
                // Blender's custom properties would
                if (File.Exists(Lua.ScriptPath(path)))
                {
                    var doc = ExpandShorthand(Lua.Document(path), path);
                    ScriptProperties(model, doc, path);
                    if (overrides == null) overrides = doc;
                    else foreach (var kv in doc) overrides[kv.Key] = kv.Value;
                }
            }
            catch (Exception e)
            {
                if (have != null)
                {
                    have.Stamp = stamp; // not again until the files change
                    Plugin.Log.LogError($"custom prefab: {Path.GetFileName(path)} not reloaded, the previous version stays: {e.Message}");
                    return have.Templates;
                }
                ByPath[path] = new FileEntry { Stamp = stamp };   // a new file that fails: once, then not until it changes
                throw;
            }
            if (have != null)
                foreach (var old in have.Templates)
                {
                    ByPrefabId.Remove(old.PrefabId);
                    mgr.prefabs.Remove(old.Es3);
                    if (old.Prefab != null) UnityEngine.Object.Destroy(old.Prefab);
                }
            var entry = new FileEntry { Stamp = stamp };
            ByPath[path] = entry;
            foreach (int root in model.Roots)
            {
                var rootNode = model.Nodes[root];
                var rootEx = rootNode.Extras;
                if (rootEx == null || !(rootEx.TryGetValue(IdKey, out var iv) && iv is string ids && ids.Trim().Length > 0))
                {
                    Plugin.Verbose($"custom prefab: {Path.GetFileName(path)}: root '{rootNode.Name}' has no {IdKey}, not loaded");
                    continue;
                }
                string id = ids.Trim();
                long prefabId = StableId(Plugin.GUID + "/prefab/" + id);
                if (ByPrefabId.TryGetValue(prefabId, out var clash))
                {
                    Plugin.Log.LogWarning($"custom prefab: {path}: id '{id}' is already used by {clash.Path}; skipped");
                    continue;
                }
                var donor = Donor(model, root, mgr, out string how, out bool scratch);
                if (donor == null && !scratch)
                {
                    Plugin.Log.LogWarning($"custom prefab: {path}: '{id}': no game prefab found to build on ({how})");
                    continue;
                }
                Dictionary<string, object> spec = null;
                if (overrides != null && overrides.TryGetValue(id, out var sv) && !(sv is Dictionary<string, object>))
                    Plugin.Log.LogWarning($"custom prefab: {JsonPath(path)}: '{id}' must be an object");
                else if (overrides != null && overrides.TryGetValue(id, out sv)) spec = (Dictionary<string, object>)sv;
                BakeScale(model, root, spec, id);
                var t = Assemble(model, root, path, id, prefabId, rootEx, donor, mgr, spec,
                                 overrides != null && overrides.TryGetValue("fsms", out var fl) ? fl as Dictionary<string, object> : null);
                t.Stamp = stamp;
                t.RootName = rootNode.Name;
                entry.Templates.Add(t);
                ByPrefabId[prefabId] = t;
                Plugin.Verbose($"custom prefab: '{id}': {how}");
            }
            return entry.Templates;
        }

        // The root's scale (Blender's, not applied) times the .json's "scale" (a number, or [x, y, z]) goes into the
        // model itself: the root's mesh and every child's offset and scale, so the template's root is unscaled (a
        // vehicle's body must be) and everything in it, colliders and the .json's own positions included, is in metres.
        // A non-uniform scale is exact only for children not turned against it.
        private static void BakeScale(GltfModel model, int root, Dictionary<string, object> spec, string id)
        {
            var node = model.Nodes[root];
            float[] k = { node.S[0], node.S[1], node.S[2] };
            if (spec != null && spec.TryGetValue("scale", out var sv))
            {
                if (sv is double d) { k[0] *= (float)d; k[1] *= (float)d; k[2] *= (float)d; }
                else if (sv is List<object> l && l.Count == 3)
                    for (int i = 0; i < 3; i++) k[i] *= Convert.ToSingle(l[i], System.Globalization.CultureInfo.InvariantCulture);
                else Plugin.Log.LogWarning($"custom prefab: '{id}': \"scale\" is a number or [x, y, z]");
            }
            node.S = new float[] { 1, 1, 1 };
            if (Math.Abs(k[0] - 1) < 1e-5 && Math.Abs(k[1] - 1) < 1e-5 && Math.Abs(k[2] - 1) < 1e-5) return;
            foreach (int c in node.Children)
            {
                var n = model.Nodes[c];
                n.T = new[] { n.T[0] * k[0], n.T[1] * k[1], n.T[2] * k[2] };
                n.S = new[] { n.S[0] * k[0], n.S[1] * k[1], n.S[2] * k[2] };
            }
            if (node.Mesh >= 0)
            {
                var m = model.Meshes[node.Mesh];
                if (m.Extras != null && m.Extras.ContainsKey("unreadable"))
                    Plugin.Log.LogWarning($"custom prefab: '{id}': the root's mesh is the game's own (unreadable), so it keeps its size");
                else
                {
                    // a copy: another object may share the mesh
                    var copy = new GMesh { Name = m.Name, Uvs = m.Uvs, Extras = m.Extras, Positions = (float[])m.Positions.Clone(), Normals = m.Normals != null ? (float[])m.Normals.Clone() : null };
                    copy.Submeshes.AddRange(m.Submeshes); copy.SubmeshMaterials.AddRange(m.SubmeshMaterials);
                    for (int i = 0; i + 2 < copy.Positions.Length; i += 3)
                        for (int a = 0; a < 3; a++) copy.Positions[i + a] *= k[a];
                    if (copy.Normals != null)
                        for (int i = 0; i + 2 < copy.Normals.Length; i += 3)
                        {
                            var v = new Vector3(copy.Normals[i] / k[0], copy.Normals[i + 1] / k[1], copy.Normals[i + 2] / k[2]).normalized;
                            copy.Normals[i] = v.x; copy.Normals[i + 1] = v.y; copy.Normals[i + 2] = v.z;
                        }
                    model.Meshes.Add(copy);
                    node.Mesh = model.Meshes.Count - 1;
                }
            }
            Plugin.Log.LogInfo($"custom prefab: '{id}': scaled {k[0]:0.####} x {k[1]:0.####} x {k[2]:0.####} (root scale and \"scale\")");
        }

        // The game prefab a template builds on. An unchanged export names it on its root (es3prefab).
        // Otherwise the objects say it themselves: every es3local id belongs to exactly one game prefab,
        // and all of them must belong to the same one. Ids from different prefabs refuse the template.
        private static Dictionary<long, ES3Prefab> _donorIndex;

        // No game ids anywhere (no es3prefab, no es3local): built from scratch, scratch = true.
        private static ES3Prefab Donor(GltfModel model, int root, ES3ReferenceMgrBase mgr, out string how, out bool scratch)
        {
            scratch = false;
            ES3Prefab named = null;
            var ex = model.Nodes[root].Extras;
            if (ex != null && ex.TryGetValue("es3prefab", out var dv) && dv is string ds && long.TryParse(ds, out long donorId))
            {
                named = mgr.GetPrefab(donorId, true);
                if (named == null) { how = $"root es3prefab {donorId} is not a loaded game prefab"; return null; }
            }
            if (_donorIndex == null)
            {
                _donorIndex = new Dictionary<long, ES3Prefab>();
                foreach (var p in mgr.prefabs)
                    if (p != null && !ByPrefabId.ContainsKey(p.prefabId))
                        foreach (var kv in p.localRefs) _donorIndex[kv.Value] = p;
            }
            var owners = new Dictionary<ES3Prefab, List<string>>();
            var unknown = new List<string>();
            void Walk(int i)
            {
                var n = model.Nodes[i];
                var e = n.Extras;
                if (i != root && e != null && e.ContainsKey("es3prefab")) return; // a fitted part: its own prefab, skipped later
                if (e != null && e.TryGetValue("es3local", out var lv) && lv is string ls && long.TryParse(ls, out long l))
                {
                    if (_donorIndex.TryGetValue(l, out var p)) { if (!owners.TryGetValue(p, out var list)) owners[p] = list = new List<string>(); list.Add(n.Name); }
                    else unknown.Add(n.Name);
                }
                foreach (int c in n.Children) Walk(c);
            }
            Walk(root);
            if (unknown.Count > 0)
                Plugin.Log.LogWarning($"custom prefab: {unknown.Count} objects have ids no game prefab owns (runtime objects, skipped): {string.Join(", ", unknown.Take(12).ToArray())}");
            if (named != null) owners.Remove(named);
            if (named == null && owners.Count == 1) { var only = owners.Keys.First(); how = $"its objects' ids, all from '{only.name}'"; return only; }
            if (named != null && owners.Count == 0) { how = $"root es3prefab ('{named.name}')"; return named; }
            if (owners.Count == 0 && unknown.Count == 0) { scratch = true; how = "built from scratch (no game ids in it)"; return null; }
            if (owners.Count == 0) { how = "no es3prefab on the root, and no object with an id a game prefab owns"; return null; }
            how = "objects come from different game prefabs: " + string.Join("; ", owners.Select(o => $"'{o.Key.name}': {string.Join(", ", o.Value.Take(8).ToArray())}{(o.Value.Count > 8 ? " ..." : "")}").ToArray())
                  + (named != null ? $" (root names '{named.name}')" : "");
            return null;
        }

        private static Info Assemble(GltfModel model, int root, string path, string id, long prefabId, Dictionary<string, object> rootEx,
                                     ES3Prefab donor, ES3ReferenceMgrBase mgr, Dictionary<string, object> overrides, Dictionary<string, object> library)
        {
            // built under an inactive parent so no Awake runs, then kept as an inactive root object
            if (_workbench == null)
            {
                var h = new GameObject(Plugin.NAME + " workbench");
                h.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(h);
                _workbench = h.transform;
            }
            string name = rootEx.TryGetValue(NameKey, out var nv) && nv is string ns && ns.Trim().Length > 0 ? ns.Trim() : id;
            // a donor's clone, or (from scratch) a new object with nothing on it but the Easy Save prefab marker
            GameObject go;
            ES3Prefab es3;
            if (donor != null)
            {
                go = UnityEngine.Object.Instantiate(donor.gameObject, _workbench);
                es3 = go.GetComponent<ES3Prefab>();
            }
            else
            {
                go = new GameObject(id);
                go.transform.SetParent(_workbench, false);
                if (rootEx.TryGetValue("layer", out var lv) && lv is double ld) go.layer = (int)ld;
                es3 = go.AddComponent<ES3Prefab>();
                es3.localRefs.Clear();
                es3.localRefs[go] = StableId(prefabId + "/root");
                es3.localRefs[go.transform] = StableId(prefabId + "/root#transform");
                es3.localRefs[es3] = StableId(prefabId + "/root#ES3Prefab");
            }
            go.name = id;
            es3.prefabId = prefabId;
            var t = new Info
            {
                Id = id, Name = name, PrefabId = prefabId, Path = path, DonorName = donor != null ? donor.name : "nothing", Prefab = go, Es3 = es3,
                Donor = donor != null ? donor.gameObject : null,
                SpawnWeight = SpawnWeightOf(rootEx),
            };
            t.Spawns = t.SpawnWeight > 0;
            foreach (long like in Ids(rootEx.TryGetValue(SpawnLikeKey, out var slv) ? slv : null))
            {
                var p = mgr.GetPrefab(like, true);
                if (p != null) t.SpawnLike.Add(p.gameObject);
                else Plugin.Log.LogWarning($"custom prefab: '{id}': {SpawnLikeKey} {like} is not a loaded game prefab");
            }

            // donor objects by their local id (Instantiate remaps the dictionary onto the clone)
            var byId = new Dictionary<long, UnityEngine.Object>();
            foreach (var kv in es3.localRefs) if (kv.Key != null) byId[kv.Value] = kv.Key;

            // A root with no children is a variant: the game prefab as it is, with its own id, name and .json
            // values. Otherwise the template is applied object by object.
            bool variant = donor != null && model.Nodes[root].Children.Count == 0 && model.Nodes[root].Mesh < 0;
            var a = new Applier(model, es3, byId, prefabId, mgr, t.NodeExtras) { Scratch = donor == null };
            if (variant)
            {
                if (es3.localRefs.TryGetValue(go, out long variantRoot)) t.NodeExtras[variantRoot] = rootEx;
                Plugin.Log.LogInfo($"custom prefab: '{id}' is a variant of '{donor.name}' (no objects in the template)");
            }
            else
            {
                a.Node(root, null, go);
                a.Finish(go);
                FsmOffsets.Apply(go, a.Moved, "custom prefab '" + id + "'");
            }

            // node ids: sigma_id on any node but the root, unique within the template
            long rootLocal = es3.localRefs.TryGetValue(go, out long rl) ? rl : -1;
            foreach (var kv in t.NodeExtras)
            {
                if (kv.Key == rootLocal || !(kv.Value.TryGetValue(IdKey, out var nv2) && nv2 is string nid) || nid.Trim().Length == 0) continue;
                if (t.NodeIds.ContainsKey(nid.Trim())) Plugin.Log.LogWarning($"custom prefab: {path}: sigma_id '{nid}' is used on more than one object; the first is kept");
                else t.NodeIds[nid.Trim()] = kv.Key;
            }

            // objects for the .json: sigma_id, else the Blender object name
            GameObject Node(string nid)
            {
                if (string.Equals(nid, "root", StringComparison.OrdinalIgnoreCase)) return go;
                if (nid.StartsWith("/")) return nid.Length == 1 ? go : go.transform.Find(nid.Substring(1))?.gameObject;
                if (t.NodeIds.TryGetValue(nid, out long l)) return es3.localRefs.Where(kv => kv.Value == l).Select(kv => kv.Key).OfType<GameObject>().FirstOrDefault();
                return a.Named.TryGetValue(nid, out var g) ? g : null;
            }
            string where = Path.GetFileName(JsonPath(path)) + "/" + id;
            var ctx = new JsonValues { Root = go, Node = Node, AddNode = (n, g) => a.Named[n] = g, Dir = Path.GetDirectoryName(path), Mgr = mgr, Library = library,
                                       Created = new List<KeyValuePair<string, UnityEngine.Object>>() };

            // a POI's working parts: the game's own objects in place of their copies in the export
            if (donor == null && rootEx.TryGetValue(TypeKey, out var ptv) && string.Equals(ptv as string, "poi", StringComparison.OrdinalIgnoreCase))
                PlaceParts.Build(go, t, a.Named, mgr, ctx, overrides, where);

            // components and FSMs the .json adds; what it adds gets save ids like everything else
            if (overrides != null)
            {
                var before = new HashSet<Component>(go.GetComponentsInChildren<Component>(true));
                Overrides.Build(go, overrides, Node, ctx, where);
                var counts = new Dictionary<string, int>();
                foreach (var tr in go.GetComponentsInChildren<Transform>(true))
                    if (!before.Contains(tr) && !es3.localRefs.ContainsKey(tr.gameObject))
                        es3.localRefs[tr.gameObject] = StableId(prefabId + "/json/" + PathBelow(go.transform, tr) + "#GameObject");
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || before.Contains(c) || es3.localRefs.ContainsKey(c)) continue;
                    string key = PathBelow(go.transform, c.transform) + "#" + c.GetType().FullName;
                    counts.TryGetValue(key, out int k);
                    counts[key] = k + 1;
                    es3.localRefs[c] = StableId(prefabId + "/json/" + key + "/" + k);
                }

            }

            string type = rootEx.TryGetValue(TypeKey, out var tv) ? tv as string : null;
            bool chassis = string.Equals(type, "chassis", StringComparison.OrdinalIgnoreCase);
            bool poi = string.Equals(type, "poi", StringComparison.OrdinalIgnoreCase);
            t.Type = type;
            t.Spec = overrides;
            Dictionary<string, string> donorValues = null, resetValues = null;
            if (chassis)
            {
                donorValues = ValueReport.Snapshot(go);
                NewChassis(go, id, overrides);
                resetValues = ValueReport.Snapshot(go);
            }
            else if (poi) { t.Spawns = false; if (go.GetComponent<PoiProbe>() == null) go.AddComponent<PoiProbe>(); }   // placed by the terrain generator (Poi), not by spawn lists
            else if (type != null) Plugin.Log.LogWarning($"custom prefab: '{id}': unknown {TypeKey} '{type}'");

            if (overrides != null) Overrides.Apply(go, overrides, Node, ctx, where);
            if (chassis)
            {
                var added = CenterOfMass(go, id, overrides, Node);
                if (added != null) es3.localRefs[added] = StableId(prefabId + "/root#VariableCenterOfMass");
            }
            var vcl =go.GetComponent<NWH.VehiclePhysics2.VehicleController>();
            t.IsVehicle = vcl != null;
            if (vcl != null && vcl.stateSettings == null)
                Plugin.Log.LogWarning($"custom prefab: '{id}': VehicleController has no stateSettings, NWH won't run it " +
                                      "(\"VehicleController\": { \"stateSettings\": \"asset:StateSettings/DefaultStateSettings\" })");
            if (chassis)
                try { ValueReport.Write(id, donorValues, resetValues, ValueReport.Snapshot(go)); }
                catch (Exception e) { Plugin.Log.LogWarning($"custom prefab: '{id}': chassis value report failed: {e.Message}"); }

            // collider meshes and loaded files: files are shared between templates, so their ids come from the path alone
            foreach (var kv in ctx.Created.GroupBy(x => x.Value).Select(g => g.First()))
                t.Refs.Add(new KeyValuePair<long, UnityEngine.Object>(
                    StableId(kv.Key.StartsWith("file:") ? Plugin.GUID + "/" + kv.Key : prefabId + "/" + kv.Key), kv.Value));
            foreach (var kv in a.Builder.Created())
                t.Refs.Add(new KeyValuePair<long, UnityEngine.Object>(StableId(prefabId + "/" + kv.Key + "/" + a.Occurrence(kv.Key)), kv.Value));

            // display name where the game and Apocaspawner read it
            foreach (var fsm in go.GetComponents<PlayMakerFSM>())
                if (fsm.FsmName == "ItemName")
                {
                    var v = fsm.FsmVariables.GetFsmString("ItemName");
                    if (v != null) v.Value = name;
                }

            // stays under the inactive holder: copies made by Instantiate come out active, exactly like copies of
            // a vanilla prefab asset, so the game's own spawners, Easy Save and other mods can use it directly
            Plugin.Log.LogInfo($"custom prefab: '{id}' ({name}) " + (donor != null ? $"from donor '{donor.name}'" : "from scratch") +
                               $", prefab id {prefabId}" + (variant ? "" : ": " + a.Stats));
            return t;
        }

        private static string PathBelow(Transform root, Transform t)
        {
            string p = "";
            for (; t != null && t != root; t = t.parent) p = "/" + t.name + p;
            return p;
        }

        // sigma_type = chassis: the root's Rigidbody and VehicleController start from defaults, not from the
        // game vehicle the parts came from. NWH's SetDefaults rebuilds the drivetrain (axles, steering,
        // brakes, differentials, engine, clutch, gearbox) from the WheelControllers under the chassis and
        // keeps asset references (clips, mixer, ground preset). The components stay the same objects
        // because FSMs (input, pedals, handbrake, audio) hold references to them.
        private static void NewChassis(GameObject go, string id, Dictionary<string, object> overrides)
        {
            var rb = go.GetComponent<Rigidbody>();
            var vc = go.GetComponent<NWH.VehiclePhysics2.VehicleController>();
            if (rb == null || vc == null)
            {
                Plugin.Log.LogWarning($"custom prefab: '{id}': chassis root has no {(rb == null ? "Rigidbody" : "VehicleController")} (\"add\": [\"Rigidbody\", \"VehicleController\"] in the .json)");
                return;
            }
            bool wheels = go.GetComponentsInChildren<NWH.Common.Vehicles.WheelUAPI>(true).Length > 0;
            rb.mass = 1f; rb.drag = 0f; rb.angularDrag = 0.05f;
            rb.useGravity = true; rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.constraints = RigidbodyConstraints.None;
            rb.ResetCenterOfMass();
            rb.ResetInertiaTensor();
            vc.SetDefaults();
            if (!wheels)
            {
                // NWH sets up engine, clutch and gearbox and stops there
                Plugin.Log.LogWarning($"custom prefab: '{id}': chassis has no WheelControllers under it; engine, clutch and gearbox only, nothing driven (add WheelController to the wheel hinges)");
                return;
            }
            string drive = Drive(vc.powertrain, overrides, id);
            Plugin.Log.LogInfo($"custom prefab: '{id}': new chassis, {vc.powertrain.wheels.Count} wheels in {vc.powertrain.wheelGroups.Count} axles, {drive} drive");
            if (overrides == null || !overrides.TryGetValue("Rigidbody", out var r) || !(r is Dictionary<string, object> rd) || !rd.ContainsKey("mass"))
                Plugin.Log.LogWarning($"custom prefab: '{id}': chassis mass is the default 1 kg; set Rigidbody.mass in the .json");
        }

        // The centre of mass and the inertia, set the way every vanilla car sets them: NWH's VariableCenterOfMass on
        // the root, a fixed point (useDefaultCenterOfMass off) and the inertia of a solid box of the car's mass and
        // dimensions (NWH's CalculateInertia, which is what the vanilla values are: Outrider 1400 kg, 1.8 x 1.31 x
        // 4.74 m -> 2821, 2999, 578), written into the Rigidbody every physics step. Without it Unity puts the
        // centre of mass in the middle of the colliders and works the inertia out from their shapes.
        //   "center_of_mass": an object (Blender name or sigma_id: its position) or [x, y, z] from the root, in metres
        //   "dimensions": [width, height, length] in metres for the inertia (else the body's size, from its meshes)
        // A donor's VariableCenterOfMass (a changed game car) keeps its point unless "center_of_mass" moves it; its
        // inertia is worked out again for the chassis' own mass. Returns the component when it was added here.
        private static Component CenterOfMass(GameObject go, string id, Dictionary<string, object> overrides, Func<string, GameObject> node)
        {
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) return null;
            var vcom = go.GetComponent<NWH.Common.CoM.VariableCenterOfMass>();
            object cv = null, dv = null;
            overrides?.TryGetValue("center_of_mass", out cv);
            overrides?.TryGetValue("dimensions", out dv);
            Vector3? at = null;
            if (cv is string cn)
            {
                var n = node(cn);
                if (n == null) Plugin.Log.LogWarning($"custom prefab: '{id}': center_of_mass '{cn}' is no object in the model");
                else at = go.transform.InverseTransformPoint(n.transform.position);
            }
            else if (Vec(cv) is Vector3 cp) at = cp;
            if (vcom == null && at == null)
            {
                Plugin.Log.LogWarning($"custom prefab: '{id}': no center_of_mass, so Unity puts it in the middle of the colliders " +
                                      "and the inertia comes from their shapes (vanilla cars set both; see the Blender add-on's Tuning)");
                return null;
            }
            Component added = null;
            if (vcom == null)
            {
                vcom = go.AddComponent<NWH.Common.CoM.VariableCenterOfMass>();
                added = vcom;
                vcom.dimensions = Vec(dv) ?? BodySize(go);
            }
            else if (Vec(dv) is Vector3 d) vcom.dimensions = d;
            vcom.useDefaultMass = true;   // the Rigidbody's mass, as the vanilla cars have it
            vcom.useMassAffectors = false;
            if (at != null)
            {
                vcom.useDefaultCenterOfMass = false;
                vcom.centerOfMass = vcom.combinedCenterOfMass = at.Value;
            }
            for (int i = 0; i < 3; i++) vcom.dimensions[i] = Mathf.Max(vcom.dimensions[i], 0.01f);
            vcom.useDefaultInertia = false;
            vcom.baseMass = vcom.combinedMass = rb.mass;
            vcom.inertiaTensor = vcom.combinedInertiaTensor = NWH.Common.CoM.VariableCenterOfMass.CalculateInertia(vcom.dimensions, rb.mass);
            rb.centerOfMass = vcom.centerOfMass;
            rb.inertiaTensor = vcom.inertiaTensor;
            Plugin.Log.LogInfo($"custom prefab: '{id}': centre of mass {vcom.centerOfMass:F3} from the root{(at == null ? " (the donor's)" : "")}, " +
                               $"inertia {vcom.inertiaTensor:F0} ({rb.mass:F0} kg, {vcom.dimensions:F2} m)");
            return added;
        }

        private static Vector3? Vec(object v)
        {
            if (!(v is List<object> l) || l.Count != 3) return null;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return new Vector3(Convert.ToSingle(l[0], ci), Convert.ToSingle(l[1], ci), Convert.ToSingle(l[2], ci));
        }

        // the size of what's shown of the car (its meshes, switched-off ones left out), in the root's axes
        private static Vector3 BodySize(GameObject go)
        {
            var b = new Bounds();
            bool any = false;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || !mf.gameObject.activeSelf) continue;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = go.transform.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (any) b.Encapsulate(p); else { b = new Bounds(p, Vector3.zero); any = true; }
                }
            }
            return any ? b.size : new Vector3(1.8f, 1.6f, 4.6f);   // NWH's own default
        }

        // Drive layout from "drive" in the chassis .json ("front", "rear", "all", "none", or an axle number, front first:
        // with more than two axles "rear" is the second one). SetDefaults builds a front, a rear and a centre
        // differential (all open) with the gearbox on the centre one: all-wheel drive, 50/50. Front or rear
        // drive puts the gearbox straight onto that axle's differential and drops the other two, so the
        // undriven axle's wheels only steer and brake.
        private static string Drive(NWH.VehiclePhysics2.Powertrain.Powertrain pt, Dictionary<string, object> overrides, string id)
        {
            object dv = null;
            overrides?.TryGetValue("drive", out dv);
            // none: the engine turns only itself and what is on its crank (a belt-driven generator, a pump); the clutch
            // hands nothing on, so the wheels are free and simulate themselves, turned only by what acts on them
            if (dv is string ns && ns.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                pt.clutch.Output = null;
                foreach (var w in pt.wheels) if (w != null && w.wheelUAPI != null) w.wheelUAPI.AutoSimulate = true;
                return "none (the clutch drives nothing; the wheels are free)";
            }
            // one wheel (a bike, a dyno flywheel): NWH leaves it unconnected and says to put it on the gearbox directly
            if (pt.wheels.Count == 1)
            {
                pt.transmission.Output = pt.wheels[0];
                pt.differentials = new List<NWH.VehiclePhysics2.Powertrain.DifferentialComponent>();
                return "single-wheel (gearbox straight to the wheel)";
            }
            // a number: that axle (0 = the front one; NWH wires a differential to the first two axles only)
            if (dv is double dn)
            {
                int n = (int)dn;
                if (n < 0 || n > 1 || n >= pt.wheelGroups.Count || pt.differentials.Count != 3)
                {
                    Plugin.Log.LogWarning($"custom prefab: '{id}': \"drive\": {n}: only axle 0 or 1 of {pt.wheelGroups.Count} can be driven; all-wheel drive");
                    return "all-wheel";
                }
                var nd = pt.differentials[n];
                pt.transmission.Output = nd;
                pt.differentials = new List<NWH.VehiclePhysics2.Powertrain.DifferentialComponent> { nd };
                return $"axle {n} ({pt.wheelGroups[n].name})";
            }
            string drive = (dv as string)?.Trim().ToLowerInvariant();
            if (drive == null)
            {
                Plugin.Log.LogWarning($"custom prefab: '{id}': no \"drive\" in the .json (rear, front or all); all-wheel drive");
                return "all-wheel";
            }
            int axle = drive == "front" ? 0 : drive == "rear" ? 1 : drive == "all" ? -1 : -2;
            // one axle: NWH only wires the front differential, so that axle is the driven one whatever "drive" says
            if (axle >= 0 && pt.wheelGroups.Count == 1) axle = 0;
            if (axle == -2) { Plugin.Log.LogWarning($"custom prefab: '{id}': unknown drive '{drive}' (rear, front or all); all-wheel drive"); return "all-wheel"; }
            if (axle < 0) return "all-wheel";
            if (pt.differentials.Count != 3) { Plugin.Log.LogWarning($"custom prefab: '{id}': {pt.differentials.Count} differentials after setup, expected 3; all-wheel drive"); return "all-wheel"; }
            var diff = pt.differentials[axle];
            pt.transmission.Output = diff;
            pt.differentials = new List<NWH.VehiclePhysics2.Powertrain.DifferentialComponent> { diff };
            return drive + "-wheel";
        }

        // NWH finds wheels with GetComponentsInChildren<WheelUAPI>(), which skips inactive objects; templates are
        // inactive, so SetDefaults would find none. Only SetDefaults calls it (in the editor: Reset).
        [HarmonyLib.HarmonyPatch(typeof(NWH.VehiclePhysics2.Powertrain.Powertrain), "VC_SetDefaults")]
        private static class PowertrainWheelsPatch
        {
            private static IEnumerable<HarmonyLib.CodeInstruction> Transpiler(IEnumerable<HarmonyLib.CodeInstruction> code)
            {
                var find = HarmonyLib.AccessTools.Method(typeof(Component), nameof(Component.GetComponentsInChildren), Type.EmptyTypes,
                                                         new[] { typeof(NWH.Common.Vehicles.WheelUAPI) });
                var with = HarmonyLib.AccessTools.Method(typeof(PowertrainWheelsPatch), nameof(Wheels));
                int n = 0;
                foreach (var c in code)
                {
                    if (c.Calls(find)) { n++; yield return new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Call, with); }
                    else yield return c;
                }
                if (n == 0) Plugin.Log.LogWarning("Powertrain.VC_SetDefaults: wheel lookup not found; chassis on inactive templates will have no wheels");
            }

            private static NWH.Common.Vehicles.WheelUAPI[] Wheels(Component c) => c.GetComponentsInChildren<NWH.Common.Vehicles.WheelUAPI>(true);
        }

        // ---------------------------------------------------------------- instances

        // ES3Prefab.Awake runs for every new instance: spawned, loaded from a save, or made by another mod
        [HarmonyLib.HarmonyPatch(typeof(ES3Prefab), "Awake")]
        private static class AwakePatch
        {
            private static void Postfix(ES3Prefab __instance)
            {
                if (!ByPrefabId.TryGetValue(__instance.prefabId, out var info) || __instance.gameObject == info.Prefab) return;
                try { OnInstance(__instance, info); }
                catch (Exception e) { Plugin.Log.LogError($"custom prefab {info.Id}: instance setup failed: {e}"); }
            }
        }

        private static void OnInstance(ES3Prefab es3, Info info)
        {
            Plugin.Verbose($"custom prefab: instance {es3.name} of '{info.Id}'");
            Instantiated?.Invoke(es3.gameObject, info);
            Lua.Attach(es3.gameObject, info.Id);
            foreach (var kv in es3.localRefs)
            {
                if (!(kv.Key is GameObject obj) || !info.NodeExtras.TryGetValue(kv.Value, out var ex)) continue;
                if (!ex.TryGetValue(BehaviourKey, out var bv) || !(bv is string names)) continue;
                foreach (var raw in names.Split(','))
                {
                    string b = raw.Trim();
                    if (b.Length == 0) continue;
                    if (Behaviours.TryGetValue(b, out var handlers))
                        foreach (var h in handlers) h(obj, ex, info);
                    else if (UnknownBehaviours.Add(b))
                        Plugin.Log.LogWarning($"custom prefab {info.Id}: no behaviour registered as '{b}' (node {obj.name})");
                }
            }
        }

        private sealed class Applier
        {
            public readonly GltfUnity.Builder Builder;
            private readonly GltfModel _m;
            private readonly ES3Prefab _es3;
            private readonly Dictionary<long, UnityEngine.Object> _byId;
            private readonly long _prefabId;
            private readonly HashSet<UnityEngine.Object> _seen = new HashSet<UnityEngine.Object>();
            private readonly Dictionary<string, int> _occ = new Dictionary<string, int>();
            private int _mapped, _added, _skipped, _collidersMapped, _collidersAdded;
            public string Stats => $"{_mapped} objects mapped, {_added} added, {_skipped} skipped (fitted parts, runtime objects), " +
                                   $"colliders {_collidersMapped} mapped, {_collidersAdded} added, {_hidden} donor renderers hidden, {_disabled} donor colliders disabled";
            private int _hidden, _disabled;
            // no donor: objects copied from the game's exports are built like any other (a place's building in a POI)
            public bool Scratch;

            private readonly Dictionary<long, Dictionary<string, object>> _nodeExtras;
            // template objects by Blender object name
            public readonly Dictionary<string, GameObject> Named = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            // game objects the template moved, by how far (in their parent's space)
            public readonly Dictionary<Transform, Vector3> Moved = new Dictionary<Transform, Vector3>();

            public Applier(GltfModel m, ES3Prefab es3, Dictionary<long, UnityEngine.Object> byId, long prefabId, ES3ReferenceMgrBase mgr,
                           Dictionary<long, Dictionary<string, object>> nodeExtras)
            {
                _m = m; _es3 = es3; _byId = byId; _prefabId = prefabId; _nodeExtras = nodeExtras;
                Builder = new GltfUnity.Builder(m) { Inert = true, Mgr = mgr };
            }

            public int Occurrence(string key)
            {
                _occ.TryGetValue(key, out int n);
                _occ[key] = n + 1;
                return n;
            }

            private T Mapped<T>(Dictionary<string, object> ex) where T : UnityEngine.Object =>
                ex != null && ex.TryGetValue("es3local", out var v) && v is string s && long.TryParse(s, out long id) && _byId.TryGetValue(id, out var o) ? o as T : null;

            private static bool FromGame(Dictionary<string, object> ex) =>
                ex != null && (ex.ContainsKey("components") || ex.ContainsKey("es3local") || ex.ContainsKey("es3global"));

            // path from the root, for stable ids of new objects
            private string PathOf(int i) => _m.Nodes[i].Parent < 0 ? "" : PathOf(_m.Nodes[i].Parent) + "/" + _m.Nodes[i].Name;

            public void Node(int i, Transform parent, GameObject rootTarget)
            {
                var n = _m.Nodes[i];
                var ex = n.Extras ?? new Dictionary<string, object>();
                if (ex.TryGetValue(GltfUnity.MarkerKey, out var mk) && (mk as string) == GltfUnity.ColliderMarker)
                {
                    Collider(i, parent.gameObject);
                    return;
                }

                GameObject target;
                if (rootTarget != null) target = rootTarget;
                else
                {
                    if (ex.ContainsKey("es3prefab")) { _skipped++; return; } // a fitted part or cargo: its own prefab
                    target = Mapped<GameObject>(ex);
                    if (target == null && FromGame(ex) && !Scratch) { _skipped++; return; } // runtime object the donor prefab lacks
                    if (target == null)
                    {
                        target = new GameObject(n.Name ?? "node" + i);
                        target.transform.SetParent(parent, false);
                        target.layer = parent.gameObject.layer;
                        if (ex.TryGetValue("layer", out var l) && l is double ld) target.layer = (int)ld;
                        Local(target, PathOf(i));
                        Local(target.transform, PathOf(i) + "#transform");
                        if (ex.TryGetValue("active", out var av) && av is bool ab) target.SetActive(ab);
                        _added++;
                    }
                    else
                    {
                        bool sameParent = target.transform.parent == parent;
                        if (!sameParent) target.transform.SetParent(parent, false);
                        var d = new Vector3(n.T[0], n.T[1], n.T[2]) - target.transform.localPosition;
                        if (sameParent && d.sqrMagnitude > 1e-8f) Moved[target.transform] = d;
                        _mapped++;
                    }
                    target.transform.localPosition = new Vector3(n.T[0], n.T[1], n.T[2]);
                    target.transform.localRotation = new Quaternion(n.R[0], n.R[1], n.R[2], n.R[3]);
                    target.transform.localScale = new Vector3(n.S[0], n.S[1], n.S[2]);
                }
                _seen.Add(target);
                if (n.Name != null)
                {
                    if (!Named.ContainsKey(n.Name)) Named[n.Name] = target;
                    else Plugin.Log.LogWarning($"custom prefab: two objects are named '{n.Name}'; the .json gets the first");
                }
                if (_es3.localRefs.TryGetValue(target, out long localId)) _nodeExtras[localId] = ex;

                if (target.GetComponent<SkinnedMeshRenderer>() != null)
                {
                    // a skinned mesh (the seated player model) is drawn by its bones: leave it alone. The
                    // export holds its bind pose, which as a static mesh is a T-pose.
                }
                else if (n.Mesh >= 0)
                {
                    var mesh = Builder.Mesh(n.Mesh);
                    var mf = target.GetComponent<MeshFilter>();
                    var mr = target.GetComponent<MeshRenderer>();
                    if (mf == null) { mf = target.AddComponent<MeshFilter>(); Local(mf, PathOf(i) + "#MeshFilter"); }
                    if (mr == null) { mr = target.AddComponent<MeshRenderer>(); Local(mr, PathOf(i) + "#MeshRenderer"); }
                    mf.sharedMesh = mesh;
                    mr.sharedMaterials = Builder.Materials(n.Mesh, mesh);
                }
                else
                {
                    var mr = target.GetComponent<MeshRenderer>();
                    if (mr != null && mr.enabled) { mr.enabled = false; _hidden++; }
                }

                ApplyData(target, ex);
                foreach (int c in n.Children) Node(c, target.transform, null);
            }

            private void ApplyData(GameObject target, Dictionary<string, object> ex)
            {
                if (ex.TryGetValue("rigidbody", out var rv) && rv is Dictionary<string, object> rd)
                {
                    var rb = target.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        if (rd.TryGetValue("mass", out var mv) && mv is double md) rb.mass = (float)md;
                        if (rd.TryGetValue("centerOfMass", out var cv) && cv is List<object> cl && cl.Count == 3)
                            rb.centerOfMass = new Vector3(Convert.ToSingle(cl[0]), Convert.ToSingle(cl[1]), Convert.ToSingle(cl[2]));
                    }
                }
                if (ex.TryGetValue("wheel", out var wv) && wv is Dictionary<string, object> wd)
                {
                    var wc = target.GetComponent<WheelController>();
                    if (wc != null)
                    {
                        if (wd.TryGetValue("radius", out var v) && v is double d) wc.wheel.radius = (float)d;
                        if (wd.TryGetValue("width", out v) && v is double d2) wc.wheel.width = (float)d2;
                        if (wd.TryGetValue("mass", out v) && v is double d3) wc.wheel.mass = (float)d3;
                        if (wd.TryGetValue("springMaxLength", out v) && v is double d4) wc.spring.maxLength = (float)d4;
                        if (wd.TryGetValue("springMaxForce", out v) && v is double d5) wc.spring.maxForce = (float)d5;
                    }
                }
            }

            private void Collider(int i, GameObject owner)
            {
                var n = _m.Nodes[i];
                var ex = n.Extras;
                string shape = ex.TryGetValue("shape", out var sv) ? sv as string : "box";
                bool trigger = ex.TryGetValue("trigger", out var tv) && tv is bool tb && tb;
                var c = Mapped<Collider>(ex);
                if (c == null && FromGame(ex) && !Scratch) { _skipped++; return; }
                var center = new Vector3(n.T[0], n.T[1], n.T[2]);
                var size = new Vector3(n.S[0], n.S[1], n.S[2]);
                bool rotated = Quaternion.Angle(Quaternion.identity, new Quaternion(n.R[0], n.R[1], n.R[2], n.R[3])) > 0.1f;

                if (c == null)
                {
                    // a collider drawn in Blender: rotated ones get their own child object
                    var host = owner;
                    if (rotated)
                    {
                        host = new GameObject(n.Name ?? "collider");
                        host.transform.SetParent(owner.transform, false);
                        host.transform.localPosition = center;
                        host.transform.localRotation = new Quaternion(n.R[0], n.R[1], n.R[2], n.R[3]);
                        host.layer = owner.layer;
                        Local(host, PathOf(i));
                        Local(host.transform, PathOf(i) + "#transform");
                        center = Vector3.zero;
                    }
                    switch (shape)
                    {
                        case "sphere": c = host.AddComponent<SphereCollider>(); break;
                        case "capsule": c = host.AddComponent<CapsuleCollider>(); break;
                        case "mesh": c = host.AddComponent<MeshCollider>(); break;
                        default: c = host.AddComponent<BoxCollider>(); break;
                    }
                    Local(c, PathOf(i) + "#collider");
                    _collidersAdded++;
                }
                else _collidersMapped++;
                _seen.Add(c);

                switch (c)
                {
                    case BoxCollider b: b.center = center; b.size = size; break;
                    case SphereCollider s: s.center = center; s.radius = Mathf.Max(size.x, Mathf.Max(size.y, size.z)); break;
                    case CapsuleCollider cc:
                    {
                        int dir = ex.TryGetValue("direction", out var dv) && dv is double dd ? (int)dd : cc.direction;
                        cc.direction = dir;
                        cc.center = center;
                        cc.height = size[dir];
                        cc.radius = Mathf.Max(size[(dir + 1) % 3], size[(dir + 2) % 3]) / 2f;
                        break;
                    }
                    case MeshCollider mc:
                        if (n.Mesh >= 0) mc.sharedMesh = Builder.Mesh(n.Mesh);
                        mc.convex = ex.TryGetValue("convex", out var cv) && cv is bool cb && cb;
                        break;
                }
                c.isTrigger = trigger;
            }

            // donor objects the template no longer has: stop drawing / colliding, keep their logic
            public void Finish(GameObject root)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (_seen.Contains(t.gameObject)) continue;
                    var mr = t.GetComponent<MeshRenderer>();
                    if (mr != null && mr.enabled) { mr.enabled = false; _hidden++; }
                }
                foreach (var c in root.GetComponentsInChildren<Collider>(true))
                {
                    if (_seen.Contains(c) || c is WheelCollider || !_byId.ContainsValue(c)) continue;
                    if (c.enabled) { c.enabled = false; _disabled++; }
                }
            }

            // a stable local id for a new object, so saves map it the same way every session
            private void Local(UnityEngine.Object o, string key)
            {
                long id = StableId(_prefabId + "/local" + key + "/" + Occurrence("local" + key));
                _es3.localRefs[o] = id;
            }
        }

        // ---------------------------------------------------------------- spawning

        // Spawns a copy of a template the way the game's spawners do: a unique item name from the game's
        // counter, and an entry in the save lists (cars or items) so the copy is saved with the game.
        // Returns the copy, or null when the template is not ready (no scene with a save manager yet).
        public static GameObject Spawn(Info t, Vector3 position, Quaternion rotation)
        {
            if (t == null || t.Prefab == null) return null;
            var staging = new GameObject(Plugin.NAME + " staging");
            staging.SetActive(false);
            try
            {
                var go = UnityEngine.Object.Instantiate(t.Prefab, position, rotation, staging.transform);
                int n = NextNameId();
                go.name = t.Id + "(Clone)" + (n < 0 ? "" : n.ToString());
                bool listed = AddToList(go, t.IsVehicle ? "ArrayList_Cars" : "ArrayList_Items");
                go.transform.SetParent(null, true);
                Plugin.Verbose($"custom prefab: spawned {go.name} at {position}, " + (listed ? "saved with the game" : "NOT saved (save list not found)"));
                return go;
            }
            finally { UnityEngine.Object.Destroy(staging); }
        }

        // a game item spawned as the game's spawners spawn one (its name numbered, saved with the game's items)
        public static GameObject SpawnItem(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (prefab == null) return null;
            var staging = new GameObject(Plugin.NAME + " staging");
            staging.SetActive(false);
            try
            {
                var go = UnityEngine.Object.Instantiate(prefab, position, rotation, staging.transform);
                int n = NextNameId();
                go.name = prefab.name + "(Clone)" + (n < 0 ? "" : n.ToString());
                AddToList(go, "ArrayList_Items");
                go.transform.SetParent(null, true);
                return go;
            }
            finally { UnityEngine.Object.Destroy(staging); }
        }

        private static int NextNameId()
        {
            var holder = GameObject.Find("itemNameID");
            var fsm = holder != null ? holder.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "itemNameID") : null;
            var v = fsm != null ? fsm.FsmVariables.GetFsmInt("intName") : null;
            if (v == null) return -1;
            v.Value += 1;
            return v.Value;
        }

        // out of the save lists, before the copy is destroyed
        internal static void Unlist(GameObject go)
        {
            var holder = GameObject.Find("NewGO_ArrayList");
            if (holder == null) return;
            foreach (var list in holder.GetComponents<PlayMakerArrayListProxy>())
                while (list.arrayList != null && list.arrayList.Contains(go)) list.arrayList.Remove(go);
        }

        private static bool AddToList(GameObject go, string reference)
        {
            var holder = GameObject.Find("NewGO_ArrayList");
            if (holder == null) return false;
            var list = holder.GetComponents<PlayMakerArrayListProxy>().FirstOrDefault(p => p.referenceName == reference);
            if (list == null) return false;
            list.arrayList.Add(go);
            return true;
        }

        // 64-bit FNV-1a, kept positive like ES3's own ids
        public static long StableId(string s)
        {
            ulong h = 14695981039346656037;
            foreach (char c in s) { h ^= c; h *= 1099511628211; }
            return (long)(h & 0x7FFFFFFFFFFFFFFF);
        }
    }
}

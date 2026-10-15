using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ES3Internal;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace SigmaMod
{
    // Export all: every game prefab Easy Save knows (vehicles, parts, items) as a .glb template in
    // BepInEx/SigmaMod-export/, one per frame, as <name>.glb (<name>-<prefab id>.glb when two share a
    // name). In a game, the nearest live vehicle, with its fitted parts, is also written as
    // <name>-live.glb. Textures go once to textures/ and are shared by all files.
    internal static class ModelExport
    {
        public static string Dir => Path.Combine(BepInEx.Paths.BepInExRootPath, Plugin.NAME + "-export");
        public static bool Busy { get; private set; }

        public static IEnumerator ExportAll()
        {
            if (Busy) yield break;
            Busy = true;
            try
            {
                Directory.CreateDirectory(Dir);
                var store = new GltfUnity.TextureStore(Dir);
                var jobs = new System.Collections.Generic.List<(GameObject go, string name)>();
                var cam = Camera.main;
                var live = Resources.FindObjectsOfTypeAll<VehicleController>().Where(v => v.gameObject.scene.IsValid() && v.gameObject.activeInHierarchy).ToArray();
                if (live.Length > 0 && cam != null)
                {
                    var near = live.OrderBy(v => (v.transform.position - cam.transform.position).sqrMagnitude).First();
                    jobs.Add((near.gameObject, Clean(near.name) + "-live"));
                }

                // game prefabs, not the templates this mod registers
                var mgr = ES3ReferenceMgrBase.Current;
                var prefabs = mgr != null
                    ? mgr.prefabs.Where(p => p != null && CustomPrefabs.All.All(t => t.Prefab != p.gameObject)).Select(p => p.gameObject)
                    : Resources.FindObjectsOfTypeAll<VehicleController>().Where(v => !v.gameObject.scene.IsValid()).Select(v => v.gameObject);
                var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var go in prefabs.Distinct().OrderBy(g => g.name))
                {
                    string name = Clean(go.name);
                    if (!names.Add(name)) name += "-" + (go.GetComponent<ES3Prefab>()?.prefabId.ToString() ?? go.GetInstanceID().ToString());
                    jobs.Add((go: go, name: name));
                }
                Plugin.Log.LogInfo($"export: {jobs.Count} objects to {Dir}" + (mgr == null ? " (vehicles only: no save manager in this scene)" : ""));

                var total = Stopwatch.StartNew();
                foreach (var (go, name) in jobs)
                {
                    if (go != null) Write(go, name, store);
                    yield return null;
                }
                Plugin.Log.LogInfo($"export: done in {total.ElapsedMilliseconds} ms, textures written {store.Written}, reused {store.Reused}");
            }
            finally { Busy = false; }
        }

        public static void Write(GameObject go, string name, GltfUnity.TextureStore store) => Write(go, name, store, Dir, null);

        // rootExtras: properties added to the exported root (a place's poi_in)
        public static void Write(GameObject go, string name, GltfUnity.TextureStore store, string dir, Dictionary<string, object> rootExtras)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var stats = new GltfUnity.ExportStats();
                var model = GltfUnity.Export(go, stats, store);
                if (rootExtras != null)
                    foreach (int r in model.Roots)
                    {
                        if (model.Nodes[r].Extras == null) model.Nodes[r].Extras = new Dictionary<string, object>();
                        foreach (var kv in rootExtras) model.Nodes[r].Extras[kv.Key] = kv.Value;
                    }
                string path = Path.Combine(dir, name + ".glb");
                GltfWriter.Save(model, path);
                int fsms = WriteFsms(go, Path.Combine(dir, name + ".fsm.json"));
                Plugin.Log.LogInfo($"export: {name}.glb: {stats}, {fsms} FSMs, in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e) { Plugin.Log.LogError("export " + name + " failed: " + e); }
        }

        // ---- the places the terrain generator puts in the world (Tools.ExportPlaces)

        public static string PlacesDir => Path.Combine(BepInEx.Paths.BepInExRootPath, Plugin.NAME + "-export-places");

        // Every prefab the world's MapMagic graph places (camps, wrecks, caves, buildings, rocks, plants, sandstorms),
        // as .glb templates and .fsm.json, like Export All, to BepInEx/SigmaMod-export-places. Each root gets poi_in:
        // the objects outputs (graph node ids) that place it, as a POI's "in" takes them. outputs.txt lists every
        // output with what it places. Mesh geometry the game doesn't keep readable comes out as boxes: tools/fill.sh.
        public static IEnumerator ExportPlaces()
        {
            if (Busy) yield break;
            Busy = true;
            try
            {
                var mm = Resources.FindObjectsOfTypeAll<MapMagic.Core.MapMagicObject>().FirstOrDefault(m => m.gameObject.scene.IsValid());
                if (mm == null || mm.graph == null) { Plugin.Log.LogWarning("export places: no MapMagic world in this scene (load a save first)"); yield break; }
                Directory.CreateDirectory(PlacesDir);
                var store = new GltfUnity.TextureStore(PlacesDir);
                var outputsOf = new Dictionary<GameObject, List<ulong>>();
                var index = new System.Text.StringBuilder();
                index.Append("Objects outputs of the world's terrain graph '").Append(mm.graph.name).Append("' (graph node id: what it places)\n\n");
                var poiPrefabs = new HashSet<GameObject>(CustomPrefabs.All.Where(t => t.Prefab != null).Select(t => t.Prefab));
                foreach (var output in mm.graph.generators.OfType<MapMagic.Nodes.ObjectsGenerators.ObjectsOutput>())
                {
                    var prefabs = System.Linq.Enumerable.Where(output.prefabs, p => p != null && !poiPrefabs.Contains(p)).ToList();
                    if (prefabs.Count == 0) continue;   // ours (POIs), or empty
                    index.Append(output.id).Append(output.enabled ? "" : " (off)").Append(": ").Append(string.Join(", ", prefabs.Select(p => p.name).ToArray())).Append('\n');
                    foreach (var p in prefabs)
                    {
                        if (!outputsOf.TryGetValue(p, out var list)) outputsOf[p] = list = new List<ulong>();
                        if (!list.Contains(output.id)) list.Add(output.id);
                    }
                }
                File.WriteAllText(Path.Combine(PlacesDir, "outputs.txt"), index.ToString());
                Plugin.Log.LogInfo($"export places: {outputsOf.Count} prefabs from the world's terrain graph to {PlacesDir}");
                var total = Stopwatch.StartNew();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in outputsOf.OrderBy(k => k.Key.name, StringComparer.Ordinal))
                {
                    var go = kv.Key;
                    string name = Clean(go.name);
                    if (!names.Add(name)) name += "-" + go.GetInstanceID();
                    var extras = new Dictionary<string, object> { { "poi_in", string.Join(", ", kv.Value.Select(id => id.ToString()).ToArray()) } };
                    if (go != null) Write(go, name, store, PlacesDir, extras);
                    yield return null;
                }
                Plugin.Log.LogInfo($"export places: done in {total.ElapsedMilliseconds} ms, textures written {store.Written}, reused {store.Reused}");
            }
            finally { Busy = false; }
        }

        // ---- the game's materials, to use in Blender (Tools.ExportMaterials)

        public static string MaterialsDir => Path.Combine(BepInEx.Paths.BepInExRootPath, Plugin.NAME + "-export-materials");

        // materials.glb in BepInEx/SigmaMod-export-materials: a flat swatch (1 m square, named after it) for every
        // loaded material with an Easy Save id, so a template using it gets the game's own material back, and one for
        // every terrain layer (extras.terrainLayer; sized to the layer's tile, so the texture shows at its real
        // scale). Append them in Blender and assign them. materials.txt lists them.
        public static IEnumerator ExportMaterials()
        {
            if (Busy) yield break;
            Busy = true;
            GameObject root = null;
            Mesh quad = null;
            var made = new List<Material>();
            try
            {
                var mgr = ES3ReferenceMgrBase.Current;
                if (mgr == null) { Plugin.Log.LogWarning("export materials: no save manager in this scene (load a save first)"); yield break; }
                Directory.CreateDirectory(MaterialsDir);
                var store = new GltfUnity.TextureStore(MaterialsDir);
                var materials = Resources.FindObjectsOfTypeAll<Material>()
                    .Where(m => m != null && m.shader != null && mgr.Get(m) != -1)
                    .GroupBy(m => mgr.Get(m)).Select(g => g.First())
                    .OrderBy(m => m.name, StringComparer.OrdinalIgnoreCase).ToList();
                var layers = Resources.FindObjectsOfTypeAll<TerrainLayer>()
                    .Where(l => l != null && l.diffuseTexture != null)
                    .GroupBy(l => l.name).Select(g => g.First())
                    .OrderBy(l => l.name, StringComparer.OrdinalIgnoreCase).ToList();

                quad = new Mesh { name = "swatch" };
                quad.vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 0, -0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(-0.5f, 0, 0.5f) };
                quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
                quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };   // facing up
                quad.RecalculateNormals();
                quad.RecalculateBounds();

                root = new GameObject("game materials");
                var index = new System.Text.StringBuilder("Game materials (es3global id, shader) and terrain layers (tile size) in materials.glb\n\n");
                int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(materials.Count)));
                for (int i = 0; i < materials.Count; i++)
                {
                    var m = materials[i];
                    Swatch(root.transform, m.name, m, quad, new Vector3(i % cols * 1.5f, 0, -(i / cols) * 1.5f), Vector3.one);
                    index.Append(m.name).Append("  ").Append(mgr.Get(m)).Append("  ").Append(m.shader.name).Append('\n');
                }
                float z = -(materials.Count / cols + 2) * 1.5f, x = 0;
                index.Append("\nTerrain layers:\n");
                foreach (var l in layers)
                {
                    var m = new Material(GltfUnity.StandardShader()) { name = "terrain " + l.name, mainTexture = l.diffuseTexture };
                    if (l.normalMapTexture != null) { m.SetTexture("_BumpMap", l.normalMapTexture); m.SetFloat("_BumpScale", l.normalScale); }
                    m.SetFloat("_Metallic", l.metallic);
                    m.SetFloat("_Glossiness", l.smoothness);
                    made.Add(m);
                    GltfUnity.ExportExtras[m] = new Dictionary<string, object> { ["terrainLayer"] = l.name, ["tileSize"] = new[] { l.tileSize.x, l.tileSize.y } };
                    var size = new Vector3(Mathf.Max(0.5f, l.tileSize.x), 1f, Mathf.Max(0.5f, l.tileSize.y));
                    Swatch(root.transform, "terrain " + l.name, m, quad, new Vector3(x + size.x / 2f, 0, z - size.z / 2f), size);
                    x += size.x + 1.5f;
                    index.Append(l.name).Append("  tile ").Append(l.tileSize.x).Append(" x ").Append(l.tileSize.y).Append(" m\n");
                }
                File.WriteAllText(Path.Combine(MaterialsDir, "materials.txt"), index.ToString());
                Plugin.Log.LogInfo($"export materials: {materials.Count} materials, {layers.Count} terrain layers to {MaterialsDir}");
                Write(root, "materials", store, MaterialsDir, null);
                Plugin.Log.LogInfo($"export materials: textures written {store.Written}, reused {store.Reused}");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (quad != null) UnityEngine.Object.DestroyImmediate(quad);
                foreach (var m in made) { GltfUnity.ExportExtras.Remove(m); UnityEngine.Object.DestroyImmediate(m); }
                Busy = false;
            }
        }

        private static void Swatch(Transform parent, string name, Material m, Mesh quad, Vector3 at, Vector3 scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            go.AddComponent<MeshRenderer>().sharedMaterial = m;
        }

        // Every FSM of the object as FsmJson definitions, by object: { "<root or /path>": { "<FsmName>": {...} } }.
        // References inside the object are written as root and /paths, game assets as asset:<Type>/<name>,
        // anything else as ?<name> (outside the object: not loadable as it is).
        public static int WriteFsms(GameObject go, string path)
        {
            var root = go.transform;
            string Ref(UnityEngine.Object o)
            {
                var t = (o as GameObject)?.transform ?? (o as Component)?.transform;
                if (t != null && (t == root || t.IsChildOf(root)))
                {
                    string p = t == root ? "root" : "/" + PathBelow(root, t);
                    return o is GameObject || o is Transform ? p : p + ":" + o.GetType().Name;
                }
                if (t != null) return "?" + o.name;
                return "asset:" + o.GetType().Name + "/" + o.name;
            }
            var all = new System.Collections.Generic.Dictionary<string, object>();
            int n = 0;
            foreach (var pm in go.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                string key = pm.transform == root ? "root" : "/" + PathBelow(root, pm.transform);
                if (!all.TryGetValue(key, out var have)) all[key] = have = new System.Collections.Generic.Dictionary<string, object>();
                var byName = (System.Collections.Generic.Dictionary<string, object>)have;
                string fname = pm.FsmName;
                for (int k = 2; byName.ContainsKey(fname); k++) fname = pm.FsmName + "#" + k;
                try { byName[fname] = FsmJson.Export(pm, Ref); n++; }
                catch (Exception e) { byName[fname] = new System.Collections.Generic.Dictionary<string, object> { ["error"] = e.Message }; }
            }
            if (n > 0) File.WriteAllText(path, Json.Pretty(all));
            return n;
        }

        private static string PathBelow(Transform root, Transform t)
        {
            string p = t.name;
            while (t.parent != null && t.parent != root) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        private static string Clean(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace("(Clone)", "").Trim();
        }
    }
}

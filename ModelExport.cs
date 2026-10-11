using System;
using System.Collections;
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

        public static void Write(GameObject go, string name, GltfUnity.TextureStore store)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var stats = new GltfUnity.ExportStats();
                var model = GltfUnity.Export(go, stats, store);
                string path = Path.Combine(Dir, name + ".glb");
                GltfWriter.Save(model, path);
                int fsms = WriteFsms(go, Path.Combine(Dir, name + ".fsm.json"));
                Plugin.Log.LogInfo($"export: {name}.glb: {stats}, {fsms} FSMs, in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e) { Plugin.Log.LogError("export " + name + " failed: " + e); }
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

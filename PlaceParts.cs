using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ES3Internal;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SigmaMod
{
    // A POI's working parts are the game's own. An object copied out of a place export (Tools.ExportPlaces) that had
    // an FSM in the game (a spawner, a map icon, a codex trigger, a building's lights) is, in the POI, a copy of the
    // game's object itself, found by its Easy Save id: the export only holds its shape, the game holds what it does.
    // It goes where the copied object is, with what was added under it in Blender; what the game had under it comes
    // with the game's copy. An object the script makes a spot (spawns) stays a spot. The object that followed it in the game (a spawner's spawn points: the game's spawners take
    // their next sibling as their list of spots) follows it again, whatever order Blender put them in.
    //
    // Spawners keep what they have done with the save as the game's places do: the root is tagged SpawnerStatic, so
    // the game's save sends it Save and Load, and an FSM like a camp's saveItemVar stores each spawner's OnOff under
    // the POI's position.
    //
    // "one_of" in the script: of a set of spawners (by their Blender names) only one works in each POI, the others
    // are spent from the start. A list (as likely as each other), or name = weight, or a list of such sets:
    //   one_of = { "EnemySpawn_Wild_V1.007", "CritterSpawn" }
    // The pick is by the POI's place in the world, so the same POI picks the same way every time it is made.
    internal static class PlaceParts
    {
        public const string Spawner = "ItemSpawner", Spent = "OnOff", Save = "saveItemVar", SaveTag = "SpawnerStatic";

        public static void Build(GameObject root, CustomPrefabs.Info t, Dictionary<string, GameObject> named, ES3ReferenceMgrBase mgr,
                                 JsonValues ctx, Dictionary<string, object> spec, string where)
        {
            // export objects that were working parts in the game, and the game objects they were
            var extras = new Dictionary<GameObject, Dictionary<string, object>>();
            foreach (var kv in t.Es3.localRefs)
                if (kv.Key is GameObject g && g != root && t.NodeExtras.TryGetValue(kv.Value, out var ex)) extras[g] = ex;
            var parts = new Dictionary<GameObject, GameObject>();
            foreach (var kv in extras)
                // a spot of a template (spawns) is that, not the game spawner it was copied from
                if (HadFsm(kv.Value) && !kv.Value.ContainsKey(Places.PlaceKey) && Game(kv.Value, mgr) is GameObject src) parts[kv.Key] = src;
            // the outermost only: the game's copy brings what is below it
            foreach (var g in parts.Keys.ToList())
                for (var p = g.transform.parent; p != null && p != root.transform; p = p.parent)
                    if (parts.ContainsKey(p.gameObject)) { parts.Remove(g); break; }
            if (parts.Count == 0) return;

            var copies = new Dictionary<GameObject, GameObject>();   // copy -> the game's object
            foreach (var kv in parts)
            {
                var built = kv.Key;
                var src = kv.Value;
                var copy = UnityEngine.Object.Instantiate(src, built.transform.parent, false);
                copy.name = src.name;
                copy.transform.localPosition = built.transform.localPosition;
                copy.transform.localRotation = built.transform.localRotation;
                copy.transform.localScale = built.transform.localScale;
                copy.transform.SetSiblingIndex(built.transform.GetSiblingIndex());
                copy.SetActive(built.activeSelf);
                // what was put under it in Blender stays; what the export had of the game's own is in the copy
                foreach (Transform c in built.transform.Cast<Transform>().ToList())
                    if (extras.TryGetValue(c.gameObject, out var cex) && !cex.ContainsKey("es3global")) c.SetParent(copy.transform, false);
                foreach (var n in named.Where(x => x.Value == built).Select(x => x.Key).ToList()) named[n] = copy;
                Forget(built, t);
                UnityEngine.Object.DestroyImmediate(built);
                copies[copy] = src;
            }

            // what followed each in the game follows it again
            foreach (var kv in copies)
            {
                var s = kv.Value.transform;
                if (s.parent == null || s.GetSiblingIndex() + 1 >= s.parent.childCount) continue;
                var next = s.parent.GetChild(s.GetSiblingIndex() + 1).gameObject;
                long nid = mgr.Get(next);
                var follower = kv.Key.transform.parent.Cast<Transform>().Select(x => x.gameObject).FirstOrDefault(g =>
                    copies.TryGetValue(g, out var gs) ? gs == next
                    : nid != -1 && extras.TryGetValue(g, out var gex) && gex.TryGetValue("es3global", out var gv) && gv as string == nid.ToString(CultureInfo.InvariantCulture));
                if (follower != null) follower.transform.SetSiblingIndex(kv.Key.transform.GetSiblingIndex() + 1);
            }
            Plugin.Log.LogInfo($"{where}: the game's working parts: {string.Join(", ", copies.Values.Select(PathOf).ToArray())}");

            var spawners = copies.Keys.Where(g => OnOff(g) != null).ToList();
            if (spawners.Count == 0) return;
            SaveSpawners(root, spawners, ctx, spec, where);
            OneOf(root, named, spec, where);
        }

        private static bool HadFsm(Dictionary<string, object> ex) =>
            ex.TryGetValue("components", out var cv) && cv is List<object> l && l.OfType<string>().Any(c => c.StartsWith("PlayMakerFSM:", StringComparison.Ordinal));

        private static GameObject Game(Dictionary<string, object> ex, ES3ReferenceMgrBase mgr) =>
            mgr != null && ex.TryGetValue("es3global", out var v) && v is string s && long.TryParse(s, out long id) ? mgr.Get(id, true) as GameObject : null;

        // the built object and what is below it leave the template's ids
        private static void Forget(GameObject built, CustomPrefabs.Info t)
        {
            var gone = new HashSet<UnityEngine.Object>(built.GetComponentsInChildren<Component>(true).Cast<UnityEngine.Object>()
                .Concat(built.GetComponentsInChildren<Transform>(true).Select(x => (UnityEngine.Object)x.gameObject)));
            foreach (var k in t.Es3.localRefs.Keys.Where(gone.Contains).ToList())
            {
                t.NodeExtras.Remove(t.Es3.localRefs[k]);
                t.Es3.localRefs.Remove(k);
            }
        }

        internal static FsmInt OnOff(GameObject g)
        {
            var fsm = g.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == Spawner);
            var v = fsm != null ? fsm.FsmVariables.FindFsmInt(Spent) : null;
            return v;
        }

        private static string PathOf(GameObject go)
        {
            var t = go.transform;
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        // the game's camp save: on Save each spawner's OnOff into this FSM's variables, saved under the root's position;
        // on Load back. A POI the save doesn't have yet: LoadAll finds no key and stops there.
        private static void SaveSpawners(GameObject root, List<GameObject> spawners, JsonValues ctx, Dictionary<string, object> spec, string where)
        {
            if (spec != null && spec.TryGetValue("playmaker", out var pv) && pv is Dictionary<string, object> pm && pm.ContainsKey(Save))
            {
                Plugin.Log.LogInfo($"{where}: the script has its own {Save}; its spawners are saved by that");
                return;
            }
            root.tag = SaveTag;
            var ints = new Dictionary<string, object>();
            var objs = new Dictionary<string, object>();
            var save = new List<object> { Act("GetPosition", "gameObject", "owner", "vector", "{pos}", "space", "World") };
            var load = new List<object>
            {
                Act("GetPosition", "gameObject", "owner", "vector", "{pos}", "space", "World"),
                Act("LoadAll", "key", "{pos}", "loadFsmVariables", true, "loadGlobalVariables", false, "path", "SaveFile.es3"),
            };
            var used = new HashSet<string>();
            int n = 0;
            foreach (var s in spawners)
            {
                // named by where it is, so a save keeps meaning the same spawner when the POI changes around it
                string key = PathBelow(root.transform, s.transform);
                while (!used.Add(key)) key += "+";
                string v = "OnOff " + key, o = "spawner " + key, refName = "sigma spawner " + n++;   // a reference is a plain name: no ':' or '/'
                ints[v] = 0;
                objs[o] = refName;
                ctx.AddNode?.Invoke(refName, s);
                save.Add(Act("GetFsmInt", "gameObject", "{" + o + "}", "fsmName", Spawner, "variableName", Spent, "storeValue", "{" + v + "}"));
                load.Add(Act("SetFsmInt", "gameObject", "{" + o + "}", "fsmName", Spawner, "variableName", Spent, "setValue", "{" + v + "}"));
            }
            save.Add(Act("SaveAll", "key", "{pos}", "saveFsmVariables", true, "saveGlobalVariables", false, "path", "SaveFile.es3"));
            var def = new Dictionary<string, object>
            {
                ["variables"] = new Dictionary<string, object>
                {
                    ["int"] = ints, ["gameObject"] = objs,
                    ["vector3"] = new Dictionary<string, object> { ["pos"] = new List<object> { 0.0, 0.0, 0.0 } },
                },
                ["globalEvents"] = new List<object> { "Save", "Load" },
                ["global"] = new Dictionary<string, object> { ["Save"] = "save", ["Load"] = "load" },
                ["start"] = "off",
                ["states"] = new Dictionary<string, object>
                {
                    ["off"] = new Dictionary<string, object>(),
                    ["save"] = new Dictionary<string, object> { ["actions"] = save, ["transitions"] = new Dictionary<string, object> { ["FINISHED"] = "off" } },
                    ["load"] = new Dictionary<string, object> { ["actions"] = load, ["transitions"] = new Dictionary<string, object> { ["FINISHED"] = "off" } },
                },
            };
            FsmJson.Build(root, Save, def, ctx, where);
        }

        private static Dictionary<string, object> Act(string type, params object[] kv)
        {
            var f = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) f[(string)kv[i]] = kv[i + 1];
            return new Dictionary<string, object> { [type] = f };
        }

        private static string PathBelow(Transform root, Transform t)
        {
            string p = t.name;
            while (t.parent != null && t.parent != root) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        private static void OneOf(GameObject root, Dictionary<string, GameObject> named, Dictionary<string, object> spec, string where)
        {
            if (spec == null || !spec.TryGetValue("one_of", out var ov) || ov == null) return;
            var sets = ov is List<object> l && l.Count > 0 && l.All(x => x is List<object> || x is Dictionary<string, object>) ? l : new List<object> { ov };
            var members = new List<GameObject>();
            var weights = new List<float>();
            var groups = new List<int>();
            for (int gi = 0; gi < sets.Count; gi++)
            {
                IEnumerable<KeyValuePair<string, float>> set =
                    sets[gi] is Dictionary<string, object> d ? d.Select(x => new KeyValuePair<string, float>(x.Key, Convert.ToSingle(x.Value, CultureInfo.InvariantCulture)))
                    : sets[gi] is List<object> names ? names.Select(x => new KeyValuePair<string, float>(Convert.ToString(x, CultureInfo.InvariantCulture), 1f))
                    : Enumerable.Empty<KeyValuePair<string, float>>();
                foreach (var m in set)
                {
                    if (!named.TryGetValue(m.Key, out var g) || g == null || OnOff(g) == null)
                    {
                        Plugin.Log.LogWarning($"{where}: one_of: '{m.Key}' is not one of its spawners (a Blender name)");
                        continue;
                    }
                    members.Add(g); weights.Add(Mathf.Max(0f, m.Value)); groups.Add(gi);
                }
            }
            if (members.Count == 0) return;
            var r = root.GetComponent<PlaceRoll>() ?? root.AddComponent<PlaceRoll>();
            r.spawners = members.ToArray();
            r.weights = weights.ToArray();
            r.groups = groups.ToArray();
        }
    }

    // A POI's one_of, on each copy: by its place in the world, one spawner of each set works and the others are spent
    public sealed class PlaceRoll : MonoBehaviour
    {
        public GameObject[] spawners;
        public float[] weights;
        public int[] groups;

        private void Start()
        {
            if (spawners == null) return;
            var p = transform.position;
            int seed = Mathf.RoundToInt(p.x * 10f) * 73856093 ^ Mathf.RoundToInt(p.z * 10f) * 19349663;
            foreach (int g in groups.Distinct())
            {
                var idx = Enumerable.Range(0, spawners.Length).Where(i => groups[i] == g).ToList();
                float sum = idx.Sum(i => weights[i]);
                float x = (float)new System.Random(seed + g * 7919).NextDouble() * sum;
                int pick = idx[idx.Count - 1];
                foreach (int i in idx) { if (x < weights[i]) { pick = i; break; } x -= weights[i]; }
                foreach (int i in idx)
                {
                    if (i == pick || spawners[i] == null) continue;
                    var v = PlaceParts.OnOff(spawners[i]);
                    if (v != null && v.Value == 0) v.Value = 1;
                }
            }
        }
    }
}

namespace SigmaMod
{
    // VerboseLog: where a placed POI ended up against the terrain (its root, and each top-level part's bottom, top and
    // the ground under its middle), a few seconds after it appears, once the ground under it is final
    public sealed class PoiProbe : MonoBehaviour
    {
        private System.Collections.IEnumerator Start()
        {
            if (!Plugin.VerboseLogEntry.Value) yield break;
            yield return new WaitForSeconds(5f);
            var sb = new System.Text.StringBuilder();
            var p = transform.position;
            sb.Append($"POI probe {name}: root at ({p.x:0.0}, {p.y:0.00}, {p.z:0.0}) turned {transform.eulerAngles.y:0.0}, tilted {transform.eulerAngles.x:0.00}/{transform.eulerAngles.z:0.00}; ground there {Ground(p):0.00}");
            foreach (Transform c in transform)
            {
                var rs = c.GetComponentsInChildren<Renderer>(true);
                if (rs.Length == 0) continue;
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                sb.Append($"\n  {c.name}: y {b.min.y:0.00} .. {b.max.y:0.00}, ground under its middle {Ground(b.center):0.00}, at its ends {Ground(b.center - c.right * b.extents.magnitude * 0.6f):0.00} / {Ground(b.center + c.right * b.extents.magnitude * 0.6f):0.00}");
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static float Ground(Vector3 at)
        {
            foreach (var t in Terrain.activeTerrains)
            {
                var o = t.GetPosition(); var s = t.terrainData.size;
                if (at.x >= o.x && at.x <= o.x + s.x && at.z >= o.z && at.z <= o.z + s.z) return t.SampleHeight(at) + o.y;
            }
            return float.NaN;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ES3Internal;
using MapMagic.Core;
using MapMagic.Nodes;
using MapMagic.Nodes.ObjectsGenerators;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SigmaMod
{
    // Templates put at spots in the game's own places (camps, buildings, caves, wrecks) and in POI templates. A place
    // with a spot spawns one copy of the template there once the game is playing (a save loaded, or a new world
    // made) and the player is near, unless a copy is already there. The copy is an object of its own, spawned and
    // saved as the game's spawners do a car or an item, so what is fitted to it or put in it is kept, and the next
    // time the place is made it finds the copy and spawns nothing. A copy taken away is replaced.
    //
    //   "my_generator": { "places": { "Camp_13": { "at": [4.2, -7.5, 0], "turn": 90 },
    //                                 "Camp_7":  [ { "at": [...] }, { "at": [...], "turn": 180 } ] } }
    //
    // keyed by the game's place prefab (the names Tools.ExportPlaces gives its files) or a place built into the map
    // (a scene object such as the starter camp, Starter_Area), "at" where it goes in the place's export as Blender
    // shows it (x, y, z; z up), "turn" its turn about Blender's z, degrees. A spawned copy settles onto whatever is
    // under the spot (within a few metres; "ground": false keeps it where it is put). In a POI template: an empty
    // with the custom property sigma_place = "<template sigma_id>" is a spot, posed as the empty is (in a script:
    // nodes = { ["<object>"] = { spawns = "<template sigma_id>" } }).
    //
    // "swap": { "<object in the place>": "<place>/<object>" } on a spot: the place's object (a path below the place)
    // is switched off and a copy of the other place's object put where it was (a fire-in-a-can light swapped for an
    // electric lamp).
    //
    // "replaces": [ "<mesh id>", ... ]: the game's own stand-ins for this template (a decorative generator), by their
    // mesh's Easy Save id. In every place they are in, each is switched off (and with it its sound and collider)
    // and a copy of the template spawns where it stood.
    internal static class Places
    {
        public const string PlaceKey = "sigma_place";
        private static readonly List<PlaceSpots> Added = new List<PlaceSpots>();

        // on the main thread, after the templates are loaded (with the world's graph, for the game's places)
        public static void Rebuild()
        {
            foreach (var old in Added) if (old != null) UnityEngine.Object.DestroyImmediate(old, true);
            Added.Clear();

            var byPlace = new Dictionary<string, List<Spot>>(StringComparer.Ordinal);
            var replaces = new Dictionary<long, string>();   // stand-in mesh id -> template
            foreach (var t in CustomPrefabs.All)
            {
                if (t.Prefab == null) continue;
                // spots in this template (a POI's)
                var own = new List<Spot>();
                if (t.Es3 != null)
                    foreach (var kv in t.Es3.localRefs)
                    {
                        if (!(kv.Key is GameObject go) || go == t.Prefab || !t.NodeExtras.TryGetValue(kv.Value, out var ex)) continue;
                        if (!ex.TryGetValue(PlaceKey, out var pv) || !(pv is string id) || id.Trim().Length == 0) continue;
                        var local = t.Prefab.transform.InverseTransformPoint(go.transform.position);
                        var fwd = t.Prefab.transform.InverseTransformDirection(go.transform.forward);
                        own.Add(new Spot { Id = id.Trim(), At = local, Turn = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg, Ground = true });
                    }
                if (own.Count > 0) Attach(t.Prefab, own, new Dictionary<string, GameObject>(), "template '" + t.Id + "'");

                if (t.Spec != null && t.Spec.TryGetValue("replaces", out var rv) && rv is List<object> rl)
                    foreach (var r in rl)
                        if (long.TryParse(Convert.ToString(r, CultureInfo.InvariantCulture), out long mid)) replaces[mid] = t.Id;
                        else Plugin.Log.LogWarning($"template '{t.Id}': replaces: '{r}' is not a mesh id");

                // spots in the game's places
                if (t.Spec == null || !t.Spec.TryGetValue("places", out var pl) || !(pl is Dictionary<string, object> places)) continue;
                foreach (var kv in places)
                {
                    var spots = kv.Value is List<object> list ? list : new List<object> { kv.Value };
                    foreach (var s in spots)
                    {
                        if (!(s is Dictionary<string, object> spot) || !(spot.TryGetValue("at", out var av) && av is List<object> at && at.Count == 3))
                        {
                            Plugin.Log.LogWarning($"template '{t.Id}': places.{kv.Key}: a spot needs \"at\": [x, y, z]");
                            continue;
                        }
                        float bx = F(at[0]), by = F(at[1]), bz = F(at[2]);
                        float turn = spot.TryGetValue("turn", out var tv) ? F(tv) : 0f;
                        bool ground = !(spot.TryGetValue("ground", out var gv) && gv is bool gb && !gb);
                        var swaps = spot.TryGetValue("swap", out var sv) ? sv as Dictionary<string, object> : null;
                        // Blender (z up) to the game: x mirrored, its z is up, its -y is forward; a turn about Blender's z is the other way about Unity's y
                        if (!byPlace.TryGetValue(kv.Key, out var l)) byPlace[kv.Key] = l = new List<Spot>();
                        l.Add(new Spot { Id = t.Id, At = new Vector3(-bx, bz, -by), Turn = -turn, Ground = ground,
                                         Swaps = swaps?.ToDictionary(x => x.Key, x => Convert.ToString(x.Value, CultureInfo.InvariantCulture)) });
                    }
                }
            }
            if (byPlace.Count == 0 && replaces.Count == 0) return;

            // the world's places (the terrain generator's prefabs) and the ones built into the map (scene roots)
            var mm = Resources.FindObjectsOfTypeAll<MapMagicObject>().FirstOrDefault(m => m.gameObject.scene.IsValid());
            var prefabs = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            if (mm != null && mm.graph != null)
                foreach (var gen in Poi.AllGenerators(mm.graph, new HashSet<Graph>()))
                    if (gen is ObjectsOutput o && o.prefabs != null)
                        foreach (var p in o.prefabs) if (p != null && !prefabs.ContainsKey(p.name)) prefabs[p.name] = p;
            var generated = prefabs.Values.ToList();
            var scene = SceneManager.GetActiveScene();
            if (scene.IsValid())
                foreach (var r in scene.GetRootGameObjects())
                    if (r != null && !prefabs.ContainsKey(r.name)) prefabs[r.name] = r;

            // stand-ins: in every place, each object with a replaced mesh becomes a spot of its template
            if (replaces.Count > 0)
            {
                var mgr = ES3ReferenceMgrBase.Current;
                foreach (var place in generated)
                {
                    foreach (var mf in place.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var mesh = mf.sharedMesh;
                        long mid = mesh != null && mgr != null ? mgr.Get(mesh) : -1;
                        if (mid == -1 || !replaces.TryGetValue(mid, out var tid)) continue;
                        var tr = mf.transform;
                        var fwd = place.transform.InverseTransformDirection(tr.forward);
                        if (!byPlace.TryGetValue(place.name, out var l)) byPlace[place.name] = l = new List<Spot>();
                        l.Add(new Spot { Id = tid, At = place.transform.InverseTransformPoint(tr.position), Turn = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg,
                                         Ground = true, Hide = PathIn(place.transform, tr) });
                    }
                }
            }

            foreach (var kv in byPlace)
            {
                if (!prefabs.TryGetValue(kv.Key, out var place))
                {
                    Plugin.Log.LogWarning($"places: no place '{kv.Key}' in the world's graph or the map (the names are Tools.ExportPlaces' file names); its spots ({string.Join(", ", kv.Value.Select(x => x.Id).Distinct().ToArray())}) are not placed");
                    continue;
                }
                Attach(place, kv.Value, prefabs, "place '" + kv.Key + "'");
            }
        }

        internal sealed class Spot
        {
            public string Id;
            public Vector3 At;
            public float Turn;
            public bool Ground;
            public string Hide;                          // a stand-in to switch off, below the place
            public Dictionary<string, string> Swaps;     // object below the place -> "<place>/<object>" to copy in
        }

        private static void Attach(GameObject place, List<Spot> spots, Dictionary<string, GameObject> places, string where)
        {
            var hide = new List<string>();
            var swapAt = new List<string>();
            var swapFrom = new List<GameObject>();
            foreach (var s in spots)
            {
                if (s.Hide != null) hide.Add(s.Hide);
                if (s.Swaps == null) continue;
                foreach (var kv in s.Swaps)
                {
                    int slash = kv.Value.IndexOf('/');
                    var from = slash > 0 && places.TryGetValue(kv.Value.Substring(0, slash), out var src) ? src.transform.Find(kv.Value.Substring(slash + 1)) : null;
                    if (from == null) { Plugin.Log.LogWarning($"places: {where}: swap: no '{kv.Value}' to copy"); continue; }
                    if (place.transform.Find(kv.Key) == null) { Plugin.Log.LogWarning($"places: {where}: swap: no '{kv.Key}' in it"); continue; }
                    swapAt.Add(kv.Key); swapFrom.Add(from.gameObject);
                }
            }
            var c = place.AddComponent<PlaceSpots>();
            c.Templates = spots.Select(s => s.Id).ToArray();
            c.At = spots.Select(s => s.At).ToArray();
            c.Turn = spots.Select(s => s.Turn).ToArray();
            c.Ground = spots.Select(s => s.Ground).ToArray();
            c.Hide = hide.ToArray();
            c.SwapAt = swapAt.ToArray();
            c.SwapFrom = swapFrom.ToArray();
            c.Redress();   // a place in the map is awake already: it dressed before it had its lists
            Added.Add(c);
            Plugin.Log.LogInfo($"places: {where} spawns {string.Join(", ", spots.Select(s => s.Id).ToArray())}"
                + (hide.Count > 0 ? $", in place of {string.Join(", ", hide.ToArray())}" : "") + (swapAt.Count > 0 ? $"; swaps {string.Join(", ", swapAt.ToArray())}" : ""));
        }

        private static string PathIn(Transform root, Transform t)
        {
            string p = t.name;
            while (t.parent != null && t.parent != root) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        private static float F(object o) => Convert.ToSingle(o, CultureInfo.InvariantCulture);
    }

    // On a place: its spots. Plain arrays, so the place's copies get them too.
    internal sealed class PlaceSpots : MonoBehaviour
    {
        private const float Near = 300f;      // m: the player this near the place, it gets its things
        private const float Already = 3f;     // m: a copy this near the spot is the one it spawned before

        public string[] Templates = new string[0];
        public Vector3[] At = new Vector3[0];
        public float[] Turn = new float[0];
        public bool[] Ground = new bool[0];
        public string[] Hide = new string[0];
        public string[] SwapAt = new string[0];
        public GameObject[] SwapFrom = new GameObject[0];
        private bool _done, _dressed;
        private float _next;

        private void OnEnable() { _done = false; _next = Time.time + UnityEngine.Random.value; Dress(); }

        internal void Redress() { if (!gameObject.scene.IsValid()) return; _dressed = false; Dress(); }

        // stand-ins off, swaps made: once per copy of the place
        private void Dress()
        {
            if (_dressed) return;
            _dressed = true;
            foreach (var h in Hide)
            {
                var t = transform.Find(h);
                if (t != null) t.gameObject.SetActive(false);
            }
            for (int i = 0; i < SwapAt.Length && i < SwapFrom.Length; i++)
            {
                var t = transform.Find(SwapAt[i]);
                if (t == null || SwapFrom[i] == null || !t.gameObject.activeSelf) continue;
                var copy = Instantiate(SwapFrom[i], t.parent, false);
                copy.name = SwapFrom[i].name;
                copy.transform.localPosition = t.localPosition;
                copy.transform.localRotation = SwapFrom[i].transform.localRotation;
                copy.SetActive(true);
                t.gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            if (_done || Time.time < _next) return;
            _next = Time.time + 1f;
            if (!Playing()) return;
            var player = HutongGames.PlayMaker.FsmVariables.GlobalVariables.FindFsmGameObject("Player")?.Value;
            if (player == null || (player.transform.position - transform.position).sqrMagnitude > Near * Near) return;
            _done = true;
            for (int i = 0; i < Templates.Length; i++)
            {
                var t = CustomPrefabs.All.FirstOrDefault(x => x.Id == Templates[i]);
                if (t == null || t.Prefab == null) { Plugin.Log.LogWarning($"places: {name}: no template '{Templates[i]}'"); continue; }
                var pos = transform.TransformPoint(At[i]);
                if (i < Ground.Length && Ground[i]) pos = Settle(pos);
                if (There(t, pos)) continue;
                var go = CustomPrefabs.Spawn(t, pos, transform.rotation * Quaternion.Euler(0f, Turn[i], 0f));
                Plugin.Log.LogInfo($"places: {name} spawned {(go != null ? go.name : "nothing (no save lists yet)")}");
                if (go == null) _done = false;
            }
        }

        // onto what is under the spot: the first solid thing within a few metres below (a pad, a floor, the ground)
        private static Vector3 Settle(Vector3 pos)
        {
            const float above = 0.5f, depth = 4f;
            return Physics.Raycast(pos + Vector3.up * above, Vector3.down, out var hit, above + depth, ~0, QueryTriggerInteraction.Ignore) ? hit.point : pos;
        }

        // a copy of it already near the spot (spawned before and saved, or still here)
        private static bool There(CustomPrefabs.Info t, Vector3 pos)
        {
            var holder = GameObject.Find("NewGO_ArrayList");
            if (holder == null) return false;
            foreach (var list in holder.GetComponents<PlayMakerArrayListProxy>())
            {
                if (list.arrayList == null) continue;
                foreach (var o in list.arrayList)
                    if (o is GameObject g && g != null && (g.transform.position - pos).sqrMagnitude < Already * Already)
                    {
                        var es3 = g.GetComponent<ES3Prefab>();
                        if (es3 != null && es3.prefabId == t.PrefabId) return true;
                    }
            }
            return false;
        }

        // the game's save/load FSM has finished: a save loaded (with its spawned things) or a new world made
        private static PlayMakerFSM _saveLoad;
        internal static bool Playing()
        {
            if (_saveLoad == null)
            {
                var go = GameObject.Find("SaveLoadGame");
                _saveLoad = go != null ? go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "SaveLoadGame") : null;
                if (_saveLoad == null) return false;
            }
            return _saveLoad.Fsm != null && _saveLoad.Fsm.Initialized && _saveLoad.ActiveStateName == "isPlay";
        }
    }
}

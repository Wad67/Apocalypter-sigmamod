using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SigmaMod
{
    // The game spawns cars, trailers and merchant stock by drawing a random entry from PlayMaker array
    // lists (ItemSpawner FSMs: ArrayListGetRandom on "CarSpawn", "ItemBuySellList", ...). A custom
    // prefab joins every such list that holds the game prefab its objects come from, so it turns up
    // wherever that one can: starting car, camps, merchants. Spawner prefabs get it in their pre-fill
    // list (copied into the live list on Awake, so spawners the map generates later include it);
    // spawners already in the scene get it in their live list.
    //
    // A root sigma_spawn of a number other than 1 makes it that much as likely as each other entry of a list
    // (0.25: a quarter as likely); WeightedDraw draws by weight from the lists that hold one.
    internal static class SpawnLists
    {
        // templates with a spawn weight other than 1, by their prefab
        public static readonly Dictionary<GameObject, double> Weights = new Dictionary<GameObject, double>();

        public static void Join(IEnumerable<CustomPrefabs.Info> templates)
        {
            var list = templates.Where(t => (t.Donor != null || t.SpawnLike.Count > 0) && t.Prefab != null).ToList();
            if (list.Count == 0) return;
            foreach (var t in list)
                if (Math.Abs(t.SpawnWeight - 1) > 1e-9) Weights[t.Prefab] = t.SpawnWeight;
                else Weights.Remove(t.Prefab);
            var joined = new Dictionary<CustomPrefabs.Info, List<string>>();
            foreach (var proxy in Resources.FindObjectsOfTypeAll<PlayMakerArrayListProxy>())
            {
                if (proxy == null || IsTemplate(proxy.gameObject)) continue;
                bool asset = !proxy.gameObject.scene.IsValid();
                foreach (var t in list)
                {
                    bool added = false;
                    if (asset)
                    {
                        var pre = proxy.preFillGameObjectList;
                        if (pre != null && Holds(pre, t) && !pre.Contains(t.Prefab)) { pre.Add(t.Prefab); proxy.preFillCount = pre.Count; added = true; }
                    }
                    else
                    {
                        var live = proxy.arrayList;
                        if (live != null && Holds(live, t) && !live.Contains(t.Prefab)) { live.Add(t.Prefab); added = true; }
                    }
                    if (!added) continue;
                    if (!joined.TryGetValue(t, out var names)) joined[t] = names = new List<string>();
                    names.Add($"{Path(proxy.transform)}:{proxy.referenceName}{(asset ? " (prefab)" : "")}");
                }
            }
            foreach (var kv in joined)
                Plugin.Log.LogInfo($"custom prefab '{kv.Key.Id}': joined {kv.Value.Count} spawn lists that hold " +
                                   string.Join(" or ", (kv.Key.SpawnLike.Count > 0 ? kv.Key.SpawnLike : new List<GameObject> { kv.Key.Donor }).Where(g => g != null).Select(g => "'" + g.name + "'").ToArray())
                                   + (Weights.TryGetValue(kv.Key.Prefab, out var w) ? $", {w:0.###} as likely as each other entry" : "")
                                   + $": {string.Join(", ", kv.Value.ToArray())}");
        }

        // a list holds one of the prefabs the template spawns like, or, when it names none, its donor
        private static bool Holds(IList list, CustomPrefabs.Info t) =>
            t.SpawnLike.Count > 0 ? t.SpawnLike.Any(g => g != null && list.Contains(g)) : t.Donor != null && list.Contains(t.Donor);

        private static bool IsTemplate(GameObject go) => CustomPrefabs.All.Any(t => t.Prefab != null && go.transform.IsChildOf(t.Prefab.transform));

        private static string Path(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }
    }

    // The game's random pick from a spawn list (ArrayListGetRandom: one index, uniform), by weight when the list holds
    // a template with a spawn weight: each entry weighs 1, those templates their sigma_spawn. Lists without one are
    // drawn as the game draws them.
    [HarmonyLib.HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.ArrayListGetRandom), "GetRandomItem")]
    internal static class WeightedDraw
    {
        private static readonly FieldInfo Proxy = HarmonyLib.AccessTools.Field(typeof(HutongGames.PlayMaker.Actions.ArrayListActions), "proxy");

        private static bool Prefix(HutongGames.PlayMaker.Actions.ArrayListGetRandom __instance)
        {
            if (SpawnLists.Weights.Count == 0 || Proxy == null) return true;
            try
            {
                var list = (Proxy.GetValue(__instance) as PlayMakerArrayListProxy)?.arrayList;
                if (list == null || list.Count == 0) return true;
                var w = new double[list.Count];
                double total = 0;
                bool weighted = false;
                for (int i = 0; i < list.Count; i++)
                {
                    w[i] = list[i] is GameObject g && g != null && SpawnLists.Weights.TryGetValue(g, out var x) ? x : 1;
                    weighted |= w[i] != 1;
                    total += w[i];
                }
                if (!weighted || total <= 0) return true;
                double r = UnityEngine.Random.value * total;
                int pick = list.Count - 1;
                for (int i = 0; i < list.Count; i++)
                    if ((r -= w[i]) < 0) { pick = i; break; }
                __instance.randomIndex.Value = pick;
                if (!PlayMakerUtils.ApplyValueToFsmVar(__instance.Fsm, __instance.randomItem, list[pick]))
                    __instance.Fsm.Event(__instance.failureEvent);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Verbose("weighted spawn draw: " + e.Message);
                return true;
            }
        }
    }

    // Apocaspawner lists root objects only, and custom prefabs live under an inactive holder (so the
    // game's spawners get active copies). Its catalog gets them added directly instead. Soft
    // dependency: nothing happens when Apocaspawner is not installed or its internals differ.
    internal static class ApocaspawnerBridge
    {
        private static Type _entry;
        private static MethodInfo _groupFor;

        public static void Init(HarmonyLib.Harmony harmony)
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Apocaspawner");
                var catalog = asm?.GetType("Apocaspawner.Catalog");
                _entry = asm?.GetType("Apocaspawner.Entry");
                var entries = catalog?.GetMethod("Entries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _groupFor = catalog?.GetMethod("GroupFor", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (entries == null || _entry == null) return;
                harmony.Patch(entries, postfix: new HarmonyLib.HarmonyMethod(typeof(ApocaspawnerBridge), nameof(EntriesPostfix)));
                Plugin.Verbose("Apocaspawner catalog bridge installed");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Apocaspawner bridge not installed: " + e.Message); }
        }

        private static void EntriesPostfix(object __result)
        {
            if (!(__result is IList list)) return;
            try
            {
                foreach (var t in CustomPrefabs.All)
                {
                    if (t.Prefab == null) continue;
                    // Apocaspawner's scan also lists live root vehicles by base name; a spawned copy of a template would
                    // stand in for it (and get cloned with whatever is fitted to it). The template replaces those.
                    var same = list.Cast<object>().Where(x => _entry.GetField("Key")?.GetValue(x) as string == t.Id).ToList();
                    if (same.Any(x => _entry.GetField("Prefab")?.GetValue(x) as GameObject == t.Prefab)) continue;
                    foreach (var x in same) list.Remove(x);
                    var e = Activator.CreateInstance(_entry, true);
                    Set(e, "Key", t.Id);
                    Set(e, "Display", t.Name);
                    Set(e, "Category", Category(t));
                    Set(e, "Prefab", t.Prefab);
                    Set(e, "IsAsset", true);
                    Set(e, "IsVehicle", t.IsVehicle);
                    Set(e, "Value", BuyValue(t.Prefab));
                    Set(e, "Mod", t.Mod ?? Plugin.NAME);
                    if (_groupFor != null) Set(e, "Group", _groupFor.Invoke(null, new[] { e }));
                    list.Add(e);
                }
            }
            catch (Exception ex) { Plugin.Verbose("Apocaspawner catalog: " + ex.Message); }
        }

        private static string Category(CustomPrefabs.Info t)
        {
            var fsms = t.Prefab.GetComponents<PlayMakerFSM>();
            if (fsms.Any(f => f.FsmName == "TrailerAttached")) return "trailer";
            if (t.IsVehicle) return "vehicle";
            var id = fsms.FirstOrDefault(f => f.FsmName == "ID")?.FsmVariables.GetFsmString("ID")?.Value;
            return string.IsNullOrEmpty(id) ? "misc" : id;
        }

        private static int BuyValue(GameObject go) =>
            go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Value")?.FsmVariables.GetFsmInt("BuyValue")?.Value ?? 0;

        private static void Set(object o, string field, object v) => _entry.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(o, v);
    }
}

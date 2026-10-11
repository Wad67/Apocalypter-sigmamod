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
    internal static class SpawnLists
    {
        public static void Join(IEnumerable<CustomPrefabs.Info> templates)
        {
            var list = templates.Where(t => t.Donor != null && t.Prefab != null).ToList();
            if (list.Count == 0) return;
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
                        if (pre != null && pre.Contains(t.Donor) && !pre.Contains(t.Prefab)) { pre.Add(t.Prefab); proxy.preFillCount = pre.Count; added = true; }
                    }
                    else
                    {
                        var live = proxy.arrayList;
                        if (live != null && live.Contains(t.Donor) && !live.Contains(t.Prefab)) { live.Add(t.Prefab); added = true; }
                    }
                    if (!added) continue;
                    if (!joined.TryGetValue(t, out var names)) joined[t] = names = new List<string>();
                    names.Add($"{Path(proxy.transform)}:{proxy.referenceName}{(asset ? " (prefab)" : "")}");
                }
            }
            foreach (var kv in joined)
                Plugin.Log.LogInfo($"custom prefab '{kv.Key.Id}': joined {kv.Value.Count} spawn lists that hold '{kv.Key.Donor.name}': {string.Join(", ", kv.Value.ToArray())}");
        }

        private static bool IsTemplate(GameObject go) => CustomPrefabs.All.Any(t => t.Prefab != null && go.transform.IsChildOf(t.Prefab.transform));

        private static string Path(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
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

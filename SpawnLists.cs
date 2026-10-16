using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
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
            IndexedDraws.Extend(list);
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

    // Most of the world's loot isn't drawn from its list at random: an ItemSpawner picks an index with SelectRandomInt
    // (a fixed table of indices, 0 to 271 for the game's ItemList, each with a weight), then ArrayListGet takes the entry
    // there. A template appended to that list is past the table's end and never picked. Each such table gets a row for
    // every template its list holds, weighted as the template's donor is there (times its sigma_spawn); a table that
    // doesn't hold the donor's index doesn't spawn that kind of thing, and gets nothing. On a later scene the rows are
    // kept and re-pointed at the template's index in that scene's list.
    internal static class IndexedDraws
    {
        private sealed class Row { public FsmInt Index; public FsmFloat Weight; public GameObject Prefab; public float Base; }
        private static readonly Dictionary<SelectRandomInt, List<Row>> Added = new Dictionary<SelectRandomInt, List<Row>>();

        public static void Extend(List<CustomPrefabs.Info> templates)
        {
            int tables = 0, rows = 0;
            var scene = Resources.FindObjectsOfTypeAll<PlayMakerArrayListProxy>().Where(x => x != null && x.gameObject.scene.IsValid()).ToList();
            foreach (var fsm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (fsm == null || IsTemplate(fsm.gameObject)) continue;
                FsmState[] states;
                try { states = fsm.FsmStates; } catch { continue; }
                if (states == null) continue;
                var actions = new List<(FsmState st, FsmStateAction a)>();
                foreach (var st in states)
                {
                    FsmStateAction[] list;
                    try { list = st.Actions; } catch { continue; }
                    if (list != null) foreach (var a in list) if (a != null) actions.Add((st, a));
                }
                foreach (var (_, a) in actions)
                {
                    if (!(a is ArrayListGet get) || get.atIndex == null || !get.atIndex.UseVariable || string.IsNullOrEmpty(get.atIndex.Name)) continue;
                    var items = ListOf(fsm, get, scene);
                    if (items == null) continue;
                    foreach (var (st, b) in actions)
                    {
                        if (!(b is SelectRandomInt pick) || pick.storeInt == null || pick.storeInt.Name != get.atIndex.Name || pick.ints == null || pick.weights == null) continue;
                        int n = Extend(pick, items, templates);
                        if (n < 0) continue;
                        tables++; rows += n;
                        if (n > 0) st.SaveActions();
                    }
                }
            }
            if (rows > 0 || tables > 0) Plugin.Log.LogInfo($"custom prefabs: {rows} rows added to {tables} indexed spawn tables (SelectRandomInt into a spawn list)");
        }

        // -1: nothing of ours there; else the rows added (0: the ones there were re-pointed)
        private static int Extend(SelectRandomInt pick, IList items, List<CustomPrefabs.Info> templates)
        {
            if (!Added.TryGetValue(pick, out var mine)) mine = null;
            if (mine != null)
                foreach (var r in mine)
                {
                    int i = items.IndexOf(r.Prefab);
                    r.Index.Value = i < 0 ? 0 : i;
                    r.Weight.Value = i < 0 ? 0f : r.Base;
                }
            int added = 0;
            bool any = mine != null;
            var ints = pick.ints.ToList();
            var weights = pick.weights.ToList();
            foreach (var t in templates)
            {
                if (mine != null && mine.Any(r => r.Prefab == t.Prefab)) continue;
                int at = items.IndexOf(t.Prefab);
                if (at < 0) continue;
                var like = t.SpawnLike.Count > 0 ? t.SpawnLike : new List<GameObject> { t.Donor };
                var donors = new HashSet<int>(like.Where(g => g != null).Select(g => items.IndexOf(g)).Where(i => i >= 0));
                float w = 0f;
                int count = Math.Min(ints.Count, weights.Count);
                for (int i = 0; i < count; i++)
                    if (ints[i] != null && weights[i] != null && donors.Contains(ints[i].Value)) w = Math.Max(w, weights[i].Value);
                if (w <= 0f) continue;
                w = (float)(w * t.SpawnWeight);
                var row = new Row { Index = new FsmInt { Value = at }, Weight = new FsmFloat { Value = w }, Prefab = t.Prefab, Base = w };
                // keep the two arrays the same length (PlayMaker pairs them by position)
                while (weights.Count < ints.Count) weights.Add(new FsmFloat { Value = 1f });
                ints.Add(row.Index); weights.Add(row.Weight);
                if (mine == null) Added[pick] = mine = new List<Row>();
                mine.Add(row);
                added++; any = true;
            }
            if (added > 0) { pick.ints = ints.ToArray(); pick.weights = weights.ToArray(); }
            return any ? added : -1;
        }

        // the list an ArrayListGet reads: its target's proxy of that reference (a prefab's own: the pre-fill list; the
        // scene's: the live one); a target held in a global variable a prefab can't resolve yet is the scene's only
        // proxy of that reference
        private static IList ListOf(PlayMakerFSM fsm, ArrayListGet get, List<PlayMakerArrayListProxy> scene)
        {
            string reference = get.reference != null ? get.reference.Value : null;
            GameObject target = null;
            var owner = get.gameObject;
            if (owner == null || owner.OwnerOption == OwnerDefaultOption.UseOwner) target = fsm.gameObject;
            else if (owner.GameObject != null)
            {
                var g = owner.GameObject;
                target = g.Value;
                if (target == null && g.UseVariable && !string.IsNullOrEmpty(g.Name))
                    target = FsmVariables.GlobalVariables.FindFsmGameObject(g.Name)?.Value;
                if (target == null)
                {
                    var only = scene.Where(x => x.referenceName == reference).ToList();
                    if (only.Count == 1) return only[0].arrayList;
                }
            }
            if (target == null) return null;
            var proxy = target.GetComponents<PlayMakerArrayListProxy>().FirstOrDefault(x => x != null && x.referenceName == reference);
            if (proxy == null) return null;
            return proxy.gameObject.scene.IsValid() ? (IList)proxy.arrayList : proxy.preFillGameObjectList;
        }

        private static bool IsTemplate(GameObject go) => CustomPrefabs.All.Any(t => t.Prefab != null && go.transform.IsChildOf(t.Prefab.transform));
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
            if (Proxy == null) return true;
            try
            {
                var list = (Proxy.GetValue(__instance) as PlayMakerArrayListProxy)?.arrayList;
                if (list == null || list.Count == 0) return true;
                if (StarterKits.Chosen(__instance, list) is int chosen)
                {
                    __instance.randomIndex.Value = chosen;
                    if (!PlayMakerUtils.ApplyValueToFsmVar(__instance.Fsm, __instance.randomItem, list[chosen]))
                        __instance.Fsm.Event(__instance.failureEvent);
                    return false;
                }
                if (SpawnLists.Weights.Count == 0) return true;
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

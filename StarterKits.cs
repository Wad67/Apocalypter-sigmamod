using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace SigmaMod
{
    // What comes with a car the game draws. The starter camp's engine spawner reads which entry its car spawner drew (the
    // draw's index) and spawns that car's kit: an engine, a radiator or a pair of wheels, by the index's place in the
    // list. A template there is past the entries it knows, so it would get nothing. After each draw of a template, the
    // FSMs that read the index get, instead:
    //   - the template's own kit, "starter" in its script: items by a template's sigma_id, a game prefab's id or a game
    //     prefab's name, spawned as that spawner spawns its own (its spawn points in turn, the same top-ups of liquid,
    //     condition and oil), and the spawner marked as done, so the save keeps it done;
    //       starter = { "594cc I2 23HP 39Nm Gasoline", "radiator_motorcycle" }
    //   - without one, the kit of the game car it spawns like (its first spawn_like in the list, else its donor): the
    //     index read is that car's.
    [HarmonyLib.HarmonyPatch(typeof(ArrayListGetRandom), "GetRandomItem")]
    internal static class StarterKits
    {
        public const string Key = "starter";
        private static readonly System.Reflection.FieldInfo Proxy = HarmonyLib.AccessTools.Field(typeof(ArrayListActions), "proxy");

        private static void Postfix(ArrayListGetRandom __instance)
        {
            try { Drawn(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning("starter kit: " + e.Message); }
        }

        private static void Drawn(ArrayListGetRandom draw)
        {
            if (draw.randomIndex == null || !draw.randomIndex.UseVariable || string.IsNullOrEmpty(draw.randomIndex.Name)) return;
            // the entry drawn, from the list itself (the action's randomItem keeps a stale copy of the variable)
            var list = (Proxy.GetValue(draw) as PlayMakerArrayListProxy)?.arrayList;
            int index = draw.randomIndex.Value;
            var item = list != null && index >= 0 && index < list.Count ? list[index] as GameObject : null;
            var t = item != null ? CustomPrefabs.All.FirstOrDefault(x => x.Prefab == item) : null;
            if (t == null) return;
            var owner = draw.Fsm.GameObject;
            var readers = Readers(owner, draw.Fsm.Name, draw.randomIndex.Name);
            Plugin.Verbose($"starter kit: {owner.name} drew '{t.Id}' (entry {index}); {readers.Count} spawners read it");
            if (readers.Count == 0) return;

            var kit = t.Spec != null && t.Spec.TryGetValue(Key, out var kv) ? kv as List<object> : null;
            if (kit == null)
            {
                // the game car's index: its kit is what the readers spawn
                var like = list == null ? null : t.SpawnLike.Concat(new[] { t.Donor }).FirstOrDefault(g => g != null && list.Contains(g));
                if (like == null) { Plugin.Log.LogWarning($"starter kit: '{t.Id}' has no {Key} and spawns like no car in {owner.name}'s list: nothing comes with it"); return; }
                draw.randomIndex.Value = list.IndexOf(like);
                Plugin.Verbose($"starter kit: '{t.Id}' gets '{like.name}''s");
                return;
            }
            foreach (var r in readers) Spawn(r, kit, t.Id);
        }

        // Debug.StarterCar: the entry a starting car's draw takes (a draw whose index a kit spawner reads), by a template's
        // id or the game car's name; null: drawn as usual
        internal static int? Chosen(ArrayListGetRandom draw, System.Collections.IList list)
        {
            string want = Plugin.StarterCarEntry != null ? Plugin.StarterCarEntry.Value.Trim() : "";
            if (want.Length == 0 || draw.randomIndex == null || !draw.randomIndex.UseVariable || string.IsNullOrEmpty(draw.randomIndex.Name)) return null;
            if (Readers(draw.Fsm.GameObject, draw.Fsm.Name, draw.randomIndex.Name).Count == 0) return null;
            var names = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is GameObject g) || g == null) continue;
                string name = CustomPrefabs.All.FirstOrDefault(t => t.Prefab == g)?.Id ?? g.name;
                if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) { Plugin.Log.LogInfo($"starter car: '{name}' (Debug.StarterCar)"); return i; }
                names.Add(name);
            }
            Plugin.Log.LogWarning($"Debug.StarterCar: no '{want}' among the starting cars: {string.Join(", ", names.ToArray())}");
            return null;
        }

        // the FSMs that read this draw's index (GetFsmInt of that variable from that FSM)
        private static List<PlayMakerFSM> Readers(GameObject owner, string fsmName, string variable)
        {
            var found = new List<PlayMakerFSM>();
            foreach (var fsm in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
                if (fsm.Fsm != null && fsm.Fsm.Initialized)   // an FSM not started yet can't load its actions
                foreach (var st in fsm.FsmStates)
                    foreach (var a in st.Actions.OfType<GetFsmInt>())
                        if (a.fsmName.Value == fsmName && a.variableName.Value == variable && fsm.Fsm.GetOwnerDefaultTarget(a.gameObject) == owner && !found.Contains(fsm))
                            found.Add(fsm);
            return found;
        }

        private static void Spawn(PlayMakerFSM reader, List<object> kit, string id)
        {
            var done = reader.FsmVariables.FindFsmInt(PlaceParts.Spent);
            if (done != null && done.Value > 0) return;
            // its spawn points, in the order it uses them, and its top-ups: the state after its first item
            var creates = reader.FsmStates.SelectMany(s => s.Actions.OfType<CreateObject>().Select(a => new { s, a })).ToList();
            var spots = creates.Select(c => c.a.spawnPoint.Value).Where(g => g != null).Distinct().ToList();
            if (spots.Count == 0) spots.Add(reader.gameObject);
            var after = creates.Count > 0 ? creates[0].s.Transitions.FirstOrDefault(x => x.EventName == "FINISHED")?.ToState : null;
            var topUp = reader.FsmStates.FirstOrDefault(s => s.Name == after);
            string itemVar = creates.Count > 0 && creates[0].a.storeObject.UseVariable ? creates[0].a.storeObject.Name : null;

            var spawned = new List<string>();
            for (int i = 0; i < kit.Count; i++)
            {
                var prefab = Prefab(Convert.ToString(kit[i], CultureInfo.InvariantCulture), out var info);
                if (prefab == null) { Plugin.Log.LogWarning($"starter kit of '{id}': no template or game prefab '{kit[i]}'"); continue; }
                var spot = spots[Math.Min(i, spots.Count - 1)].transform;
                // more items than spots: the rest on the last one, each a little above the one before, to drop onto it
                var at = spot.position + Vector3.up * (0.3f * Math.Max(0, i - (spots.Count - 1)));
                var rot = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) * spot.rotation;
                var go = info != null ? CustomPrefabs.Spawn(info, at, rot) : CustomPrefabs.SpawnItem(prefab, at, rot);
                if (go == null) continue;
                foreach (var f in go.GetComponents<PlayMakerFSM>().Where(f => f.FsmName == "Randomize")) f.SendEvent("Randomize");
                if (topUp != null) TopUp(topUp, itemVar, go);
                spawned.Add(go.name);
            }
            if (done != null) done.Value = 1;
            Plugin.Log.LogInfo($"starter kit of '{id}' at {reader.name}: {string.Join(", ", spawned.ToArray())}");
        }

        // a template's sigma_id, a game prefab's id, or a game prefab's name
        private static GameObject Prefab(string s, out CustomPrefabs.Info info)
        {
            info = CustomPrefabs.All.FirstOrDefault(t => t.Id == s && t.Prefab != null);
            if (info != null) return info.Prefab;
            var mgr = ES3Internal.ES3ReferenceMgrBase.Current;
            if (mgr == null) return null;
            if (long.TryParse(s, out long pid)) return mgr.GetPrefab(pid, true)?.gameObject;
            return mgr.prefabs.FirstOrDefault(p => p != null && p.name == s)?.gameObject;
        }

        // the reader's own top-ups, on the item: FsmFloatAdd on the item, or on a child its GetChild finds
        private static void TopUp(FsmState state, string itemVar, GameObject item)
        {
            var found = new Dictionary<string, GameObject>();
            if (itemVar != null) found[itemVar] = item;
            foreach (var a in state.Actions)
            {
                if (a is GetChild gc && gc.storeResult.UseVariable)
                {
                    var c = item.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == gc.childName.Value &&
                        (string.IsNullOrEmpty(gc.withTag.Value) || gc.withTag.Value == "Untagged" || x.CompareTag(gc.withTag.Value)));
                    if (c != null) found[gc.storeResult.Name] = c.gameObject;
                }
                else if (a is FsmFloatAdd add && add.gameObject.GameObject != null && add.gameObject.GameObject.UseVariable
                         && found.TryGetValue(add.gameObject.GameObject.Name, out var target))
                {
                    var v = target.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == add.fsmName.Value)?.FsmVariables.FindFsmFloat(add.variableName.Value);
                    if (v != null) v.Value += add.add.Value;
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SigmaMod
{
    // The game's working parts in a template: a camp's item spawner, enemy spawner, map icon, codex trigger and the
    // like are prefabs of the game's own with no Easy Save id, so they are picked by what they do: a loaded game
    // prefab (not one in a scene) with an FSM of that name, and, where several do, FSM variables of the given values.
    //
    //   "instance": { "fsm": "ItemSpawner", "where": { "minItemNum": 21 }, "prefab": "ItemSpawn_V1_All", "as": "loot" }
    //
    // A copy goes in just before the object that asks for it, under the same parent, at its position and turn, so
    // that object comes right after it. The game's parts lean on that: a camp's item spawner takes the object right
    // after it as its list of spawn points (its children), so an empty with empties under it is a loot spot. "as"
    // names the copy for the rest of the .json ("nodes": { "loot": { "fsm": { "ItemSpawner": { "maxItemNum": 6 } },
    // "BoxCollider": { "size": [20, 6, 20] } } }). It keeps the game's name, which the game's own FSMs may look for.
    // "prefab" names the game prefab, like an asset: where the game's own copies differ only in what they hold (a
    // car spawner of every car, or of the unique ones), which no FSM value tells apart. Several matching prefabs: a prefab of its own is taken before a copy inside another, then the first by name,
    // and the log lists them with their values to tell apart.
    internal static class Instances
    {
        public static void Place(GameObject at, Dictionary<string, object> spec, JsonValues ctx, string where)
        {
            if (spec == null || !(spec.TryGetValue("fsm", out var fv) && fv is string fsmName)) throw new Exception("needs \"fsm\": the name of an FSM the game part has");
            if (at.transform.parent == null) throw new Exception("goes before an object, under the same parent: not on the template's root");
            var wanted = spec.TryGetValue("where", out var wv) ? wv as Dictionary<string, object> : null;
            string prefab = spec.TryGetValue("prefab", out var pv) ? pv as string : null;

            var found = new List<GameObject>();
            foreach (var fsm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (fsm == null || fsm.FsmName != fsmName) continue;
                var go = fsm.gameObject;
                if (go.scene.IsValid() || found.Contains(go)) continue;   // loaded game prefabs only
                if (wanted != null && !Matches(fsm, wanted)) continue;
                if (prefab != null && go.name != prefab) continue;
                found.Add(go);
            }
            if (found.Count == 0) throw new Exception($"no game prefab" + (prefab != null ? $" '{prefab}'" : "") + $" has an FSM '{fsmName}'" + (wanted != null ? " with " + Json.Write(wanted) : ""));
            // a prefab of its own before a copy inside another (a building's own spawner), then by name
            found = found.OrderBy(g => g.transform.parent == null ? 0 : 1).ThenBy(g => g.name, StringComparer.Ordinal).ToList();
            if (found.Count > 1)
                Plugin.Log.LogWarning($"{where}: instance '{fsmName}': {found.Count} game prefabs have it, '{found[0].name}' taken; " +
                                      "tell them apart with \"where\": " + string.Join("; ", found.Take(8).Select(g => Describe(g, fsmName)).ToArray()));

            var source = found[0];
            var copy = UnityEngine.Object.Instantiate(source, at.transform.parent, false);
            copy.name = source.name;
            copy.transform.localPosition = at.transform.localPosition;
            copy.transform.localRotation = at.transform.localRotation;
            copy.transform.SetSiblingIndex(at.transform.GetSiblingIndex());   // just before it: it comes right after
            copy.SetActive(true);
            if (spec.TryGetValue("as", out var av) && av is string name && name.Trim().Length > 0) ctx.AddNode?.Invoke(name.Trim(), copy);
            Plugin.Verbose($"{where}: instance '{fsmName}': '{source.name}' placed before '{at.name}'");
        }

        private static bool Matches(PlayMakerFSM fsm, Dictionary<string, object> wanted)
        {
            foreach (var kv in wanted)
            {
                var v = fsm.FsmVariables.FindVariable(kv.Key);
                if (v == null || !Same(v.RawValue, kv.Value)) return false;
            }
            return true;
        }

        private static bool Same(object have, object want)
        {
            if (have == null || want == null) return have == want;
            if (want is double d)
                return double.TryParse(Convert.ToString(have, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && Math.Abs(h - d) < 1e-4;
            if (want is bool b) return have is bool hb && hb == b;
            if (have is UnityEngine.Object o) return o != null && o.name == Convert.ToString(want, CultureInfo.InvariantCulture);
            return string.Equals(Convert.ToString(have, CultureInfo.InvariantCulture), Convert.ToString(want, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        // a candidate and its FSM's simple variables, for telling candidates apart
        private static string Describe(GameObject go, string fsmName)
        {
            var fsm = go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == fsmName);
            if (fsm == null) return go.name;
            var vars = fsm.FsmVariables.GetAllNamedVariables()
                .Where(v => v is FsmInt || v is FsmFloat || v is FsmBool || v is FsmString)
                .Select(v => v.Name + "=" + Convert.ToString(v.RawValue, CultureInfo.InvariantCulture)).Take(8);
            return go.name + " {" + string.Join(", ", vars.ToArray()) + "}";
        }
    }
}

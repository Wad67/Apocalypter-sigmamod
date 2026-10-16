using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SigmaMod
{
    // The JSON file next to a template (<name>.glb -> <name>.json) defines and tunes it:
    //
    //   { "<sigma_id>": { <object spec for the root>,
    //                     "nodes": { "<object>": { <object spec> }, ... },
    //                     "drive": "rear" | "front" | "all" } }   (chassis only)
    //
    // <object> is a Blender object name or a sigma_id. An object spec holds, applied in this order:
    //   "layer": <number or layer name>, "tag": "<tag>"
    //   "add": [ "<Component>", ... ]                 components to add (skipped if already there)
    //   "collider": "convex" | { ... }                colliders from the object's mesh, see Colliders
    //   "instance": { "fsm": "<FsmName>", "where": { "<variable>": value }, "as": "<name>" }   (or a list)
    //                                                 a working part of the game placed just before this object,
    //                                                 at its position and turn, so this object comes right after
    //                                                 it (see Instances)
    //   "playmaker": { "<FsmName>": { definition } }   new PlayMaker FSMs, see FsmJson
    //   "<Component>": { "<field.path>": value, ... }  values on a component of the object
    //   "fsm": { "<FsmName>": { "<key>": value } }      values in FSMs on the object or below it
    //
    // <Component> is a type name (Rigidbody, VehicleController, NWH.WheelController3D.WheelController). A
    // path walks public or serialized fields and properties (powertrain.transmission.gears); lists and
    // arrays take an index as a path step. Values are JsonValues: numbers, bools, strings, enums by name,
    // vectors and colours from arrays, AnimationCurve from [[time, value], ...], objects by name or
    // sigma_id, assets ("asset:AudioClip/name"), files ("file:idle.wav"), and nested classes as objects.
    //
    // "fsm" keys:
    //   "<variable>"                   an FSM variable
    //   "set:<property.path>"          every SetProperty constant writing that property, e.g.
    //                                  "set:powertrain.engine.idleRPM" (the value's type picks the parameter)
    //   "<State>/<Action>[n].<field>"  a field of the n-th action of that type in a state
    //                                  ([n] optional, 0-based), e.g. "condition100/SetFloatValue.floatValue"
    // Edited states are written back with SaveActions, so copies of the template carry them.
    internal static class Overrides
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly HashSet<string> Reserved = new HashSet<string> { "nodes", "drive", "poi", "places", "replaces", "scale", "instance", "fsm", "add", "collider", "playmaker", "layer", "tag", "parent", "position", "rotation", "at", "align" };

        private static IEnumerable<KeyValuePair<GameObject, Dictionary<string, object>>> Objects(GameObject root, Dictionary<string, object> spec,
                                                                                               Func<string, GameObject> node, string where)
        {
            yield return new KeyValuePair<GameObject, Dictionary<string, object>>(root, spec);
            if (!spec.TryGetValue("nodes", out var nv)) yield break;
            if (!(nv is Dictionary<string, object> nodes)) { Plugin.Log.LogWarning($"{where}: 'nodes' must be an object"); yield break; }
            foreach (var nk in nodes)
            {
                var go = node(nk.Key);
                if (go == null) { Plugin.Log.LogWarning($"{where}: nodes: no object named '{nk.Key}' (Blender name or sigma_id)"); continue; }
                if (nk.Value is Dictionary<string, object> ns) yield return new KeyValuePair<GameObject, Dictionary<string, object>>(go, ns);
                else Plugin.Log.LogWarning($"{where}: nodes.{nk.Key} must be an object");
            }
        }

        // layer, tag, added components and new FSMs, for the root and every node. FSMs come last, after every
        // object has its components, so an action can point at a component on any object whatever the order
        public static void Build(GameObject root, Dictionary<string, object> spec, Func<string, GameObject> node, JsonValues ctx, string where)
        {
            Create(spec, node, ctx, where);
            PlaceInstances(root, spec, node, ctx, where);
            var objects = Objects(root, spec, node, where).ToList();
            foreach (var kv in objects)
            {
                var go = kv.Key;
                var s = kv.Value;
                string at = go == root ? where : where + "/" + go.name;
                if (s.TryGetValue("layer", out var lv))
                {
                    int layer = lv is double ld ? (int)ld : LayerMask.NameToLayer(lv as string ?? "");
                    if (layer < 0 || layer > 31) Plugin.Log.LogWarning($"{at}: unknown layer {Json.Write(lv)}");
                    else go.layer = layer;
                }
                if (s.TryGetValue("tag", out var tv) && tv is string tag)
                    try { go.tag = tag; } catch (UnityException) { Plugin.Log.LogWarning($"{at}: tag '{tag}' is not defined in this game"); }
                if (s.TryGetValue("add", out var av))
                {
                    if (!(av is List<object> adds)) Plugin.Log.LogWarning($"{at}: 'add' must be a list of component type names");
                    else
                        foreach (var name in adds.OfType<string>())
                            try
                            {
                                var type = Types.Component(name);
                                if (go.GetComponent(type) == null) go.AddComponent(type);
                            }
                            catch (Exception e) { Plugin.Log.LogWarning($"{at}: add '{name}': {e.Message}"); }
                }
                if (s.TryGetValue("collider", out var cv))
                    try { Colliders.Apply(go, cv, ctx, at); }
                    catch (Exception e) { Plugin.Log.LogWarning($"{at}: collider: {e.Message}"); }
            }
            foreach (var kv in objects)
            {
                var go = kv.Key;
                var s = kv.Value;
                string at = go == root ? where : where + "/" + go.name;
                if (s.TryGetValue("playmaker", out var pv))
                {
                    if (!(pv is Dictionary<string, object> fsms)) { Plugin.Log.LogWarning($"{at}: 'playmaker' must be an object of FSM name: definition"); continue; }
                    foreach (var f in fsms)
                    {
                        if (!(f.Value is Dictionary<string, object> def)) { Plugin.Log.LogWarning($"{at}: playmaker.{f.Key} must be an object"); continue; }
                        try { FsmJson.Build(go, f.Key, def, ctx, at); }
                        catch (Exception e) { Plugin.Log.LogWarning($"{at}: playmaker '{f.Key}': {e.Message}"); }
                    }
                }
            }
        }

        // the game's working parts first, so the rest of the .json can name them ("as")
        private static void PlaceInstances(GameObject root, Dictionary<string, object> spec, Func<string, GameObject> node, JsonValues ctx, string where)
        {
            var specs = new List<KeyValuePair<GameObject, Dictionary<string, object>>> { new KeyValuePair<GameObject, Dictionary<string, object>>(root, spec) };
            if (spec.TryGetValue("nodes", out var nv) && nv is Dictionary<string, object> nodes)
                foreach (var nk in nodes)
                    if (nk.Value is Dictionary<string, object> ns && ns.ContainsKey("instance") && node(nk.Key) is GameObject g)
                        specs.Add(new KeyValuePair<GameObject, Dictionary<string, object>>(g, ns));
            foreach (var kv in specs)
            {
                if (!kv.Value.TryGetValue("instance", out var iv)) continue;
                string at = kv.Key == root ? where : where + "/" + kv.Key.name;
                foreach (var one in iv is List<object> many ? many : new List<object> { iv })
                    try { Instances.Place(kv.Key, one as Dictionary<string, object>, ctx, at); }
                    catch (Exception e) { Plugin.Log.LogWarning($"{at}: instance: {e.Message}"); }
            }
        }

        // "nodes" entries with "parent": an object the Blender file doesn't have becomes a new empty one (logic-only
        // things like a sound source), placed at the position of the object named by "at", then moved by "position"
        // [x,y,z] (Unity space, local to the parent: x is Blender's -X), turned to match the object named by "align"
        // (its rotation, not its scale) and by "rotation" [x,y,z] degrees; one
        // it has moves under that parent, staying where it is in the world (e.g. under a spinning wheel visual).
        private static void Create(Dictionary<string, object> spec, Func<string, GameObject> node, JsonValues ctx, string where)
        {
            if (!spec.TryGetValue("nodes", out var nv) || !(nv is Dictionary<string, object> nodes)) return;
            var pending = nodes.Where(kv => kv.Value is Dictionary<string, object> d && d.ContainsKey("parent")).ToList();
            for (bool progress = true; progress && pending.Count > 0;)
            {
                progress = false;
                foreach (var kv in pending.ToList())
                {
                    var d = (Dictionary<string, object>)kv.Value;
                    var parent = node(d["parent"] as string ?? "");
                    if (parent == null) continue;
                    var have = node(kv.Key);
                    if (have != null)
                    {
                        if (parent.transform.IsChildOf(have.transform)) { Plugin.Log.LogWarning($"{where}: nodes.{kv.Key}: can't go under its own child '{parent.name}'"); }
                        else have.transform.SetParent(parent.transform, true);
                        pending.Remove(kv);
                        progress = true;
                        continue;
                    }
                    // placed at (or turned as) an object still to be made: after it
                    bool Waits(string key) => d.TryGetValue(key, out var r) && r is string rn && node(rn) == null && pending.Any(p => p.Key == rn);
                    if (Waits("at") || Waits("align")) continue;
                    var go = new GameObject(kv.Key);
                    go.transform.SetParent(parent.transform, false);
                    go.layer = parent.layer;
                    if (d.TryGetValue("at", out var av))
                    {
                        var at = node(av as string ?? "");
                        if (at == null) Plugin.Log.LogWarning($"{where}: nodes.{kv.Key}: \"at\": no object '{av}'");
                        else go.transform.position = at.transform.position;
                    }
                    if (d.TryGetValue("align", out var lv))
                    {
                        var al = node(lv as string ?? "");
                        if (al == null) Plugin.Log.LogWarning($"{where}: nodes.{kv.Key}: \"align\": no object '{lv}'");
                        else go.transform.rotation = al.transform.rotation;
                    }
                    if (d.TryGetValue("position", out var pv)) go.transform.localPosition += (Vector3)ctx.Convert(pv, typeof(Vector3));
                    if (d.TryGetValue("rotation", out var rv)) go.transform.localRotation = (Quaternion)ctx.Convert(rv, typeof(Quaternion));
                    ctx.AddNode(kv.Key, go);
                    pending.Remove(kv);
                    progress = true;
                }
            }
            foreach (var kv in pending)
                Plugin.Log.LogWarning($"{where}: nodes.{kv.Key}: parent '{((Dictionary<string, object>)kv.Value)["parent"]}' not found");
        }

        // component and FSM values, for the root and every node
        public static void Apply(GameObject root, Dictionary<string, object> spec, Func<string, GameObject> node, JsonValues ctx, string where)
        {
            foreach (var kv in Objects(root, spec, node, where))
                Components(kv.Key, kv.Value, ctx, kv.Key == root ? where : where + "/" + kv.Key.name);
        }

        private static void Components(GameObject go, Dictionary<string, object> spec, JsonValues ctx, string where)
        {
            foreach (var kv in spec)
            {
                if (kv.Key == "fsm")
                {
                    if (kv.Value is Dictionary<string, object> fsms) Fsms(go, fsms, ctx, where);
                    else Plugin.Log.LogWarning($"{where}: 'fsm' must be an object");
                    continue;
                }
                if (Reserved.Contains(kv.Key) || Array.IndexOf(CustomPrefabs.ScriptOnly, kv.Key) >= 0) continue;
                Component comp = go.GetComponents<Component>().FirstOrDefault(c => c != null && (c.GetType().Name == kv.Key || c.GetType().FullName == kv.Key));
                if (comp == null) { Plugin.Log.LogWarning($"{where}: no {kv.Key} on '{go.name}' (add it with \"add\")"); continue; }
                if (!(kv.Value is Dictionary<string, object> fields)) { Plugin.Log.LogWarning($"{where}/{kv.Key}: must be an object"); continue; }
                foreach (var f in fields)
                {
                    try
                    {
                        Set(comp, f.Key.Split('.'), 0, f.Value, ctx);
                        Plugin.Verbose($"{where}: {kv.Key}.{f.Key} = {Json.Write(f.Value)}");
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"{where}: {kv.Key}.{f.Key}: {e.Message}"); }
                }
            }
        }

        private static void Fsms(GameObject go, Dictionary<string, object> spec, JsonValues ctx, string where)
        {
            foreach (var kv in spec)
            {
                // every FSM of that name on the object or below it (the engine's FinalDriveRatio lives on its
                // transmission child)
                var fsms = go.GetComponentsInChildren<PlayMakerFSM>(true).Where(f => f.FsmName == kv.Key).ToList();
                if (fsms.Count == 0) { Plugin.Log.LogWarning($"{where}: no FSM '{kv.Key}' on or under '{go.name}'"); continue; }
                if (!(kv.Value is Dictionary<string, object> keys)) { Plugin.Log.LogWarning($"{where}/fsm/{kv.Key}: must be an object"); continue; }
                foreach (var k in keys)
                {
                    string at = $"{where}: fsm '{kv.Key}' {k.Key}";
                    int n = 0;
                    foreach (var fsm in fsms)
                    {
                        var dirty = new HashSet<HutongGames.PlayMaker.FsmState>();
                        try
                        {
                            if (k.Key.StartsWith("set:", StringComparison.Ordinal)) n += SetProperty(fsm, k.Key.Substring(4), k.Value, dirty, ctx);
                            else if (k.Key.Contains("/")) n += ActionField(fsm, k.Key, k.Value, dirty, ctx);
                            else n += Variable(fsm, k.Key, k.Value, ctx);
                        }
                        catch (Exception e) { Plugin.Log.LogWarning($"{at}: {e.Message}"); }
                        foreach (var st in dirty) st.SaveActions();
                    }
                    if (n == 0) Plugin.Log.LogWarning($"{at}: nothing matched");
                    else Plugin.Verbose($"{at} = {Json.Write(k.Value)} ({n})");
                }
            }
        }

        private static int Variable(PlayMakerFSM fsm, string name, object v, JsonValues ctx)
        {
            // FindVariable returns null for an unknown name (GetFsmFloat would make a detached placeholder)
            var nv = fsm.FsmVariables.FindVariable(name);
            if (nv == null) return 0;
            Assign(nv, v, ctx);
            return 1;
        }

        private static int SetProperty(PlayMakerFSM fsm, string property, object v, HashSet<HutongGames.PlayMaker.FsmState> dirty, JsonValues ctx)
        {
            int n = 0;
            foreach (var st in fsm.FsmStates)
                foreach (var a in st.Actions)
                {
                    if (a == null || a.GetType().Name != "SetProperty") continue;
                    if (!(a.GetType().GetField("targetProperty")?.GetValue(a) is HutongGames.PlayMaker.FsmProperty p) || !p.setProperty || p.PropertyName != property) continue;
                    HutongGames.PlayMaker.NamedVariable param = v is bool ? (HutongGames.PlayMaker.NamedVariable)p.BoolParameter
                        : v is string ? (HutongGames.PlayMaker.NamedVariable)p.StringParameter
                        : p.PropertyType == typeof(int) ? (HutongGames.PlayMaker.NamedVariable)p.IntParameter : p.FloatParameter;
                    if (param == null) continue;
                    if (param.UseVariable) throw new Exception($"state '{st.Name}' writes it from variable '{param.Name}'; set that variable instead");
                    Assign(param, v, ctx);
                    dirty.Add(st);
                    n++;
                }
            return n;
        }

        // "<State>/<Action>[n].<field>"
        private static int ActionField(PlayMakerFSM fsm, string key, object v, HashSet<HutongGames.PlayMaker.FsmState> dirty, JsonValues ctx)
        {
            int slash = key.IndexOf('/'), dot = key.IndexOf('.', slash);
            if (dot < 0) throw new Exception("expected <State>/<Action>[n].<field>");
            string state = key.Substring(0, slash), action = key.Substring(slash + 1, dot - slash - 1), field = key.Substring(dot + 1);
            int index = 0, br = action.IndexOf('[');
            if (br >= 0) { index = int.Parse(action.Substring(br + 1, action.IndexOf(']') - br - 1)); action = action.Substring(0, br); }
            var st = fsm.FsmStates.FirstOrDefault(s => s.Name == state);
            if (st == null) throw new Exception($"no state '{state}'");
            var a = st.Actions.Where(x => x != null && x.GetType().Name == action).Skip(index).FirstOrDefault();
            if (a == null) return 0;
            var f = a.GetType().GetField(field, Flags);
            if (f == null) throw new Exception($"{action} has no field '{field}'");
            var cur = f.GetValue(a);
            if (cur is HutongGames.PlayMaker.NamedVariable nv && !(v is string s && s.StartsWith("{")))
            {
                if (nv.UseVariable) throw new Exception($"it reads variable '{nv.Name}'; set that variable instead");
                Assign(nv, v, ctx);
            }
            else f.SetValue(a, ctx.Convert(v, f.FieldType, cur));
            dirty.Add(st);
            return 1;
        }

        // a variable's or parameter's value, keeping its name and binding
        private static void Assign(HutongGames.PlayMaker.NamedVariable nv, object v, JsonValues ctx)
        {
            switch (nv)
            {
                case HutongGames.PlayMaker.FsmGameObject g: g.Value = ctx.Ref(v as string, typeof(GameObject)) as GameObject; return;
                case HutongGames.PlayMaker.FsmObject o: o.Value = ctx.Ref(v as string, typeof(UnityEngine.Object)); return;
            }
            var vp = nv.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (vp == null || !vp.CanWrite) throw new Exception($"{nv.GetType().Name} values are not supported");
            vp.SetValue(nv, ctx.Convert(v, vp.PropertyType), null);
        }

        // sets path[i..] on obj; returns obj, so a struct on the way is written back by its parent
        private static object Set(object obj, string[] path, int i, object json, JsonValues ctx)
        {
            if (obj == null) throw new Exception($"'{path[i - 1]}' is null");
            string step = path[i];
            bool last = i == path.Length - 1;

            if (obj is IList list && int.TryParse(step, out int idx))
            {
                if (idx < 0 || idx >= list.Count) throw new Exception($"index {idx} out of range ({list.Count})");
                var et = ElementType(list.GetType());
                list[idx] = last ? ctx.Convert(json, et, list[idx]) : Set(list[idx], path, i + 1, json, ctx);
                return obj;
            }

            var t = obj.GetType();
            var f = FindField(t, step);
            if (f != null)
            {
                f.SetValue(obj, last ? ctx.Convert(json, f.FieldType, f.GetValue(obj)) : Set(f.GetValue(obj), path, i + 1, json, ctx));
                return obj;
            }
            var p = t.GetProperty(step, Flags);
            if (p != null)
            {
                if (last || p.PropertyType.IsValueType)
                {
                    if (!p.CanWrite) throw new Exception($"'{step}' is read-only");
                    p.SetValue(obj, last ? ctx.Convert(json, p.PropertyType, p.CanRead ? p.GetValue(obj) : null) : Set(p.GetValue(obj), path, i + 1, json, ctx));
                }
                else Set(p.GetValue(obj), path, i + 1, json, ctx);
                return obj;
            }
            throw new Exception($"{t.Name} has no '{step}'");
        }

        private static FieldInfo FindField(Type t, string name)
        {
            for (var x = t; x != null; x = x.BaseType)
            {
                var f = x.GetField(name, Flags | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private static Type ElementType(Type t) => t.IsArray ? t.GetElementType() : t.IsGenericType ? t.GetGenericArguments()[0] : typeof(object);
    }
}

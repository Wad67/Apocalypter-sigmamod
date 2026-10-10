using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SigmaMod
{
    // Parameter overrides from the JSON file next to a template (<name>.glb -> <name>.json):
    //
    //   { "<sigma_id>": { "<Component>": { "<field.path>": value, ... },
    //                     "fsm": { "<FsmName>": { "<key>": value, ... } },
    //                     "nodes": { "<node sigma_id>": { "<Component>": { ... }, "fsm": { ... } } },
    //                     "drive": "rear" | "front" | "all" } }   (chassis only)
    //
    // <Component> is a component type name on the object (Rigidbody, VehicleController, ...). A path
    // walks public or serialized fields and properties (powertrain.transmission.gears); lists and
    // arrays take an index as a path step (powertrain.wheels.0.wheelUAPI). Values convert to the
    // target type: numbers, bools, strings, enums by name, Vector2/3/4 and Quaternion from arrays,
    // float/int lists and arrays from arrays, AnimationCurve from [[time, value], ...].
    //
    // "fsm" keys, for values the game keeps in PlayMaker FSMs (every FSM of that name on the object or below):
    //   "<variable>"                   an FSM variable (float, int, bool, string)
    //   "set:<property.path>"          every SetProperty constant writing that property, e.g.
    //                                  "set:powertrain.engine.idleRPM" (the value's type picks the parameter)
    //   "<State>/<Action>[n].<field>"  a constant field of the n-th action of that type in a state
    //                                  ([n] optional, 0-based), e.g. "condition100/SetFloatValue.floatValue"
    // Edited states are written back with SaveActions, so copies of the template carry them.
    internal static class Overrides
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Apply(GameObject root, Dictionary<string, object> spec, Func<string, GameObject> node, string where)
        {
            foreach (var kv in spec)
            {
                if (kv.Key == "nodes")
                {
                    if (!(kv.Value is Dictionary<string, object> nodes)) { Plugin.Log.LogWarning($"{where}: 'nodes' must be an object"); continue; }
                    foreach (var nk in nodes)
                    {
                        var go = node(nk.Key);
                        if (go == null) { Plugin.Log.LogWarning($"{where}: no object with sigma_id '{nk.Key}'"); continue; }
                        if (nk.Value is Dictionary<string, object> ns) Components(go, ns, where + "/" + nk.Key);
                    }
                }
            }
            Components(root, spec, where);
        }

        private static void Components(GameObject go, Dictionary<string, object> spec, string where)
        {
            foreach (var kv in spec)
            {
                if (kv.Key == "nodes" || kv.Key == "drive") continue; // drive: read by the chassis setup
                if (kv.Key == "fsm")
                {
                    if (kv.Value is Dictionary<string, object> fsms) Fsms(go, fsms, where);
                    else Plugin.Log.LogWarning($"{where}: 'fsm' must be an object");
                    continue;
                }
                var comp = go.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == kv.Key);
                if (comp == null) { Plugin.Log.LogWarning($"{where}: no {kv.Key} on '{go.name}'"); continue; }
                if (!(kv.Value is Dictionary<string, object> fields)) { Plugin.Log.LogWarning($"{where}/{kv.Key}: must be an object"); continue; }
                foreach (var f in fields)
                {
                    try
                    {
                        Set(comp, f.Key.Split('.'), 0, f.Value);
                        Plugin.Verbose($"{where}: {kv.Key}.{f.Key} = {Json.Write(f.Value)}");
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"{where}: {kv.Key}.{f.Key}: {e.Message}"); }
                }
            }
        }

        private static void Fsms(GameObject go, Dictionary<string, object> spec, string where)
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
                            if (k.Key.StartsWith("set:", StringComparison.Ordinal)) n += SetProperty(fsm, k.Key.Substring(4), k.Value, dirty);
                            else if (k.Key.Contains("/")) n += ActionField(fsm, k.Key, k.Value, dirty);
                            else n += Variable(fsm, k.Key, k.Value);
                        }
                        catch (Exception e) { Plugin.Log.LogWarning($"{at}: {e.Message}"); }
                        foreach (var st in dirty) st.SaveActions();
                    }
                    if (n == 0) Plugin.Log.LogWarning($"{at}: nothing matched");
                    else Plugin.Verbose($"{at} = {Json.Write(k.Value)} ({n})");
                }
            }
        }

        private static int Variable(PlayMakerFSM fsm, string name, object v)
        {
            // FindVariable returns null for an unknown name (GetFsmFloat would make a detached placeholder)
            var nv = fsm.FsmVariables.FindVariable(name);
            if (nv == null) return 0;
            Assign(nv, v);
            return 1;
        }

        private static int SetProperty(PlayMakerFSM fsm, string property, object v, HashSet<HutongGames.PlayMaker.FsmState> dirty)
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
                    Assign(param, v);
                    dirty.Add(st);
                    n++;
                }
            return n;
        }

        // "<State>/<Action>[n].<field>"
        private static int ActionField(PlayMakerFSM fsm, string key, object v, HashSet<HutongGames.PlayMaker.FsmState> dirty)
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
            if (cur is HutongGames.PlayMaker.NamedVariable nv)
            {
                if (nv.UseVariable) throw new Exception($"it reads variable '{nv.Name}'; set that variable instead");
                Assign(nv, v);
            }
            else f.SetValue(a, Convert(v, f.FieldType));
            dirty.Add(st);
            return 1;
        }

        private static void Assign(HutongGames.PlayMaker.NamedVariable nv, object v)
        {
            switch (nv)
            {
                case HutongGames.PlayMaker.FsmFloat x: x.Value = (float)Convert(v, typeof(float)); break;
                case HutongGames.PlayMaker.FsmInt x: x.Value = (int)Convert(v, typeof(int)); break;
                case HutongGames.PlayMaker.FsmBool x: x.Value = (bool)Convert(v, typeof(bool)); break;
                case HutongGames.PlayMaker.FsmString x: x.Value = (string)Convert(v, typeof(string)); break;
                case HutongGames.PlayMaker.FsmVector3 x: x.Value = (Vector3)Convert(v, typeof(Vector3)); break;
                default: throw new Exception($"{nv.GetType().Name} values are not supported");
            }
        }

        // sets path[i..] on obj; returns obj, so a struct on the way is written back by its parent
        private static object Set(object obj, string[] path, int i, object json)
        {
            if (obj == null) throw new Exception($"'{path[i - 1]}' is null");
            string step = path[i];
            bool last = i == path.Length - 1;

            if (obj is IList list && int.TryParse(step, out int idx))
            {
                if (idx < 0 || idx >= list.Count) throw new Exception($"index {idx} out of range ({list.Count})");
                var et = ElementType(list.GetType());
                list[idx] = last ? Convert(json, et) : Set(list[idx], path, i + 1, json);
                return obj;
            }

            var t = obj.GetType();
            var f = FindField(t, step);
            if (f != null)
            {
                f.SetValue(obj, last ? Convert(json, f.FieldType) : Set(f.GetValue(obj), path, i + 1, json));
                return obj;
            }
            var p = t.GetProperty(step, Flags);
            if (p != null)
            {
                if (last || p.PropertyType.IsValueType)
                {
                    if (!p.CanWrite) throw new Exception($"'{step}' is read-only");
                    p.SetValue(obj, last ? Convert(json, p.PropertyType) : Set(p.GetValue(obj), path, i + 1, json));
                }
                else Set(p.GetValue(obj), path, i + 1, json);
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

        private static object Convert(object v, Type t)
        {
            if (t == typeof(object)) return v;
            if (t.IsEnum) return Enum.Parse(t, v.ToString(), true);
            if (t == typeof(string)) return v?.ToString();
            if (t == typeof(bool)) return v is bool b ? b : bool.Parse(v.ToString());
            if (t == typeof(float)) return System.Convert.ToSingle(v, CultureInfo.InvariantCulture);
            if (t == typeof(double)) return System.Convert.ToDouble(v, CultureInfo.InvariantCulture);
            if (t == typeof(int)) return System.Convert.ToInt32(v, CultureInfo.InvariantCulture);
            if (t == typeof(long)) return System.Convert.ToInt64(v, CultureInfo.InvariantCulture);
            var arr = v as List<object>;
            float F(int k) => System.Convert.ToSingle(arr[k], CultureInfo.InvariantCulture);
            if (t == typeof(Vector2)) return new Vector2(F(0), F(1));
            if (t == typeof(Vector3)) return new Vector3(F(0), F(1), F(2));
            if (t == typeof(Vector4)) return new Vector4(F(0), F(1), F(2), F(3));
            if (t == typeof(Quaternion)) return arr.Count == 3 ? Quaternion.Euler(F(0), F(1), F(2)) : new Quaternion(F(0), F(1), F(2), F(3));
            if (t == typeof(Color)) return new Color(F(0), F(1), F(2), arr.Count > 3 ? F(3) : 1f);
            if (t == typeof(AnimationCurve))
                return new AnimationCurve(arr.Cast<List<object>>().Select(k => new Keyframe(System.Convert.ToSingle(k[0], CultureInfo.InvariantCulture),
                                                                                             System.Convert.ToSingle(k[1], CultureInfo.InvariantCulture))).ToArray());
            if (t.IsArray && arr != null)
            {
                var a = Array.CreateInstance(t.GetElementType(), arr.Count);
                for (int k = 0; k < arr.Count; k++) a.SetValue(Convert(arr[k], t.GetElementType()), k);
                return a;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) && arr != null)
            {
                var l = (IList)Activator.CreateInstance(t);
                foreach (var e in arr) l.Add(Convert(e, t.GetGenericArguments()[0]));
                return l;
            }
            throw new Exception($"cannot convert {Json.Write(v)} to {t.Name}");
        }
    }
}

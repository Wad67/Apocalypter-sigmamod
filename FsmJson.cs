using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SigmaMod
{
    // PlayMaker FSMs as JSON, both ways. Build makes a PlayMakerFSM from a definition; Export writes a
    // game FSM in the same form, to read or start from.
    //
    //   "<FsmName>": {
    //     "variables": { "float": { "Fuel": 0 }, "int": {...}, "bool": {...}, "string": {...}, "vector2": {...},
    //                    "vector3": {...}, "quaternion": {...}, "color": {...}, "rect": {...},
    //                    "gameObject": { "tank": "fuel_tank" }, "object": {...}, "material": {...}, "texture": {...} },
    //     "globalEvents": [ "SOME_EVENT" ],              events other FSMs may send to this one
    //     "start": "<state>",                             default: the first state
    //     "global": { "<EVENT>": "<state>" },             global transitions
    //     "states": {
    //       "<state>": {
    //         "actions": [ { "<ActionType>": { "<field>": value, ..., "_enabled": false } }, ... ],
    //         "transitions": { "<EVENT>": "<state>" } } } }
    //
    // Action types are PlayMaker's (HutongGames.PlayMaker.Actions) or the game's, by name. Fields take
    // JsonValues: "{var}" binds a variable, "owner" is the FSM's own object, objects by name, sigma_id,
    // /path or root. Fields left out keep the action's defaults (its Reset). Events are created by name;
    // FINISHED and the other system events are PlayMaker's own.
    //
    // "use": "<name>" starts from the definition of that name in the file's top-level "fsms" block; the
    // rest of the entry is merged over it (objects key by key, anything else replaced), e.g. a shared
    // hinge FSM with "variables": { "string": { "AcceptID": "engine" } }.
    internal static class FsmJson
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public;

        public static PlayMakerFSM Build(GameObject go, string name, Dictionary<string, object> def, JsonValues ctx, string where)
        {
            if (def.TryGetValue("use", out var use))
            {
                if (!(use is string un) || ctx.Library == null || !ctx.Library.TryGetValue(un, out var lib) || !(lib is Dictionary<string, object> baseDef))
                    throw new Exception($"\"use\": no definition '{use}' in the file's top-level \"fsms\"");
                def = Merge(baseDef, def.Where(kv => kv.Key != "use").ToDictionary(kv => kv.Key, kv => kv.Value));
            }
            var pm = go.AddComponent<PlayMakerFSM>();
            pm.Reset();
            var fsm = pm.Fsm;
            fsm.Name = name;
            ctx.Events = new List<FsmEvent>();
            try
            {
                if (def.TryGetValue("variables", out var vv)) fsm.Variables = Variables(vv as Dictionary<string, object>, ctx, where);

                if (def.TryGetValue("globalEvents", out var gv) && gv is List<object> globals)
                    foreach (var g in globals.OfType<string>()) ctx.Event(g).IsGlobal = true;

                var states = new List<FsmState>();
                var sd = def.TryGetValue("states", out var sv) ? sv as Dictionary<string, object> : null;
                if (sd == null || sd.Count == 0) throw new Exception("no \"states\"");
                foreach (var kv in sd)
                {
                    var st = new FsmState(fsm) { Name = kv.Key };
                    var body = kv.Value as Dictionary<string, object> ?? new Dictionary<string, object>();
                    var actions = new List<FsmStateAction>();
                    if (body.TryGetValue("actions", out var av) && av is List<object> al)
                        for (int i = 0; i < al.Count; i++)
                        {
                            try { actions.Add(Action(al[i], ctx)); }
                            catch (Exception e) { Plugin.Log.LogWarning($"{where}: fsm '{name}' state '{kv.Key}' action {i}: {e.Message}; left out"); }
                        }
                    st.Actions = actions.ToArray();
                    st.Transitions = Transitions(body.TryGetValue("transitions", out var tv) ? tv : null, ctx);
                    states.Add(st);
                }
                fsm.States = states.ToArray();
                fsm.StartState = def.TryGetValue("start", out var ss) && ss is string s0 ? s0 : states[0].Name;
                fsm.GlobalTransitions = Transitions(def.TryGetValue("global", out var gt) ? gt : null, ctx);
                fsm.Events = ctx.Events.ToArray();

                var names = new HashSet<string>(states.Select(x => x.Name));
                if (!names.Contains(fsm.StartState)) Plugin.Log.LogWarning($"{where}: fsm '{name}': start state '{fsm.StartState}' does not exist");
                foreach (var tr in states.SelectMany(x => x.Transitions).Concat(fsm.GlobalTransitions))
                    if (!names.Contains(tr.ToState)) Plugin.Log.LogWarning($"{where}: fsm '{name}': transition {tr.EventName} goes to a state that does not exist: '{tr.ToState}'");
                foreach (var st in states)
                    foreach (var a in st.Actions)
                        foreach (var miss in Unbound(a, fsm))
                            Plugin.Log.LogWarning($"{where}: fsm '{name}' state '{st.Name}' {a.GetType().Name}: no variable '{miss}' (local or global)");

                // the serialized form is what copies of the template get
                foreach (var st in states) st.SaveActions();
            }
            finally { ctx.Events = null; }
            return pm;
        }

        private static Dictionary<string, object> Merge(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            var r = new Dictionary<string, object>(a);
            foreach (var kv in b)
                r[kv.Key] = r.TryGetValue(kv.Key, out var old) && old is Dictionary<string, object> od && kv.Value is Dictionary<string, object> nd ? Merge(od, nd) : kv.Value;
            return r;
        }

        private static FsmVariables Variables(Dictionary<string, object> d, JsonValues ctx, string where)
        {
            var v = new FsmVariables();
            if (d == null) return v;
            T[] Make<T>(string key) where T : NamedVariable, new()
            {
                if (!d.TryGetValue(key, out var x)) return new T[0];
                if (!(x is Dictionary<string, object> vars)) throw new Exception($"variables.{key} must be an object of name: value");
                return vars.Select(kv =>
                {
                    var nv = (T)ctx.Convert(kv.Value, typeof(T));
                    nv.UseVariable = false;
                    nv.SetName(kv.Key);
                    return nv;
                }).ToArray();
            }
            v.FloatVariables = Make<FsmFloat>("float");
            v.IntVariables = Make<FsmInt>("int");
            v.BoolVariables = Make<FsmBool>("bool");
            v.StringVariables = Make<FsmString>("string");
            v.Vector2Variables = Make<FsmVector2>("vector2");
            v.Vector3Variables = Make<FsmVector3>("vector3");
            v.QuaternionVariables = Make<FsmQuaternion>("quaternion");
            v.ColorVariables = Make<FsmColor>("color");
            v.RectVariables = Make<FsmRect>("rect");
            v.GameObjectVariables = Make<FsmGameObject>("gameObject");
            v.ObjectVariables = Make<FsmObject>("object");
            v.MaterialVariables = Make<FsmMaterial>("material");
            v.TextureVariables = Make<FsmTexture>("texture");
            foreach (var k in d.Keys)
                if (!new[] { "float", "int", "bool", "string", "vector2", "vector3", "quaternion", "color", "rect", "gameObject", "object", "material", "texture" }.Contains(k))
                    Plugin.Log.LogWarning($"{where}: variables.{k}: unknown variable type");
            return v;
        }

        private static FsmStateAction Action(object def, JsonValues ctx)
        {
            if (!(def is Dictionary<string, object> d) || d.Count != 1) throw new Exception("an action is { \"<ActionType>\": { fields } }");
            var kv = d.First();
            Type type;
            try { type = Types.Find("HutongGames.PlayMaker.Actions." + kv.Key, typeof(FsmStateAction)); }
            catch { type = Types.Find(kv.Key, typeof(FsmStateAction)); }
            var a = (FsmStateAction)Activator.CreateInstance(type);
            a.Reset();
            if (kv.Value is Dictionary<string, object> fields)
                foreach (var f in fields)
                {
                    if (f.Key == "_enabled") { a.Enabled = !(f.Value is bool b) || b; continue; }
                    if (f.Key == "_name") { a.Name = f.Value as string; continue; }
                    try { ctx.SetMember(a, f.Key, f.Value); }
                    catch (Exception e) { throw new Exception($"{kv.Key}.{f.Key}: {e.Message}"); }
                }
            else if (kv.Value != null) throw new Exception($"{kv.Key}: fields must be an object");
            return a;
        }

        private static FsmTransition[] Transitions(object def, JsonValues ctx)
        {
            if (!(def is Dictionary<string, object> d)) return new FsmTransition[0];
            return d.Select(kv => new FsmTransition { FsmEvent = ctx.Event(kv.Key), ToState = kv.Value as string ?? "" }).ToArray();
        }

        // variable names an action uses that the FSM does not have (top-level parameters)
        private static IEnumerable<string> Unbound(FsmStateAction a, Fsm fsm)
        {
            foreach (var f in a.GetType().GetFields(Fields))
            {
                var v = f.GetValue(a);
                IEnumerable<NamedVariable> vars = v is NamedVariable nv ? new[] { nv }
                    : v is FsmOwnerDefault od && od.OwnerOption == OwnerDefaultOption.SpecifyGameObject && od.GameObject != null ? new NamedVariable[] { od.GameObject }
                    : v is IEnumerable e && !(v is string) ? e.OfType<NamedVariable>() : Enumerable.Empty<NamedVariable>();
                foreach (var x in vars)
                    if (x != null && x.UseVariable && !string.IsNullOrEmpty(x.Name) && fsm.Variables.GetVariable(x.Name) == null) yield return x.Name;
            }
        }

        // ================================================================ export

        // refName: how to write an object reference ("root", "/path", "asset:Type/name", ...)
        public static Dictionary<string, object> Export(PlayMakerFSM pm, Func<UnityEngine.Object, string> refName)
        {
            var fsm = pm.Fsm;
            var d = new Dictionary<string, object>();
            var vars = new Dictionary<string, object>();
            void Vars<T>(string key, T[] arr) where T : NamedVariable
            {
                if (arr == null || arr.Length == 0) return;
                var o = new Dictionary<string, object>();
                foreach (var v in arr) o[v.Name] = Constant(v, refName);
                vars[key] = o;
            }
            var fv = fsm.Variables;
            Vars("float", fv.FloatVariables); Vars("int", fv.IntVariables); Vars("bool", fv.BoolVariables); Vars("string", fv.StringVariables);
            Vars("vector2", fv.Vector2Variables); Vars("vector3", fv.Vector3Variables); Vars("quaternion", fv.QuaternionVariables);
            Vars("color", fv.ColorVariables); Vars("rect", fv.RectVariables); Vars("gameObject", fv.GameObjectVariables);
            Vars("object", fv.ObjectVariables); Vars("material", fv.MaterialVariables); Vars("texture", fv.TextureVariables);
            if (vars.Count > 0) d["variables"] = vars;
            var globals = fsm.Events.Where(e => e != null && e.IsGlobal && !e.IsSystemEvent).Select(e => (object)e.Name).ToList();
            if (globals.Count > 0) d["globalEvents"] = globals;
            d["start"] = fsm.StartState;
            if (fsm.GlobalTransitions.Length > 0) d["global"] = Transitions(fsm.GlobalTransitions);
            var states = new Dictionary<string, object>();
            foreach (var st in fsm.States)
            {
                var s = new Dictionary<string, object>();
                var actions = new List<object>();
                FsmStateAction[] list;
                try { list = st.Actions; }
                catch (Exception e) { s["error"] = e.Message; list = new FsmStateAction[0]; }
                foreach (var a in list)
                {
                    if (a == null) continue;
                    var fields = new Dictionary<string, object>();
                    foreach (var f in a.GetType().GetFields(Fields))
                    {
                        if (f.IsNotSerialized || f.IsInitOnly) continue;
                        try { fields[f.Name] = Value(f.GetValue(a), refName, 0); }
                        catch (Exception e) { fields[f.Name] = "?" + e.Message; }
                    }
                    if (!a.Enabled) fields["_enabled"] = false;
                    actions.Add(new Dictionary<string, object> { [a.GetType().Name] = fields });
                }
                if (actions.Count > 0) s["actions"] = actions;
                if (st.Transitions.Length > 0) s["transitions"] = Transitions(st.Transitions);
                states[st.Name] = s;
            }
            d["states"] = states;
            return d;
        }

        private static Dictionary<string, object> Transitions(FsmTransition[] list)
        {
            var d = new Dictionary<string, object>();
            foreach (var t in list) d[t.EventName ?? ""] = t.ToState;
            return d;
        }

        private static object Constant(NamedVariable v, Func<UnityEngine.Object, string> refName)
        {
            switch (v)
            {
                case FsmGameObject g: return g.Value != null ? refName(g.Value) : null;
                case FsmObject o: return o.Value != null ? refName(o.Value) : null;
                case FsmEnum e: return e.Value?.ToString();
                case FsmArray _: return "?array";
            }
            var vp = v.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            return vp != null ? Plain(vp.GetValue(v, null), refName) : "?" + v.GetType().Name;
        }

        private static object Value(object v, Func<UnityEngine.Object, string> refName, int depth)
        {
            switch (v)
            {
                case null: return null;
                case NamedVariable nv: return nv.UseVariable ? "{" + nv.Name + "}" : Constant(nv, refName);
                case FsmOwnerDefault od: return od.OwnerOption == OwnerDefaultOption.UseOwner ? "owner" : Value(od.GameObject, refName, depth + 1);
                case FsmEvent e: return e.Name;
            }
            if (depth > 6) return "?deep";
            var t = v.GetType();
            if (v is UnityEngine.Object || t.IsPrimitive || t.IsEnum || v is string || t.IsValueType) return Plain(v, refName);
            if (v is IEnumerable en) return en.Cast<object>().Select(x => Value(x, refName, depth + 1)).ToList();
            var o = new Dictionary<string, object>();
            foreach (var f in t.GetFields(Fields))
            {
                if (f.IsNotSerialized || typeof(Type).IsAssignableFrom(f.FieldType) || typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                o[f.Name] = Value(f.GetValue(v), refName, depth + 1);
            }
            return o;
        }

        private static object Plain(object v, Func<UnityEngine.Object, string> refName)
        {
            switch (v)
            {
                case null: return null;
                case UnityEngine.Object u: return u != null ? refName(u) : null;
                case Enum e: return e.ToString();
                case Vector2 a: return new List<object> { (double)a.x, (double)a.y };
                case Vector3 a: return new List<object> { (double)a.x, (double)a.y, (double)a.z };
                case Vector4 a: return new List<object> { (double)a.x, (double)a.y, (double)a.z, (double)a.w };
                case Quaternion q: return new List<object> { (double)q.x, (double)q.y, (double)q.z, (double)q.w };
                case Color c: return new List<object> { (double)c.r, (double)c.g, (double)c.b, (double)c.a };
                case Rect r: return new List<object> { (double)r.x, (double)r.y, (double)r.width, (double)r.height };
                case float f: return (double)f;
                case int i: return (double)i;
                case bool _: case string _: case double _: return v;
            }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }
}

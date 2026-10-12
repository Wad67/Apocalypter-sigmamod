using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SigmaMod
{
    // Shorthand in a template's .json, expanded to the long form before anything else reads the file. The long form
    // is unchanged and always works; expanding it again changes nothing, so the expansion of a file is a file that
    // loads the same (VerboseLog writes it to BepInEx/SigmaMod-expanded, GltfTest prints it offline).
    //
    // Libraries, top level next to the templates (all optional):
    //   "fsms":    { "<name>": <FSM> }               whole FSMs
    //   "states":  { "<name>": <state> }             single states
    //   "actions": { "<name>": [ <action>, ... ] }   runs of actions
    //
    // "use" takes one from its library, anywhere that kind of thing goes; "with" fills its $placeholders:
    //   "playmaker": { "TrailerAttach": { "use": "TrailerAttach", "with": { "roll": "Locked" } } }
    //   "states":    { "clear": { "use": "unjoint", "with": { "then": "attach" } } }
    //   "actions":   [ { "use": "joint", "with": { "joint": "{ConfigurableJoint}" } }, ... ]
    // A placeholder that is the whole string ("$roll") takes the value as it is (a number stays a number); inside a
    // longer string it is replaced as text. A library entry may give defaults: "params": { "roll": "Free" }. For an
    // FSM or state, whatever else is written next to "use" is merged over it (objects key by key, anything else
    // replaced), as before.
    //
    // SetProperties / GetProperties: one SetProperty / GetProperty per property, the parameter slot and enum type
    // found from the property's own type:
    //   { "SetProperties": { "on": "{ConfigurableJoint}", "everyFrame": false,
    //                        "xMotion": "Locked", "anchor": "{attacher_pos}", "connectedBody": "{Rigidbody}" } }
    //   { "GetProperties": { "on": "{vc}", "powertrain.engine.OutputRPM": "{rpm}" } }
    // "on" is a typed object variable ("{name}", its declared type), a gameObject variable or object (GameObject),
    // or "<object>:<Component>". "type" names the type where none of those says it (a global variable).
    internal static class Shorthand
    {
        // a type by full or short name (abstract ones too), null when there is none; set by the host
        public static Func<string, Type> FindType;

        private static readonly string[] Libraries = { "fsms", "states", "actions" };

        private sealed class Ctx
        {
            public Dictionary<string, object> Fsms, States, Actions;
            public Action<string> Warn;
        }

        public static Dictionary<string, object> Expand(Dictionary<string, object> doc, Action<string> warn)
        {
            var c = new Ctx { Fsms = Lib(doc, "fsms"), States = Lib(doc, "states"), Actions = Lib(doc, "actions"), Warn = warn ?? (_ => { }) };
            var res = new Dictionary<string, object>();
            foreach (var kv in doc)
            {
                if (Libraries.Contains(kv.Key)) continue;
                res[kv.Key] = kv.Value is Dictionary<string, object> spec ? Spec(spec, kv.Key, c) : kv.Value;
            }
            return res;
        }

        private static Dictionary<string, object> Lib(Dictionary<string, object> doc, string key) =>
            doc.TryGetValue(key, out var v) && v is Dictionary<string, object> d ? d : new Dictionary<string, object>();

        // a template, or one of its "nodes": its "playmaker" FSMs expanded
        private static Dictionary<string, object> Spec(Dictionary<string, object> spec, string where, Ctx c)
        {
            var res = new Dictionary<string, object>(spec);
            if (spec.TryGetValue("playmaker", out var pv) && pv is Dictionary<string, object> pm)
            {
                var fsms = new Dictionary<string, object>();
                foreach (var kv in pm)
                    fsms[kv.Key] = kv.Value is Dictionary<string, object> def ? Fsm(def, $"{where}: fsm '{kv.Key}'", c) : kv.Value;
                res["playmaker"] = fsms;
            }
            if (spec.TryGetValue("nodes", out var nv) && nv is Dictionary<string, object> nodes)
            {
                var n = new Dictionary<string, object>();
                foreach (var kv in nodes)
                    n[kv.Key] = kv.Value is Dictionary<string, object> node ? Spec(node, $"{where}/{kv.Key}", c) : kv.Value;
                res["nodes"] = n;
            }
            return res;
        }

        private static Dictionary<string, object> Fsm(Dictionary<string, object> def, string where, Ctx c)
        {
            def = Use(def, c.Fsms, "fsms", where, c, 0);
            var vars = def.TryGetValue("variables", out var vv) ? vv as Dictionary<string, object> : null;
            if (def.TryGetValue("states", out var sv) && sv is Dictionary<string, object> states)
            {
                var res = new Dictionary<string, object>();
                foreach (var kv in states)
                {
                    if (!(kv.Value is Dictionary<string, object> st)) { res[kv.Key] = kv.Value; continue; }
                    string w = $"{where} state '{kv.Key}'";
                    st = Use(st, c.States, "states", w, c, 0);
                    if (st.TryGetValue("actions", out var av) && av is List<object> al)
                    {
                        st = new Dictionary<string, object>(st);
                        st["actions"] = Actions(al, vars, w, c, 0);
                    }
                    res[kv.Key] = st;
                }
                def = new Dictionary<string, object>(def);
                def["states"] = res;
            }
            return def;
        }

        // "use": the library entry with "with" filled in, then what is written next to it merged over it
        private static Dictionary<string, object> Use(Dictionary<string, object> d, Dictionary<string, object> lib, string libName,
                                                      string where, Ctx c, int depth)
        {
            if (!d.TryGetValue("use", out var u))
            {
                if (!d.ContainsKey("params")) return d;
                var r = new Dictionary<string, object>(d);
                r.Remove("params");
                return r;
            }
            if (depth > 16) { c.Warn($"{where}: \"use\" nested too deep"); return d; }
            if (!(u is string name) || !lib.TryGetValue(name, out var bv) || !(bv is Dictionary<string, object> baseDef))
            {
                c.Warn($"{where}: \"use\": no '{u}' in the file's top-level \"{libName}\"");
                return d;
            }
            var pars = Params(baseDef, d, where, c);
            var b = (Dictionary<string, object>)Subst(baseDef, pars, where, c);
            b.Remove("params");
            b = Use(b, lib, libName, where, c, depth + 1);
            var rest = d.Where(kv => kv.Key != "use" && kv.Key != "with").ToDictionary(kv => kv.Key, kv => kv.Value);
            return Merge(b, rest);
        }

        private static Dictionary<string, object> Params(Dictionary<string, object> baseDef, Dictionary<string, object> d, string where, Ctx c)
        {
            var p = new Dictionary<string, object>();
            if (baseDef.TryGetValue("params", out var dp) && dp is Dictionary<string, object> defaults)
                foreach (var kv in defaults) p[kv.Key] = kv.Value;
            if (d.TryGetValue("with", out var wv))
            {
                if (wv is Dictionary<string, object> with) foreach (var kv in with) p[kv.Key] = kv.Value;
                else c.Warn($"{where}: \"with\" must be an object of name: value");
            }
            return p;
        }

        private static List<object> Actions(List<object> list, Dictionary<string, object> vars, string where, Ctx c, int depth)
        {
            var res = new List<object>();
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                string w = $"{where} action {i}";
                if (item is Dictionary<string, object> d && d.TryGetValue("use", out var u) && d.Keys.All(k => k == "use" || k == "with"))
                {
                    if (depth > 16) { c.Warn($"{w}: \"use\" nested too deep"); continue; }
                    if (!(u is string name) || !c.Actions.TryGetValue(name, out var lv) || !(lv is List<object> run))
                    {
                        c.Warn($"{w}: \"use\": no '{u}' in the file's top-level \"actions\"");
                        res.Add(item);
                        continue;
                    }
                    var pars = Params(new Dictionary<string, object>(), d, w, c);
                    res.AddRange(Actions((List<object>)Subst(run, pars, w, c), vars, $"{w} ('{name}')", c, depth + 1));
                    continue;
                }
                if (item is Dictionary<string, object> a && a.Count == 1)
                {
                    var kv = a.First();
                    if ((kv.Key == "SetProperties" || kv.Key == "GetProperties") && kv.Value is Dictionary<string, object> f)
                    {
                        try { res.AddRange(Properties(f, kv.Key == "SetProperties", vars)); }
                        catch (Exception e) { c.Warn($"{w}: {kv.Key}: {e.Message}"); res.Add(item); }
                        continue;
                    }
                }
                res.Add(item);
            }
            return res;
        }

        // ---------------------------------------------------------------- SetProperties / GetProperties

        private static IEnumerable<object> Properties(Dictionary<string, object> f, bool set, Dictionary<string, object> vars)
        {
            if (!(f.TryGetValue("on", out var ov) && ov is string on)) throw new Exception("\"on\" (the object) is missing");
            Type t = f.TryGetValue("type", out var tv) && tv is string tn ? Need(tn) : TargetType(on, vars);
            f.TryGetValue("everyFrame", out var every);
            var list = new List<object>();
            foreach (var kv in f)
            {
                if (kv.Key == "on" || kv.Key == "type" || kv.Key == "everyFrame") continue;
                var mt = MemberType(t, kv.Key);
                var tp = new Dictionary<string, object>
                {
                    ["TargetObject"] = on,
                    ["TargetTypeName"] = t.FullName,
                    ["PropertyName"] = kv.Key,
                    ["setProperty"] = set,
                };
                bool bound = kv.Value is string s && s.StartsWith("{") && s.EndsWith("}");
                if (!set && !bound) throw new Exception($"{kv.Key}: GetProperties stores into a variable, \"{{name}}\"");
                tp[Slot(mt)] = mt.IsEnum && !bound
                    ? new Dictionary<string, object> { ["type"] = mt.FullName, ["value"] = kv.Value }
                    : kv.Value;
                var fields = new Dictionary<string, object> { ["targetProperty"] = tp };
                if (every != null) fields["everyFrame"] = every;
                list.Add(new Dictionary<string, object> { [set ? "SetProperty" : "GetProperty"] = fields });
            }
            return list;
        }

        private static Type Need(string name)
        {
            var t = FindType?.Invoke(name);
            if (t == null) throw new Exception($"no type named '{name}'");
            return t;
        }

        private static Type TargetType(string on, Dictionary<string, object> vars)
        {
            if (on.StartsWith("{") && on.EndsWith("}"))
            {
                string v = on.Substring(1, on.Length - 2);
                if (vars != null && vars.TryGetValue("object", out var ov) && ov is Dictionary<string, object> objs && objs.TryGetValue(v, out var o))
                {
                    if (o is Dictionary<string, object> typed && typed.TryGetValue("type", out var tn) && tn is string ts) return Need(ts);
                    throw new Exception($"object variable '{v}' has no type; declare it {{ \"type\": \"<type>\", \"value\": null }} or give \"type\"");
                }
                if (vars != null && vars.TryGetValue("gameObject", out var gv) && gv is Dictionary<string, object> gos && gos.ContainsKey(v))
                    return Need("UnityEngine.GameObject");
                throw new Exception($"'{v}' is not an object or gameObject variable of this FSM (a global?); give \"type\"");
            }
            int colon = on.LastIndexOf(':');
            if (colon > 0 && !on.StartsWith("asset:") && !on.StartsWith("file:")) return Need(on.Substring(colon + 1));
            return Need("UnityEngine.GameObject");
        }

        // a.b.c through public fields and properties, as FsmProperty walks it
        private static Type MemberType(Type t, string path)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy;
            foreach (var part in path.Split('.'))
            {
                var p = t.GetProperty(part, all);
                if (p != null) { t = p.PropertyType; continue; }
                var fi = t.GetField(part, all);
                if (fi != null) { t = fi.FieldType; continue; }
                throw new Exception($"{t.FullName} has no field or property '{part}'");
            }
            return t;
        }

        // the FsmProperty parameter field for a type (by name: offline the types come from another load context)
        private static string Slot(Type t)
        {
            switch (t.FullName)
            {
                case "System.Boolean": return "BoolParameter";
                case "System.Single": case "System.Double": return "FloatParameter";
                case "System.Int32": case "System.Int16": case "System.Byte": case "System.UInt32": return "IntParameter";
                case "System.String": return "StringParameter";
                case "UnityEngine.Vector2": return "Vector2Parameter";
                case "UnityEngine.Vector3": return "Vector3Parameter";
                case "UnityEngine.Rect": return "RectParamater";
                case "UnityEngine.Quaternion": return "QuaternionParameter";
                case "UnityEngine.Color": return "ColorParameter";
                case "UnityEngine.GameObject": return "GameObjectParameter";
            }
            if (t.IsEnum) return "EnumParameter";
            if (t.IsArray) return "ArrayParameter";
            for (var b = t; b != null; b = b.BaseType)
            {
                if (b.FullName == "UnityEngine.Material") return "MaterialParameter";
                if (b.FullName == "UnityEngine.Texture") return "TextureParameter";
                if (b.FullName == "UnityEngine.Object") return "ObjectParameter";
            }
            throw new Exception($"FsmProperty has no parameter for {t.FullName}");
        }

        // ---------------------------------------------------------------- helpers

        // a deep copy with $placeholders filled in
        private static object Subst(object v, Dictionary<string, object> p, string where, Ctx c)
        {
            switch (v)
            {
                case Dictionary<string, object> d:
                    return d.ToDictionary(kv => kv.Key, kv => Subst(kv.Value, p, where, c));
                case List<object> l:
                    return l.Select(x => Subst(x, p, where, c)).ToList();
                case string s when s.IndexOf('$') >= 0:
                    if (s.Length > 1 && s[0] == '$' && p.TryGetValue(s.Substring(1), out var whole)) return Subst(whole, new Dictionary<string, object>(), where, c);
                    foreach (var kv in p.OrderByDescending(kv => kv.Key.Length))
                        if (kv.Value is string || kv.Value is double || kv.Value is bool)
                            s = s.Replace("$" + kv.Key, Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture));
                    if (s.Length > 1 && s[0] == '$' && s.Skip(1).All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                        c.Warn($"{where}: no value for {s}");
                    return s;
                default:
                    return v;
            }
        }

        private static Dictionary<string, object> Merge(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            var r = new Dictionary<string, object>(a);
            foreach (var kv in b)
                r[kv.Key] = r.TryGetValue(kv.Key, out var old) && old is Dictionary<string, object> od && kv.Value is Dictionary<string, object> nd ? Merge(od, nd) : kv.Value;
            return r;
        }
    }
}

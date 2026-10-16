using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;
using MoonSharp.Interpreter.Loaders;
using UnityEngine;

namespace SigmaMod
{
    // Lua (MoonSharp, an interpreter in plain C#). Two uses:
    //
    //   - An asset's <name>.lua next to its <name>.glb builds what its .json would: `template "<sigma_id>" { ... }`
    //     takes the same document as the .json's entry for that id (Lua tables for objects and lists), `fsms { ... }`,
    //     `states { ... }` and `actions { ... }` the same libraries. Scripts `require` shared parts (BepInEx/plugins/SigmaMod/lua, and the asset's own folder),
    //     so a part written once is a function call. A .json next to it still loads; the .lua's entries win. Saving the
    //     script, or any library it can require, rebuilds the asset (with Tools.HotReload).
    //   - The console (LuaConsole): one Lua state for the session, for poking the running game.
    //
    // Both see C# through reflection: clr.type("Name") is a type's statics (live code's newest generation first),
    // clr.new("Name", ...) makes one, objects passed to Lua are usable as they are (fields, properties, methods).
    public static class Lua
    {
        public static string LibraryDir => Path.Combine(Path.GetDirectoryName(typeof(Lua).Assembly.Location) ?? Paths.PluginPath, "lua");
        private static bool _registered;

        internal static Script NewScript(string assetDir, Action<string> print)
        {
            if (!_registered)
            {
                UserData.RegistrationPolicy = InteropRegistrationPolicy.Automatic;
                _registered = true;
            }
            var s = new Script(CoreModules.Preset_SoftSandbox | CoreModules.LoadMethods);
            var dirs = new List<string>();
            if (assetDir != null) dirs.Add(assetDir);
            dirs.Add(LibraryDir);
            s.Options.ScriptLoader = new Loader { ModulePaths = dirs.Select(d => Path.Combine(d, "?.lua")).ToArray() };
            s.Options.DebugPrint = print;

            var clr = new Table(s);
            clr["type"] = (Func<string, DynValue>)(name =>
            {
                var t = Types.Any(name);
                return t == null ? DynValue.Nil : UserData.CreateStatic(t);
            });
            clr["new"] = DynValue.NewCallback((ctx, args) =>
            {
                string name = args.AsType(0, "new", DataType.String).String;
                var t = Types.Any(name) ?? throw new ScriptRuntimeException("no type '" + name + "'");
                var rest = new object[args.Count - 1];
                for (int i = 1; i < args.Count; i++) rest[i - 1] = args[i].ToObject();
                return DynValue.FromObject(ctx.GetScript(), Activator.CreateInstance(t, rest));
            });
            clr["objects"] = (Func<string, Table>)(name =>
            {
                var t = Types.Any(name) ?? throw new ScriptRuntimeException("no type '" + name + "'");
                var list = new Table(s);
                foreach (var o in Resources.FindObjectsOfTypeAll(t))
                    if (o != null && (!(o is Component c) || c.gameObject.scene.IsValid())) list.Append(UserData.Create(o));
                return list;
            });
            clr["find"] = (Func<string, GameObject>)GameObject.Find;
            clr["component"] = (Func<GameObject, string, Component>)((go, name) =>
            {
                var t = Types.Any(name);
                return go == null || t == null ? null : go.GetComponent(t);
            });
            s.Globals["clr"] = clr;
            s.Globals["vec"] = (Func<float, float, float, Vector3>)((x, y, z) => new Vector3(x, y, z));
            s.Globals["log"] = (Action<string>)(m => Plugin.Log.LogInfo("[lua] " + m));
            // the .json's null: there, but empty (a Lua nil isn't there at all; an FSM variable declared empty needs this)
            s.Globals["null"] = Null;
            return s;
        }

        // ---------------------------------------------------------------- asset scripts

        // a template's Lua behaviour (its script's functions, the script kept alive with them)
        internal sealed class Hooks { public Script Script; public DynValue Spawn, Update; public string Source; }
        internal static readonly Dictionary<string, Hooks> Behaviours = new Dictionary<string, Hooks>();

        // a copy of a template is made: its behaviour, if the script gave one, runs on it
        internal static void Attach(GameObject go, string id)
        {
            if (!Behaviours.TryGetValue(id, out var h)) return;
            var b = go.GetComponent<LuaBehaviour>() ?? go.AddComponent<LuaBehaviour>();
            b.Begin(h);
        }

        public static string ScriptPath(string glb) => Path.ChangeExtension(glb, ".lua");

        // what each asset's script required when it last ran (its libraries: their changes are its changes)
        private static readonly Dictionary<string, List<string>> Requires = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        [ThreadStatic] private static List<string> _requiring;

        // MoonSharp's file loader, noting the files a script requires
        private sealed class Loader : FileSystemScriptLoader
        {
            public override object LoadFile(string file, Table globalContext)
            {
                _requiring?.Add(Path.GetFullPath(file));
                return base.LoadFile(file, globalContext);
            }
        }

        // newest write of the asset's script and the libraries it required (only those: a library nothing of it uses
        // changing is no change to it; rebuilding everything rebuilds the places too, and the world under them)
        internal static DateTime Stamp(string glb)
        {
            var lua = ScriptPath(glb);
            if (!File.Exists(lua)) return DateTime.MinValue;
            var stamp = File.GetLastWriteTimeUtc(lua);
            if (Requires.TryGetValue(glb, out var libs))
                foreach (var f in libs)
                {
                    if (!File.Exists(f)) continue;
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > stamp) stamp = t;
                }
            return stamp;
        }

        // the document the asset's script builds: { "<sigma_id>": {...}, "fsms": {...} }, as the .json's would be
        internal static Dictionary<string, object> Document(string glb)
        {
            var path = ScriptPath(glb);
            var doc = new Dictionary<string, object>();
            var s = NewScript(Path.GetDirectoryName(glb), m => Plugin.Log.LogInfo($"[lua {Path.GetFileName(path)}] {m}"));
            s.Globals["template"] = (Func<string, DynValue>)(id => DynValue.NewCallback((ctx, args) =>
            {
                var t = args.Count > 0 ? args[0] : DynValue.Nil;
                if (t.Type != DataType.Table) throw new ScriptRuntimeException($"template \"{id}\" takes a table");
                doc[id] = Plain(t);
                return DynValue.Nil;
            }));
            // what runs on each copy of a template: behaviour "<sigma_id>" { spawn = function(obj, state) ... end,
            // update = function(obj, state, dt) ... end }; the script stays alive for them
            s.Globals["behaviour"] = (Func<string, DynValue>)(id => DynValue.NewCallback((ctx, args) =>
            {
                var t = args.Count > 0 ? args[0] : DynValue.Nil;
                if (t.Type != DataType.Table) throw new ScriptRuntimeException($"behaviour \"{id}\" takes a table");
                Behaviours[id] = new Hooks { Script = s, Spawn = t.Table.Get("spawn"), Update = t.Table.Get("update"), Source = Path.GetFileName(path) };
                return DynValue.Nil;
            }));
            // the libraries the .json keeps at its top: fsms, states, actions ({ "<name>": ... } each)
            foreach (var key in new[] { "fsms", "states", "actions" })
            {
                string k = key;
                s.Globals[k] = (Action<Table>)(t =>
                {
                    if (!(doc.TryGetValue(k, out var have) && have is Dictionary<string, object> lib)) doc[k] = lib = new Dictionary<string, object>();
                    foreach (var kv in (Dictionary<string, object>)Plain(DynValue.NewTable(t))) lib[kv.Key] = kv.Value;
                });
            }
            // this script's earlier behaviours go: it says again which it has
            foreach (var k in Behaviours.Where(kv => kv.Value.Source == Path.GetFileName(path)).Select(kv => kv.Key).ToList()) Behaviours.Remove(k);
            _requiring = new List<string>();
            try { s.DoString(File.ReadAllText(path), null, Path.GetFileName(path)); }
            catch (InterpreterException e) { throw new Exception(Path.GetFileName(path) + ": " + (e.DecoratedMessage ?? e.Message)); }
            finally { Requires[glb] = _requiring; _requiring = null; }
            return doc;
        }

        // a Lua value as the .json reader gives it: objects as dictionaries (in the order they were written), lists as
        // lists, numbers as doubles; a table with keys 1..n is a list, an empty one an object
        public sealed class NullValue { public override string ToString() => "null"; }
        public static readonly NullValue Null = new NullValue();

        internal static object Plain(DynValue v)
        {
            if (v.Type == DataType.UserData && v.UserData.Object is NullValue) return null;
            switch (v.Type)
            {
                case DataType.Nil: case DataType.Void: return null;
                case DataType.Boolean: return v.Boolean;
                case DataType.Number: return v.Number;
                case DataType.String: return v.String;
                case DataType.UserData: return v.UserData.Object;
                case DataType.Table:
                    var t = v.Table;
                    int n = t.Length;
                    if (n > 0 && t.Pairs.Count(p => !p.Value.IsNil()) == n)
                    {
                        var list = new List<object>(n);
                        for (int i = 1; i <= n; i++) list.Add(Plain(t.Get(i)));
                        return list;
                    }
                    var d = new Dictionary<string, object>();
                    foreach (var p in t.Pairs)
                        if (!p.Value.IsNil())   // an option left out (a nil in a table constructor) isn't there; `null` is
                            d[p.Key.Type == DataType.String ? p.Key.String : p.Key.ToPrintString()] = Plain(p.Value);
                    return d;
                default: return null;
            }
        }
    }

    // A template's Lua behaviour on one of its copies: spawn(obj, state) once, update(obj, state, dt) every frame, `state`
    // a table of the copy's own. An error stops it (logged once).
    public sealed class LuaBehaviour : MonoBehaviour
    {
        private Lua.Hooks _h;
        private Table _state;
        private bool _failed;

        internal void Begin(Lua.Hooks h)
        {
            _h = h; _failed = false;
            _state = new Table(h.Script);
            if (h.Spawn.Type == DataType.Function) Call(h.Spawn, DynValue.Nil);
        }

        private void Update()
        {
            if (_h == null || _failed || _h.Update.Type != DataType.Function) return;
            Call(_h.Update, DynValue.NewNumber(Time.deltaTime));
        }

        private void Call(DynValue fn, DynValue dt)
        {
            try { _h.Script.Call(fn, UserData.Create(gameObject), _state, dt); }
            catch (InterpreterException e) { _failed = true; Plugin.Log.LogError($"lua {_h.Source} on {name}: {e.DecoratedMessage ?? e.Message}"); }
            catch (Exception e) { _failed = true; Plugin.Log.LogError($"lua {_h.Source} on {name}: {(e.InnerException ?? e).Message}"); }
        }
    }

    // The console's Lua: one state for the session. Apoca-95's console (or anything else) calls Run and listens to Output.
    public static class LuaConsole
    {
        // a line out: text, and 0 output, 1 error, 2 the command echoed
        public static event Action<string, int> Output;
        private static Script _script;

        public static void Run(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            Say("> " + code, 2);
            if (_script == null) _script = Lua.NewScript(null, m => Say(m, 0));
            try
            {
                // an expression prints its value; a statement just runs
                DynValue r;
                try { r = _script.DoString("return " + code, null, "console"); }
                catch (SyntaxErrorException) { r = _script.DoString(code, null, "console"); }
                if (r.Type != DataType.Void && r.Type != DataType.Nil) Say(Show(r), 0);
            }
            catch (InterpreterException e) { Say(e.DecoratedMessage ?? e.Message, 1); }
            catch (Exception e) { Say((e.InnerException ?? e).Message, 1); }
        }

        // a fresh state (live code reloaded, or a mess made)
        public static void Reset() { _script = null; Say("lua: reset", 0); }

        private static void Say(string s, int kind)
        {
            foreach (var line in s.Replace("\r", "").Split('\n')) Output?.Invoke(line, kind);
        }

        private static string Show(DynValue v)
        {
            if (v.Type == DataType.Tuple) return string.Join("\t", v.Tuple.Select(Show).ToArray());
            if (v.Type == DataType.UserData && v.UserData.Object != null) return v.UserData.Object.ToString();
            if (v.Type == DataType.Table)
            {
                var parts = v.Table.Pairs.Take(20).Select(p => p.Key.ToPrintString() + " = " + p.Value.ToPrintString()).ToList();
                return "{ " + string.Join(", ", parts.ToArray()) + (v.Table.Pairs.Count() > 20 ? ", ..." : "") + " }";
            }
            return v.ToPrintString();
        }
    }
}

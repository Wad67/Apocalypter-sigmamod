using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx;
using HarmonyLib;
using Mono.Cecil;
using UnityEngine;

namespace SigmaMod.Live
{
    // In a live DLL: a static method run after each load of its generation (components already carried over), and one
    // run before its generation is replaced. No parameters.
    [AttributeUsage(AttributeTargets.Method)] public sealed class OnLoadAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public sealed class OnUnloadAttribute : Attribute { }
}

namespace SigmaMod
{
    // Live code: C# loaded at start from every plugin's live folder (and BepInEx/live), and with Dev.LiveCode reloaded
    // while the game runs: built again (dotnet build straight into that folder), a DLL is loaded as a new generation:
    //   - renamed first (Cecil: <name>-gen<n>), since the runtime won't load two assemblies of one name, and Mono
    //     can't unload the old one (each generation stays in memory: a dev tool, off for play);
    //   - the old generation's Harmony patches come off, the new one's go on (its [HarmonyPatch] classes);
    //   - every live component of an old type gets the new type of the same name on its object, with every field
    //     carried over by name (references to old components, old-generation objects, lists, arrays and dictionaries
    //     of them converted), then the old one is destroyed. The new one is disabled while its fields are filled
    //     and enabled after, so OnEnable sees them; Awake runs once with defaults: set up in OnEnable. Static fields
    //     carry over the same way. A type gone from the new build loses its components (logged);
    //   - references other code holds to the old components (stable components' fields, FSM object variables) are
    //     pointed at the new ones;
    //   - [OnUnload] methods of the old generation run before, [OnLoad] methods of the new one after;
    //   - type lookups by name (SigmaMod's .json "add", components by name) give the newest generation.
    // Rules for live code: stable code (other plugins) never names a live type; it is reached through interfaces the
    // stable code defines, by name, or through these hooks. Undo every registration in OnDisable or [OnUnload]
    // (static events, other plugins' lists): after a reload the log says if any old component is still alive.
    // One live DLL must not reference another (the renamed generation wouldn't resolve).
    internal static class LiveCode
    {
        private sealed class Module
        {
            public string Path, Name;
            public DateTime Stamp;
            public long Size;
            public int Generation;
            public Assembly Assembly;
            public Harmony Harmony;
        }

        private static readonly Dictionary<string, Module> Modules = new Dictionary<string, Module>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Type> Latest = new Dictionary<string, Type>();   // full name -> newest generation's type
        private static readonly HashSet<Assembly> Retired = new HashSet<Assembly>();
        private static readonly Dictionary<string, (DateTime, long)> Seen = new Dictionary<string, (DateTime, long)>(StringComparer.OrdinalIgnoreCase);
        private static string _dir;
        private static float _next;
        private static int _generation;

        public static bool IsRetired(Assembly a) => Retired.Contains(a);

        // the newest live type of that full or short name, or null
        public static Type Find(string name)
        {
            if (Latest.Count == 0 || string.IsNullOrEmpty(name)) return null;
            if (Latest.TryGetValue(name, out var t)) return t;
            Type pick = null;
            foreach (var kv in Latest) if (kv.Value.Name == name) { if (pick != null) return null; pick = kv.Value; }
            return pick;
        }

        // where live DLLs are: every plugin's own live folder (a mod ships its live code there: loaded at start, always),
        // and BepInEx/live (a modder's own)
        private static List<string> Dirs()
        {
            var dirs = new List<string>();
            try { foreach (var d in Directory.GetDirectories(Paths.PluginPath)) { var l = System.IO.Path.Combine(d, "live"); if (Directory.Exists(l)) dirs.Add(l); } } catch { }
            if (_dir == null)
            {
                _dir = System.IO.Path.Combine(Paths.BepInExRootPath, "live");
                try { Directory.CreateDirectory(_dir); } catch { }
            }
            dirs.Add(_dir);
            return dirs;
        }

        private static bool _started;

        public static void Tick()
        {
            // without Dev.LiveCode: what is there loads once, and nothing is watched
            if (_started && !Plugin.LiveCodeEntry.Value || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            CheckLingering();
            var files = new List<string>();
            foreach (var d in Dirs()) try { files.AddRange(Directory.GetFiles(d, "*.dll")); } catch { }
            if (!_started)
            {
                _started = true;
                Plugin.Log.LogInfo("live code: " + files.Count + " DLL(s)" + (Plugin.LiveCodeEntry.Value ? ", watched for rebuilds" : ""));
                foreach (var f in files)
                {
                    try { var fi = new FileInfo(f); Seen[f] = (fi.LastWriteTimeUtc, fi.Length); Load(f, fi.LastWriteTimeUtc, fi.Length); }
                    catch (Exception e) { Plugin.Log.LogError("live code: " + f + ": " + e.Message); }
                }
                return;
            }
            foreach (var f in files)
            {
                FileInfo fi;
                try { fi = new FileInfo(f); } catch { continue; }
                var now = (fi.LastWriteTimeUtc, fi.Length);
                // the same stamp and size on two polls: the build has finished writing it
                bool settled = Seen.TryGetValue(f, out var was) && was == now;
                Seen[f] = now;
                if (!settled) continue;
                if (Modules.TryGetValue(f, out var m) && m.Stamp == now.LastWriteTimeUtc && m.Size == now.Length) continue;
                Load(f, now.LastWriteTimeUtc, now.Length);
            }
        }

        private static void Load(string path, DateTime stamp, long size)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Modules.TryGetValue(path, out var old);
            if (old != null) { old.Stamp = stamp; old.Size = size; }   // a broken build isn't tried again until it changes
            int gen = ++_generation;
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            Assembly asm;
            try { asm = Assembly.Load(Renamed(path, name + "-gen" + gen)); }
            catch (Exception e) { Plugin.Log.LogError($"live code: {name}: not loaded: {e.Message}"); return; }
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                Plugin.Log.LogError($"live code: {name}: types failed to load: " + string.Join("; ", e.LoaderExceptions.Select(x => x.Message).Distinct().Take(5).ToArray()));
                return;
            }

            // the old generation stands down
            if (old != null)
            {
                Hooks(old.Assembly, typeof(Live.OnUnloadAttribute), name);
                try { old.Harmony?.UnpatchSelf(); } catch (Exception e) { Plugin.Log.LogError($"live code: {name}: old patches: {e.Message}"); }
                Retired.Add(old.Assembly);
            }
            foreach (var key in Latest.Where(kv => old != null && kv.Value.Assembly == old.Assembly).Select(kv => kv.Key).ToList()) Latest.Remove(key);
            foreach (var t in types) if (t.FullName != null) Latest[t.FullName] = t;
            Types.Clear();

            var module = new Module { Path = path, Name = name, Stamp = stamp, Size = size, Generation = gen, Assembly = asm };
            Modules[path] = module;

            int migrated = 0, dropped = 0;
            if (old != null) Migrate(old.Assembly, asm, name, out migrated, out dropped);

            int patched = 0;
            try
            {
                module.Harmony = new Harmony("sigmamod.live." + name + ".gen" + gen);
                foreach (var t in types)
                    if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0) { module.Harmony.CreateClassProcessor(t).Patch(); patched++; }
            }
            catch (Exception e) { Plugin.Log.LogError($"live code: {name}: patches: {e}"); }

            Hooks(asm, typeof(Live.OnLoadAttribute), name);

            Plugin.Log.LogInfo($"live code: {name} generation {gen} loaded in {watch.ElapsedMilliseconds} ms: {types.Length} types, {patched} patch classes"
                + (old != null ? $", {migrated} components carried over" + (dropped > 0 ? $", {dropped} dropped (type gone)" : "") : ""));
            if (old != null) Pending.Add((old.Assembly, name, gen - 1));
        }

        // the old generation's components are destroyed at the end of the frame: looked for on the next poll
        private static readonly List<(Assembly asm, string name, int gen)> Pending = new List<(Assembly, string, int)>();
        private static void CheckLingering()
        {
            if (Pending.Count == 0) return;
            var all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
            foreach (var (asm, name, gen) in Pending)
            {
                var left = all.Where(c => c != null && c.GetType().Assembly == asm).ToList();
                if (left.Count > 0)
                    Plugin.Log.LogWarning($"live code: {name}: {left.Count} components of an old generation still alive: " +
                                          string.Join(", ", left.Take(8).Select(c => c.GetType().Name + " on " + c.name).ToArray()));
            }
            Pending.Clear();
        }

        // the DLL's bytes under another assembly name
        private static byte[] Renamed(string path, string newName)
        {
            var resolver = new DefaultAssemblyResolver();
            foreach (var d in new[] { Paths.ManagedPath, Paths.BepInExAssemblyDirectory, Paths.PluginPath, System.IO.Path.GetDirectoryName(path) })
                if (d != null && Directory.Exists(d)) resolver.AddSearchDirectory(d);
            foreach (var d in Directory.GetDirectories(Paths.PluginPath)) resolver.AddSearchDirectory(d);
            using (var input = new MemoryStream(File.ReadAllBytes(path)))
            using (var def = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver }))
            using (var output = new MemoryStream())
            {
                def.Name.Name = newName;
                def.MainModule.Name = newName + ".dll";
                def.Write(output);
                return output.ToArray();
            }
        }

        private static void Hooks(Assembly asm, Type attribute, string name)
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { return; }
            foreach (var t in types)
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.GetCustomAttributes(attribute, false).Length == 0 || m.GetParameters().Length > 0) continue;
                    try { m.Invoke(null, null); }
                    catch (Exception e) { Plugin.Log.LogError($"live code: {name}: {t.Name}.{m.Name}: {(e.InnerException ?? e)}"); }
                }
        }

        // ---------------------------------------------------------------- carrying state over

        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static void Migrate(Assembly from, Assembly to, string name, out int migrated, out int dropped)
        {
            migrated = dropped = 0;
            var olds = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(c => c != null && c.GetType().Assembly == from).ToList();
            var map = new Dictionary<object, object>(ReferenceComparer.Instance);
            var pairs = new List<(MonoBehaviour old, MonoBehaviour now, bool enabled)>();

            // new components first, quiet, so references between them can be pointed across
            foreach (var o in olds)
            {
                var nt = to.GetType(o.GetType().FullName);
                bool enabled = o.enabled;
                o.enabled = false;   // its OnDisable: it unregisters
                if (nt == null || !typeof(MonoBehaviour).IsAssignableFrom(nt)) { UnityEngine.Object.Destroy(o); dropped++; continue; }
                MonoBehaviour n;
                try { n = (MonoBehaviour)o.gameObject.AddComponent(nt); }
                catch (Exception e) { Plugin.Log.LogError($"live code: {name}: {nt.Name} on {o.name}: {e.Message}"); continue; }
                if (n == null) continue;
                n.enabled = false;
                map[o] = n;
                pairs.Add((o, n, enabled));
            }
            // statics, then fields
            foreach (var ot in SafeTypes(from))
            {
                var nt = to.GetType(ot.FullName);
                if (nt == null || ot.ContainsGenericParameters) continue;
                foreach (var f in ot.GetFields(Stat))
                {
                    if (f.IsLiteral) continue;
                    var nf = nt.GetField(f.Name, Stat);
                    if (nf == null || nf.IsLiteral) continue;
                    try { nf.SetValue(null, Convert(f.GetValue(null), nf.FieldType, to, map)); }
                    catch (Exception e) { Plugin.Verbose($"live code: {name}: static {ot.Name}.{f.Name} not carried over: {e.Message}"); }
                }
            }
            foreach (var (o, n, enabled) in pairs) CopyFields(o, n, o.GetType(), n.GetType(), to, map, name);
            int repointed = Repoint(from, to, map);
            foreach (var (o, n, enabled) in pairs)
            {
                n.enabled = enabled;   // OnEnable with its fields
                UnityEngine.Object.Destroy(o);
                migrated++;
            }
            if (repointed > 0) Plugin.Verbose($"live code: {name}: {repointed} references from other code re-pointed");
        }

        // References other code holds to the old components (a stable component's field, an FSM's object variable)
        // go to the new ones. Lists and arrays of them in other code are not followed.
        private static readonly Dictionary<Type, FieldInfo[]> RefFields = new Dictionary<Type, FieldInfo[]>();
        private static int Repoint(Assembly from, Assembly to, Dictionary<object, object> map)
        {
            if (map.Count == 0) return 0;
            int n = 0;
            foreach (var c in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (c == null) continue;
                var t = c.GetType();
                if (t.Assembly == from || t.Assembly == to || Retired.Contains(t.Assembly)) continue;
                if (c is PlayMakerFSM fsm)
                {
                    var vars = fsm.Fsm?.Variables?.ObjectVariables;
                    if (vars != null)
                        foreach (var v in vars)
                            if (v != null && v.Value != null && map.TryGetValue(v.Value, out var nv)) { v.Value = (UnityEngine.Object)nv; n++; }
                    continue;
                }
                if (!RefFields.TryGetValue(t, out var fields))
                {
                    var list = new List<FieldInfo>();
                    for (var bt = t; bt != null && bt != typeof(MonoBehaviour); bt = bt.BaseType)
                        foreach (var f in bt.GetFields(Inst))
                            if (!f.FieldType.IsValueType && f.FieldType != typeof(string) && !f.FieldType.IsArray && !(f.FieldType.IsGenericType))
                                list.Add(f);
                    RefFields[t] = fields = list.ToArray();
                }
                foreach (var f in fields)
                {
                    object v;
                    try { v = f.GetValue(c); } catch { continue; }
                    if (v == null || !map.TryGetValue(v, out var nv) || !f.FieldType.IsInstanceOfType(nv)) continue;
                    try { f.SetValue(c, nv); n++; } catch { }
                }
            }
            return n;
        }

        private static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(x => x != null); }
        }

        // every instance field, up to (not including) the first type that isn't from the old generation
        private static void CopyFields(object from, object to, Type ft, Type tt, Assembly gen, Dictionary<object, object> map, string name)
        {
            for (; ft != null && tt != null && ft.FullName == tt.FullName && ft.Assembly != tt.Assembly; ft = ft.BaseType, tt = tt.BaseType)
                foreach (var f in ft.GetFields(Inst))
                {
                    var nf = tt.GetField(f.Name, Inst);
                    if (nf == null) continue;
                    try { nf.SetValue(to, Convert(f.GetValue(from), nf.FieldType, gen, map)); }
                    catch (Exception e) { Plugin.Verbose($"live code: {name}: {ft.Name}.{f.Name} not carried over: {e.Message}"); }
                }
        }

        // a value of the old generation as the new generation's type
        private static object Convert(object v, Type target, Assembly gen, Dictionary<object, object> map)
        {
            if (v == null) return null;
            if (v is UnityEngine.Object uo)
            {
                if (uo == null) return null;
                if (map.TryGetValue(v, out var m)) return m;
                return target.IsInstanceOfType(v) ? v : null;
            }
            var vt = v.GetType();
            if (vt.IsEnum) return target.IsInstanceOfType(v) ? v : ChangeEnum(v, target);
            if (vt.IsPrimitive || v is string)
            {
                if (target.IsInstanceOfType(v)) return v;
                try { return System.Convert.ChangeType(v, target); } catch { return null; }
            }
            if (!FromOldGeneration(vt) && target.IsInstanceOfType(v)) return v;   // nothing of the old generation in it
            if (map.TryGetValue(v, out var done)) return done;
            if (v is Array arr && target.IsArray)
            {
                var et = target.GetElementType();
                var na = Array.CreateInstance(et, arr.Length);
                map[v] = na;
                for (int i = 0; i < arr.Length; i++) na.SetValue(Convert(arr.GetValue(i), et, gen, map), i);
                return na;
            }
            if (target.IsGenericType && v is IDictionary dict && typeof(IDictionary).IsAssignableFrom(target))
            {
                var nd = (IDictionary)Activator.CreateInstance(target);
                map[v] = nd;
                var args = target.GetGenericArguments();
                foreach (DictionaryEntry e in dict)
                {
                    var k = Convert(e.Key, args[0], gen, map);
                    if (k != null) nd[k] = Convert(e.Value, args[1], gen, map);
                }
                return nd;
            }
            if (target.IsGenericType && v is IList list && typeof(IList).IsAssignableFrom(target))
            {
                var nl = (IList)Activator.CreateInstance(target);
                map[v] = nl;
                var et = target.GetGenericArguments()[0];
                foreach (var e in list) nl.Add(Convert(e, et, gen, map));
                return nl;
            }
            // a class or struct of the old generation: the new one's, field by field
            var nt = vt.Assembly != gen && vt.FullName != null ? gen.GetType(vt.FullName) : null;
            if (nt != null && target.IsAssignableFrom(nt))
            {
                var n = FormatterServices.GetUninitializedObject(nt);
                if (!nt.IsValueType) map[v] = n;
                CopyFields(v, n, vt, nt, gen, map, "");
                return n;
            }
            return target.IsInstanceOfType(v) ? v : null;
        }

        private static object ChangeEnum(object v, Type target)
        {
            if (!target.IsEnum) return null;
            try { return Enum.Parse(target, v.ToString()); } catch { return Activator.CreateInstance(target); }
        }

        private static bool FromOldGeneration(Type t)
        {
            if (Retired.Contains(t.Assembly)) return true;
            if (t.IsArray) return FromOldGeneration(t.GetElementType());
            if (t.IsGenericType) return t.GetGenericArguments().Any(FromOldGeneration);
            return false;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SigmaMod
{
    // Every value of a chassis' Rigidbody, VehicleController and wheels, as paths the .json takes
    // (Rigidbody.mass, VehicleController.powertrain.wheels.0.wheelUAPI.spring.maxForce, ...), taken at
    // three points: the donor vehicle's values, after the reset to NWH defaults, and final (after the
    // .json). Written to BepInEx/SigmaMod-chassis-<id>.txt. The first section lists what the reset
    // changed and the .json does not set: values still at NWH defaults rather than chosen for the car.
    internal static class ValueReport
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const int MaxDepth = 10, MaxList = 64;

        public static Dictionary<string, string> Snapshot(GameObject root)
        {
            var d = new Dictionary<string, string>();
            var rb = root.GetComponent<Rigidbody>();
            if (rb != null)
            {
                d["Rigidbody.mass"] = Str(rb.mass);
                d["Rigidbody.drag"] = Str(rb.drag);
                d["Rigidbody.angularDrag"] = Str(rb.angularDrag);
                d["Rigidbody.centerOfMass"] = Str(rb.centerOfMass);
                d["Rigidbody.inertiaTensor"] = Str(rb.inertiaTensor);
                d["Rigidbody.inertiaTensorRotation"] = Str(rb.inertiaTensorRotation.eulerAngles);
                d["Rigidbody.maxAngularVelocity"] = Str(rb.maxAngularVelocity);
                d["Rigidbody.interpolation"] = rb.interpolation.ToString();
                d["Rigidbody.collisionDetectionMode"] = rb.collisionDetectionMode.ToString();
                d["Rigidbody.solverIterations"] = Str(rb.solverIterations);
            }
            var vc = root.GetComponent<NWH.VehiclePhysics2.VehicleController>();
            if (vc != null) Walk(vc, "VehicleController", d, 0, new HashSet<object>(new RefEq()), true);
            return d;
        }

        public static void Write(string id, Dictionary<string, string> donor, Dictionary<string, string> reset, Dictionary<string, string> final)
        {
            var keys = donor.Keys.Union(reset.Keys).Union(final.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
            string Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "-";
            var open = keys.Where(k => Get(donor, k) != Get(reset, k) && Get(reset, k) == Get(final, k)).ToList();
            var json = keys.Where(k => Get(reset, k) != Get(final, k)).ToList();
            var path = Path.Combine(BepInEx.Paths.BepInExRootPath, $"{Plugin.NAME}-chassis-{id}.txt");
            using (var w = new StreamWriter(path))
            {
                w.WriteLine($"chassis '{id}': {keys.Count} values, {json.Count} set by the .json, {open.Count} changed by the reset and not set by the .json");
                w.WriteLine("columns: path | donor | NWH default | final");
                w.WriteLine();
                w.WriteLine($"==== not set by the .json, differs from the donor ({open.Count})");
                foreach (var k in open) w.WriteLine($"{k} | {Get(donor, k)} | {Get(reset, k)} | {Get(final, k)}");
                w.WriteLine();
                w.WriteLine($"==== set by the .json ({json.Count})");
                foreach (var k in json) w.WriteLine($"{k} | {Get(donor, k)} | {Get(reset, k)} | {Get(final, k)}");
                w.WriteLine();
                w.WriteLine($"==== all ({keys.Count})");
                foreach (var k in keys) w.WriteLine($"{k} | {Get(donor, k)} | {Get(reset, k)} | {Get(final, k)}");
            }
            Plugin.Log.LogInfo($"custom prefab: '{id}': {open.Count} chassis values left at NWH defaults (differ from the donor, not in the .json); see {Path.GetFileName(path)}");
        }

        private static void Walk(object o, string path, Dictionary<string, string> d, int depth, HashSet<object> seen, bool top)
        {
            if (depth > MaxDepth || (!o.GetType().IsValueType && !seen.Add(o))) return;
            for (var t = o.GetType(); t != null && t != typeof(object) && t != typeof(MonoBehaviour) && t != typeof(Behaviour) && t != typeof(Component) && t != typeof(UnityEngine.Object) && t != typeof(ValueType); t = t.BaseType)
                foreach (var f in t.GetFields(Flags))
                {
                    if (f.IsNotSerialized || typeof(Delegate).IsAssignableFrom(f.FieldType) || f.IsDefined(typeof(CompilerGeneratedAttribute), false)) continue;
                    if (!f.IsPublic && !f.IsDefined(typeof(SerializeField), false)) continue;
                    object v;
                    try { v = f.GetValue(o); }
                    catch { continue; }
                    Value(v, path + "." + f.Name, d, depth + 1, seen);
                }
        }

        private static void Value(object v, string path, Dictionary<string, string> d, int depth, HashSet<object> seen)
        {
            if (v == null) { d[path] = "null"; return; }
            if (v is UnityEngine.Object uo)
            {
                // wheels are part of the chassis: walk into them; other objects are references
                if (uo != null && v is NWH.Common.Vehicles.WheelUAPI) Walk(v, path, d, depth, seen, false);
                else d[path] = uo == null ? "null" : $"<{uo.GetType().Name} '{uo.name}'>";
                return;
            }
            if (Leaf(v, out string s)) { d[path] = s; return; }
            if (v is AnimationCurve curve) { d[path] = "[" + string.Join(", ", curve.keys.Select(k => $"[{Str(k.time)}, {Str(k.value)}]").ToArray()) + "]"; return; }
            if (v is IList list)
            {
                var leaves = new List<string>();
                bool allLeaf = true;
                foreach (var e in list) { if (e != null && Leaf(e, out string es)) leaves.Add(es); else { allLeaf = false; break; } }
                if (allLeaf) { d[path] = "[" + string.Join(", ", leaves.ToArray()) + "]"; return; }
                d[path + ".Count"] = Str(list.Count);
                for (int i = 0; i < Math.Min(list.Count, MaxList); i++) Value(list[i], path + "." + i, d, depth, seen);
                return;
            }
            if (v is IEnumerable) return; // dictionaries, sets: not settable by path
            Walk(v, path, d, depth, seen, false);
        }

        private static bool Leaf(object v, out string s)
        {
            switch (v)
            {
                case float f: s = Str(f); return true;
                case double db: s = db.ToString("G7", CultureInfo.InvariantCulture); return true;
                case bool b: s = b ? "true" : "false"; return true;
                case string str: s = "\"" + str + "\""; return true;
                case Enum e: s = e.ToString(); return true;
                case Vector2 v2: s = $"[{Str(v2.x)}, {Str(v2.y)}]"; return true;
                case Vector3 v3: s = Str(v3); return true;
                case Vector4 v4: s = $"[{Str(v4.x)}, {Str(v4.y)}, {Str(v4.z)}, {Str(v4.w)}]"; return true;
                case Quaternion q: s = $"[{Str(q.x)}, {Str(q.y)}, {Str(q.z)}, {Str(q.w)}]"; return true;
                case Color c: s = $"[{Str(c.r)}, {Str(c.g)}, {Str(c.b)}, {Str(c.a)}]"; return true;
                case LayerMask lm: s = Str(lm.value); return true;
            }
            if (v.GetType().IsPrimitive) { s = Convert.ToString(v, CultureInfo.InvariantCulture); return true; }
            s = null;
            return false;
        }

        private static string Str(float f) => f.ToString("G7", CultureInfo.InvariantCulture);
        private static string Str(int i) => i.ToString(CultureInfo.InvariantCulture);
        private static string Str(Vector3 v) => $"[{Str(v.x)}, {Str(v.y)}, {Str(v.z)}]";

        private sealed class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using SigmaMod;

// dump <file>            print the node tree, meshes, materials and extras
// synth <out.glb>        write a synthetic model (hierarchy, rotations, extras, 2 submeshes)
// compare <a> <b>        compare node tree, TRS, extras and mesh sizes of two files
// variant <out.glb> <es3prefab> <sigma_id> [sigma_name] [key=value ...]
//                        a template that is a game prefab as it is: one root node with those properties
// expand <file.json> [Managed dir]
//                        print a template .json with its shorthand expanded to the long form (types from the
//                        game's assemblies, found above this tool unless given)
static class Program
{
    static int Main(string[] a)
    {
        switch (a[0])
        {
            case "expand": return Expand(a[1], a.Length > 2 ? a[2] : null);
            case "dump": Dump(GltfReader.Load(a[1])); return 0;
            case "synth": GltfWriter.Save(Synth(), a[1]); return 0;
            case "compare": return Compare(GltfReader.Load(a[1]), GltfReader.Load(a[2]));
            case "variant":
            {
                var ex = new Dictionary<string, object> { ["es3prefab"] = a[2], ["sigma_id"] = a[3] };
                if (a.Length > 4) ex["sigma_name"] = a[4];
                foreach (var kv in a.Skip(5)) { int eq = kv.IndexOf('='); ex[kv.Substring(0, eq)] = kv.Substring(eq + 1); }
                var m = new GltfModel();
                m.Nodes.Add(new GNode { Name = a[3], Extras = ex });
                m.Roots.Add(0);
                GltfWriter.Save(m, a[1]);
                Console.WriteLine("wrote " + a[1] + " " + Json.Write(ex));
                return 0;
            }
        }
        return 2;
    }

    // types are looked up in the game's own assemblies, loaded for reflection only (nothing in them runs)
    static int Expand(string file, string managed)
    {
        for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); managed == null && d != null; d = d.Parent)
            if (System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "Apocalypter_Data", "Managed"))) managed = System.IO.Path.Combine(d.FullName, "Apocalypter_Data", "Managed");
        if (managed == null || !System.IO.Directory.Exists(managed)) { Console.Error.WriteLine("no Managed folder at " + managed + "; give it as the second argument"); return 2; }
        var asms = new List<System.Reflection.Assembly>();
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var p = System.IO.Path.Combine(managed, name.Name + ".dll");
            return System.IO.File.Exists(p) ? ctx.LoadFromAssemblyPath(System.IO.Path.GetFullPath(p)) : null;
        };
        foreach (var dll in System.IO.Directory.GetFiles(managed, "*.dll"))
        {
            var n = System.IO.Path.GetFileNameWithoutExtension(dll);
            if (n == "mscorlib" || n == "netstandard" || n.StartsWith("System") || n.StartsWith("Microsoft")) continue;
            try { asms.Add(System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(System.IO.Path.GetFullPath(dll))); } catch { }
        }
        var cache = new Dictionary<string, Type>();
        Shorthand.FindType = name =>
        {
            if (cache.TryGetValue(name, out var have)) return have;
            Type pick = Type.GetType(name);
            foreach (var asm in asms)
            {
                if (pick != null && pick.FullName == name) break;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t.FullName == name) { pick = t; break; }
                    if (t.Name == name && (pick == null || (t.Namespace ?? "").StartsWith("UnityEngine") && !(pick.Namespace ?? "").StartsWith("UnityEngine"))) pick = t;
                }
            }
            return cache[name] = pick;
        };
        var doc = Json.Parse(System.IO.File.ReadAllText(file)) as Dictionary<string, object>;
        int warnings = 0;
        var res = Shorthand.Expand(doc, w => { warnings++; Console.Error.WriteLine("warning: " + w); });
        Console.WriteLine(Json.Pretty(res));
        if (Json.Write(Shorthand.Expand(res, null)) != Json.Write(res)) { Console.Error.WriteLine("error: the long form doesn't expand to itself"); return 1; }
        return warnings > 0 ? 1 : 0;
    }

    static GltfModel Synth()
    {
        var m = new GltfModel();
        // a box with two materials (top face separate)
        var mesh = new GMesh { Name = "body" };
        var p = new List<float>(); var n = new List<float>(); var uv = new List<float>();
        void V(float x, float y, float z, float nx, float ny, float nz) { p.AddRange(new[] { x, y, z }); n.AddRange(new[] { nx, ny, nz }); uv.AddRange(new[] { x + 0.5f, z + 0.5f }); }
        V(-1, 1, -2, 0, 1, 0); V(1, 1, -2, 0, 1, 0); V(1, 1, 2, 0, 1, 0); V(-1, 1, 2, 0, 1, 0);
        V(-1, 0, -2, 0, -1, 0); V(1, 0, -2, 0, -1, 0); V(1, 0, 2, 0, -1, 0); V(-1, 0, 2, 0, -1, 0);
        mesh.Positions = p.ToArray(); mesh.Normals = n.ToArray(); mesh.Uvs = uv.ToArray();
        // Unity winding (clockwise seen from the normal side)
        mesh.Submeshes.Add(new[] { 0, 3, 2, 0, 2, 1 }); mesh.SubmeshMaterials.Add(0);
        mesh.Submeshes.Add(new[] { 4, 5, 6, 4, 6, 7 }); mesh.SubmeshMaterials.Add(1);
        m.Meshes.Add(mesh);
        m.Materials.Add(new GMaterial { Name = "paint", BaseColor = new[] { 0.8f, 0, 0, 1 }, Metallic = 0.2f, Roughness = 0.4f });
        m.Materials.Add(new GMaterial { Name = "glass", BaseColor = new[] { 0, 0, 0, 0.2f }, AlphaMode = "BLEND", Emissive = new[] { 2f, 1f, 0f } });

        var q = Q(10, 30, -20);
        m.Nodes.Add(new GNode { Name = "chassis", Mesh = 0, Extras = new Dictionary<string, object> { ["es3ref"] = "1234567890123", ["mass"] = 900.0 } });
        m.Nodes.Add(new GNode { Name = "hinge_engine", T = new[] { 0.1f, 0.4f, 1.3f }, R = q, Extras = new Dictionary<string, object> { ["fsm"] = new List<object> { "vehPart_Attach" } } });
        m.Nodes.Add(new GNode { Name = "wheel_FL", T = new[] { -0.8f, 0.3f, 1.4f }, S = new[] { 1f, 2f, 3f }, Extras = new Dictionary<string, object> { ["wheel_radius"] = 0.33 } });
        m.Nodes.Add(new GNode { Name = "nested", T = new[] { 0.2f, 0, 0 }, R = Q(0, 90, 0) });
        m.Nodes[0].Children.AddRange(new[] { 1, 2 }); m.Nodes[1].Parent = 0; m.Nodes[2].Parent = 0;
        m.Nodes[1].Children.Add(3); m.Nodes[3].Parent = 1;
        m.Roots.Add(0);
        return m;
    }

    // Unity's Euler order (Z, X, Y applied) as a quaternion
    static float[] Q(float x, float y, float z)
    {
        double r = Math.PI / 360;
        double cx = Math.Cos(x * r), sx = Math.Sin(x * r), cy = Math.Cos(y * r), sy = Math.Sin(y * r), cz = Math.Cos(z * r), sz = Math.Sin(z * r);
        // q = qy * qx * qz
        return new[]
        {
            (float)(cy * sx * cz + sy * cx * sz), (float)(sy * cx * cz - cy * sx * sz),
            (float)(cy * cx * sz - sy * sx * cz), (float)(cy * cx * cz + sy * sx * sz)
        };
    }

    static void Dump(GltfModel m)
    {
        foreach (var w in m.Warnings) Console.WriteLine("warning: " + w);
        foreach (int r in m.Roots) Node(m, r, 0);
        for (int i = 0; i < m.Meshes.Count; i++)
        {
            var g = m.Meshes[i];
            var (mn, mx) = Bounds(g.Positions);
            Console.WriteLine($"mesh {i} '{g.Name}': {g.Positions.Length / 3} verts, submeshes [{string.Join(", ", g.Submeshes.Select((s, k) => (s.Length / 3) + " tris mat " + g.SubmeshMaterials[k]))}], normals={g.Normals != null}, uvs={g.Uvs != null}, winding {Facing(g)}, bounds {F(mn)}..{F(mx)}{(g.Extras != null ? " extras " + Json.Write(g.Extras) : "")}");
        }
        for (int i = 0; i < m.Materials.Count; i++)
        {
            var g = m.Materials[i];
            Console.WriteLine($"material {i} '{g.Name}': base {F(g.BaseColor)} metal {g.Metallic} rough {g.Roughness} alpha {g.AlphaMode} emissive {F(g.Emissive)} image {g.BaseColorImage}");
        }
        for (int i = 0; i < m.Images.Count; i++) Console.WriteLine($"image {i} '{m.Images[i].Name}' {m.Images[i].MimeType} {m.Images[i].Data?.Length} bytes");
    }

    static void Node(GltfModel m, int i, int depth)
    {
        var n = m.Nodes[i];
        Console.WriteLine($"{new string(' ', depth * 2)}{n.Name} T{F(n.T)} R{F(n.R)} S{F(n.S)}{(n.Mesh >= 0 ? " mesh " + n.Mesh : "")}{(n.Extras != null ? " extras " + Json.Write(n.Extras) : "")}");
        foreach (int c in n.Children) Node(m, c, depth + 1);
    }

    static int Compare(GltfModel a, GltfModel b)
    {
        int bad = 0;
        var bn = b.Nodes.Where(n => n.Name != null).GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var n in a.Nodes)
        {
            if (!bn.TryGetValue(n.Name, out var o)) { Console.WriteLine("missing node " + n.Name); bad++; continue; }
            string pa = n.Parent >= 0 ? a.Nodes[n.Parent].Name : null, pb = o.Parent >= 0 ? b.Nodes[o.Parent].Name : null;
            if (pa != pb) { Console.WriteLine($"{n.Name}: parent {pa} vs {pb}"); bad++; }
            if (!Near(n.T, o.T)) { Console.WriteLine($"{n.Name}: T {F(n.T)} vs {F(o.T)}"); bad++; }
            if (!Near(n.S, o.S)) { Console.WriteLine($"{n.Name}: S {F(n.S)} vs {F(o.S)}"); bad++; }
            float dot = Math.Abs(n.R[0] * o.R[0] + n.R[1] * o.R[1] + n.R[2] * o.R[2] + n.R[3] * o.R[3]);
            if (dot < 0.9999f) { Console.WriteLine($"{n.Name}: R {F(n.R)} vs {F(o.R)}"); bad++; }
            string ea = n.Extras != null ? Json.Write(n.Extras) : "{}", eb = o.Extras != null ? Json.Write(o.Extras) : "{}";
            if (ea != eb) Console.WriteLine($"{n.Name}: extras {ea} vs {eb}");
            if ((n.Mesh >= 0) != (o.Mesh >= 0)) { Console.WriteLine($"{n.Name}: mesh presence differs"); bad++; }
            else if (n.Mesh >= 0)
            {
                var ma = a.Meshes[n.Mesh]; var mb = b.Meshes[o.Mesh];
                var (a0, a1) = Bounds(ma.Positions); var (b0, b1) = Bounds(mb.Positions);
                int ta = ma.Submeshes.Sum(s => s.Length), tb = mb.Submeshes.Sum(s => s.Length);
                if (!Near(a0, b0) || !Near(a1, b1) || ta != tb) { Console.WriteLine($"{n.Name}: mesh bounds/tris {F(a0)}..{F(a1)}/{ta} vs {F(b0)}..{F(b1)}/{tb}"); bad++; }
                if (!SameWinding(ma, mb)) { Console.WriteLine($"{n.Name}: winding/normals disagree"); bad++; }
            }
        }
        Console.WriteLine(bad == 0 ? "MATCH" : bad + " differences");
        return bad == 0 ? 0 : 1;
    }

    // +1 when most triangles' face normals agree with their vertex normals (correct Unity winding)
    static bool SameWinding(GMesh a, GMesh b) => Facing(a) == Facing(b);

    static int Facing(GMesh m)
    {
        if (m.Normals == null || m.Submeshes.Count == 0) return 0;
        int agree = 0;
        foreach (var s in m.Submeshes)
            for (int t = 0; t < s.Length; t += 3)
            {
                float[] P(int i) => new[] { m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2] };
                var p0 = P(s[t]); var p1 = P(s[t + 1]); var p2 = P(s[t + 2]);
                float[] u = { p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2] }, v = { p2[0] - p0[0], p2[1] - p0[1], p2[2] - p0[2] };
                // Unity winding: (p1 - p0) x (p2 - p0) points along the normal
                float[] c = { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };
                int k = s[t];
                float d = c[0] * m.Normals[k * 3] + c[1] * m.Normals[k * 3 + 1] + c[2] * m.Normals[k * 3 + 2];
                agree += d > 0 ? 1 : -1;
            }
        return Math.Sign(agree);
    }

    static (float[], float[]) Bounds(float[] p)
    {
        var mn = new[] { float.MaxValue, float.MaxValue, float.MaxValue }; var mx = new[] { float.MinValue, float.MinValue, float.MinValue };
        for (int i = 0; i < p.Length; i++) { mn[i % 3] = Math.Min(mn[i % 3], p[i]); mx[i % 3] = Math.Max(mx[i % 3], p[i]); }
        return (mn, mx);
    }

    static bool Near(float[] a, float[] b) => a.Length == b.Length && a.Zip(b, (x, y) => Math.Abs(x - y) < 1e-3f).All(x => x);
    static string F(float[] v) => "(" + string.Join(",", v.Select(x => x.ToString("0.###"))) + ")";
}

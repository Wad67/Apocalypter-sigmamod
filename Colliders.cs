using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SigmaMod
{
    // "collider" on an object in the .json makes colliders from the object's own Blender mesh:
    //   "collider": "convex" | "box" | "sphere" | "mesh"
    //   "collider": { "shape": "convex", "trigger": false, "render": false, "material": "asset:PhysicMaterial/<name>",
    //                 "from": "<object>" }
    // "from" makes a box or sphere around another object's mesh instead (that object's renderer is switched off
    // unless "render": true): an unscaled hinge can take its trigger from a scaled marker mesh, so parts fitted to
    // the hinge aren't stretched by the marker's scale.
    //
    // convex  every loose part of the mesh becomes one convex MeshCollider. A moving Rigidbody (a car) can only
    //         collide with convex shapes; PhysX wraps each part in its convex hull, so model concave things
    //         (a floor pan with sills, a tunnel) as several loose convex parts in one object. Parts whose hull is
    //         noticeably bigger than the part are logged.
    // box     a BoxCollider around the mesh bounds; sphere likewise.
    // mesh    the mesh as it is (concave allowed): static or kinematic objects only, PhysX refuses it on a
    //         moving Rigidbody.
    // The object's renderer is switched off unless "render": true.
    internal static class Colliders
    {
        public static void Apply(GameObject go, object spec, JsonValues ctx, string where)
        {
            string shape = spec as string;
            bool trigger = false, render = false;
            PhysicMaterial material = null;
            if (spec is Dictionary<string, object> d)
            {
                shape = d.TryGetValue("shape", out var sv) ? sv as string : "convex";
                trigger = d.TryGetValue("trigger", out var tv) && tv is bool tb && tb;
                render = d.TryGetValue("render", out var rv) && rv is bool rb && rb;
                if (d.TryGetValue("material", out var mv)) material = ctx.Ref(mv as string, typeof(PhysicMaterial)) as PhysicMaterial;
            }
            GameObject source = go;
            if (spec is Dictionary<string, object> fd && fd.TryGetValue("from", out var fv))
            {
                source = ctx.Node(fv as string ?? "");
                if (source == null) { Plugin.Log.LogWarning($"{where}: collider: \"from\": no object '{fv}'"); return; }
            }
            var mf = source.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) { Plugin.Log.LogWarning($"{where}: collider: '{source.name}' has no mesh to make it from"); return; }
            var bounds = mesh.bounds;
            if (source != go)
            {
                // the other mesh's corners in this object's space
                var b = mesh.bounds;
                bool first = true;
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                    var p = go.transform.InverseTransformPoint(source.transform.TransformPoint(c));
                    if (first) { bounds = new Bounds(p, Vector3.zero); first = false; }
                    else bounds.Encapsulate(p);
                }
                if ((shape ?? "convex").ToLowerInvariant() != "box" && (shape ?? "").ToLowerInvariant() != "sphere")
                {
                    Plugin.Log.LogWarning($"{where}: collider: \"from\" works with box and sphere; using box");
                    shape = "box";
                }
            }

            var made = new List<Collider>();
            switch ((shape ?? "convex").ToLowerInvariant())
            {
                case "box":
                {
                    var c = go.AddComponent<BoxCollider>();
                    c.center = bounds.center;
                    c.size = bounds.size;
                    made.Add(c);
                    break;
                }
                case "sphere":
                {
                    var c = go.AddComponent<SphereCollider>();
                    c.center = bounds.center;
                    c.radius = bounds.extents.magnitude / Mathf.Sqrt(3f);
                    made.Add(c);
                    break;
                }
                case "mesh":
                {
                    var body = go.GetComponentInParent<Rigidbody>();
                    if (body != null && !body.isKinematic)
                        Plugin.Log.LogWarning($"{where}: collider: '{go.name}' is a concave mesh collider on a moving Rigidbody; PhysX ignores those. Use \"convex\".");
                    var c = go.AddComponent<MeshCollider>();
                    c.sharedMesh = mesh;
                    made.Add(c);
                    break;
                }
                case "convex":
                {
                    var parts = Islands(mesh);
                    for (int k = 0; k < parts.Count; k++)
                    {
                        var part = parts[k];
                        part.name = $"{go.name} collider {k}";
                        ctx.Created?.Add(new KeyValuePair<string, UnityEngine.Object>("collider/" + go.name + "/" + k, part));
                        var c = go.AddComponent<MeshCollider>();
                        c.sharedMesh = part;
                        c.convex = true;
                        made.Add(c);
                        int tris = part.triangles.Length / 3;
                        if (tris > 255) Plugin.Log.LogWarning($"{where}: collider: '{go.name}' part {k} has {tris} triangles; its hull gets simplified to 255");
                        double grow = HullGrowth(part, go.transform.lossyScale);
                        if (grow > 0.05)
                            Plugin.Log.LogWarning($"{where}: collider: '{go.name}' part {k} is concave; it collides as its convex hull, {grow * 100:0}% bigger. " +
                                                  "Split it into convex loose parts if that matters.");
                    }
                    Plugin.Verbose($"{where}: collider: '{go.name}': {parts.Count} convex parts");
                    break;
                }
                default:
                    Plugin.Log.LogWarning($"{where}: collider: unknown shape '{shape}' (convex, box, sphere, mesh)");
                    return;
            }
            foreach (var c in made)
            {
                c.isTrigger = trigger;
                if (material != null) c.sharedMaterial = material;
            }
            if (!render)
            {
                var mr = source.GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = false;
            }
        }

        // loose parts, vertices joined by position (glTF splits them at UV and normal seams)
        private static List<Mesh> Islands(Mesh mesh)
        {
            var v = mesh.vertices;
            var tri = mesh.triangles;
            var byPos = new Dictionary<Vector3, int>();
            var weld = new int[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                var key = new Vector3(Mathf.Round(v[i].x * 1e4f), Mathf.Round(v[i].y * 1e4f), Mathf.Round(v[i].z * 1e4f));
                if (!byPos.TryGetValue(key, out int w)) byPos[key] = w = i;
                weld[i] = w;
            }
            var parent = Enumerable.Range(0, v.Length).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            for (int t = 0; t < tri.Length; t += 3)
            {
                int a = Find(weld[tri[t]]);
                parent[Find(weld[tri[t + 1]])] = a;
                parent[Find(weld[tri[t + 2]])] = a;
            }
            var groups = new Dictionary<int, List<int>>();
            for (int t = 0; t < tri.Length; t += 3)
            {
                int g = Find(weld[tri[t]]);
                if (!groups.TryGetValue(g, out var list)) groups[g] = list = new List<int>();
                list.Add(t);
            }
            var result = new List<Mesh>();
            foreach (var g in groups.Values)
            {
                var map = new Dictionary<int, int>();
                var pv = new List<Vector3>();
                var pt = new List<int>();
                foreach (int t in g)
                    for (int k = 0; k < 3; k++)
                    {
                        int w = weld[tri[t + k]];
                        if (!map.TryGetValue(w, out int ni)) { map[w] = ni = pv.Count; pv.Add(v[w]); }
                        pt.Add(ni);
                    }
                var m = new Mesh();
                m.SetVertices(pv);
                m.SetTriangles(pt, 0);
                m.RecalculateBounds();
                result.Add(m);
            }
            return result;
        }

        // (hull volume - part volume) / part volume, in world scale; 0 when unknown (too many points to check)
        private static double HullGrowth(Mesh m, Vector3 scale)
        {
            var mv = m.vertices;
            var p = mv.Select(x => Vector3.Scale(x, scale)).Distinct().ToArray();
            if (p.Length < 4 || p.Length > 64) return 0;
            var t = m.triangles;
            double vol = 0;
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 a = Vector3.Scale(mv[t[i]], scale), b = Vector3.Scale(mv[t[i + 1]], scale), c = Vector3.Scale(mv[t[i + 2]], scale);
                vol += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
            }
            vol = Math.Abs(vol);
            if (vol < 1e-9) return 0; // open or flat: nothing to compare

            // brute-force hull: every plane through three points with all points on one side
            var centre = p.Aggregate(Vector3.zero, (s, x) => s + x) / p.Length;
            var seen = new HashSet<string>();
            double hull = 0;
            for (int i = 0; i < p.Length; i++)
                for (int j = i + 1; j < p.Length; j++)
                    for (int k = j + 1; k < p.Length; k++)
                    {
                        var n = Vector3.Cross(p[j] - p[i], p[k] - p[i]);
                        if (n.sqrMagnitude < 1e-12f) continue;
                        n.Normalize();
                        float d = Vector3.Dot(n, p[i]);
                        float lo = 0, hi = 0;
                        foreach (var q in p) { float s = Vector3.Dot(n, q) - d; lo = Math.Min(lo, s); hi = Math.Max(hi, s); }
                        if (lo < -1e-5f && hi > 1e-5f) continue;
                        if (hi > 1e-5f) { n = -n; d = -d; }
                        string key = $"{Math.Round(n.x, 3)},{Math.Round(n.y, 3)},{Math.Round(n.z, 3)},{Math.Round(d, 3)}";
                        if (!seen.Add(key)) continue;
                        var face = p.Where(q => Math.Abs(Vector3.Dot(n, q) - d) < 1e-4f).ToList();
                        var fc = face.Aggregate(Vector3.zero, (s, x) => s + x) / face.Count;
                        var ax = (face[0] - fc).normalized;
                        var ay = Vector3.Cross(n, ax);
                        face.Sort((x, y) => Math.Atan2(Vector3.Dot(x - fc, ay), Vector3.Dot(x - fc, ax)).CompareTo(Math.Atan2(Vector3.Dot(y - fc, ay), Vector3.Dot(y - fc, ax))));
                        double area = 0;
                        for (int q = 0; q < face.Count; q++) area += Vector3.Dot(Vector3.Cross(face[q] - fc, face[(q + 1) % face.Count] - fc), n) / 2.0;
                        hull += Math.Abs(area) * Math.Abs(Vector3.Dot(n, centre) - d) / 3.0;
                    }
            return (hull - vol) / vol;
        }
    }
}
